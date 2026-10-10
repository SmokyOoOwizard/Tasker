//! `DaemonState` и записи `/daemon/*` (.NET `Tasker.Daemon/DaemonState.cs`): что сейчас делает демон — процесс, порт, рабочие
//! области; итог сверки `/daemon/sync`. JSON — `TaskerJson`: camelCase, enum строками, порядок свойств как у записей .NET
//! (сначала параметры конструктора, затем свойства), null не пропускаются.
use serde_json::{Value, json};
use std::sync::Mutex;
use tasker_core::Timestamp;
use tasker_core::settings::{GlobalSettings, WorkspaceKind};
use tasker_files::index::WorkspaceProblem;
use tasker_services::health::{self, ProjectSeriesHealth};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WorkspaceState {
    /// Открывается: первая сверка индекса ещё идёт.
    Opening,
    Open,
    Failed,
}

impl WorkspaceState {
    pub fn json_name(self) -> &'static str {
        match self {
            Self::Opening => "opening",
            Self::Open => "open",
            Self::Failed => "failed",
        }
    }
}

/// Область в статусе демона. `key` — имя для агента MCP (аргумент `workspace`); None, пока область не открыта.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WorkspaceStatus {
    pub kind: WorkspaceKind,
    pub path: String,
    pub state: WorkspaceState,
    pub key: Option<String>,
    pub error: Option<String>,
}

impl WorkspaceStatus {
    pub fn to_json(&self) -> Value {
        json!({
            "kind": self.kind.json_name(),
            "path": self.path,
            "state": self.state.json_name(),
            "key": self.key,
            "error": self.error,
        })
    }

    /// Из JSON другой стороны (`ready` рабочего процесса): имена свойств без учёта регистра, enum — строкой или числом.
    pub fn from_json(value: &Value) -> Option<WorkspaceStatus> {
        let kind = match property(value, "kind") {
            None | Some(Value::Null) => WorkspaceKind::Files,
            Some(Value::String(s)) => WorkspaceKind::parse(s)?,
            Some(Value::Number(n)) => match n.as_i64()? {
                0 => WorkspaceKind::Files,
                1 => WorkspaceKind::Sqlite,
                _ => return None,
            },
            Some(_) => return None,
        };
        let state = match property(value, "state") {
            None | Some(Value::Null) => WorkspaceState::Opening,
            Some(Value::String(s)) => match s.to_ascii_lowercase().as_str() {
                "opening" => WorkspaceState::Opening,
                "open" => WorkspaceState::Open,
                "failed" => WorkspaceState::Failed,
                _ => return None,
            },
            Some(Value::Number(n)) => match n.as_i64()? {
                0 => WorkspaceState::Opening,
                1 => WorkspaceState::Open,
                2 => WorkspaceState::Failed,
                _ => return None,
            },
            Some(_) => return None,
        };
        Some(WorkspaceStatus {
            kind,
            path: optional_string(value, "path")?.unwrap_or_default(),
            state,
            key: optional_string(value, "key")?,
            error: optional_string(value, "error")?,
        })
    }
}

/// Свойство объекта JSON без учёта регистра имени (`JsonSerializerDefaults.Web`); при повторах — последнее.
pub fn property<'a>(value: &'a Value, name: &str) -> Option<&'a Value> {
    value
        .as_object()?
        .iter()
        .filter(|(k, _)| k.eq_ignore_ascii_case(name))
        .map(|(_, v)| v)
        .next_back()
}

/// Строка или null/нет свойства (`Some(None)`); другой тип — ошибка разбора (`None`).
pub fn optional_string(value: &Value, name: &str) -> Option<Option<String>> {
    match property(value, name) {
        None | Some(Value::Null) => Some(None),
        Some(Value::String(s)) => Some(Some(s.clone())),
        Some(_) => None,
    }
}

/// Что делает рабочий процесс демона: принимает вызовы (`Active`), готовится принимать (`Starting`) или заканчивает начатые (`Draining`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WorkerRole {
    Starting,
    Active,
    Draining,
}

impl WorkerRole {
    pub fn json_name(self) -> &'static str {
        match self {
            Self::Starting => "starting",
            Self::Active => "active",
            Self::Draining => "draining",
        }
    }

    fn parse(value: &Value) -> Option<WorkerRole> {
        match value {
            Value::String(s) => match s.to_ascii_lowercase().as_str() {
                "starting" => Some(Self::Starting),
                "active" => Some(Self::Active),
                "draining" => Some(Self::Draining),
                _ => None,
            },
            Value::Number(n) => match n.as_i64()? {
                0 => Some(Self::Starting),
                1 => Some(Self::Active),
                2 => Some(Self::Draining),
                _ => None,
            },
            _ => None,
        }
    }
}

/// Рабочий процесс демона (`WorkerStatus`). Обычно он один; на время замены (`tasker mcp upgrade`) их два. `build` — короткий
/// идентификатор сборки программы демона: по нему видно, что процесс заменён на новую сборку.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WorkerStatus {
    pub pid: i64,
    pub version: String,
    pub build: String,
    pub role: WorkerRole,
    pub started_at: Timestamp,
}

impl WorkerStatus {
    pub fn to_json(&self) -> Value {
        json!({
            "pid": self.pid,
            "version": self.version,
            "build": self.build,
            "role": self.role.json_name(),
            "startedAt": self.started_at.format_json(),
        })
    }

    pub fn from_json(value: &Value) -> Option<WorkerStatus> {
        Some(WorkerStatus {
            pid: match property(value, "pid") {
                None | Some(Value::Null) => 0,
                Some(v) => v.as_i64()?,
            },
            version: optional_string(value, "version")?.unwrap_or_default(),
            build: optional_string(value, "build")?.unwrap_or_default(),
            role: match property(value, "role") {
                None | Some(Value::Null) => WorkerRole::Starting,
                Some(v) => WorkerRole::parse(v)?,
            },
            started_at: match property(value, "startedAt") {
                Some(Value::String(s)) => Timestamp::parse(s)?,
                _ => Timestamp::from_unix_ticks(0),
            },
        })
    }
}

/// Просьба заменить рабочий процесс демона на лету (`POST /daemon/upgrade`): программа нового рабочего процесса
/// (`tasker-mcpd` новой сборки) и её начальные аргументы; сколько ждать, пока новый процесс откроет области и будет готов.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UpgradeRequest {
    pub file: String,
    pub arguments: Vec<String>,
    pub timeout_seconds: i64,
}

impl UpgradeRequest {
    pub fn to_json(&self) -> Value {
        json!({"file": self.file, "arguments": self.arguments, "timeoutSeconds": self.timeout_seconds})
    }

    /// None — тело не объект или свойство не того типа (как исключение System.Text.Json).
    pub fn from_json(value: &Value) -> Option<UpgradeRequest> {
        value.as_object()?;
        let arguments = match property(value, "arguments") {
            None | Some(Value::Null) => Vec::new(),
            Some(Value::Array(items)) => items.iter().map(|x| x.as_str().map(str::to_string)).collect::<Option<Vec<_>>>()?,
            Some(_) => return None,
        };
        let timeout_seconds = match property(value, "timeoutSeconds") {
            None | Some(Value::Null) => 0,
            Some(Value::Number(n)) => n.as_i64()?,
            Some(Value::String(s)) => s.trim().parse().ok()?,
            Some(_) => return None,
        };
        Some(UpgradeRequest {
            file: optional_string(value, "file")?.unwrap_or_default(),
            arguments,
            timeout_seconds,
        })
    }
}

/// Итог замены (`UpgradeResult`). `ok = false` — старый процесс продолжает работать, `message` объясняет почему.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct UpgradeResult {
    pub ok: bool,
    pub message: String,
    pub old_pid: Option<i64>,
    pub new_pid: Option<i64>,
    pub old_build: Option<String>,
    pub new_build: Option<String>,
}

impl UpgradeResult {
    pub fn failed(message: impl Into<String>) -> UpgradeResult {
        UpgradeResult {
            ok: false,
            message: message.into(),
            ..UpgradeResult::default()
        }
    }

    /// JSON со всеми свойствами (null тоже) — как ответ HTTP; в обмене с супервизором null опускаются (`wire`).
    pub fn to_json(&self) -> Value {
        json!({
            "ok": self.ok,
            "message": self.message,
            "oldPid": self.old_pid,
            "newPid": self.new_pid,
            "oldBuild": self.old_build,
            "newBuild": self.new_build,
        })
    }

    pub fn from_json(value: &Value) -> Option<UpgradeResult> {
        let int = |name: &str| match property(value, name) {
            None | Some(Value::Null) => Some(None),
            Some(v) => v.as_i64().map(Some),
        };
        Some(UpgradeResult {
            ok: match property(value, "ok") {
                None | Some(Value::Null) => false,
                Some(v) => v.as_bool()?,
            },
            message: optional_string(value, "message")?.unwrap_or_default(),
            old_pid: int("oldPid")?,
            new_pid: int("newPid")?,
            old_build: optional_string(value, "oldBuild")?,
            new_build: optional_string(value, "newBuild")?,
        })
    }
}

/// Что сейчас делает демон: его процесс, порт и рабочие области. Отдаётся по `/daemon/status`.
pub struct DaemonState {
    pid: u32,
    port: i32,
    settings_port: i32,
    started_at: Timestamp,
    workspaces: Mutex<Vec<WorkspaceStatus>>,
    /// Рабочие процессы под супервизором (их присылает он сам); у демона в одном процессе пусто.
    workers: Mutex<Vec<WorkerStatus>>,
    /// Этот рабочий процесс и его сборка (pid, версия, сборка), см. [`DaemonState::identify`].
    own: Mutex<Option<(i64, String, String)>>,
}

impl DaemonState {
    pub fn new(port: i32, settings_port: i32) -> DaemonState {
        DaemonState::with_process(port, settings_port, std::process::id(), Timestamp::now_utc())
    }

    /// Состояние рабочего процесса: процесс демона для статуса — супервизор (`pid`, `started_at` из `hello`), а не он сам.
    pub fn with_process(port: i32, settings_port: i32, pid: u32, started_at: Timestamp) -> DaemonState {
        DaemonState {
            pid,
            port,
            settings_port,
            started_at,
            workspaces: Mutex::new(Vec::new()),
            workers: Mutex::new(Vec::new()),
            own: Mutex::new(None),
        }
    }

    pub fn set_workers(&self, mut workers: Vec<WorkerStatus>) {
        if let Some((pid, version, build)) = self.own.lock().unwrap_or_else(|e| e.into_inner()).as_ref() {
            for worker in workers.iter_mut().filter(|w| w.pid == *pid && w.build.is_empty()) {
                worker.version = version.clone();
                worker.build = build.clone();
            }
        }
        *self.workers.lock().unwrap_or_else(|e| e.into_inner()) = workers;
    }

    /// Этот рабочий процесс и его сборка. Отличие от .NET: до того, как супервизор узнает сборку из `ready` и разошлёт новый список
    /// рабочих процессов, статус уже показывает сборку этого процесса — иначе `tasker mcp start`, вернувшийся сразу после открытия
    /// областей, мог увидеть рабочий процесс с пустой сборкой (гонка есть и в .NET, там её прячет медленный запуск консоли).
    pub fn identify(&self, pid: u32, version: &str, build: &str) {
        *self.own.lock().unwrap_or_else(|e| e.into_inner()) = Some((i64::from(pid), version.to_string(), build.to_string()));
        let workers = self.workers();
        self.set_workers(workers);
    }

    pub fn workers(&self) -> Vec<WorkerStatus> {
        self.workers.lock().unwrap_or_else(|e| e.into_inner()).clone()
    }

    pub fn pid(&self) -> u32 {
        self.pid
    }

    pub fn port(&self) -> i32 {
        self.port
    }

    pub fn started_at(&self) -> Timestamp {
        self.started_at
    }

    pub fn set_workspaces(&self, workspaces: Vec<WorkspaceStatus>) {
        *self.workspaces.lock().unwrap_or_else(|e| e.into_inner()) = workspaces;
    }

    pub fn workspaces(&self) -> Vec<WorkspaceStatus> {
        self.workspaces.lock().unwrap_or_else(|e| e.into_inner()).clone()
    }

    /// `DaemonStatus` в JSON. `current` — свежие настройки: порт в них отличается от порта демона — статус подскажет про перезапуск.
    /// `workers` пусто и `supervised` false — демон в одном процессе, заменить его на лету нельзя.
    pub fn snapshot(&self, current: Option<&GlobalSettings>) -> Value {
        let workspaces: Vec<Value> = self.workspaces().iter().map(WorkspaceStatus::to_json).collect();
        let workers: Vec<Value> = self.workers().iter().map(WorkerStatus::to_json).collect();
        let supervised = !workers.is_empty();
        json!({
            "pid": self.pid,
            "port": self.port,
            "settingsPort": current.map(|s| s.mcp.port).unwrap_or(self.settings_port),
            "startedAt": self.started_at.format_json(),
            "workspaces": workspaces,
            "workers": workers,
            "supervised": supervised,
            "mcpUrl": mcp_url(self.port),
        })
    }
}

/// Адрес MCP: один на все области (`McpRegistration.LocalUrl`).
pub fn mcp_url(port: i32) -> String {
    format!("http://127.0.0.1:{port}/mcp")
}

/// Сколько проблемных файлов показывается (`SyncResult.ProblemsShown`).
pub const PROBLEMS_SHOWN: usize = 10;

/// Итог сверки области (`/daemon/sync`): файлы, которые не удалось прочитать, и состояние серий.
pub struct SyncResult {
    pub path: String,
    pub problem_count: usize,
    pub problems: Vec<WorkspaceProblem>,
    pub series: Vec<ProjectSeriesHealth>,
}

impl SyncResult {
    pub fn to_json(&self) -> Value {
        json!({
            "path": self.path,
            "problemCount": self.problem_count,
            "problems": self.problems.iter().map(|p| json!({"path": p.path, "error": p.error})).collect::<Vec<_>>(),
            "series": self.series.iter().map(health::to_json).collect::<Vec<_>>(),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn status_json_has_the_shape_of_the_dotnet_record() {
        let state = DaemonState::new(5719, 5719);
        state.set_workspaces(vec![
            WorkspaceStatus {
                kind: WorkspaceKind::Files,
                path: "/w/a".into(),
                state: WorkspaceState::Open,
                key: Some("a".into()),
                error: None,
            },
            WorkspaceStatus {
                kind: WorkspaceKind::Sqlite,
                path: "/w/b.db".into(),
                state: WorkspaceState::Failed,
                key: None,
                error: Some("/w/b.db does not exist".into()),
            },
        ]);
        let text = tasker_core::json::to_string(&state.snapshot(None));
        let expected = format!(
            "{{\"pid\":{},\"port\":5719,\"settingsPort\":5719,\"startedAt\":\"{}\",\"workspaces\":[{{\"kind\":\"files\",\"path\":\"/w/a\",\"state\":\"open\",\"key\":\"a\",\"error\":null}},{{\"kind\":\"sqlite\",\"path\":\"/w/b.db\",\"state\":\"failed\",\"key\":null,\"error\":\"/w/b.db does not exist\"}}],\"workers\":[],\"supervised\":false,\"mcpUrl\":\"http://127.0.0.1:5719/mcp\"}}",
            std::process::id(),
            state.started_at().format_json()
        );
        assert_eq!(text, expected);

        let mut settings = GlobalSettings::default();
        settings.mcp.port = 6000;
        assert_eq!(state.snapshot(Some(&settings))["settingsPort"], 6000);
    }

    #[test]
    fn the_own_worker_shows_its_build_before_the_supervisor_knows_it() {
        let state = DaemonState::with_process(1, 1, 10, Timestamp::now_utc());
        let worker = |pid: i64, build: &str| WorkerStatus {
            pid,
            version: String::new(),
            build: build.into(),
            role: WorkerRole::Active,
            started_at: Timestamp::now_utc(),
        };
        state.set_workers(vec![worker(11, ""), worker(12, "")]);
        state.identify(11, "0.1.0", "abcd1234");
        let status = state.snapshot(None);
        assert_eq!(status["supervised"], true);
        assert_eq!(status["workers"][0]["build"], "abcd1234");
        assert_eq!(status["workers"][0]["version"], "0.1.0");
        assert_eq!(status["workers"][1]["build"], "");
        // Список от супервизора со сборкой — как есть.
        state.set_workers(vec![worker(11, "ffff0000")]);
        assert_eq!(state.snapshot(None)["workers"][0]["build"], "ffff0000");
    }

    #[test]
    fn sync_result_json() {
        let result = SyncResult {
            path: "/w/a".into(),
            problem_count: 12,
            problems: vec![WorkspaceProblem {
                path: "users/x.yaml".into(),
                error: "bad".into(),
            }],
            series: vec![],
        };
        assert_eq!(
            tasker_core::json::to_string(&result.to_json()),
            "{\"path\":\"/w/a\",\"problemCount\":12,\"problems\":[{\"path\":\"users/x.yaml\",\"error\":\"bad\"}],\"series\":[]}"
        );
    }
}
