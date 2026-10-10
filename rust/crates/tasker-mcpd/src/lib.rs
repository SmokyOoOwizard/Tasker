//! `tasker-mcpd` — демон MCP Tasker на Rust (фаза 4 плана `docs/rust-migration-plan.md`): отдельная программа, которую запускают
//! `tasker mcp start | run`, launchd и systemd. Два режима, как у .NET (`Program.cs`):
//!
//! - супервизор ([`supervisor`], по умолчанию на macOS и Linux, на Windows — `--supervised` или `TASKER_MCP_SUPERVISOR=1`): держит
//!   `daemon.lock`, `daemon.json` и слушающий сокет, запускает рабочие процессы `tasker-mcpd --worker --listen-fd N` ([`worker`]) и
//!   заменяет их на лету (`tasker mcp upgrade`) без закрытия порта; обмен с ними — NDJSON ([`wire`]), затвор приёма и завершение —
//!   [`gate`], передача сокета — [`handoff`]. Рабочий процесс может быть и .NET-сборкой, и наоборот;
//! - один процесс ([`daemon`], `--single`, по умолчанию на Windows): файлы `daemon.lock`/`daemon.json`, HTTP `127.0.0.1:<port>` с
//!   `/health`, `/ready`, `/daemon/*` и `/mcp` (инструменты из крейта `tasker-mcp`; каталог областей для них — [`mcp`]), реестр
//!   областей из `settings.json`, слежение за ним, журнал `logs/mcp-YYYYMMDD.log`.
//!
//! Библиотека — для бинарника `tasker-mcpd` (`main.rs`) и его тестов.
pub mod daemon;
pub mod files;
pub mod gate;
pub mod handoff;
pub mod http;
pub mod logging;
pub mod mcp;
pub mod registry;
pub mod shutdown;
pub mod state;
pub mod supervisor;
pub mod sync;
pub mod version;
pub mod wire;
pub mod worker;

/// Включает режим супервизора там, где он не по умолчанию (Windows): `1`, `true`, `yes` или `on`.
pub const SUPERVISOR_VARIABLE: &str = "TASKER_MCP_SUPERVISOR";

/// Какой режим демона выбрать (`DaemonPlatform.UseSupervisor`): `--single` сильнее всего; на macOS и Linux по умолчанию —
/// супервизор, на Windows — только по `--supervised` или [`SUPERVISOR_VARIABLE`].
pub fn use_supervisor(is_windows: bool, single: bool, supervised: bool, variable: Option<&str>) -> bool {
    if single {
        return false;
    }
    !is_windows || supervised || is_on(variable)
}

pub fn is_on(value: Option<&str>) -> bool {
    matches!(value.map(|v| v.trim().to_lowercase()).as_deref(), Some("1" | "true" | "yes" | "on"))
}

#[cfg(test)]
mod tests {
    #[test]
    fn mode_is_chosen_like_daemon_platform() {
        use super::use_supervisor;
        assert!(use_supervisor(false, false, false, None));
        assert!(!use_supervisor(false, true, false, None));
        assert!(!use_supervisor(true, false, false, None));
        assert!(use_supervisor(true, false, true, None));
        assert!(use_supervisor(true, false, false, Some(" Yes ")));
        assert!(!use_supervisor(true, false, false, Some("0")));
        assert!(!use_supervisor(true, true, true, Some("1")));
    }
}
