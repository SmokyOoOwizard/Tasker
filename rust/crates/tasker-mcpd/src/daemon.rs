//! `McpDaemon.Run` (.NET): демон в одном процессе. Один экземпляр на пользователя (`daemon.lock`), порт и список областей — из
//! `settings.json` (изменение списка применяется на лету, смена порта требует перезапуска), ожидание занятого порта до 5 с (десктоп
//! видит блокировку демона и отпускает порт сам), `daemon.json` с секретом управления, HTTP на `127.0.0.1:<port>`, остановка
//! по сигналу или `/daemon/stop`: закрыть области, остановить сервер (до 5 с на начатые запросы), убрать `daemon.json`, снять
//! блокировку.
//!
//! Порядок шагов и тексты журнала — как в .NET: консоль читает хвост журнала при неудачном старте.
use crate::files::{DaemonFiles, DaemonInfo, new_token};
use crate::http::{self, App, Control};
use crate::mcp::DaemonWorkspaces;
use crate::registry::WorkspaceRegistry;
use crate::shutdown::Shutdown;
use crate::state::{DaemonState, mcp_url};
use crate::sync::{RETRY_EVERY, WorkspaceSync};
use std::future::IntoFuture as _;
use std::io::Write as _;
use std::net::{Ipv4Addr, SocketAddr, TcpListener};
use std::sync::Arc;
use std::time::{Duration, Instant};
use tasker_core::settings::{SettingsStore, SettingsWatch, WorkspaceEntry, logs_dir};
use tracing::{error, info};

const PORT_WAIT: Duration = Duration::from_secs(5);
const PORT_POLL: Duration = Duration::from_millis(200);
/// `HostOptions.ShutdownTimeout`: сколько ждать начатые запросы при остановке.
pub const SHUTDOWN_TIMEOUT: Duration = Duration::from_secs(5);

/// Код выхода: 0 — остановлен штатно, 1 — не запустился. `detached` — запущен в фоне: отвязаться от терминала и писать только в файл.
pub fn run(detached: bool) -> i32 {
    let files = DaemonFiles::current();
    let instance = match files.try_hold() {
        Ok(Some(lock)) => lock,
        Ok(None) => {
            let _ = writeln!(std::io::stderr(), "The MCP server is already running: see 'tasker mcp status'");
            return 1;
        }
        Err(e) => {
            let _ = writeln!(std::io::stderr(), "The MCP server did not start: {e}");
            return 1;
        }
    };

    if detached {
        detach();
    }
    crate::logging::configure(&logs_dir(), detached, false);

    let runtime = match tokio::runtime::Builder::new_multi_thread().enable_all().build() {
        Ok(runtime) => runtime,
        Err(e) => return crashed(&format!("cannot start the runtime: {e}")),
    };
    let code = runtime.block_on(serve(files));
    // Потоки наблюдателей и блокирующие задачи могли остаться — не ждём их дольше необходимого.
    runtime.shutdown_timeout(Duration::from_secs(2));
    drop(instance);
    code
}

async fn serve(files: DaemonFiles) -> i32 {
    info!("Starting the MCP server");
    let store = SettingsStore::new(None);
    let settings = match store.load() {
        Ok(settings) => settings,
        Err(e) => return crashed(&e.to_string()),
    };
    let port = settings.mcp.port;

    // Десктоп мог занять порт раньше: он видит блокировку демона и отпускает порт сам — даём ему на это время.
    if !wait_for_port(port).await {
        let message = format!(
            "Port {port} is busy: another program (for example Tasker desktop) listens on it. Free it or change the port with 'tasker mcp port <number>'"
        );
        error!("{message}");
        let _ = writeln!(std::io::stderr(), "{message}");
        return 1;
    }

    let listener = match tokio::net::TcpListener::bind(address(port)).await {
        Ok(listener) => listener,
        Err(e) => return crashed(&format!("cannot listen on {}: {e}", address(port))),
    };

    let state = Arc::new(DaemonState::new(port, port));
    let token = new_token();
    let Server { sync, shutdown, app } = Server::new(state.clone(), token.clone(), Control::default());

    sync.prepare(settings.mcp.workspaces.clone());
    let serving = shutdown.clone();
    let server = tokio::spawn(
        axum::serve(listener, http::router(app))
            .with_graceful_shutdown(async move { serving.triggered().await })
            .into_future(),
    );

    let info = DaemonInfo {
        pid: state.pid(),
        port,
        token,
        started_at: state.started_at(),
    };
    if let Err(e) = files.write_info(&info) {
        shutdown.trigger();
        let _ = server.await;
        return crashed(&format!("cannot write {}: {e}", files.info_file().display()));
    }
    info!(
        "MCP server is running: {}, the workspace is the 'workspace' argument of a tool (pid {})",
        mcp_url(port),
        state.pid()
    );

    let watch = watch_settings(&store, &sync);
    let retry = retry_failed(&sync);
    sync.apply(settings.mcp.workspaces).await;

    let reason = shutdown.wait(files.directory()).await;
    if reason != "request" {
        info!("Stop requested through {reason}");
    }
    info!("Stopping the MCP server");
    retry.abort();
    drop(watch);
    sync.dispose().await;
    shutdown.trigger();
    if tokio::time::timeout(SHUTDOWN_TIMEOUT, server).await.is_err() {
        error!("Requests did not finish in {} s, closing anyway", SHUTDOWN_TIMEOUT.as_secs());
    }
    if let Err(e) = files.delete_info(state.pid()) {
        error!("Cannot delete {}: {e}", files.info_file().display());
    }
    0
}

/// Сервер области: реестр, сверка областей, остановка и HTTP-приложение — общие у демона в одном процессе и у рабочего процесса.
pub struct Server {
    pub sync: Arc<WorkspaceSync>,
    pub shutdown: Arc<Shutdown>,
    pub app: Arc<App>,
}

impl Server {
    pub fn new(state: Arc<DaemonState>, token: String, control: Control) -> Server {
        let registry = Arc::new(WorkspaceRegistry::new());
        let sync = Arc::new(WorkspaceSync::new(registry.clone(), state.clone(), state.port()));
        let shutdown = Arc::new(Shutdown::new());
        let mcp = tasker_mcp::http_service(tasker_mcp::TaskerMcp::new(Arc::new(DaemonWorkspaces::new(registry, state.clone()))));
        let app = Arc::new(App {
            state,
            sync: sync.clone(),
            store: SettingsStore::new(None),
            token,
            shutdown: shutdown.clone(),
            mcp,
            control,
        });
        Server { sync, shutdown, app }
    }
}

/// Слежение за настройками: обработчик зовётся в потоке наблюдателя, применение — задачей рантайма.
pub fn watch_settings(store: &SettingsStore, sync: &Arc<WorkspaceSync>) -> Option<SettingsWatch> {
    let handle = tokio::runtime::Handle::current();
    let applying = sync.clone();
    let watch = store.watch(move |changed| {
        info!("Settings changed: {} workspaces", changed.mcp.workspaces.len());
        let sync = applying.clone();
        let workspaces: Vec<WorkspaceEntry> = changed.mcp.workspaces;
        handle.spawn(async move { sync.apply(workspaces).await });
    });
    match watch {
        Ok(watch) => Some(watch),
        Err(e) => {
            error!("Cannot watch the settings file: {e}");
            None
        }
    }
}

/// Области, которые не открылись, пробуются снова каждые [`RETRY_EVERY`].
pub fn retry_failed(sync: &Arc<WorkspaceSync>) -> tokio::task::JoinHandle<()> {
    let retrying = sync.clone();
    tokio::spawn(async move {
        let mut timer = tokio::time::interval_at(tokio::time::Instant::now() + RETRY_EVERY, RETRY_EVERY);
        loop {
            timer.tick().await;
            retrying.retry().await;
        }
    })
}

/// `Log.Fatal` + сообщение в stderr; код выхода 1.
fn crashed(message: &str) -> i32 {
    error!(fatal = true, "The MCP server crashed: {message}");
    let _ = writeln!(std::io::stderr(), "The MCP server did not start: {message}");
    1
}

pub fn address(port: i32) -> SocketAddr {
    SocketAddr::from((Ipv4Addr::LOCALHOST, u16::try_from(port).unwrap_or(0)))
}

/// Порт свободен сейчас или освободится за [`PORT_WAIT`] (десктоп уступает его демону на лету).
async fn wait_for_port(port: i32) -> bool {
    let deadline = Instant::now() + PORT_WAIT;
    let mut announced = false;
    while !is_port_free(port) {
        if Instant::now() >= deadline {
            return false;
        }
        if !announced {
            info!(
                "Port {port} is busy: waiting up to {} s for the desktop to release it",
                PORT_WAIT.as_secs()
            );
            announced = true;
        }
        tokio::time::sleep(PORT_POLL).await;
    }
    true
}

fn is_port_free(port: i32) -> bool {
    TcpListener::bind(address(port)).is_ok()
}

/// Новый сеанс: закрытие терминала (SIGHUP) не убьёт фоновый демон. Делает сам демон (консоль лишь перенаправляет его stdio в
/// `/dev/null`), как `McpDaemon.Detach` → `setsid()`.
#[cfg(unix)]
pub fn detach() {
    let _ = rustix::process::setsid();
}

#[cfg(not(unix))]
pub fn detach() {}
