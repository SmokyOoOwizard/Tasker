//! Spike TSK-127: эхо-сервер MCP на rmcp — Streamable HTTP без сессий (stateless), инструменты со схемами, собранными руками
//! (как в Tasker: аргумент `workspace` добавляется, `projectId` убирается из `required`), заголовок X-Tasker-Agent из HTTP-запроса,
//! ошибка инструмента с кодом в начале текста и isError: true.
use std::sync::Arc;

use rmcp::handler::server::ServerHandler;
use rmcp::model::*;
use rmcp::service::RequestContext;
use rmcp::transport::streamable_http_server::session::never::NeverSessionManager;
use rmcp::transport::streamable_http_server::{StreamableHttpServerConfig, StreamableHttpService};
use rmcp::{ErrorData as McpError, RoleServer};

#[derive(Clone)]
struct Echo {
    tools: Arc<Vec<Tool>>,
}

/// Схема инструмента руками: как .NET-демон — `workspace` добавлен, `projectId` необязателен (DefaultProject).
fn manual_tool(name: &'static str, description: &'static str, schema: serde_json::Value) -> Tool {
    let object = schema.as_object().expect("schema is an object").clone();
    Tool::new(name, description, Arc::new(object))
}

impl Echo {
    fn new() -> Self {
        let tools = vec![
            manual_tool(
                "echo",
                "Returns the arguments as JSON text",
                serde_json::json!({
                    "type": "object",
                    "properties": {
                        "projectId": {"type": "string", "description": "Project id; optional when the workspace has one project"},
                        "text": {"type": "string"},
                        "workspace": {"type": "string", "description": "Workspace name or path from list_workspaces"}
                    },
                    "required": ["text"]
                }),
            ),
            manual_tool(
                "fail",
                "Always fails with a coded error",
                serde_json::json!({"type": "object", "properties": {"workspace": {"type": "string"}}}),
            ),
        ];
        Self { tools: Arc::new(tools) }
    }
}

impl ServerHandler for Echo {
    fn get_info(&self) -> ServerInfo {
        ServerInfo::new(ServerCapabilities::builder().enable_tools().build()).with_server_info(Implementation::new("tasker", "1.0.0"))
    }

    async fn list_tools(&self, _request: Option<PaginatedRequestParams>, _context: RequestContext<RoleServer>) -> Result<ListToolsResult, McpError> {
        Ok(ListToolsResult::with_all_items(self.tools.as_ref().clone()))
    }

    fn get_tool(&self, name: &str) -> Option<Tool> {
        self.tools.iter().find(|t| t.name == name).cloned()
    }

    async fn call_tool(&self, request: CallToolRequestParams, context: RequestContext<RoleServer>) -> Result<CallToolResponse, McpError> {
        // Заголовки HTTP: rmcp кладёт http::request::Parts в extensions контекста.
        let agent = context
            .extensions
            .get::<http::request::Parts>()
            .and_then(|parts| parts.headers.get("X-Tasker-Agent"))
            .and_then(|value| value.to_str().ok())
            .map(str::to_owned);
        match request.name.as_ref() {
            "echo" => {
                let arguments = request.arguments.unwrap_or_default();
                // Сырой UTF-8 на проводе, как WireJson: serde_json не экранирует не-ASCII.
                let text = serde_json::json!({"arguments": arguments, "agent": agent}).to_string();
                Ok(CallToolResult::success(vec![ContentBlock::text(text)]).into())
            }
            "fail" => Ok(CallToolResult::error(vec![ContentBlock::text("[not_found] Task GLD-999 not found")]).into()),
            other => Ok(CallToolResult::error(vec![ContentBlock::text(format!("Unknown tool: '{other}'"))]).into()),
        }
    }
}

#[tokio::main]
async fn main() {
    let port: u16 = std::env::args().nth(1).and_then(|x| x.parse().ok()).unwrap_or(5799);
    let config = StreamableHttpServerConfig::default()
        .with_legacy_session_mode(false) // без сессий: каждый POST — самостоятельный запрос
        .with_json_response(false); // ответ как у .NET SDK: text/event-stream с одной строкой data:
    let service = StreamableHttpService::new(|| Ok(Echo::new()), Arc::new(NeverSessionManager::default()), config);
    let app = axum::Router::new().nest_service("/mcp", service);
    let listener = tokio::net::TcpListener::bind(("127.0.0.1", port)).await.expect("bind");
    eprintln!("listening on http://127.0.0.1:{port}/mcp");
    axum::serve(listener, app).await.expect("serve");
}
