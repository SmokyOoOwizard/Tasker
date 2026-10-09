//! Рабочие области, которые MCP разрешает агентам (`IMcpWorkspaces`, `McpWorkspace`, `McpWorkspaceScope`, `LocalMcpAgent` в .NET):
//! у демона — список из настроек, у десктопа — открытые вкладки. Других областей агент открыть не может: ни по имени, ни по пути.
//! Сам каталог даёт хост (демон: реестр открытых областей плюс статусы ещё не открытых).
use crate::error::Result;
use std::any::Any;
use std::sync::{Arc, Mutex};
use tasker_services::Workspace;
use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum McpWorkspaceStatus {
    /// Открывается: первая сверка индекса ещё идёт.
    Opening,
    Open,
    /// Не открылась (папку удалили, диск не подключён…): причина — [`McpWorkspace::error`].
    Failed,
}

impl McpWorkspaceStatus {
    /// `JsonStringEnumConverter(CamelCase)`.
    pub fn json_name(self) -> &'static str {
        match self {
            Self::Opening => "opening",
            Self::Open => "open",
            Self::Failed => "failed",
        }
    }
}

/// Рабочая область, разрешённая агентам: `name` — имя, которым агент выбирает область (аргумент `workspace`): имя папки,
/// у одинаковых — с суффиксом (`Tasker-2`); `path` — канонический путь папки; `projects` заполняет `list_workspaces`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpWorkspace {
    pub name: String,
    pub path: String,
    pub status: McpWorkspaceStatus,
    pub projects: Option<usize>,
    pub error: Option<String>,
}

/// Открытая область на время одного вызова инструмента: сервисы именно этой папки, её локальный агент и «аренда» — то, что держит
/// область открытой, пока вызов идёт (у демона — `Arc<OpenWorkspace>`).
pub struct McpWorkspaceScope {
    pub name: String,
    pub workspace: Workspace,
    pub agent: Arc<LocalAgent>,
    pub lease: Arc<dyn Any + Send + Sync>,
}

/// Области, разрешённые агентам. Реализует хост.
pub trait McpWorkspaces: Send + Sync {
    /// Все разрешённые области, в том числе ещё не открытые, — по имени (без учёта регистра), затем по пути.
    fn list(&self) -> Vec<McpWorkspace>;

    /// Открытая разрешённая область по имени (без учёта регистра) или каноническому пути. None — такой области нет среди
    /// разрешённых либо она не открыта: для агента это одно и то же.
    fn enter(&self, name_or_path: &str) -> Option<McpWorkspaceScope>;
}

/// Локальный агент области, от имени которого идут вызовы без заголовка `X-Tasker-Agent` (`LocalMcpAgent`): создаётся при первом
/// обращении, id запоминается, пока открыта область (у каждой области свои пользователи — и свой агент).
#[derive(Default)]
pub struct LocalAgent {
    id: Mutex<Option<Uuid>>,
}

impl LocalAgent {
    pub fn new() -> LocalAgent {
        LocalAgent::default()
    }

    /// Блокирующий: первый вызов может создать пользователя-агента.
    pub fn get_id(&self, ws: &Workspace) -> Result<Uuid> {
        let mut id = self.id.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(id) = *id {
            return Ok(id);
        }
        let agent = ws.agents().ensure_local_agent()?;
        *id = Some(agent.id);
        Ok(agent.id)
    }
}
