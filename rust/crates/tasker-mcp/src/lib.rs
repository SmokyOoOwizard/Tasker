//! `tasker-mcp` — инструменты MCP Tasker на Rust (TSK-138, фаза 4 плана `docs/rust-migration-plan.md`, п. 2, раздел 3.4): те же
//! 62 инструмента, что у `Tasker.Mcp` (.NET), поверх [`tasker_services`]. Таблица инструментов с ручными `inputSchema`
//! ([`catalog`], `tools.json`), аргумент `workspace` у всех, кроме `list_workspaces`, необязательный `projectId` при одном проекте
//! ([`call`]), заголовок `X-Tasker-Agent` и локальный агент области ([`workspaces`]), коды ошибок в начале текста ([`error`]),
//! ответы — JSON-текст сырым UTF-8 ([`json`], `tasker_core::json`). Сервер на rmcp ([`server`]): Streamable HTTP без сессий;
//! хост (демон `tasker-mcpd`) подключает его к своему маршрутизатору и даёт каталог областей ([`workspaces::McpWorkspaces`]).
pub mod args;
pub mod call;
pub mod catalog;
pub mod error;
pub mod json;
pub mod schema;
pub mod server;
pub mod tools;
pub mod workspaces;

pub use call::{AGENT_HEADER, OPEN_WAIT_VARIABLE, Outcome};
pub use error::ToolError;
pub use server::{SERVER_NAME, SERVER_VERSION, TaskerMcp, http_service};
pub use workspaces::{LocalAgent, McpWorkspace, McpWorkspaceScope, McpWorkspaceStatus, McpWorkspaces};
