//! `tasker-mcpd` — демон MCP Tasker на Rust (TSK-137, фаза 4 плана `docs/rust-migration-plan.md`, п. 1): отдельная программа, которую
//! запускают `tasker mcp start | run`, launchd и systemd. В этой сборке — только одиночный режим (`McpDaemon.Run` в .NET): файлы
//! `daemon.lock`/`daemon.json`, HTTP `127.0.0.1:<port>` с `/health`, `/ready`, `/daemon/*`, реестр областей из `settings.json`,
//! слежение за ним, журнал `logs/mcp-YYYYMMDD.log`. Супервизор и рабочий процесс (`--supervised`, `--worker`) — TSK-139, MCP
//! (`/mcp`) — TSK-138.
//!
//! Библиотека — для бинарника `tasker-mcpd` (`main.rs`) и его тестов; реестр областей ([`registry`]) понадобится и MCP (TSK-138).
pub mod daemon;
pub mod files;
pub mod http;
pub mod logging;
pub mod registry;
pub mod shutdown;
pub mod state;
pub mod sync;
