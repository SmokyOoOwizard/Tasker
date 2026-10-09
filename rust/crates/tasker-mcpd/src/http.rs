//! HTTP демона (.NET `McpDaemon.MapControl` + `TaskerWebExtensions.MapTaskerMcpHost`): управление `/daemon/*` с секретом в заголовке
//! `X-Tasker-Control`, готовность `/ready`, живость `/health` и `/api/health`, фильтр «только с этой машины» по `Host`/`Origin`
//! (`LoopbackOnly`), ответы на адреса десктопа `/w/{key}/…` (`UseWorkspaces`). `/mcp` в этой сборке ещё нет (TSK-138): 501.
//!
//! Коды и тела повторяют .NET: 401 без тела у управления без секрета, 403 `{"error":"Only local requests are allowed"}` у чужого
//! Host/Origin, 409 на `/daemon/upgrade` (демон в одном процессе заменить на лету нельзя), 503 с `Retry-After` у неготового `/ready`.
use crate::shutdown::Shutdown;
use crate::state::{DaemonState, WorkspaceState};
use crate::sync::WorkspaceSync;
use axum::Router;
use axum::body::Bytes;
use axum::extract::{Request, State};
use axum::http::{HeaderMap, HeaderValue, StatusCode, Uri, header};
use axum::middleware::{self, Next};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use serde_json::{Value, json};
use std::sync::Arc;
use tasker_core::settings::{SettingsStore, WorkspaceKind, WorkspaceLocation};
use tracing::info;

/// Заголовок с секретом управления демоном (из `daemon.json`).
pub const CONTROL_HEADER: &str = "X-Tasker-Control";

/// Адрес MCP (`McpRegistration.Path`).
pub const MCP_PATH: &str = "/mcp";

pub struct App {
    pub state: Arc<DaemonState>,
    pub sync: Arc<WorkspaceSync>,
    pub store: SettingsStore,
    pub token: String,
    pub shutdown: Arc<Shutdown>,
}

pub fn router(app: Arc<App>) -> Router {
    Router::new()
        .route("/daemon/status", get(status))
        .route("/daemon/sync", post(sync))
        .route("/daemon/stop", post(stop))
        .route("/daemon/upgrade", post(upgrade))
        .route("/ready", get(ready))
        .route("/health", get(health))
        .route("/api/health", get(api_health))
        .route(MCP_PATH, axum::routing::any(mcp))
        .fallback(fallback)
        .layer(middleware::from_fn(loopback_only))
        .with_state(app)
}

/// JSON-ответ как `Results.Ok(obj)`/`Results.Json`: `application/json; charset=utf-8`, экранирование `TaskerJson`.
fn json(status: StatusCode, value: &Value) -> Response {
    (
        status,
        [(header::CONTENT_TYPE, HeaderValue::from_static("application/json; charset=utf-8"))],
        tasker_core::json::to_string(value),
    )
        .into_response()
}

/// Десктоп: запросы только с этой машины. Хост слушает 127.0.0.1, поэтому другие устройства в сети до него не достают; здесь —
/// защита от DNS rebinding: страница в браузере на чужом домене, который указывает на 127.0.0.1, пришлёт свой Host/Origin — такие
/// запросы отклоняются. Локальные программы на этой же машине доступ имеют.
async fn loopback_only(request: Request, next: Next) -> Response {
    let host = request.headers().get(header::HOST).and_then(|h| h.to_str().ok()).unwrap_or("");
    let origin = request.headers().get(header::ORIGIN).and_then(|h| h.to_str().ok()).unwrap_or("");
    if !is_loopback(host_of(host)) || (!origin.is_empty() && !is_loopback_origin(origin)) {
        return json(StatusCode::FORBIDDEN, &json!({"error": "Only local requests are allowed"}));
    }
    next.run(request).await
}

/// `HostString.Host`: без порта; IPv6 в скобках остаётся в скобках.
pub fn host_of(host: &str) -> &str {
    let host = host.trim();
    if host.starts_with('[') {
        match host.find(']') {
            Some(end) => &host[..=end],
            None => host,
        }
    } else {
        host.rsplit_once(':').map(|(h, _)| h).unwrap_or(host)
    }
}

pub fn is_loopback(host: &str) -> bool {
    if host.eq_ignore_ascii_case("localhost") {
        return true;
    }
    match host.trim_matches(['[', ']']).parse::<std::net::IpAddr>() {
        Ok(ip) => ip.is_loopback(),
        Err(_) => false,
    }
}

/// `Uri.TryCreate(origin, Absolute)` и проверка хоста.
pub fn is_loopback_origin(origin: &str) -> bool {
    match origin.parse::<Uri>() {
        Ok(uri) if uri.scheme().is_some() => uri.host().is_some_and(is_loopback),
        _ => false,
    }
}

fn authorized(headers: &HeaderMap, token: &str) -> bool {
    let Some(given) = headers.get(CONTROL_HEADER).and_then(|h| h.to_str().ok()) else {
        return false;
    };
    !given.is_empty() && fixed_time_equals(given.as_bytes(), token.as_bytes())
}

/// `CryptographicOperations.FixedTimeEquals`.
fn fixed_time_equals(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut diff = 0u8;
    for (x, y) in a.iter().zip(b) {
        diff |= x ^ y;
    }
    diff == 0
}

async fn status(State(app): State<Arc<App>>, headers: HeaderMap) -> Response {
    if !authorized(&headers, &app.token) {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    // Порт из настроек — свежий: если его изменили после запуска, статус подскажет про перезапуск.
    let current = app.store.load().ok();
    json(StatusCode::OK, &app.state.snapshot(current.as_ref()))
}

/// Сверить индекс области с файлами сейчас — после git pull, checkout и т. п. Возвращается, когда кэш актуален.
async fn sync(State(app): State<Arc<App>>, headers: HeaderMap, body: Bytes) -> Response {
    if !authorized(&headers, &app.token) {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    let Some(location) = parse_sync_request(&body) else {
        return json(StatusCode::BAD_REQUEST, &json!({"error": "Body {\"kind\", \"path\"} is required"}));
    };
    match app.sync.sync(location.clone()).await {
        Some(Ok(result)) => json(StatusCode::OK, &result.to_json()),
        Some(Err(e)) => json(StatusCode::INTERNAL_SERVER_ERROR, &json!({"error": e.to_string()})),
        None => json(
            StatusCode::NOT_FOUND,
            &json!({"error": format!("The daemon does not serve {}", location.path())}),
        ),
    }
}

/// `SyncRequest(Kind, Path)`: `kind` — строка `files`/`sqlite` без учёта регистра или число (по умолчанию `files`), `path` непустой.
fn parse_sync_request(body: &[u8]) -> Option<WorkspaceLocation> {
    let value: Value = serde_json::from_slice(body).ok()?;
    let object = value.as_object()?;
    let get = |name: &str| {
        object
            .iter()
            .filter(|(k, _)| k.eq_ignore_ascii_case(name))
            .map(|(_, v)| v)
            .next_back()
    };
    let kind = match get("kind") {
        None | Some(Value::Null) => WorkspaceKind::Files,
        Some(Value::String(s)) => WorkspaceKind::parse(s)?,
        Some(Value::Number(n)) => match n.as_i64()? {
            0 => WorkspaceKind::Files,
            1 => WorkspaceKind::Sqlite,
            _ => return None,
        },
        Some(_) => return None,
    };
    let path = get("path")?.as_str().filter(|p| !p.is_empty())?;
    Some(match kind {
        WorkspaceKind::Files => WorkspaceLocation::files(path),
        WorkspaceKind::Sqlite => WorkspaceLocation::sqlite(path),
    })
}

async fn stop(State(app): State<Arc<App>>, headers: HeaderMap) -> Response {
    if !authorized(&headers, &app.token) {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    info!("Stop requested");
    app.shutdown.trigger();
    json(StatusCode::OK, &json!({"stopping": true}))
}

/// Заменить рабочий процесс на лету может только супервизор (TSK-139); демон в одном процессе отвечает, как .NET без него.
async fn upgrade(State(app): State<Arc<App>>, headers: HeaderMap) -> Response {
    if !authorized(&headers, &app.token) {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    json(
        StatusCode::CONFLICT,
        &json!({"error": "The MCP server runs as a single process and cannot be replaced on the fly: restart it"}),
    )
}

/// Готов принимать вызовы: все области открыты (или не открылись) и процесс не закрывается. Иначе 503.
async fn ready(State(app): State<Arc<App>>) -> Response {
    let workspaces = app.state.workspaces();
    let opening = workspaces.iter().filter(|w| w.state == WorkspaceState::Opening).count();
    if opening == 0 {
        json(StatusCode::OK, &json!({"ready": true, "pid": app.state.pid()}))
    } else {
        json(
            StatusCode::SERVICE_UNAVAILABLE,
            &json!({"ready": false, "opening": opening, "draining": false}),
        )
    }
}

/// Процесс жив и отвечает (в отличие от `/ready` не говорит, открыты ли области).
async fn health(State(app): State<Arc<App>>) -> Response {
    json(
        StatusCode::OK,
        &json!({"status": "ok", "mode": "McpDaemon", "pid": app.state.pid()}),
    )
}

async fn api_health() -> Response {
    json(StatusCode::OK, &json!({"status": "ok", "mode": "McpDaemon"}))
}

/// MCP появится в TSK-138 (rmcp). Пока — 501, чтобы клиент отличал «нет в этой сборке» от «демон не готов» (503 у `/ready`).
async fn mcp() -> Response {
    json(
        StatusCode::NOT_IMPLEMENTED,
        &json!({"error": "MCP is not implemented in this build of tasker-mcpd yet: use the .NET build (TSK-138)"}),
    )
}

/// `UseWorkspaces` без REST и фронтенда: `/w/{key}/mcp` — подсказка про общий адрес, `/w/{key}/…` — область не открыта,
/// `/api/…` — нет области в адресе; остальное — 404 без тела, как у маршрутизации ASP.NET.
async fn fallback(State(app): State<Arc<App>>, uri: Uri) -> Response {
    let path = uri.path();
    if let Some((key, rest)) = split_workspace_path(path) {
        if rest == MCP_PATH || rest.starts_with("/mcp/") {
            return json(
                StatusCode::NOT_FOUND,
                &json!({"error": format!("MCP has no per-workspace address: use {MCP_PATH} and pass the folder in the 'workspace' argument of the tool")}),
            );
        }
        // Открытая область: запрос попал бы в её контейнер и не нашёл маршрута — 404 без тела.
        if app.sync.registry().find_by_name_or_path(key).is_some() && !key.contains(['/', '\\']) {
            return StatusCode::NOT_FOUND.into_response();
        }
        return json(StatusCode::NOT_FOUND, &json!({"error": format!("Workspace '{key}' is not open")}));
    }
    if path.starts_with("/api/") || path == "/api" {
        return json(
            StatusCode::NOT_FOUND,
            &json!({"error": "No workspace in the address: use /w/{folder}/api/…"}),
        );
    }
    StatusCode::NOT_FOUND.into_response()
}

/// `/w/{key}/rest` → (key, `/rest`); `/w/{key}` → (key, `/`).
pub fn split_workspace_path(path: &str) -> Option<(&str, &str)> {
    let remaining = path.strip_prefix("/w/")?;
    if remaining.is_empty() {
        return None;
    }
    Some(match remaining.find('/') {
        Some(slash) => (&remaining[..slash], &remaining[slash..]),
        None => (remaining, "/"),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn loopback_hosts_and_origins() {
        assert!(is_loopback(host_of("127.0.0.1:5719")));
        assert!(is_loopback(host_of("localhost")));
        assert!(is_loopback(host_of("LOCALHOST:80")));
        assert!(is_loopback(host_of("[::1]:5719")));
        assert!(is_loopback(host_of("127.0.0.2")));
        assert!(!is_loopback(host_of("evil.example.com:5719")));
        assert!(!is_loopback(host_of("10.0.0.1")));
        assert!(!is_loopback(host_of("")));

        assert!(is_loopback_origin("http://localhost:3000"));
        assert!(is_loopback_origin("http://127.0.0.1"));
        assert!(is_loopback_origin("http://[::1]:5719"));
        assert!(!is_loopback_origin("http://evil.example.com"));
        assert!(!is_loopback_origin("null"));
        assert!(!is_loopback_origin("localhost"));
    }

    #[test]
    fn workspace_paths_split_like_dotnet() {
        assert_eq!(split_workspace_path("/w/a/mcp"), Some(("a", "/mcp")));
        assert_eq!(split_workspace_path("/w/a"), Some(("a", "/")));
        assert_eq!(split_workspace_path("/w/a/api/tasks"), Some(("a", "/api/tasks")));
        assert_eq!(split_workspace_path("/w/"), None);
        assert_eq!(split_workspace_path("/w"), None);
        assert_eq!(split_workspace_path("/mcp"), None);
    }

    #[test]
    fn sync_request_parsing() {
        let files = parse_sync_request(br#"{"kind":"files","path":"/x"}"#).unwrap();
        assert_eq!(files.kind(), WorkspaceKind::Files);
        assert_eq!(
            parse_sync_request(br#"{"Kind":"SQLITE","Path":"/x/t.db"}"#).unwrap().kind(),
            WorkspaceKind::Sqlite
        );
        assert_eq!(
            parse_sync_request(br#"{"kind":1,"path":"/x/t.db"}"#).unwrap().kind(),
            WorkspaceKind::Sqlite
        );
        assert_eq!(parse_sync_request(br#"{"path":"/x"}"#).unwrap().kind(), WorkspaceKind::Files);
        assert!(parse_sync_request(b"").is_none());
        assert!(parse_sync_request(b"null").is_none());
        assert!(parse_sync_request(br#"{"kind":"files"}"#).is_none());
        assert!(parse_sync_request(br#"{"kind":"files","path":""}"#).is_none());
        assert!(parse_sync_request(br#"{"kind":"other","path":"/x"}"#).is_none());
    }

    #[test]
    fn token_comparison_is_exact() {
        assert!(fixed_time_equals(b"abc", b"abc"));
        assert!(!fixed_time_equals(b"abc", b"abd"));
        assert!(!fixed_time_equals(b"abc", b"ab"));
    }
}
