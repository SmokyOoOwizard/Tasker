//! Сервер MCP на rmcp (`McpRegistration` в .NET): `initialize` с `serverInfo {tasker, 1.0.0}` и возможностями `logging`, `tools`;
//! `tools/list` из таблицы; `tools/call` через [`crate::call`] с заголовком `X-Tasker-Agent` из HTTP-запроса. Транспорт —
//! Streamable HTTP без сессий: каждый POST самостоятелен, `Mcp-Session-Id` не выдаётся, ответ — `text/event-stream` с одной
//! строкой `data:` (как у .NET SDK в stateless-режиме).
use crate::call::{AGENT_HEADER, Server, open_wait};
use crate::catalog::Catalog;
use crate::workspaces::McpWorkspaces;
use rmcp::handler::server::ServerHandler;
use rmcp::model::{
    CallToolRequestParams, CallToolResponse, CallToolResult, ContentBlock, Implementation, ListToolsResult, PaginatedRequestParams,
    ServerCapabilities, ServerConfig, Tool,
};
use rmcp::service::RequestContext;
use rmcp::transport::streamable_http_server::session::never::NeverSessionManager;
use rmcp::transport::streamable_http_server::{StreamableHttpServerConfig, StreamableHttpService};
use rmcp::{ErrorData as McpError, RoleServer};
use std::sync::Arc;
use std::time::Duration;

/// Имя и версия сервера в `initialize` (`ServerInfo` у .NET).
pub const SERVER_NAME: &str = "tasker";
pub const SERVER_VERSION: &str = "1.0.0";

#[derive(Clone)]
pub struct TaskerMcp {
    server: Arc<Server>,
}

impl TaskerMcp {
    /// Срок ожидания открывающейся области — из `TASKER_MCP_OPEN_WAIT_MS` (по умолчанию 5 с).
    pub fn new(workspaces: Arc<dyn McpWorkspaces>) -> TaskerMcp {
        TaskerMcp::with_open_wait(workspaces, open_wait())
    }

    pub fn with_open_wait(workspaces: Arc<dyn McpWorkspaces>, open_wait: Duration) -> TaskerMcp {
        TaskerMcp {
            server: Arc::new(Server {
                catalog: Catalog::load(),
                workspaces,
                open_wait,
            }),
        }
    }

    /// Вызов инструмента без транспорта — для тестов и хостов без HTTP.
    pub async fn call(
        &self,
        name: &str,
        arguments: Option<serde_json::Map<String, serde_json::Value>>,
        agent_header: Option<String>,
    ) -> crate::call::Outcome {
        self.server.call(name, arguments, agent_header).await
    }

    pub fn catalog(&self) -> &Catalog {
        &self.server.catalog
    }
}

impl ServerHandler for TaskerMcp {
    fn get_info(&self) -> ServerConfig {
        // `logging` объявляется, как у .NET SDK (`capabilities: {logging: {}, tools: {}}` в эталоне initialize), хотя в rmcp
        // возможность уже помечена устаревшей.
        #[allow(deprecated)]
        let capabilities = ServerCapabilities::builder().enable_logging().enable_tools().build();
        ServerConfig::new(capabilities).with_server_info(Implementation::new(SERVER_NAME, SERVER_VERSION))
    }

    async fn list_tools(
        &self,
        _request: Option<PaginatedRequestParams>,
        _context: RequestContext<RoleServer>,
    ) -> Result<ListToolsResult, McpError> {
        Ok(ListToolsResult::with_all_items(self.server.catalog.tools()))
    }

    fn get_tool(&self, name: &str) -> Option<Tool> {
        self.server.catalog.tools().into_iter().find(|t| t.name == name)
    }

    async fn call_tool(&self, request: CallToolRequestParams, context: RequestContext<RoleServer>) -> Result<CallToolResponse, McpError> {
        // Заголовки HTTP: rmcp кладёт http::request::Parts в extensions контекста.
        let agent = context
            .extensions
            .get::<http::request::Parts>()
            .and_then(|parts| parts.headers.get(AGENT_HEADER))
            .and_then(|value| value.to_str().ok())
            .map(str::to_owned);
        let outcome = self.server.call(request.name.as_ref(), request.arguments, agent).await;
        let content = vec![ContentBlock::text(outcome.text)];
        Ok(if outcome.is_error {
            CallToolResult::error(content).into()
        } else {
            CallToolResult::success(content).into()
        })
    }
}

/// Служба `/mcp` для axum (`Router::route_service`): без сессий, ответ SSE, проверка `Host`/`Origin` остаётся за хостом.
pub fn http_service(server: TaskerMcp) -> StreamableHttpService<TaskerMcp, NeverSessionManager> {
    let config = StreamableHttpServerConfig::default()
        .with_legacy_session_mode(false)
        .with_json_response(false)
        .disable_allowed_hosts();
    StreamableHttpService::new(move || Ok(server.clone()), Arc::new(NeverSessionManager::default()), config)
}
