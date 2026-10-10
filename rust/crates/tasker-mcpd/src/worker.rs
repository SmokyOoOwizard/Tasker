//! Рабочий процесс демона под супервизором (.NET `McpWorker.cs`, `WorkerLink.cs`): тот же сервер MCP, что и в одиночном режиме
//! ([`crate::daemon`]), но слушающий сокет ему дали готовым (наследуемый дескриптор `--listen-fd N` или описание в `hello`), а
//! принимает он из него, только когда супервизор открыл затвор ([`crate::gate`]): первый процесс — сразу, процесс, пришедший на
//! смену, — когда открыл все области. Команды супервизора приходят в stdin, события и запросы уходят в stdout с префиксом
//! `@tasker ` ([`crate::wire`]); журнал консоли — в stderr. Супервизор закрыл канал (его больше нет) — процесс останавливается.
//!
//! Блокировку `daemon.lock` и `daemon.json` держит супервизор; `/daemon/status` показывает его pid и время запуска (из `hello`).
use crate::daemon::{SHUTDOWN_TIMEOUT, Server, retry_failed, watch_settings};
use crate::gate::{AcceptGate, DrainState, GatedListener};
use crate::handoff;
use crate::http::{self, Control, UpgradeHook};
use crate::shutdown::Shutdown;
use crate::state::{DaemonState, UpgradeResult, WorkspaceState, mcp_url};
use crate::wire::{PREFIX, WireMessage};
use std::collections::HashMap;
use std::future::IntoFuture as _;
use std::io::{BufRead as _, Write as _};
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tasker_core::Timestamp;
use tasker_core::settings::{SettingsStore, daemon_dir, logs_dir};
use tokio::sync::{mpsc, oneshot};
use tracing::{error, info, warn};

const HELLO_WAIT: Duration = Duration::from_secs(10);

/// Сколько ждать тишины (нет вызовов в работе), прежде чем старый процесс выйдет (`TASKER_MCP_DRAIN_QUIET_MS`, по умолчанию 2 с).
pub fn drain_quiet() -> Duration {
    match std::env::var("TASKER_MCP_DRAIN_QUIET_MS")
        .ok()
        .and_then(|v| v.trim().parse::<i64>().ok())
    {
        Some(ms) if ms >= 0 => Duration::from_millis(ms as u64),
        _ => Duration::from_secs(2),
    }
}

/// Сколько старый процесс доделывает начатые вызовы (`TASKER_MCP_DRAIN_TIMEOUT_MS`, по умолчанию 60 с); потом остальные обрываются.
pub fn drain_timeout() -> Duration {
    match std::env::var("TASKER_MCP_DRAIN_TIMEOUT_MS")
        .ok()
        .and_then(|v| v.trim().parse::<i64>().ok())
    {
        Some(ms) if ms > 0 => Duration::from_millis(ms as u64),
        _ => Duration::from_secs(60),
    }
}

/// Код выхода: 0 — остановлен штатно, 1 — не запустился. `listen_fd` — дескриптор из `--listen-fd` (Unix); None — сокет придёт в
/// `hello`. `console` — не в фоне (`--console`): журнал ещё и в stderr.
pub fn run(listen_fd: Option<i32>, console: bool) -> i32 {
    crate::logging::configure(&logs_dir(), !console, true);
    if let Some(fd) = listen_fd
        && cfg!(unix)
        && let Err(e) = handoff::set_inheritable(fd, false)
    {
        // Процессы, которые запустит сам сервер, сокет получить не должны.
        return crashed(&e.to_string());
    }

    let runtime = match tokio::runtime::Builder::new_multi_thread().enable_all().build() {
        Ok(runtime) => runtime,
        Err(e) => return crashed(&format!("cannot start the runtime: {e}")),
    };
    let code = runtime.block_on(serve(listen_fd));
    runtime.shutdown_timeout(Duration::from_secs(2));
    code
}

fn crashed(message: &str) -> i32 {
    error!(fatal = true, "The MCP server worker crashed: {message}");
    let _ = writeln!(std::io::stderr(), "The MCP server did not start: {message}");
    1
}

async fn serve(listen_fd: Option<i32>) -> i32 {
    let (link, mut commands) = Link::start();
    let hello = match link.read_hello(&mut commands).await {
        Ok(hello) => hello,
        Err(e) => return crashed(&e),
    };
    info!(
        "Starting the MCP server worker {} (build {}, pid {})",
        crate::version::version(),
        crate::version::build_id(),
        std::process::id()
    );

    let store = SettingsStore::new(None);
    let settings = match store.load() {
        Ok(settings) => settings,
        Err(e) => return crashed(&e.to_string()),
    };
    let port = hello.port.unwrap_or(settings.mcp.port);
    let Some(token) = hello.token.clone() else {
        return crashed("The supervisor did not send the control secret");
    };
    let listener = match handoff::import(hello.listen_socket.as_deref(), listen_fd) {
        Ok(listener) => listener,
        Err(e) => return crashed(&e.to_string()),
    };
    if listen_fd.is_none()
        && cfg!(unix)
        && let Some(handoff::Described::Fd(fd)) = handoff::parse(hello.listen_socket.as_deref())
    {
        let _ = handoff::set_inheritable(fd, false);
    }
    let listener = match listener
        .set_nonblocking(true)
        .and_then(|_| tokio::net::TcpListener::from_std(listener))
    {
        Ok(listener) => listener,
        Err(e) => return crashed(&format!("cannot use the listening socket: {e}")),
    };

    let gate = Arc::new(AcceptGate::new());
    let drain = Arc::new(DrainState::new());
    let pid = hello
        .supervisor_pid
        .and_then(|p| u32::try_from(p).ok())
        .unwrap_or_else(std::process::id);
    let started_at = hello.supervisor_started_at.unwrap_or_else(Timestamp::now_utc);
    let state = Arc::new(DaemonState::with_process(port, port, pid, started_at));

    let upgrade_link = link.clone();
    let upgrade_state = state.clone();
    let upgrade: UpgradeHook = Arc::new(move |request| {
        let link = upgrade_link.clone();
        let state = upgrade_state.clone();
        Box::pin(async move {
            let require: Vec<String> = state
                .workspaces()
                .into_iter()
                .filter(|w| w.state == WorkspaceState::Open)
                .map(|w| w.path)
                .collect();
            let timeout = Duration::from_secs(u64::try_from(request.timeout_seconds.saturating_add(30)).unwrap_or(30));
            let message = WireMessage {
                op: Some("upgrade".into()),
                upgrade: Some(request),
                require: Some(require),
                ..WireMessage::new("request")
            };
            match link.request(message, timeout).await {
                Ok(reply) => reply.result.unwrap_or_else(|| UpgradeResult {
                    ok: reply.ok.unwrap_or(false),
                    message: reply.message.unwrap_or_else(|| "The supervisor sent no result".into()),
                    ..UpgradeResult::default()
                }),
                Err(e) => UpgradeResult::failed(format!("The supervisor did not answer the upgrade request: {e}")),
            }
        })
    });
    let stop_link = link.clone();
    let control = Control {
        upgrade: Some(upgrade),
        stop: Some(Arc::new(move || {
            stop_link.send(&WireMessage {
                op: Some("stop".into()),
                ..WireMessage::new("request")
            })
        })),
        drain: Some(drain.clone()),
    };
    let Server { sync, shutdown, app } = Server::new(state.clone(), token, control);
    sync.prepare(settings.mcp.workspaces.clone());

    // Команды супервизора слушаем сразу: activate может прийти раньше, чем сервер стартует. Канал закрыт — супервизора нет.
    let listening = {
        let link = link.clone();
        let gate = gate.clone();
        let drain = drain.clone();
        let state = state.clone();
        let shutdown = shutdown.clone();
        tokio::spawn(async move {
            while let Some(line) = commands.recv().await {
                let Some(message) = WireMessage::parse(&line) else { continue };
                if message.r#type == "reply" {
                    link.resolve(message);
                    continue;
                }
                handle(message, &gate, &drain, &state, &shutdown, &link);
            }
            link.fail_all();
            warn!("The supervisor closed the pipe: stopping");
            shutdown.trigger();
        })
    };

    let serving = shutdown.clone();
    let server = tokio::spawn(
        axum::serve(GatedListener::new(listener, &gate), http::router(app))
            .with_graceful_shutdown(async move { serving.triggered().await })
            .into_future(),
    );
    info!(
        "MCP server worker is up (pid {}); it accepts calls when the supervisor opens the gate",
        std::process::id()
    );

    let watch = watch_settings(&store, &sync);
    let retry = retry_failed(&sync);
    sync.apply(settings.mcp.workspaces).await;

    // Области открыты (или не открылись): сообщаем, что можно принимать вызовы.
    link.send(&WireMessage {
        version: Some(crate::version::version().into()),
        build: Some(crate::version::build_id().into()),
        workspaces: Some(state.workspaces()),
        ..WireMessage::new("ready")
    });
    info!(
        "MCP server worker is ready: {}, the workspace is the 'workspace' argument of a tool (pid {})",
        mcp_url(port),
        std::process::id()
    );

    let _ = shutdown.wait(&daemon_dir()).await;
    info!("Stopping the MCP server worker");
    retry.abort();
    drop(watch);
    sync.dispose().await;
    shutdown.trigger();
    if tokio::time::timeout(SHUTDOWN_TIMEOUT, server).await.is_err() {
        error!("Requests did not finish in {} s, closing anyway", SHUTDOWN_TIMEOUT.as_secs());
    }
    listening.abort();
    0
}

fn handle(
    message: WireMessage,
    gate: &Arc<AcceptGate>,
    drain: &Arc<DrainState>,
    state: &DaemonState,
    shutdown: &Arc<Shutdown>,
    link: &Link,
) {
    match message.r#type.as_str() {
        "activate" => {
            gate.open();
            info!("Accepting calls");
            link.send(&WireMessage::new("active"));
        }
        "drain" => {
            // Не ждём здесь: управление должно ответить на запрос о замене, а он сам — вызов в работе.
            tokio::spawn(run_drain(gate.clone(), drain.clone(), shutdown.clone()));
        }
        "stop" => {
            info!("Stop requested by the supervisor");
            shutdown.trigger();
        }
        "workers" => state.set_workers(message.workers.unwrap_or_default()),
        _ => {}
    }
}

/// Старый процесс при замене: перестаёт принимать, доделывает начатое и выходит. Новое соединение к нему больше не придёт —
/// слушающий сокет общий, и принимает из него новый процесс.
async fn run_drain(gate: Arc<AcceptGate>, drain: Arc<DrainState>, shutdown: Arc<Shutdown>) {
    gate.close();
    drain.begin_drain();
    info!("Draining: no new connections are accepted, {} call(s) in progress", drain.active());
    let left = drain.wait_quiet(drain_quiet(), drain_timeout()).await;
    drain.begin_closing();
    info!("Drained ({left} call(s) aborted): the process exits");
    shutdown.trigger();
}

/// Связь с супервизором: stdin читает отдельный поток (строки уходят в канал), stdout пишется целыми строками под замком.
#[derive(Clone)]
pub struct Link {
    inner: Arc<LinkInner>,
}

struct LinkInner {
    write: Mutex<()>,
    requests: Mutex<HashMap<i64, oneshot::Sender<Result<WireMessage, String>>>>,
    next_id: AtomicI64,
}

impl Link {
    /// Запускает чтение stdin; канал закрывается, когда супервизор закрыл свой конец.
    fn start() -> (Link, mpsc::UnboundedReceiver<String>) {
        let (tx, rx) = mpsc::unbounded_channel();
        std::thread::Builder::new()
            .name("tasker-supervisor-link".into())
            .spawn(move || {
                let stdin = std::io::stdin();
                let mut reader = stdin.lock();
                let mut line = String::new();
                loop {
                    line.clear();
                    match reader.read_line(&mut line) {
                        Ok(0) | Err(_) => break,
                        Ok(_) => {
                            let text = line.trim_end_matches(['\n', '\r']).to_string();
                            if tx.send(text).is_err() {
                                break;
                            }
                        }
                    }
                }
            })
            .ok();
        (
            Link {
                inner: Arc::new(LinkInner {
                    write: Mutex::new(()),
                    requests: Mutex::new(HashMap::new()),
                    next_id: AtomicI64::new(0),
                }),
            },
            rx,
        )
    }

    /// Первое сообщение — `hello`: без него процесс не знает ни секрета управления, ни порта.
    async fn read_hello(&self, commands: &mut mpsc::UnboundedReceiver<String>) -> Result<WireMessage, String> {
        let line = match tokio::time::timeout(HELLO_WAIT, commands.recv()).await {
            Err(_) => return Err("The operation was canceled.".into()),
            Ok(None) => return Err("The supervisor closed the pipe before it said hello".into()),
            Ok(Some(line)) => line,
        };
        match WireMessage::parse(&line) {
            Some(message) if message.r#type == "hello" => Ok(message),
            _ => Err(format!("Expected hello from the supervisor, got: {line}")),
        }
    }

    pub fn send(&self, message: &WireMessage) {
        let _guard = self.inner.write.lock().unwrap_or_else(|e| e.into_inner());
        let mut out = std::io::stdout().lock();
        // Супервизора нет — процесс узнает об этом по концу stdin.
        let _ = writeln!(out, "{PREFIX}{}", message.to_line()).and_then(|_| out.flush());
    }

    /// Запрос супервизору с ответом (`upgrade`). Ошибка — таймаут или супервизора нет.
    async fn request(&self, mut message: WireMessage, timeout: Duration) -> Result<WireMessage, String> {
        message.id = self.inner.next_id.fetch_add(1, Ordering::SeqCst) + 1;
        let (tx, rx) = oneshot::channel();
        self.lock_requests().insert(message.id, tx);
        self.send(&message);
        let result = match tokio::time::timeout(timeout, rx).await {
            Err(_) => Err("The operation has timed out.".to_string()),
            Ok(Err(_)) => Err("The supervisor is gone".to_string()),
            Ok(Ok(reply)) => reply,
        };
        self.lock_requests().remove(&message.id);
        result
    }

    fn resolve(&self, reply: WireMessage) {
        if let Some(waiting) = self.lock_requests().remove(&reply.id) {
            let _ = waiting.send(Ok(reply));
        }
    }

    fn fail_all(&self) {
        for (_, waiting) in self.lock_requests().drain() {
            let _ = waiting.send(Err("The supervisor is gone".into()));
        }
    }

    fn lock_requests(&self) -> std::sync::MutexGuard<'_, HashMap<i64, oneshot::Sender<Result<WireMessage, String>>>> {
        self.inner.requests.lock().unwrap_or_else(|e| e.into_inner())
    }
}
