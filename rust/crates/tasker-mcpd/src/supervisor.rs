//! Супервизор демона (.NET `Supervisor.cs`): процесс, которым владеют служба (launchd, systemd) или `tasker mcp start`, и блокировка
//! `daemon.lock`. Он ничего не обслуживает сам. Его дело — слушающий сокет (открывает один раз и не закрывает, пока жив демон) и
//! рабочие процессы ([`crate::worker`]), которые принимают вызовы из этого сокета. Поэтому сервер можно заменить на лету, не трогая
//! ни службу, ни порт, ни блокировку:
//!
//! 1. рядом со старым рабочим процессом запускается новый (затвор у него закрыт — из общей очереди он не принимает);
//! 2. новый открывает все области и сообщает `ready`; не успел или не открыл область, которая открыта у старого (`require`), —
//!    его снимают, старый как работал, так и работает;
//! 3. новому открывают затвор (`activate`), старому закрывают (`drain`): он заканчивает начатые вызовы и выходит.
//!
//! Супервизор живёт до перезапуска демона и не обновляется на лету, поэтому обмен с рабочими процессами ([`crate::wire`]) совместим в
//! обе стороны — и с рабочими процессами .NET. Рабочий процесс упал — супервизор запускает его снова (последней сборкой) с паузами
//! 0/1/2/5/10 с; больше пяти падений за минуту — сдаётся (код 1). Тексты журнала — как у .NET.
//!
//! Windows (задание с «убить всех при закрытии», `CREATE_NO_WINDOW`, сокет через `WSADuplicateSocketW`) написано по C# и не проверено.
use crate::daemon::detach;
use crate::files::{DaemonFiles, DaemonInfo, new_token};
use crate::gate::seconds;
use crate::handoff::Handoff;
use crate::state::{UpgradeRequest, UpgradeResult, WorkerRole, WorkerStatus, WorkspaceState, WorkspaceStatus};
use crate::wire::{PREFIX, WireMessage};
use std::io::Write as _;
use std::net::TcpListener;
use std::path::Path;
use std::process::Stdio;
use std::sync::atomic::{AtomicI32, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};
use tasker_core::Timestamp;
use tasker_core::settings::{SettingsStore, logs_dir};
use tokio::io::{AsyncBufReadExt as _, AsyncWriteExt as _, BufReader};
use tokio::sync::{Notify, mpsc, watch};
use tracing::{error, info, warn};

const PORT_WAIT: Duration = Duration::from_secs(5);
const STOP_WAIT: Duration = Duration::from_secs(20);
const RESTART_BACKOFF: [Duration; 5] = [
    Duration::ZERO,
    Duration::from_secs(1),
    Duration::from_secs(2),
    Duration::from_secs(5),
    Duration::from_secs(10),
];

/// Код выхода: 0 — остановлен штатно, 1 — не запустился (или рабочий процесс падает без остановки).
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
    let code = runtime.block_on(supervise(&files, detached));
    runtime.shutdown_timeout(Duration::from_secs(2));
    drop(instance);
    code
}

fn crashed(message: &str) -> i32 {
    error!(fatal = true, "The MCP server crashed: {message}");
    let _ = writeln!(std::io::stderr(), "The MCP server did not start: {message}");
    1
}

async fn supervise(files: &DaemonFiles, detached: bool) -> i32 {
    info!("Starting the MCP server (supervisor, pid {})", std::process::id());
    let port = match SettingsStore::new(None).load() {
        Ok(settings) => settings.mcp.port,
        Err(e) => return crashed(&e.to_string()),
    };

    // Десктоп мог занять порт раньше: он видит блокировку демона и отпускает порт сам — даём ему на это время.
    let handoff = Handoff::for_this_system();
    let listener = match bind(port).await {
        Some(listener) => listener,
        None => {
            let message = format!(
                "Port {port} is busy: another program (for example Tasker desktop) listens on it. Free it or change the port with 'tasker mcp port <number>'"
            );
            error!("{message}");
            let _ = writeln!(std::io::stderr(), "{message}");
            return 1;
        }
    };
    if let Err(e) = handoff.prepare(&listener) {
        return crashed(&e.to_string());
    }

    let supervisor = Arc::new(Supervisor::new(listener, handoff, port, new_token(), !detached));
    let info = DaemonInfo {
        pid: std::process::id(),
        port,
        token: supervisor.token.clone(),
        started_at: supervisor.started_at,
    };
    if let Err(e) = files.write_info(&info) {
        return crashed(&format!("cannot write {}: {e}", files.info_file().display()));
    }
    let code = supervisor.serve(files.directory()).await;
    if let Err(e) = files.delete_info(std::process::id()) {
        error!("Cannot delete {}: {e}", files.info_file().display());
    }
    code
}

/// Сокет, который получают рабочие процессы. Занят — ждём до [`PORT_WAIT`].
async fn bind(port: i32) -> Option<TcpListener> {
    let port = u16::try_from(port).ok()?;
    let deadline = Instant::now() + PORT_WAIT;
    let mut announced = false;
    loop {
        match crate::handoff::bind(port) {
            Ok(listener) => return Some(listener),
            Err(_) => {
                if Instant::now() >= deadline {
                    return None;
                }
                if !announced {
                    info!(
                        "Port {port} is busy: waiting up to {} s for the desktop to release it",
                        PORT_WAIT.as_secs()
                    );
                    announced = true;
                }
                tokio::time::sleep(Duration::from_millis(200)).await;
            }
        }
    }
}

struct Inner {
    workers: Vec<Arc<WorkerProcess>>,
    failures: Vec<Instant>,
    stopping: bool,
    /// Чем запускать рабочий процесс: своя программа, после замены — программа новой сборки.
    command: (String, Vec<String>),
}

pub struct Supervisor {
    sync: Mutex<Inner>,
    finished: watch::Sender<Option<i32>>,
    upgrading: tokio::sync::Mutex<()>,
    listener: TcpListener,
    handoff: Handoff,
    token: String,
    port: i32,
    started_at: Timestamp,
    console: bool,
    job: job::WorkerJob,
}

impl Supervisor {
    fn new(listener: TcpListener, handoff: Handoff, port: i32, token: String, console: bool) -> Supervisor {
        Supervisor {
            sync: Mutex::new(Inner {
                workers: Vec::new(),
                failures: Vec::new(),
                stopping: false,
                command: own_command(),
            }),
            finished: watch::channel(None).0,
            upgrading: tokio::sync::Mutex::new(()),
            listener,
            handoff,
            token,
            port,
            started_at: Timestamp::now_utc(),
            console,
            job: job::WorkerJob::create(),
        }
    }

    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.sync.lock().unwrap_or_else(|e| e.into_inner())
    }

    async fn serve(self: &Arc<Self>, daemon_directory: &Path) -> i32 {
        let (file, arguments) = self.lock().command.clone();
        if let Err(e) = self.start_worker(&file, &arguments, true) {
            return crashed(&e);
        }
        info!(
            "MCP server is running on port {} (supervisor pid {})",
            self.port,
            std::process::id()
        );

        let signals = {
            let supervisor = self.clone();
            let directory = daemon_directory.to_path_buf();
            tokio::spawn(async move {
                let reason = crate::shutdown::os_signal(&directory).await;
                supervisor.stop(reason);
            })
        };
        let mut finished = self.finished.subscribe();
        let code = match finished.wait_for(Option::is_some).await {
            Ok(code) => code.unwrap_or(0),
            Err(_) => 0,
        };
        signals.abort();
        self.stop_workers().await;
        info!("MCP server stopped");
        code
    }

    fn finish(&self, code: i32) {
        self.finished.send_if_modified(|current| {
            if current.is_none() {
                *current = Some(code);
                true
            } else {
                false
            }
        });
    }

    fn stop(&self, reason: &str) {
        info!("Stopping the MCP server: {reason}");
        self.lock().stopping = true;
        self.finish(0);
    }

    // ---- рабочие процессы ----

    fn start_worker(self: &Arc<Self>, file: &str, arguments: &[String], activate: bool) -> Result<Arc<WorkerProcess>, String> {
        let listen = self.handoff.worker_arguments(&self.listener);
        let (worker, child, stdout) = WorkerProcess::start(file, arguments, &listen, self.console).map_err(|e| e.to_string())?;
        self.job.assign(&child);
        match self.handoff.export(&self.listener, worker.pid) {
            Ok(listen_socket) => worker.send(&WireMessage {
                token: Some(self.token.clone()),
                port: Some(self.port),
                supervisor_pid: Some(i64::from(std::process::id())),
                supervisor_started_at: Some(self.started_at),
                listen_socket,
                ..WireMessage::new("hello")
            }),
            Err(e) => {
                let mut child = child;
                drop(stdout);
                tokio::spawn(async move {
                    let _ = child.start_kill();
                    let _ = child.wait().await;
                });
                return Err(format!("Cannot give the listening socket to the worker {}: {e}", worker.pid));
            }
        }

        self.lock().workers.push(worker.clone());
        worker.begin(self.clone(), child, stdout);
        if activate {
            worker.info().role = WorkerRole::Active;
            worker.send(&WireMessage::new("activate"));
        }
        info!("Worker {} started: {} {}", worker.pid, file, arguments.join(" "));
        self.broadcast();
        Ok(worker)
    }

    fn broadcast(&self) {
        let workers = self.lock().workers.clone();
        let message = WireMessage {
            workers: Some(workers.iter().map(|w| w.status()).collect()),
            ..WireMessage::new("workers")
        };
        for worker in &workers {
            worker.send(&message);
        }
    }

    fn active(&self) -> Option<Arc<WorkerProcess>> {
        self.lock().workers.iter().find(|w| w.info().role == WorkerRole::Active).cloned()
    }

    fn on_message(self: &Arc<Self>, worker: &Arc<WorkerProcess>, message: WireMessage) {
        match (message.r#type.as_str(), message.op.as_deref()) {
            ("ready", _) => {
                {
                    let mut info = worker.info();
                    info.version = message.version.unwrap_or_default();
                    info.build = message.build.unwrap_or_default();
                    info.workspaces = message.workspaces.unwrap_or_default();
                }
                worker.ready.set();
                self.broadcast();
            }
            ("active", _) => worker.active_ack.set(),
            ("request", Some("stop")) => self.stop("stop requested through the control interface"),
            ("request", Some("upgrade")) => {
                let Some(request) = message.upgrade else { return };
                let require = message.require.unwrap_or_default();
                let supervisor = self.clone();
                let worker = worker.clone();
                let id = message.id;
                tokio::spawn(async move {
                    let result = supervisor.upgrade(request, require).await;
                    worker.send(&WireMessage {
                        id,
                        ok: Some(result.ok),
                        message: Some(result.message.clone()),
                        result: Some(result),
                        ..WireMessage::new("reply")
                    });
                });
            }
            _ => {}
        }
    }

    fn on_exited(self: &Arc<Self>, worker: &Arc<WorkerProcess>) {
        let replace_active = {
            let mut inner = self.lock();
            inner.workers.retain(|w| !Arc::ptr_eq(w, worker));
            let info = worker.info();
            !inner.stopping && info.role == WorkerRole::Active && !info.replaced
        };
        info!("Worker {} exited with code {}", worker.pid, worker.exit_code());
        self.broadcast();
        if replace_active {
            let supervisor = self.clone();
            tokio::spawn(async move { supervisor.restart().await });
        }
    }

    /// Рабочий процесс упал: запускаем снова той сборкой, что работала (после замены — новой). Падает без остановки — сдаёмся.
    fn restart(self: Arc<Self>) -> std::pin::Pin<Box<dyn std::future::Future<Output = ()> + Send>> {
        Box::pin(async move {
            let pause = {
                let mut inner = self.lock();
                let now = Instant::now();
                inner.failures.retain(|x| now.duration_since(*x) <= Duration::from_secs(60));
                inner.failures.push(now);
                if inner.failures.len() > RESTART_BACKOFF.len() {
                    drop(inner);
                    error!(fatal = true, "The MCP server worker keeps crashing: giving up");
                    self.finish(1);
                    return;
                }
                RESTART_BACKOFF[inner.failures.len() - 1]
            };

            warn!(
                "The MCP server worker exited unexpectedly: starting it again in {} s",
                seconds(pause)
            );
            tokio::time::sleep(pause).await;
            let command = {
                let inner = self.lock();
                if inner.stopping {
                    return;
                }
                inner.command.clone()
            };
            if let Err(e) = self.start_worker(&command.0, &command.1, true) {
                error!("Cannot start the worker: {e}");
                tokio::spawn(self.clone().restart());
            }
        })
    }

    // ---- замена на лету ----

    async fn upgrade(self: &Arc<Self>, request: UpgradeRequest, require: Vec<String>) -> UpgradeResult {
        let Ok(_upgrading) = self.upgrading.try_lock() else {
            return UpgradeResult::failed("Another upgrade is in progress: wait for it to finish");
        };
        let Some(old) = self.active() else {
            return UpgradeResult::failed("No worker accepts calls right now: wait for the server to start, or restart it");
        };
        if self.lock().stopping {
            return UpgradeResult::failed("The MCP server is stopping");
        }

        let timeout = Duration::from_secs(u64::try_from(request.timeout_seconds.max(1)).unwrap_or(1));
        info!("Upgrading: starting a new worker next to {}", old.pid);

        let fresh = match self.start_worker(&request.file, &request.arguments, false) {
            Ok(fresh) => fresh,
            Err(e) => {
                return self.fail(
                    format!("Cannot start the new MCP server process ({}): {e}", request.file),
                    &old,
                    None,
                );
            }
        };

        // Ждём готовности нового: он открыл все области. Вышел раньше или не уложился — старый остаётся.
        let outcome = tokio::select! {
            _ = fresh.ready.wait() => Ok(()),
            _ = fresh.exited.wait() => Err(format!("the new MCP server process exited with code {} before it was ready", fresh.exit_code())),
            _ = tokio::time::sleep(timeout) => Err(format!("the new MCP server process was not ready in {} s", seconds(timeout))),
        };
        if let Err(reason) = outcome {
            self.remove(&fresh);
            return self.fail(
                format!(
                    "{}: the running server is untouched. See the log in {}",
                    capitalize(&reason),
                    logs_dir().display()
                ),
                &old,
                Some(&fresh),
            );
        }

        let workspaces = fresh.info().workspaces.clone();
        let missing: Vec<String> = require
            .iter()
            .filter_map(|path| {
                let found: Option<&WorkspaceStatus> = workspaces.iter().find(|w| &w.path == path);
                match found {
                    Some(w) if w.state == WorkspaceState::Open => None,
                    Some(w) => Some(format!("{path}: {}", w.error.clone().unwrap_or_else(|| "not open".into()))),
                    None => Some(format!("{path}: not open")),
                }
            })
            .collect();
        if !missing.is_empty() {
            self.remove(&fresh);
            return self.fail(
                format!(
                    "The new MCP server process cannot open workspaces that are open now ({}): the running server is untouched",
                    missing.join("; ")
                ),
                &old,
                Some(&fresh),
            );
        }

        // Открываем затвор у нового раньше, чем закрываем у старого: соединения всё это время есть кому принять.
        fresh.send(&WireMessage::new("activate"));
        let activated = tokio::select! {
            _ = fresh.active_ack.wait() => true,
            _ = fresh.exited.wait() => false,
            _ = tokio::time::sleep(Duration::from_secs(10)) => false,
        };
        if !activated {
            self.remove(&fresh);
            return self.fail(
                "The new MCP server process did not start to accept calls: the running server is untouched".into(),
                &old,
                Some(&fresh),
            );
        }

        {
            let mut inner = self.lock();
            fresh.info().role = WorkerRole::Active;
            {
                let mut old_info = old.info();
                old_info.role = WorkerRole::Draining;
                old_info.replaced = true;
            }
            inner.command = (request.file.clone(), request.arguments.clone());
        }

        old.send(&WireMessage::new("drain"));
        self.broadcast();
        {
            let old = old.clone();
            tokio::spawn(async move { kill_if_stuck(old).await });
        }

        let (old_build, fresh_build) = (old.info().build.clone(), fresh.info().build.clone());
        info!(
            "Upgraded: worker {} (build {}) accepts calls, {} (build {}) finishes its calls",
            fresh.pid, fresh_build, old.pid, old_build
        );
        UpgradeResult {
            ok: true,
            message: format!(
                "The MCP server process is replaced without downtime: {} ({}) -> {} ({}); the old process finishes the calls it has started",
                old.pid,
                old.describe(),
                fresh.pid,
                fresh.describe()
            ),
            old_pid: Some(i64::from(old.pid)),
            new_pid: Some(i64::from(fresh.pid)),
            old_build: Some(old_build),
            new_build: Some(fresh_build),
        }
    }

    fn fail(&self, message: String, old: &WorkerProcess, fresh: Option<&WorkerProcess>) -> UpgradeResult {
        warn!("Upgrade rolled back: {message}");
        if let Some(fresh) = fresh {
            fresh.kill();
        }
        UpgradeResult {
            ok: false,
            message,
            old_pid: Some(i64::from(old.pid)),
            new_pid: fresh.map(|f| i64::from(f.pid)),
            old_build: Some(old.info().build.clone()),
            new_build: fresh.map(|f| f.info().build.clone()),
        }
    }

    /// Снятый при откате процесс не считается упавшим: заменять его нечем.
    fn remove(&self, worker: &Arc<WorkerProcess>) {
        worker.info().replaced = true;
        worker.kill();
        self.lock().workers.retain(|w| !Arc::ptr_eq(w, worker));
        self.broadcast();
    }

    // ---- остановка ----

    async fn stop_workers(&self) {
        let workers = self.lock().workers.clone();
        for worker in &workers {
            worker.send(&WireMessage::new("stop"));
        }
        let all = async {
            for worker in &workers {
                worker.exited.wait().await;
            }
        };
        if tokio::time::timeout(STOP_WAIT, all).await.is_err() {
            for worker in workers.iter().filter(|w| !w.exited.is_set()) {
                warn!("Worker {} did not stop in {} s: killed", worker.pid, STOP_WAIT.as_secs());
                worker.kill();
            }
            let all = async {
                for worker in &workers {
                    worker.exited.wait().await;
                }
            };
            let _ = tokio::time::timeout(Duration::from_secs(2), all).await;
        }
    }
}

/// Старый процесс не вышел за срок завершения — снимаем (его вызовы к этому времени оборваны сервером).
async fn kill_if_stuck(old: Arc<WorkerProcess>) {
    let limit = crate::worker::drain_timeout() + STOP_WAIT;
    if tokio::time::timeout(limit, old.exited.wait()).await.is_err() {
        warn!("Worker {} did not exit after draining: killed", old.pid);
        old.kill();
    }
}

fn capitalize(text: &str) -> String {
    let mut chars = text.chars();
    match chars.next() {
        Some(first) => first.to_uppercase().chain(chars).collect(),
        None => String::new(),
    }
}

/// Своя программа (`Launcher.Command`): рабочий процесс — тот же `tasker-mcpd`.
fn own_command() -> (String, Vec<String>) {
    let path = std::env::current_exe()
        .map(|p| p.to_string_lossy().into_owned())
        .unwrap_or_else(|_| "tasker-mcpd".into());
    (path, Vec::new())
}

/// Флаг, которого можно ждать (`TaskCompletionSource` без результата).
struct Flag {
    tx: watch::Sender<bool>,
}

impl Flag {
    fn new() -> Flag {
        Flag {
            tx: watch::channel(false).0,
        }
    }

    fn set(&self) {
        self.tx.send_replace(true);
    }

    fn is_set(&self) -> bool {
        *self.tx.borrow()
    }

    async fn wait(&self) {
        let mut rx = self.tx.subscribe();
        let _ = rx.wait_for(|set| *set).await;
    }
}

struct WorkerInfo {
    role: WorkerRole,
    /// Заменён или снят при откате: его выход не повод запускать рабочий процесс заново.
    replaced: bool,
    version: String,
    build: String,
    workspaces: Vec<WorkspaceStatus>,
}

/// Рабочий процесс глазами супервизора: сам процесс, канал связи с ним и то, что он о себе сообщил.
struct WorkerProcess {
    pid: u32,
    started_at: Timestamp,
    info: Mutex<WorkerInfo>,
    ready: Flag,
    active_ack: Flag,
    exited: Flag,
    exit_code: AtomicI32,
    /// Строки в stdin процесса: пишет одна задача по порядку (как синхронный `Send` у .NET).
    input: mpsc::UnboundedSender<String>,
    kill: Notify,
}

impl WorkerProcess {
    fn start(
        file: &str,
        arguments: &[String],
        listen: &[String],
        console: bool,
    ) -> std::io::Result<(Arc<WorkerProcess>, tokio::process::Child, tokio::process::ChildStdout)> {
        let mut command = tokio::process::Command::new(file);
        command
            .args(arguments)
            .arg("--worker")
            .args(listen)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .kill_on_drop(false);
        if console {
            command.arg("--console");
        }
        #[cfg(windows)]
        if !console {
            // Фоновый супервизор без консоли: без этого у каждого рабочего процесса появилось бы своё окно.
            const CREATE_NO_WINDOW: u32 = 0x0800_0000;
            command.creation_flags(CREATE_NO_WINDOW);
        }
        let mut child = command.spawn()?;
        let pid = child.id().unwrap_or(0);
        let mut stdin = child.stdin.take().ok_or_else(|| std::io::Error::other("no stdin of the worker"))?;
        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| std::io::Error::other("no stdout of the worker"))?;

        let (input, mut lines) = mpsc::unbounded_channel::<String>();
        tokio::spawn(async move {
            while let Some(line) = lines.recv().await {
                let mut bytes = line.into_bytes();
                bytes.push(b'\n');
                if stdin.write_all(&bytes).await.is_err() || stdin.flush().await.is_err() {
                    // Процесс уже вышел: об этом узнает обработчик выхода.
                    break;
                }
            }
        });

        let worker = Arc::new(WorkerProcess {
            pid,
            started_at: Timestamp::now_utc(),
            info: Mutex::new(WorkerInfo {
                role: WorkerRole::Starting,
                replaced: false,
                version: String::new(),
                build: String::new(),
                workspaces: Vec::new(),
            }),
            ready: Flag::new(),
            active_ack: Flag::new(),
            exited: Flag::new(),
            exit_code: AtomicI32::new(-1),
            input,
            kill: Notify::new(),
        });
        Ok((worker, child, stdout))
    }

    fn info(&self) -> MutexGuard<'_, WorkerInfo> {
        self.info.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn status(&self) -> WorkerStatus {
        let info = self.info();
        WorkerStatus {
            pid: i64::from(self.pid),
            version: info.version.clone(),
            build: info.build.clone(),
            role: info.role,
            started_at: self.started_at,
        }
    }

    fn describe(&self) -> String {
        let info = self.info();
        format!(
            "{}build {}",
            if info.version.is_empty() {
                String::new()
            } else {
                format!("{}, ", info.version)
            },
            if info.build.is_empty() { "?" } else { &info.build }
        )
    }

    fn exit_code(&self) -> i32 {
        self.exit_code.load(Ordering::SeqCst)
    }

    fn send(&self, message: &WireMessage) {
        let _ = self.input.send(message.to_line());
    }

    fn kill(&self) {
        self.kill.notify_one();
    }

    /// Начинает читать вывод и следить за выходом.
    fn begin(self: &Arc<Self>, supervisor: Arc<Supervisor>, mut child: tokio::process::Child, stdout: tokio::process::ChildStdout) {
        let reading = {
            let worker = self.clone();
            let supervisor = supervisor.clone();
            tokio::spawn(async move {
                let mut lines = BufReader::new(stdout).lines();
                while let Ok(Some(line)) = lines.next_line().await {
                    let Some(text) = line.strip_prefix(PREFIX) else { continue };
                    if let Some(message) = WireMessage::parse(text.trim_end_matches('\r')) {
                        supervisor.on_message(&worker, message);
                    }
                }
            })
        };
        let worker = self.clone();
        tokio::spawn(async move {
            let status = tokio::select! {
                status = child.wait() => status,
                _ = worker.kill.notified() => {
                    let _ = child.start_kill();
                    child.wait().await
                }
            };
            // Хвост вывода дочитан раньше выхода: сообщение «ready» перед самым выходом не теряется.
            tokio::time::sleep(Duration::from_millis(50)).await;
            drop(reading);
            worker
                .exit_code
                .store(status.map(|s| exit_code(&s)).unwrap_or(-1), Ordering::SeqCst);
            // Сначала журнал и список, потом ожидающие выхода (остановка, замена): «Worker … exited» — раньше «MCP server stopped».
            supervisor.on_exited(&worker);
            worker.exited.set();
        });
    }
}

/// `Process.ExitCode`: код выхода или 128 + сигнал (снят сигналом).
fn exit_code(status: &std::process::ExitStatus) -> i32 {
    #[cfg(unix)]
    {
        use std::os::unix::process::ExitStatusExt as _;
        if let Some(signal) = status.signal() {
            return 128 + signal;
        }
    }
    status.code().unwrap_or(-1)
}

/// Привязка рабочих процессов к супервизору (.NET `WorkerJob.cs`): на Unix рабочий процесс выходит сам, когда закрывается канал
/// (stdin); на Windows надёжнее объект «задание» с признаком «убить всех при закрытии». Не проверено.
mod job {
    #[cfg(windows)]
    pub struct WorkerJob {
        handle: windows_sys::Win32::Foundation::HANDLE,
    }

    // SAFETY: дескриптор задания — значение ядра, им можно пользоваться из любого потока.
    #[cfg(windows)]
    unsafe impl Send for WorkerJob {}
    #[cfg(windows)]
    unsafe impl Sync for WorkerJob {}

    #[cfg(windows)]
    impl WorkerJob {
        pub fn create() -> WorkerJob {
            use windows_sys::Win32::System::JobObjects::{
                CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
                JobObjectExtendedLimitInformation, SetInformationJobObject,
            };
            // SAFETY: обычные вызовы Win32 с проверкой результата.
            unsafe {
                let handle = CreateJobObjectW(std::ptr::null(), std::ptr::null());
                if handle.is_null() {
                    // Задания нет (запрещено окружением): работаем без него, рабочие процессы выйдут по закрытию канала.
                    return WorkerJob { handle };
                }
                let mut information: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = std::mem::zeroed();
                information.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                if SetInformationJobObject(
                    handle,
                    JobObjectExtendedLimitInformation,
                    (&information as *const JOBOBJECT_EXTENDED_LIMIT_INFORMATION).cast(),
                    std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
                ) == 0
                {
                    windows_sys::Win32::Foundation::CloseHandle(handle);
                    return WorkerJob {
                        handle: std::ptr::null_mut(),
                    };
                }
                WorkerJob { handle }
            }
        }

        pub fn assign(&self, child: &tokio::process::Child) {
            if self.handle.is_null() {
                return;
            }
            if let Some(process) = child.raw_handle() {
                // Не вышло — рабочий процесс всё равно выйдет по закрытию канала.
                // SAFETY: оба дескриптора живы на время вызова.
                unsafe {
                    windows_sys::Win32::System::JobObjects::AssignProcessToJobObject(self.handle, process as _);
                }
            }
        }
    }

    #[cfg(windows)]
    impl Drop for WorkerJob {
        fn drop(&mut self) {
            if !self.handle.is_null() {
                // SAFETY: дескриптор получен от CreateJobObjectW и закрывается один раз.
                unsafe {
                    windows_sys::Win32::Foundation::CloseHandle(self.handle);
                }
            }
        }
    }

    #[cfg(not(windows))]
    pub struct WorkerJob;

    #[cfg(not(windows))]
    impl WorkerJob {
        pub fn create() -> WorkerJob {
            WorkerJob
        }

        pub fn assign(&self, _child: &tokio::process::Child) {}
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn capitalize_like_dotnet() {
        assert_eq!(capitalize("the new MCP server"), "The new MCP server");
        assert_eq!(capitalize(""), "");
    }

    #[test]
    fn backoff_is_zero_one_two_five_ten() {
        assert_eq!(RESTART_BACKOFF.map(|d| d.as_secs()), [0, 1, 2, 5, 10]);
    }
}
