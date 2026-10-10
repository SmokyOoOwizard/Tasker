//! Автозапуск демона через службу системы (`Tasker.Daemon.Services` в .NET): launchd на macOS, пользовательский systemd на Linux,
//! Планировщик заданий на Windows. Описания служб генерируются дословно как у .NET-версии (`autostart enable` на уже стоящей
//! службе даёт байт-идентичный файл), команды системы вызываются через [`ProcessRunner`] — тесты подменяют его и проверяют аргументы.
//!
//! Чистые генераторы: [`render_launchd`], [`render_systemd`], [`render_windows_task`] на [`AutostartContext`]; службы —
//! [`LaunchdService`], [`SystemdService`], [`WindowsTaskService`] за трейтом [`ServiceManager`]; [`create`] выбирает службу для текущей
//! системы.

mod launchd;
mod systemd;
mod windows_task;

pub use launchd::{LaunchdService, render_launchd};
pub use systemd::{SystemdService, render_systemd};
pub use windows_task::{WindowsTaskService, render_windows_task, windows_task_file_name};

use std::fmt;
use std::path::{Path, PathBuf};

/// Переопределяет каталог описания службы (`~/Library/LaunchAgents`, `~/.config/systemd/user`, `%LOCALAPPDATA%\Tasker\service`).
pub const SERVICE_DIR_VARIABLE: &str = "TASKER_SERVICE_DIR";

/// Переопределяет имя службы (label launchd, unit systemd, имя задачи Планировщика).
pub const SERVICE_LABEL_VARIABLE: &str = "TASKER_SERVICE_LABEL";

/// Имя программы демона (`tasker-mcpd`) — она лежит рядом с `tasker`.
pub const DAEMON_NAME: &str = "tasker-mcpd";

/// Результат системной команды (`ProcessResult` в .NET).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProcessResult {
    pub exit_code: i32,
    pub output: String,
    pub error: String,
}

impl ProcessResult {
    pub fn new(exit_code: i32, output: impl Into<String>, error: impl Into<String>) -> Self {
        Self {
            exit_code,
            output: output.into(),
            error: error.into(),
        }
    }

    pub fn ok() -> Self {
        Self::new(0, "", "")
    }

    pub fn success(&self) -> bool {
        self.exit_code == 0
    }
}

/// Запуск системных команд (launchctl, systemctl, schtasks, powershell). Трейт — чтобы проверять команды без настоящей системы.
pub trait ProcessRunner {
    fn run(&self, file: &str, arguments: &[&str]) -> ProcessResult;
}

/// Общий исполнитель (тесты держат его и у службы, и у себя).
impl<T: ProcessRunner> ProcessRunner for std::rc::Rc<T> {
    fn run(&self, file: &str, arguments: &[&str]) -> ProcessResult {
        (**self).run(file, arguments)
    }
}

/// Настоящий запуск: stdout и stderr читаются целиком; программа не найдена — код 127 и `<file>: <ошибка>`, как у .NET
/// (`Win32Exception`).
#[derive(Debug, Default, Clone, Copy)]
pub struct SystemProcessRunner;

impl ProcessRunner for SystemProcessRunner {
    fn run(&self, file: &str, arguments: &[&str]) -> ProcessResult {
        match std::process::Command::new(file).args(arguments).output() {
            Ok(out) => ProcessResult::new(
                out.status.code().unwrap_or(-1),
                String::from_utf8_lossy(&out.stdout).into_owned(),
                String::from_utf8_lossy(&out.stderr).into_owned(),
            ),
            Err(e) => ProcessResult::new(127, "", format!("{file}: {}", os_message(&e))),
        }
    }
}

/// Текст ошибки ОС без суффикса ` (os error N)` — так его показывает `Win32Exception.Message` в .NET.
fn os_message(e: &std::io::Error) -> String {
    let text = e.to_string();
    match text.rfind(" (os error ") {
        Some(i) => text[..i].to_string(),
        None => text,
    }
}

/// Ошибки автозапуска: `ServiceException`, `DaemonStartException` (демона рядом нет) и ошибки файловой системы .NET.
#[derive(Debug)]
pub enum ServiceError {
    /// `ServiceException`: команда системы не удалась, автозапуск не включён, система не поддерживается.
    Service(String),
    /// `DaemonStartException`: программа `tasker-mcpd` не установлена рядом с `tasker`.
    DaemonNotInstalled(String),
    Io(std::io::Error),
}

impl ServiceError {
    pub fn service(message: impl Into<String>) -> Self {
        Self::Service(message.into())
    }

    pub fn message(&self) -> String {
        match self {
            Self::Service(m) | Self::DaemonNotInstalled(m) => m.clone(),
            Self::Io(e) => e.to_string(),
        }
    }
}

impl fmt::Display for ServiceError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message())
    }
}

impl std::error::Error for ServiceError {}

impl From<std::io::Error> for ServiceError {
    fn from(e: std::io::Error) -> Self {
        Self::Io(e)
    }
}

pub type Result<T> = std::result::Result<T, ServiceError>;

/// Служба, которая запускает и поддерживает демон (`IServiceManager`).
pub trait ServiceManager {
    /// `launchd`, `systemd`, `Task Scheduler`, `none`.
    fn name(&self) -> &'static str;

    /// Автозапуск включён: описание службы установлено (файл существует).
    fn is_enabled(&self) -> bool;

    /// Установить описание службы и запустить её; при входе в систему она стартует сама и перезапускается при падении.
    fn enable(&self) -> Result<()>;

    /// Остановить службу и убрать описание.
    fn disable(&self) -> Result<()>;

    /// Запустить службу сейчас (автозапуск при этом остаётся включённым).
    fn start(&self) -> Result<()>;

    /// Остановить службу до следующего входа в систему; автозапуск остаётся включённым.
    fn stop(&self) -> Result<()>;

    /// Служба запущена (загружена в систему).
    fn is_loaded(&self) -> bool;
}

/// Всё, что генераторам нужно от окружения (`ServiceManagers.DaemonCommand()`, `ServiceManagers.Environment()`, `AppDirectories`):
/// команда демона с `--detached`, переменные окружения для службы, каталог данных, имя службы и (Windows) пользователь.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AutostartContext {
    /// Программа демона и аргументы: `[<tasker-mcpd>, "--detached"]`.
    pub command: Vec<String>,
    /// Переменные окружения, которые служба должна получить: `TASKER_HOME`, если он задан.
    pub environment: Vec<(String, String)>,
    /// Каталог данных Tasker (`AppDirectories.Data`): журнал launchd — `<data>/logs/mcp-launchd.log`, рабочий каталог задачи Windows.
    pub data_dir: PathBuf,
    /// Label launchd / имя unit systemd / имя задачи Планировщика.
    pub label: String,
    /// Пользователь задачи Windows в виде `DOMAIN\name`.
    pub user_id: String,
}

impl AutostartContext {
    /// Контекст текущего процесса: демон рядом с `tasker`, `TASKER_HOME` из окружения, каталог данных по правилам `AppDirectories`.
    pub fn current(label: impl Into<String>, user_id: impl Into<String>) -> Result<Self> {
        Self::current_with(None, label, user_id)
    }

    /// То же с заданной командой демона (`None` — `tasker-mcpd` рядом с текущей программой).
    pub fn current_with(daemon_command: Option<Vec<String>>, label: impl Into<String>, user_id: impl Into<String>) -> Result<Self> {
        Ok(Self {
            command: match daemon_command {
                Some(command) => command,
                None => self::daemon_command()?,
            },
            environment: service_environment(),
            data_dir: tasker_core::settings::data_dir(),
            label: label.into(),
            user_id: user_id.into(),
        })
    }
}

/// `ServiceManagers.Environment()`: `TASKER_HOME`, если он переопределён (непустой).
pub fn service_environment() -> Vec<(String, String)> {
    match std::env::var(tasker_core::settings::HOME_VARIABLE) {
        Ok(home) if !home.is_empty() => vec![(tasker_core::settings::HOME_VARIABLE.to_string(), home)],
        _ => Vec::new(),
    }
}

/// `ServiceManagers.DaemonCommand()`: программа `tasker-mcpd` из каталога текущей программы (ссылка `~/.local/bin/tasker`
/// раскрывается до настоящего пути) и `--detached`. Rust-консоль — всегда нативный бинарник, ветки `dotnet tasker.dll` нет.
pub fn daemon_command() -> Result<Vec<String>> {
    let exe = std::env::current_exe().map_err(|_| ServiceError::service("Cannot find the path of the running program"))?;
    let program = daemon_program(&exe)?;
    Ok(vec![program.to_string_lossy().into_owned(), "--detached".to_string()])
}

/// `Launcher.DaemonCommand(processPath, null)` для нативной программы `process_path`.
pub fn daemon_program(process_path: &Path) -> Result<PathBuf> {
    let resolved = resolve_link_target(process_path);
    let directory = resolved.parent().map(Path::to_path_buf).unwrap_or_default();
    let program = directory.join(if cfg!(windows) {
        format!("{DAEMON_NAME}.exe")
    } else {
        DAEMON_NAME.to_string()
    });
    if !program.is_file() {
        return Err(ServiceError::DaemonNotInstalled(format!(
            "The MCP server program ({DAEMON_NAME}) is not installed next to tasker (looked in {}). \
             Reinstall tasker with the MCP server: scripts/install.sh (macOS, Linux) or scripts/install.ps1 (Windows), without --no-daemon",
            directory.display()
        )));
    }
    Ok(program)
}

/// `FileInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path`: цепочка символических ссылок самого файла (не каталогов).
fn resolve_link_target(path: &Path) -> PathBuf {
    let mut current = tasker_core::io::full_path(path);
    for _ in 0..40 {
        let Ok(meta) = std::fs::symlink_metadata(&current) else { break };
        if !meta.file_type().is_symlink() {
            break;
        }
        let Ok(target) = std::fs::read_link(&current) else { break };
        let next = if target.is_absolute() {
            target
        } else {
            current.parent().map(|p| p.join(&target)).unwrap_or(target)
        };
        current = tasker_core::io::full_path(next);
    }
    current
}

/// `SecurityElement.Escape`: `<`, `>`, `"`, `'`, `&`.
pub(crate) fn xml_escape(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    for c in value.chars() {
        match c {
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            '\'' => out.push_str("&apos;"),
            '&' => out.push_str("&amp;"),
            _ => out.push(c),
        }
    }
    out
}

/// `AppEnvironment.Get(name)`: `null` только у незаданной переменной — пустое значение в .NET проходит через `??` как есть, поэтому и
/// здесь возвращается `Some("")`.
pub(crate) fn env_override(name: &str) -> Option<String> {
    std::env::var(name).ok()
}

pub(crate) fn check(result: &ProcessResult, what: &str) -> Result<()> {
    if result.success() {
        return Ok(());
    }
    Err(ServiceError::service(format!(
        "{what} failed ({}): {}",
        result.exit_code,
        format!("{}{}", result.error, result.output).trim()
    )))
}

pub(crate) const NOT_ENABLED: &str = "Autostart is not enabled: run 'tasker mcp autostart enable'";

/// Служба для систем без поддержки автозапуска.
#[derive(Debug, Default)]
pub struct UnsupportedService;

impl UnsupportedService {
    fn unsupported() -> ServiceError {
        ServiceError::service(
            "Autostart is supported on macOS (launchd), Linux (systemd) and Windows (Task Scheduler) only; on this system run 'tasker mcp run' yourself",
        )
    }
}

impl ServiceManager for UnsupportedService {
    fn name(&self) -> &'static str {
        "none"
    }
    fn is_enabled(&self) -> bool {
        false
    }
    fn enable(&self) -> Result<()> {
        Err(Self::unsupported())
    }
    fn disable(&self) -> Result<()> {
        Err(Self::unsupported())
    }
    fn start(&self) -> Result<()> {
        Err(Self::unsupported())
    }
    fn stop(&self) -> Result<()> {
        Err(Self::unsupported())
    }
    fn is_loaded(&self) -> bool {
        false
    }
}

/// `ServiceManagers.Create`: служба текущей системы с каталогом и именем по умолчанию (`TASKER_SERVICE_DIR`, `TASKER_SERVICE_LABEL`).
pub fn create(runner: Box<dyn ProcessRunner>) -> Box<dyn ServiceManager> {
    if cfg!(target_os = "macos") {
        Box::new(LaunchdService::new(runner, None, None))
    } else if cfg!(target_os = "linux") {
        Box::new(SystemdService::new(runner, None, None))
    } else if cfg!(windows) {
        Box::new(WindowsTaskService::new(runner, None, None, None))
    } else {
        Box::new(UnsupportedService)
    }
}

/// Служба текущей системы с настоящим запуском команд.
pub fn create_system() -> Box<dyn ServiceManager> {
    create(Box::new(SystemProcessRunner))
}

#[cfg(test)]
pub(crate) mod test_support {
    use super::{DAEMON_NAME, ProcessResult, ProcessRunner};
    use std::cell::{Cell, RefCell};
    use std::collections::BTreeMap;
    use std::path::{Path, PathBuf};
    use std::rc::Rc;
    use std::sync::{Mutex, MutexGuard};

    /// Поддельный исполнитель как `FakeRunner` в тестах .NET: запоминает строки `file arg arg`, отвечает по префиксу.
    #[derive(Default)]
    pub struct FakeRunner {
        pub calls: RefCell<Vec<String>>,
        pub results: RefCell<BTreeMap<String, ProcessResult>>,
    }

    impl FakeRunner {
        pub fn set(&self, prefix: &str, result: ProcessResult) {
            self.results.borrow_mut().insert(prefix.to_string(), result);
        }

        pub fn calls(&self) -> Vec<String> {
            self.calls.borrow().clone()
        }

        pub fn clear(&self) {
            self.calls.borrow_mut().clear();
        }
    }

    impl ProcessRunner for FakeRunner {
        fn run(&self, file: &str, arguments: &[&str]) -> ProcessResult {
            let line = format!("{file} {}", arguments.join(" "));
            self.calls.borrow_mut().push(line.clone());
            self.results
                .borrow()
                .iter()
                .find(|(prefix, _)| line.starts_with(prefix.as_str()))
                .map(|(_, r)| r.clone())
                .unwrap_or_else(ProcessResult::ok)
        }
    }

    pub fn runner() -> Rc<FakeRunner> {
        Rc::default()
    }

    /// Пустой каталог теста в `target/tmp` (системный временный каталог не трогаем, как и остальные крейты).
    pub fn temp_dir() -> PathBuf {
        let dir = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../target/tmp").join(format!(
            "autostart-{}-{}-{}",
            std::process::id(),
            thread_id(),
            COUNTER.with(|c| {
                *c.borrow_mut() += 1;
                *c.borrow()
            })
        ));
        std::fs::create_dir_all(&dir).unwrap();
        // Без `..` в пути: `AppDirectories.Data` и каталог демона приводятся к полному пути, и тесты сравнивают их с этим каталогом.
        tasker_core::io::full_path(dir)
    }

    fn thread_id() -> String {
        format!("{:?}", std::thread::current().id()).replace(['(', ')'], "")
    }

    /// Команда демона `<dir>/bin/tasker-mcpd --detached` (файл создаётся).
    pub fn daemon_stub(dir: &Path) -> Vec<String> {
        let bin = dir.join("bin");
        std::fs::create_dir_all(&bin).unwrap();
        let program = bin.join(DAEMON_NAME);
        std::fs::write(&program, "").unwrap();
        vec![program.to_string_lossy().into_owned(), "--detached".to_string()]
    }

    thread_local! {
        static COUNTER: RefCell<u64> = const { RefCell::new(0) };
        static ENV_DEPTH: Cell<usize> = const { Cell::new(0) };
    }

    static ENV: Mutex<()> = Mutex::new(());

    /// Переменная окружения процесса на время теста под общим мьютексом (тесты идут параллельно; вложенные guard-ы в одном
    /// потоке не блокируются). Снятие восстанавливает прежнее значение.
    pub struct EnvGuard {
        name: String,
        previous: Option<String>,
        _lock: Option<MutexGuard<'static, ()>>,
    }

    impl EnvGuard {
        pub fn set(name: &str, value: &str) -> Self {
            let mut guard = Self::lock(name);
            guard.previous = std::env::var(name).ok();
            // SAFETY: все тесты крейта, которые меняют окружение, делают это под ENV.
            unsafe { std::env::set_var(name, value) };
            guard
        }

        pub fn remove(name: &str) -> Self {
            let mut guard = Self::lock(name);
            guard.previous = std::env::var(name).ok();
            // SAFETY: см. `set`.
            unsafe { std::env::remove_var(name) };
            guard
        }

        fn lock(name: &str) -> Self {
            let lock = if ENV_DEPTH.with(|d| d.get()) == 0 {
                Some(ENV.lock().unwrap_or_else(|e| e.into_inner()))
            } else {
                None
            };
            ENV_DEPTH.with(|d| d.set(d.get() + 1));
            Self {
                name: name.to_string(),
                previous: None,
                _lock: lock,
            }
        }
    }

    impl Drop for EnvGuard {
        fn drop(&mut self) {
            // SAFETY: под ENV.
            unsafe {
                match &self.previous {
                    Some(v) => std::env::set_var(&self.name, v),
                    None => std::env::remove_var(&self.name),
                }
            }
            ENV_DEPTH.with(|d| d.set(d.get() - 1));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn xml_escape_matches_security_element() {
        assert_eq!(xml_escape("a&b <c> \"d\" 'e'"), "a&amp;b &lt;c&gt; &quot;d&quot; &apos;e&apos;");
    }

    #[test]
    fn check_reports_code_and_trimmed_output() {
        let r = ProcessResult::new(5, "", "Bootstrap failed: 5: Input/output error\n");
        let e = check(&r, "launchctl bootstrap").unwrap_err();
        assert_eq!(
            e.message(),
            "launchctl bootstrap failed (5): Bootstrap failed: 5: Input/output error"
        );
        assert!(check(&ProcessResult::ok(), "x").is_ok());
    }

    #[test]
    fn unsupported_system_says_what_to_do() {
        let s = UnsupportedService;
        assert!(!s.is_enabled());
        assert!(!s.is_loaded());
        assert!(s.enable().unwrap_err().message().contains("tasker mcp run"));
    }

    #[test]
    fn daemon_program_is_next_to_the_resolved_executable() {
        let dir = test_support::temp_dir();
        let bin = dir.join("bin");
        std::fs::create_dir_all(&bin).unwrap();
        std::fs::write(bin.join("tasker"), "").unwrap();
        let missing = daemon_program(&bin.join("tasker")).unwrap_err();
        assert!(matches!(missing, ServiceError::DaemonNotInstalled(_)));
        assert!(missing.message().starts_with(&format!(
            "The MCP server program (tasker-mcpd) is not installed next to tasker (looked in {}). Reinstall tasker",
            bin.display()
        )));

        let daemon = format!("{DAEMON_NAME}{}", std::env::consts::EXE_SUFFIX);
        std::fs::write(bin.join(&daemon), "").unwrap();
        assert_eq!(daemon_program(&bin.join("tasker")).unwrap(), bin.join(&daemon));

        #[cfg(unix)]
        {
            let link_dir = dir.join("local-bin");
            std::fs::create_dir_all(&link_dir).unwrap();
            std::os::unix::fs::symlink("../bin/tasker", link_dir.join("tasker")).unwrap();
            assert_eq!(daemon_program(&link_dir.join("tasker")).unwrap(), bin.join(DAEMON_NAME));
        }
    }

    #[test]
    fn system_runner_reports_a_missing_program_like_dotnet() {
        let r = SystemProcessRunner.run("tasker-no-such-program-xyz", &["a"]);
        assert_eq!(r.exit_code, 127);
        assert!(r.error.starts_with("tasker-no-such-program-xyz: "), "{}", r.error);
        assert!(!r.error.contains("os error"));
    }
}
