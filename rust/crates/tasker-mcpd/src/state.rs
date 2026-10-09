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
}

/// Что сейчас делает демон: его процесс, порт и рабочие области. Отдаётся по `/daemon/status`.
pub struct DaemonState {
    pid: u32,
    port: i32,
    settings_port: i32,
    started_at: Timestamp,
    workspaces: Mutex<Vec<WorkspaceStatus>>,
}

impl DaemonState {
    pub fn new(port: i32, settings_port: i32) -> DaemonState {
        DaemonState {
            pid: std::process::id(),
            port,
            settings_port,
            started_at: Timestamp::now_utc(),
            workspaces: Mutex::new(Vec::new()),
        }
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
    /// `workers` пусто и `supervised` false: демон в одном процессе, заменить его на лету нельзя.
    pub fn snapshot(&self, current: Option<&GlobalSettings>) -> Value {
        let workspaces: Vec<Value> = self.workspaces().iter().map(WorkspaceStatus::to_json).collect();
        json!({
            "pid": self.pid,
            "port": self.port,
            "settingsPort": current.map(|s| s.mcp.port).unwrap_or(self.settings_port),
            "startedAt": self.started_at.format_json(),
            "workspaces": workspaces,
            "workers": [],
            "supervised": false,
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
