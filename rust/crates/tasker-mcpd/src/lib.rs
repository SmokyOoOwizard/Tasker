//! `tasker-mcpd` — демон MCP Tasker на Rust (TSK-137, фаза 4 плана `docs/rust-migration-plan.md`, п. 1): отдельная программа, которую
//! запускают `tasker mcp start | run`, launchd и systemd. В этой сборке — только одиночный режим (`McpDaemon.Run` в .NET): файлы
//! `daemon.lock`/`daemon.json`, HTTP `127.0.0.1:<port>` с `/health`, `/ready`, `/daemon/*` и `/mcp` (инструменты из крейта
//! `tasker-mcp`, TSK-138; каталог областей для них — [`mcp`]), реестр областей из `settings.json`, слежение за ним, журнал
//! `logs/mcp-YYYYMMDD.log`. Супервизор и рабочий процесс (`--supervised`, `--worker`) — TSK-139.
//!
//! Библиотека — для бинарника `tasker-mcpd` (`main.rs`) и его тестов.
pub mod daemon;
pub mod files;
pub mod http;
pub mod logging;
pub mod mcp;
pub mod registry;
pub mod shutdown;
pub mod state;
pub mod sync;
