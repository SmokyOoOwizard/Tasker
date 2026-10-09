//! Клиент демона MCP (`DaemonFiles`, `DaemonClient`, `DaemonController`, `Launcher` в .NET): те же `daemon.lock` и
//! `daemon.json` в каталоге данных, те же запросы `/daemon/status`, `/daemon/stop` с секретом управления в заголовке
//! `X-Tasker-Control`; демон запускается отсоединённым процессом `tasker-mcpd --detached` из каталога этой программы. Клиент
//! блокирующий (ureq), без tokio. Управляет и .NET-демоном: файлы и API у них общие.
use crate::errors::{CliError, Result};
use serde_json::Value;
use std::path::PathBuf;
use std::time::{Duration, Instant};
use tasker_core::Timestamp;
use tasker_core::settings::{daemon_dir, logs_dir};
use tasker_daemon::autostart::{ServiceError, ServiceManager};

pub const CONTROL_HEADER: &str = "X-Tasker-Control";

/// Пауза перед единственным повтором запроса, оборвавшегося во время замены процесса демона.
pub const RETRY_PAUSE: Duration = Duration::from_millis(300);

const READY_TIMEOUT: Duration = Duration::from_secs(30);
const STOP_TIMEOUT: Duration = Duration::from_secs(15);

/// Как подключиться к запущенному демону: процесс, порт и секрет управления (`daemon.json`).
#[derive(Debug, Clone)]
pub struct DaemonInfo {
    pub pid: i64,
    pub port: i64,
    pub token: String,
    pub started_at: Option<Timestamp>,
}

pub fn instance_lock() -> PathBuf {
    daemon_dir().join("daemon.lock")
}

pub fn info_file() -> PathBuf {
    daemon_dir().join("daemon.json")
}

/// Работает ли демон: блокировка единственного экземпляра занята (`DaemonFiles.IsRunning`). Упавший демон блокировку не оставляет.
pub fn is_running() -> bool {
    if std::fs::create_dir_all(daemon_dir()).is_err() {
        return false;
    }
    match tasker_core::io::FileLock::acquire(&instance_lock(), Some(Duration::ZERO)) {
        Ok(lock) => {
            drop(lock);
            false
        }
        Err(e) => e.kind() == std::io::ErrorKind::TimedOut || e.kind() == std::io::ErrorKind::WouldBlock,
    }
}

pub fn read_info() -> Option<DaemonInfo> {
    let text = std::fs::read_to_string(info_file()).ok()?;
    let value: Value = serde_json::from_str(text.trim_start_matches('\u{feff}')).ok()?;
    Some(DaemonInfo {
        pid: value.get("pid")?.as_i64()?,
        port: value.get("port")?.as_i64()?,
        token: value.get("token")?.as_str()?.to_string(),
        started_at: value.get("startedAt").and_then(Value::as_str).and_then(Timestamp::parse),
    })
}

/// Снимок `/daemon/status` как его отдал демон (JSON `DaemonStatus`).
#[derive(Debug, Clone)]
pub struct DaemonStatus {
    pub value: Value,
}

impl DaemonStatus {
    pub fn pid(&self) -> i64 {
        self.value.get("pid").and_then(Value::as_i64).unwrap_or_default()
    }

    pub fn port(&self) -> i64 {
        self.value.get("port").and_then(Value::as_i64).unwrap_or_default()
    }

    pub fn settings_port(&self) -> i64 {
        self.value.get("settingsPort").and_then(Value::as_i64).unwrap_or_default()
    }

    pub fn started_at(&self) -> Option<Timestamp> {
        self.value.get("startedAt").and_then(Value::as_str).and_then(Timestamp::parse)
    }

    pub fn mcp_url(&self) -> String {
        self.value
            .get("mcpUrl")
            .and_then(Value::as_str)
            .map(str::to_string)
            .unwrap_or_else(|| format!("http://127.0.0.1:{}/mcp", self.port()))
    }

    pub fn workspaces(&self) -> Vec<&Value> {
        self.value
            .get("workspaces")
            .and_then(Value::as_array)
            .map(|a| a.iter().collect())
            .unwrap_or_default()
    }

    pub fn workers(&self) -> Vec<&Value> {
        self.value
            .get("workers")
            .and_then(Value::as_array)
            .map(|a| a.iter().collect())
            .unwrap_or_default()
    }

    /// Все области из настроек уже открыты (или не открылись).
    pub fn is_settled(&self) -> bool {
        self.workspaces()
            .iter()
            .all(|w| w.get("state").and_then(Value::as_str) != Some("opening"))
    }
}

fn agent(timeout: Duration) -> ureq::Agent {
    ureq::Agent::config_builder()
        .timeout_global(Some(timeout))
        .http_status_as_error(false)
        .build()
        .into()
}

fn url(info: &DaemonInfo, path: &str) -> String {
    format!("http://127.0.0.1:{}{path}", info.port)
}

/// Выполняет запрос и, если соединение оборвалось (демон как раз заменяет рабочий процесс), повторяет его один раз после паузы.
fn retrying<T>(mut request: impl FnMut() -> std::result::Result<T, ureq::Error>) -> std::result::Result<T, ureq::Error> {
    match request() {
        Ok(value) => Ok(value),
        Err(ureq::Error::Timeout(_)) => Err(ureq::Error::Timeout(ureq::Timeout::Global)),
        Err(_) => {
            std::thread::sleep(RETRY_PAUSE);
            request()
        }
    }
}

/// Статус демона; None — он не работает или не отвечает.
pub fn get_status() -> Option<DaemonStatus> {
    if !is_running() {
        return None;
    }
    let info = read_info()?;
    let agent = agent(Duration::from_secs(5));
    let value: Value = retrying(|| {
        let mut response = agent.get(url(&info, "/daemon/status")).header(CONTROL_HEADER, &info.token).call()?;
        let text = response.body_mut().read_to_string()?;
        if !response.status().is_success() {
            return Err(ureq::Error::Other("status".into()));
        }
        serde_json::from_str::<Value>(&text).map_err(|_| ureq::Error::Other("json".into()))
    })
    .ok()?;
    Some(DaemonStatus { value })
}

/// Просит демон остановиться. false — демон не работает или не принял запрос.
pub fn request_stop() -> bool {
    if !is_running() {
        return false;
    }
    let Some(info) = read_info() else {
        return false;
    };
    let agent = agent(Duration::from_secs(5));
    agent
        .post(url(&info, "/daemon/stop"))
        .header(CONTROL_HEADER, &info.token)
        .send_empty()
        .map(|r| r.status().is_success())
        .unwrap_or(false)
}

/// Автозапуск: служба системы и её состояние.
pub struct AutostartInfo {
    pub manager: &'static str,
    pub enabled: bool,
    pub loaded: bool,
}

impl AutostartInfo {
    pub fn json(&self) -> Value {
        crate::json::object(vec![
            ("manager", Value::String(self.manager.into())),
            ("enabled", Value::Bool(self.enabled)),
            ("loaded", Value::Bool(self.loaded)),
        ])
    }
}

/// Управление демоном для `tasker mcp start | stop | restart | status | autostart` (`DaemonController`).
pub struct Controller {
    pub service: Box<dyn ServiceManager>,
}

fn service_error(e: ServiceError) -> CliError {
    CliError::new(e.message())
}

impl Controller {
    pub fn system() -> Controller {
        Controller {
            service: tasker_daemon::autostart::create_system(),
        }
    }

    pub fn autostart(&self) -> AutostartInfo {
        let enabled = self.service.is_enabled();
        AutostartInfo {
            manager: self.service.name(),
            enabled,
            loaded: enabled && self.service.is_loaded(),
        }
    }

    /// Статус запущенного демона; `true` — он уже работал.
    pub fn start(&self) -> Result<(DaemonStatus, bool)> {
        if let Some(running) = get_status() {
            return Ok((running, true));
        }
        let mut process: Option<std::process::Child> = None;
        if is_running() {
            // Уже стартует (или запущен другим способом) — просто ждём.
        } else if self.service.is_enabled() {
            self.service.start().map_err(service_error)?;
        } else {
            process = Some(spawn_daemon()?);
        }
        Ok((wait_ready(process.as_mut())?, false))
    }

    /// false — демон не работал.
    pub fn stop(&self) -> Result<bool> {
        let was_running = is_running();
        if self.service.is_enabled() {
            self.service.stop().map_err(service_error)?;
        }
        // Сначала штатно: запрос демону (он закроет области и дождётся начатых вызовов).
        if is_running() {
            request_stop();
        }
        wait_stopped();
        Ok(was_running)
    }

    pub fn restart(&self) -> Result<DaemonStatus> {
        self.stop()?;
        Ok(self.start()?.0)
    }

    /// Включает автозапуск: служба запускает демон сейчас и при каждом входе в систему.
    pub fn enable_autostart(&self) -> Result<DaemonStatus> {
        // Демон, запущенный не службой, держит порт и блокировку — освобождаем их для службы.
        self.stop()?;
        self.service.enable().map_err(service_error)?;
        wait_ready(None)
    }

    /// Выключает автозапуск. Работавший демон продолжает работать — уже без службы.
    pub fn disable_autostart(&self) -> Result<Option<DaemonStatus>> {
        let was_running = is_running();
        self.service.disable().map_err(service_error)?;
        wait_stopped();
        if was_running { Ok(Some(self.start()?.0)) } else { Ok(None) }
    }
}

/// Команда запуска демона: `tasker-mcpd` из каталога этой программы (`Launcher.DaemonCommand`).
pub fn daemon_program() -> Result<PathBuf> {
    let exe = std::env::current_exe().map_err(|_| CliError::new("Cannot find the path of the running program"))?;
    tasker_daemon::autostart::daemon_program(&exe).map_err(service_error)
}

/// Запускает демон в фоне и не ждёт его (`Launcher.SpawnDaemon`): stdio в /dev/null, новый сеанс он создаёт сам (`--detached`).
pub fn spawn_daemon() -> Result<std::process::Child> {
    let program = daemon_program()?;
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NEW_PROCESS_GROUP: u32 = 0x0000_0200;
        const DETACHED_PROCESS: u32 = 0x0000_0008;
        return std::process::Command::new(&program)
            .arg("--detached")
            .creation_flags(CREATE_NEW_PROCESS_GROUP | DETACHED_PROCESS)
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .spawn()
            .map_err(|e| CliError::new(format!("Cannot start the MCP server process: {e}")));
    }
    #[cfg(not(windows))]
    {
        std::process::Command::new("/bin/sh")
            .arg("-c")
            .arg("exec \"$0\" \"$@\" >/dev/null 2>&1 </dev/null")
            .arg(&program)
            .arg("--detached")
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .spawn()
            .map_err(|e| CliError::new(format!("Cannot start the MCP server process: {e}")))
    }
}

/// Готов, когда отвечает и все области из настроек уже открыты (или не открылись).
fn wait_ready(mut process: Option<&mut std::process::Child>) -> Result<DaemonStatus> {
    let deadline = Instant::now() + READY_TIMEOUT;
    while Instant::now() < deadline {
        if let Some(status) = get_status()
            && status.is_settled()
        {
            return Ok(status);
        }
        if let Some(child) = process.as_deref_mut()
            && child.try_wait().ok().flatten().is_some()
        {
            return Err(CliError::new(log_tail().unwrap_or_else(|| {
                format!("The MCP server exited right after start: see the log in {}", logs_dir().display())
            })));
        }
        std::thread::sleep(Duration::from_millis(150));
    }
    Err(CliError::new(log_tail().unwrap_or_else(|| {
        format!(
            "The MCP server did not answer in {} s: see the log in {}",
            READY_TIMEOUT.as_secs(),
            logs_dir().display()
        )
    })))
}

fn wait_stopped() {
    let deadline = Instant::now() + STOP_TIMEOUT;
    while is_running() {
        if Instant::now() >= deadline {
            // Штатно не остановился — снимаем процесс в последнюю очередь, и только тот, что записан в daemon.json.
            if let Some(info) = read_info() {
                kill(info.pid);
                std::thread::sleep(Duration::from_millis(300));
            }
            return;
        }
        std::thread::sleep(Duration::from_millis(100));
    }
}

fn kill(pid: i64) {
    #[cfg(windows)]
    let _ = std::process::Command::new("taskkill")
        .args(["/PID", &pid.to_string(), "/T", "/F"])
        .output();
    #[cfg(not(windows))]
    let _ = std::process::Command::new("kill").args(["-9", &pid.to_string()]).output();
}

/// Последняя ошибка из журнала демона — почему он не запустился.
fn log_tail() -> Option<String> {
    let mut logs: Vec<(std::time::SystemTime, PathBuf)> = std::fs::read_dir(logs_dir())
        .ok()?
        .flatten()
        .filter(|e| {
            let name = e.file_name().to_string_lossy().into_owned();
            name.starts_with("mcp-") && name.ends_with(".log")
        })
        .filter_map(|e| Some((e.metadata().ok()?.modified().ok()?, e.path())))
        .collect();
    logs.sort_by_key(|entry| std::cmp::Reverse(entry.0));
    let (_, path) = logs.first()?;
    let text = std::fs::read_to_string(path).ok()?;
    // Только последний запуск: ошибки прошлых попыток к этой не относятся.
    let start = text.rfind("Starting the MCP server").unwrap_or(0);
    let line = text[start..]
        .split('\n')
        .rev()
        .find(|x| x.contains("[ERR]") || x.contains("[FTL]"))?;
    let at = line.find(']')?;
    Some(line[at + 1..].trim().to_string())
}

/// `DateTimeOffset.UtcNow - startedAt` в виде «2 h 5 min» / «3 min» / «40 s».
pub fn uptime(started_at: Option<Timestamp>) -> String {
    let seconds = started_at
        .map(|s| (Timestamp::now_utc().unix_ticks() - s.unix_ticks()) / 10_000_000)
        .unwrap_or(0)
        .max(0);
    if seconds >= 3600 {
        format!("{} h {} min", seconds / 3600, (seconds % 3600) / 60)
    } else if seconds >= 60 {
        format!("{} min", seconds / 60)
    } else {
        format!("{seconds} s")
    }
}

/// Текст статуса демона (`McpCommands.Describe`).
pub fn describe(status: &DaemonStatus) -> String {
    let mut lines = vec![
        format!("pid {}, port {}, up {}", status.pid(), status.port(), uptime(status.started_at())),
        format!("address {} (the workspace is the 'workspace' argument of a tool)", status.mcp_url()),
    ];
    let workers = status.workers();
    for worker in &workers {
        let text = |key: &str| worker.get(key).and_then(Value::as_str).unwrap_or_default().to_string();
        let role = match text("role").as_str() {
            "starting" => ", starting",
            "draining" => ", finishing its calls and leaving",
            _ if workers.len() > 1 => ", accepting calls",
            _ => "",
        };
        let build = text("build");
        let version = text("version");
        lines.push(format!(
            "worker pid {}, build {}{}, up {}{role}",
            worker.get("pid").and_then(Value::as_i64).unwrap_or_default(),
            if build.is_empty() { "?".to_string() } else { build },
            if version.is_empty() {
                String::new()
            } else {
                format!(" ({version})")
            },
            uptime(worker.get("startedAt").and_then(Value::as_str).and_then(Timestamp::parse))
        ));
    }
    if status.settings_port() != status.port() {
        lines.push(format!(
            "the port in the settings is {}: 'tasker mcp restart' applies it",
            status.settings_port()
        ));
    }
    let workspaces = status.workspaces();
    if workspaces.is_empty() {
        lines.push("no workspaces: add one with 'tasker mcp workspace add <path>'".into());
    }
    for workspace in workspaces {
        let text = |key: &str| workspace.get(key).and_then(Value::as_str).unwrap_or_default().to_string();
        lines.push(match text("state").as_str() {
            "open" => format!("  open    {}  workspace \"{}\"", text("path"), text("key")),
            "opening" => format!("  opening {}", text("path")),
            _ => format!("  failed  {}  {}", text("path"), text("error")),
        });
    }
    lines.join("\n")
}
