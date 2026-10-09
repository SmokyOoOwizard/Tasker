//! Вызов инструмента: фильтры .NET в том же порядке — ошибки без префикса SDK (`ErrorText`), область из аргумента `workspace` и агент
//! из заголовка `X-Tasker-Agent` (`WorkspaceArgument`), проект по умолчанию (`DefaultProject`), затем связывание аргументов по схеме
//! (у .NET — SDK) и сам инструмент (`McpCall`). Домен синхронный: инструмент выполняется в `spawn_blocking`.
use crate::args::Args;
use crate::catalog::{Catalog, LIST_WORKSPACES, PROJECT_ARGUMENT, ToolDef, WORKSPACE_ARGUMENT};
use crate::error::{Result, ToolError, invoke_error};
use crate::tools;
use crate::workspaces::{McpWorkspaceScope, McpWorkspaceStatus, McpWorkspaces};
use serde_json::{Map, Value};
use std::sync::Arc;
use std::time::{Duration, Instant};
use tasker_core::ids::{guid_d, parse_guid};
use tasker_core::locks::EditHolder;
use tasker_core::tasks::Page;
use tasker_core::validate::eq_ignore_case;
use tasker_services::Workspace;
use uuid::Uuid;

/// Заголовок с id агента области (`McpRegistration.AgentHeader`).
pub const AGENT_HEADER: &str = "X-Tasker-Agent";

/// Переменная окружения: сколько вызов ждёт открытия области (мс); по умолчанию 5 с.
pub const OPEN_WAIT_VARIABLE: &str = "TASKER_MCP_OPEN_WAIT_MS";

/// Через сколько секунд стоит повторить вызов, когда область ещё открывается (`WorkspaceArgument.RetryAfterSeconds`).
pub const RETRY_AFTER_SECONDS: u32 = 2;

const OPEN_POLL: Duration = Duration::from_millis(50);

/// `WorkspaceArgument.OpenWait`: из окружения один раз.
pub fn open_wait() -> Duration {
    std::env::var(OPEN_WAIT_VARIABLE)
        .ok()
        .and_then(|v| v.trim().parse::<i64>().ok())
        .filter(|ms| *ms >= 0)
        .map(|ms| Duration::from_millis(ms as u64))
        .unwrap_or(Duration::from_secs(5))
}

/// Результат инструмента: JSON (объект результата) или голый текст (`"deleted"`).
pub enum Output {
    Json(Value),
    Text(String),
}

impl Output {
    pub fn text(self) -> String {
        match self {
            Output::Json(value) => tasker_core::json::to_string(&value),
            Output::Text(text) => text,
        }
    }
}

/// Ответ инструмента: текст и признак ошибки.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Outcome {
    pub text: String,
    pub is_error: bool,
}

/// Контекст одного вызова: инструмент, аргументы (без `workspace`, с подставленным `projectId`), область и сервисы от имени агента.
pub struct Call<'a> {
    pub tool: &'a ToolDef,
    pub args: Args<'a>,
    pub ws: Option<Workspace>,
    pub catalog: &'a Arc<dyn McpWorkspaces>,
}

impl Call<'_> {
    /// Сервисы области; у `list_workspaces` области нет.
    pub fn ws(&self) -> Result<&Workspace> {
        self.ws.as_ref().ok_or(ToolError::Failed)
    }

    /// `projectId` — после фильтра проекта по умолчанию он есть у всех инструментов проекта.
    pub fn project_id(&self) -> Result<Uuid> {
        self.args.required_guid(PROJECT_ARGUMENT)
    }

    /// `McpCall.RequireProject`: проект существует (`[not_found] Project <id> not found`); возвращает его id.
    pub fn require_project(&self) -> Result<Uuid> {
        let id = self.project_id()?;
        match self.ws()?.projects().get_by_id(&id)? {
            Some(_) => Ok(id),
            None => Err(ToolError::not_found(format!("Project {} not found", guid_d(&id)))),
        }
    }

    /// `McpCall.TaskId`: полный Guid или короткий id; нет — `[not_found] Task <text> not found`.
    pub fn task_id(&self, project_id: &Uuid, text: &str) -> Result<Uuid> {
        self.ws()?
            .tasks()
            .resolve_id(project_id, text)?
            .ok_or_else(|| ToolError::not_found(format!("Task {text} not found")))
    }

    /// `McpCall.Found`.
    pub fn found<T>(value: Option<T>, what: impl AsRef<str>) -> Result<T> {
        value.ok_or_else(|| ToolError::not_found(format!("{} not found", what.as_ref())))
    }
}

pub struct Server {
    pub catalog: Catalog,
    pub workspaces: Arc<dyn McpWorkspaces>,
    pub open_wait: Duration,
}

impl Server {
    /// Вызов инструмента по имени с аргументами и значением заголовка `X-Tasker-Agent`.
    pub async fn call(&self, name: &str, arguments: Option<Map<String, Value>>, agent_header: Option<String>) -> Outcome {
        let Some(tool) = self.catalog.get(name) else {
            return Outcome {
                text: format!("Unknown tool: '{name}'"),
                is_error: true,
            };
        };
        match self.run(tool, arguments.unwrap_or_default(), agent_header).await {
            Ok(output) => Outcome {
                text: output.text(),
                is_error: false,
            },
            Err(error) => Outcome {
                text: error.text(name),
                is_error: true,
            },
        }
    }

    async fn run(&self, tool: &ToolDef, mut args: Map<String, Value>, agent_header: Option<String>) -> Result<Output> {
        let (scope, ws) = if tool.takes_workspace() {
            let requested = take_workspace(&mut args)?;
            let scope = self.choose(requested).await?;
            let ws = self.enter_as_agent(&scope, agent_header).await?;
            (Some(scope), Some(ws))
        } else {
            (None, None)
        };

        if tool.takes_project()
            && args.get(PROJECT_ARGUMENT).is_none_or(|v| v.is_null())
            && let Some(ws) = ws.clone()
        {
            let found = tokio::task::spawn_blocking(move || ws.projects().get_range(Page::new(0, 2))).await??;
            match found.total_count {
                1 => {
                    args.insert(PROJECT_ARGUMENT.to_string(), Value::String(guid_d(&found.data[0].id)));
                }
                0 => {
                    return Err(ToolError::invalid(
                        "projectId is required: this workspace has no projects (create one with create_project)",
                    ));
                }
                n => {
                    return Err(ToolError::invalid(format!(
                        "projectId is required: this workspace has {n} projects; list_projects shows them"
                    )));
                }
            }
        }

        if !crate::schema::validate(&tool.schema, &Value::Object(args.clone())) {
            return Err(ToolError::Failed);
        }

        let tool = tool.clone();
        let catalog = self.workspaces.clone();
        let output = tokio::task::spawn_blocking(move || {
            let call = Call {
                tool: &tool,
                args: Args(&args),
                ws,
                catalog: &catalog,
            };
            tools::dispatch(&call)
        })
        .await;
        // Область держится открытой до конца вызова.
        drop(scope);
        output?
    }

    /// `WorkspaceArgument.Choose`: область по аргументу или (аргумента нет) единственная разрешённая; иначе — ошибка для агента.
    /// Область, которая ещё открывается, вызов ждёт до [`Server::open_wait`].
    async fn choose(&self, requested: Option<String>) -> Result<McpWorkspaceScope> {
        let all = self.workspaces.list();
        let requested = match requested {
            Some(requested) => requested,
            None => match all.len() {
                0 => {
                    return Err(ToolError::invalid(format!(
                        "{WORKSPACE_ARGUMENT} is required: no workspace is available to MCP (allow one with 'tasker mcp workspace add <path>' or open a folder in the Tasker desktop app)"
                    )));
                }
                1 => all[0].path.clone(),
                n => {
                    return Err(ToolError::invalid(format!(
                        "{WORKSPACE_ARGUMENT} is required: {n} workspaces are available ({}); {LIST_WORKSPACES} shows them",
                        all.iter().map(|x| x.name.as_str()).collect::<Vec<_>>().join(", ")
                    )));
                }
            },
        };

        let deadline = Instant::now() + self.open_wait;
        loop {
            if let Some(scope) = self.workspaces.enter(&requested) {
                return Ok(scope);
            }
            // Разрешена, но не открыта (открывается или не открылась): область «есть», но работать в ней пока нельзя.
            let unavailable = self
                .workspaces
                .list()
                .into_iter()
                .find(|x| eq_ignore_case(&x.name, &requested) || x.path == requested);
            let Some(unavailable) = unavailable else {
                // Неизвестная и не разрешённая — одинаковый ответ: по нему нельзя узнать, какие ещё папки есть на машине.
                return Err(ToolError::not_found(format!(
                    "Workspace '{requested}' not found; {LIST_WORKSPACES} shows the available ones"
                )));
            };
            if unavailable.status == McpWorkspaceStatus::Failed {
                let reason = unavailable.error.as_deref().map(|e| format!(": {e}")).unwrap_or_default();
                return Err(ToolError::failed_workspace(format!(
                    "Workspace '{}' could not be opened{reason}; fix the folder (the server retries on its own every 30 s) or check status in {LIST_WORKSPACES}",
                    unavailable.name
                )));
            }
            let now = Instant::now();
            if now >= deadline {
                return Err(ToolError::unavailable(format!(
                    "Workspace '{}' is opening, retry in {RETRY_AFTER_SECONDS} s (this is temporary, not an error in the call)",
                    unavailable.name
                )));
            }
            tokio::time::sleep((deadline - now).min(OPEN_POLL)).await;
        }
    }

    /// Агент вызова: из заголовка (агент этой области) или локальный агент области; сервисы — от его имени
    /// (держатель блокировок `user:<id>`, имя пользователя — как `CurrentUserEditor`).
    async fn enter_as_agent(&self, scope: &McpWorkspaceScope, header: Option<String>) -> Result<Workspace> {
        let ws = scope.workspace.clone();
        let agent = scope.agent.clone();
        let name = scope.name.clone();
        tokio::task::spawn_blocking(move || -> Result<Workspace> {
            let users = ws.users();
            let user = match header.as_deref().filter(|h| !h.is_empty()) {
                None => {
                    let id = agent.get_id(&ws)?;
                    users.get_by_id(&id)?
                }
                Some(header) => match parse_guid(header) {
                    Some(id) => users.get_by_id(&id)?.filter(|u| u.is_agent()),
                    None => None,
                }
                .map(Some)
                .ok_or_else(|| ToolError::forbidden(format!("{AGENT_HEADER}: no agent with id '{header}' in workspace '{name}'")))?,
            };
            let holder = match user {
                Some(user) => EditHolder::new(format!("user:{}", guid_d(&user.id)), user.username),
                None => return Err(ToolError::Failed),
            };
            Ok(ws.with_editor(holder))
        })
        .await?
    }
}

/// `WorkspaceArgument.TryTake`: забирает `workspace` из аргументов; null или пусто — не указан; не строка — `[invalid]`.
fn take_workspace(args: &mut Map<String, Value>) -> Result<Option<String>> {
    match args.remove(WORKSPACE_ARGUMENT) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(text)) => {
            let trimmed = text.trim();
            Ok(if trimmed.is_empty() { None } else { Some(trimmed.to_string()) })
        }
        Some(_) => Err(ToolError::invalid(format!(
            "{WORKSPACE_ARGUMENT} must be a string: the workspace name or path from {LIST_WORKSPACES}"
        ))),
    }
}

impl From<tokio::task::JoinError> for ToolError {
    fn from(_: tokio::task::JoinError) -> ToolError {
        ToolError::Failed
    }
}

/// Текст ошибки связывания — для тестов и сообщений.
pub fn binding_error(tool: &str) -> String {
    invoke_error(tool)
}
