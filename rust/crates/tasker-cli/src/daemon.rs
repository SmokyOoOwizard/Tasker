//! Клиент демона MCP (`DaemonFiles`, `DaemonClient`, `DaemonController`, `DaemonHost`, `Launcher` в .NET): те же `daemon.lock` и
//! `daemon.json` в каталоге данных, те же запросы `/daemon/status`, `/daemon/stop`, `/daemon/upgrade` с секретом управления в
//! заголовке `X-Tasker-Control`; демон запускается отсоединённым процессом `tasker-mcpd --detached` из каталога этой программы
//! (`mcp run` — в этом терминале, [`run_foreground`]). Клиент блокирующий (ureq), без tokio. Управляет и .NET-демоном: файлы и API
//! у них общие; демон под супервизором (`workers` в статусе) заменяется на лету ([`Controller::upgrade`]).
use crate::errors::{CliError, Result};
use serde_json::Value;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};
use tasker_core::Timestamp;
use tasker_core::settings::{daemon_dir, logs_dir};
use tasker_daemon::autostart::{ServiceError, ServiceManager};

pub const CONTROL_HEADER: &str = "X-Tasker-Control";

/// Пауза перед единственным повтором запроса, оборвавшегося во время замены процесса демона.
pub const RETRY_PAUSE: Duration = Duration::from_millis(300);

const READY_TIMEOUT: Duration = Duration::from_secs(30);
const STOP_TIMEOUT: Duration = Duration::from_secs(15);
/// Сколько `mcp run` ждёт штатной остановки демона после Ctrl+C (`DaemonHost.StopTimeout`).
const RUN_STOP_TIMEOUT: Duration = Duration::from_secs(20);

/// Сколько ждать, пока новый рабочий процесс откроет области и станет готов, по умолчанию (`DefaultUpgradeTimeoutSeconds`).
pub const DEFAULT_UPGRADE_TIMEOUT_SECONDS: i64 = 60;

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

    /// Демон под супервизором: его можно заменить на лету (`DaemonStatus.Supervised` — есть рабочие процессы).
    pub fn supervised(&self) -> bool {
        !self.workers().is_empty()
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

/// Итог замены от демона (`UpgradeResult`); остальные свойства консоли не нужны.
pub struct UpgradeResult {
    pub ok: bool,
    pub message: String,
}

/// Просит демон заменить рабочий процесс на лету и ждёт итога (`DaemonClient.Upgrade`). Ошибка связи — тоже итог (`ok = false`):
/// старый процесс жив. None — демон не работает.
pub fn request_upgrade(file: &str, arguments: &[String], timeout_seconds: i64) -> Option<UpgradeResult> {
    if !is_running() {
        return None;
    }
    let info = read_info()?;
    // Замена — это запуск нового процесса и открытие областей: дольше обычных запросов.
    let agent = agent(Duration::from_secs(u64::try_from(timeout_seconds + 30).unwrap_or(90)));
    let body = tasker_core::json::to_string(&serde_json::json!({
        "file": file,
        "arguments": arguments,
        "timeoutSeconds": timeout_seconds,
    }));
    let answer = (|| -> std::result::Result<UpgradeResult, String> {
        let mut response = agent
            .post(url(&info, "/daemon/upgrade"))
            .header(CONTROL_HEADER, &info.token)
            .header("Content-Type", "application/json; charset=utf-8")
            .send(body.as_str())
            .map_err(|e| e.to_string())?;
        let status = response.status();
        let text = response.body_mut().read_to_string().map_err(|e| e.to_string())?;
        if !status.is_success() {
            return Ok(UpgradeResult {
                ok: false,
                message: format!("The daemon refused the upgrade: HTTP {} {text}", status.as_u16())
                    .trim()
                    .to_string(),
            });
        }
        let value: Value = serde_json::from_str(&text).map_err(|e| e.to_string())?;
        let get = |name: &str| {
            value
                .as_object()
                .and_then(|o| o.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v))
        };
        Ok(UpgradeResult {
            ok: get("ok").and_then(Value::as_bool).unwrap_or(false),
            message: get("message").and_then(Value::as_str).unwrap_or_default().to_string(),
        })
    })();
    Some(answer.unwrap_or_else(|e| UpgradeResult {
        ok: false,
        message: format!("The daemon did not answer the upgrade request: {e}"),
    }))
}

/// Что сделала `tasker mcp upgrade` (`UpgradeKind`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UpgradeKind {
    /// Демон не работает: заменять нечего.
    NotRunning,
    /// Рабочий процесс заменён на лету, вызовы не прерывались.
    Replaced,
    /// Демон перезапущен (запрошено `--restart` или он запущен по-старому, одним процессом).
    Restarted,
}

impl UpgradeKind {
    pub fn json_name(self) -> &'static str {
        match self {
            UpgradeKind::NotRunning => "notRunning",
            UpgradeKind::Replaced => "replaced",
            UpgradeKind::Restarted => "restarted",
        }
    }
}

pub struct UpgradeOutcome {
    pub kind: UpgradeKind,
    pub message: String,
    pub status: Option<DaemonStatus>,
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

    /// Заменяет работающий демон на новую сборку без простоя (`DaemonController.Upgrade`): рядом поднимается новый рабочий процесс,
    /// он принимает вызовы, когда открыл все области, а старый заканчивает начатые. Демон, запущенный по-старому (одним процессом),
    /// заменить так нельзя — он перезапускается. `Ok(Err(сообщение))` — замена не удалась, старый процесс продолжает работать.
    /// `daemon` — своя программа демона вместо соседней с `tasker`: файл и начальные аргументы.
    pub fn upgrade(
        &self,
        restart: bool,
        timeout_seconds: i64,
        daemon: Option<(String, Vec<String>)>,
    ) -> Result<std::result::Result<UpgradeOutcome, String>> {
        if !is_running() {
            return Ok(Ok(UpgradeOutcome {
                kind: UpgradeKind::NotRunning,
                message: "The MCP server is not running: nothing to upgrade (start it with 'tasker mcp start')".into(),
                status: None,
            }));
        }
        if restart {
            return Ok(Ok(UpgradeOutcome {
                kind: UpgradeKind::Restarted,
                message: "Restarted as requested".into(),
                status: Some(self.restart()?),
            }));
        }
        let Some(status) = get_status() else {
            return Ok(Err(
                "The MCP server does not answer: wait for it to start, or restart it with 'tasker mcp upgrade --restart'".into(),
            ));
        };
        if !status.supervised() {
            return Ok(Ok(UpgradeOutcome {
                kind: UpgradeKind::Restarted,
                message: "The running MCP server was started by an earlier build as one process and cannot be replaced on the fly: restarted it (the next upgrade will be seamless)".into(),
                status: Some(self.restart()?),
            }));
        }

        let (file, arguments) = match daemon {
            Some(daemon) => daemon,
            None => (daemon_program()?.to_string_lossy().into_owned(), Vec::new()),
        };
        let Some(result) = request_upgrade(&file, &arguments, timeout_seconds) else {
            return Ok(Err("The MCP server stopped while it was being upgraded".into()));
        };
        if !result.ok {
            return Ok(Err(result.message));
        }

        // Старый процесс заканчивает начатые вызовы: ждём, пока он уйдёт (но не дольше срока), чтобы статус показал одну сборку.
        let deadline = Instant::now() + Duration::from_secs(u64::try_from(timeout_seconds).unwrap_or(1));
        let mut latest = get_status();
        while let Some(current) = &latest
            && current
                .workers()
                .iter()
                .any(|w| w.get("role").and_then(Value::as_str) != Some("active"))
            && Instant::now() < deadline
        {
            std::thread::sleep(Duration::from_millis(100));
            match get_status() {
                Some(next) => latest = Some(next),
                None => break,
            }
        }
        Ok(Ok(UpgradeOutcome {
            kind: UpgradeKind::Replaced,
            message: result.message,
            status: latest,
        }))
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
        // Как WindowsDetachedProcess (.NET): без окна, в своей группе процессов, по возможности вне задания родителя и без
        // унаследованных дескрипторов — иначе канал вывода вызвавшей программы (`$x = tasker mcp start`, тест с перехваченным
        // выводом) не закрылся бы, пока жив демон. std наследует все наследуемые дескрипторы, поэтому свои stdin/stdout/stderr
        // перед запуском делаются ненаследуемыми (самой консоли они после этого не нужны для детей).
        use std::os::windows::process::CommandExt;
        use windows_sys::Win32::Foundation::{HANDLE_FLAG_INHERIT, SetHandleInformation};
        use windows_sys::Win32::System::Console::{GetStdHandle, STD_ERROR_HANDLE, STD_INPUT_HANDLE, STD_OUTPUT_HANDLE};
        const CREATE_NEW_PROCESS_GROUP: u32 = 0x0000_0200;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        const CREATE_BREAKAWAY_FROM_JOB: u32 = 0x0100_0000;
        const ERROR_ACCESS_DENIED: i32 = 5;
        for which in [STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE] {
            // SAFETY: дескриптор из GetStdHandle принадлежит процессу; меняется только флаг наследования.
            unsafe {
                let handle = GetStdHandle(which);
                if !handle.is_null() && handle as isize != -1 {
                    SetHandleInformation(handle, HANDLE_FLAG_INHERIT, 0);
                }
            }
        }
        let spawn = |flags: u32| {
            std::process::Command::new(&program)
                .arg("--detached")
                .creation_flags(flags)
                .stdin(std::process::Stdio::null())
                .stdout(std::process::Stdio::null())
                .stderr(std::process::Stdio::null())
                .spawn()
        };
        let flags = CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP;
        match spawn(flags | CREATE_BREAKAWAY_FROM_JOB) {
            Err(e) if e.raw_os_error() == Some(ERROR_ACCESS_DENIED) => spawn(flags),
            result => result,
        }
        .map_err(|e| CliError::new(format!("Cannot start the MCP server process: {e}")))
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

/// `tasker mcp run` (`DaemonHost.Run`): запускает программу демона в этом терминале и ждёт её. Ввод и вывод — общие, код выхода —
/// её. Остановка (Ctrl+C, SIGTERM) передаётся демону, чтобы он закрыл области штатно; не вышел за [`RUN_STOP_TIMEOUT`] — снимается.
pub fn run_foreground(detached: bool) -> Result<i32> {
    let program = daemon_program()?;
    let interrupted = Arc::new(AtomicBool::new(false));
    // Ctrl+C и SIGTERM не завершают консоль: она дожидается демона (как отмена команды в System.CommandLine).
    for signal in [signal_hook::consts::SIGINT, signal_hook::consts::SIGTERM] {
        let _ = signal_hook::flag::register(signal, interrupted.clone());
    }
    let mut command = std::process::Command::new(&program);
    if detached {
        command.arg("--detached");
    }
    let mut child = command
        .spawn()
        .map_err(|e| CliError::new(format!("Cannot start the MCP server program: {e}")))?;
    loop {
        if let Some(status) = child.try_wait()? {
            return Ok(process_exit_code(status));
        }
        if interrupted.load(Ordering::SeqCst) {
            break;
        }
        std::thread::sleep(Duration::from_millis(50));
    }

    // Штатно: SIGTERM (Windows — запрос /daemon/stop); демон закрывает области и убирает свои файлы. Не успел — снимаем.
    #[cfg(unix)]
    {
        if let Some(pid) = rustix::process::Pid::from_raw(child.id() as i32) {
            let _ = rustix::process::kill_process(pid, rustix::process::Signal::TERM);
        }
    }
    #[cfg(not(unix))]
    {
        request_stop();
    }
    let deadline = Instant::now() + RUN_STOP_TIMEOUT;
    loop {
        if let Some(status) = child.try_wait()? {
            return Ok(process_exit_code(status));
        }
        if Instant::now() >= deadline {
            #[cfg(windows)]
            kill(i64::from(child.id()));
            let _ = child.kill();
            return Ok(process_exit_code(child.wait()?));
        }
        std::thread::sleep(Duration::from_millis(50));
    }
}

/// `Process.ExitCode`: код выхода или 128 + сигнал.
fn process_exit_code(status: std::process::ExitStatus) -> i32 {
    #[cfg(unix)]
    {
        use std::os::unix::process::ExitStatusExt as _;
        if let Some(signal) = status.signal() {
            return 128 + signal;
        }
    }
    status.code().unwrap_or(1)
}

/// `--daemon <path>` команды `upgrade`: путь к `.dll` — запуск через `dotnet` (у .NET-консоли — её же `dotnet`, здесь — найденный в
/// `PATH`), иначе — сама программа.
pub fn daemon_override(path: &str) -> (String, Vec<String>) {
    if path.to_ascii_lowercase().ends_with(".dll") {
        (find_in_path("dotnet").unwrap_or_else(|| "dotnet".into()), vec![path.to_string()])
    } else {
        (path.to_string(), Vec::new())
    }
}

fn find_in_path(name: &str) -> Option<String> {
    let paths = std::env::var_os("PATH")?;
    std::env::split_paths(&paths)
        .flat_map(|dir| {
            let plain = dir.join(name);
            let exe = dir.join(format!("{name}.exe"));
            [plain, exe]
        })
        .find(|candidate| candidate.is_file())
        .map(|p| p.to_string_lossy().into_owned())
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
