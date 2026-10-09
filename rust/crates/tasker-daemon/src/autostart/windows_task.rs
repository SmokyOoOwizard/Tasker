//! Windows: задача Планировщика заданий для текущего пользователя (`WindowsTaskService` и `WindowsTaskDefinition` в .NET) — не служба
//! Windows, права администратора не нужны. Триггер — вход пользователя, перезапуск при падении, одна копия, без окна
//! (`conhost.exe --headless`). Управление — `schtasks.exe` с XML-определением (UTF-16 LE с меткой порядка байтов). Определение лежит
//! в `%LOCALAPPDATA%\Tasker\service\<имя>.xml`: оно же признак «автозапуск включён». `TASKER_SERVICE_DIR` и `TASKER_SERVICE_LABEL`
//! переопределяют каталог и имя задачи.
//!
//! Ветки, которые исполняются только на Windows (`schtasks`, `powershell`, пользователь по умолчанию), написаны по C# и на
//! Windows не проверены; чистые функции (XML, командная строка, разбор состояния) проверены тестами на любой системе.
use super::{
    AutostartContext, NOT_ENABLED, ProcessRunner, Result, SERVICE_DIR_VARIABLE, SERVICE_LABEL_VARIABLE, ServiceError, ServiceManager,
    check, env_override, xml_escape,
};
use std::path::PathBuf;

pub const DEFAULT_TASK_NAME: &str = "Tasker MCP";

/// Перезапуск при падении: каждую минуту (минимум Планировщика), до 999 раз подряд.
pub const RESTART_INTERVAL: &str = "PT1M";
pub const RESTART_COUNT: u32 = 999;

/// Запуск без окна: `conhost.exe --headless` (Windows 10 1809 и новее) создаёт консоль без окна, поэтому окно консоли не мелькает,
/// а процесс остаётся единственным действием задачи: Планировщик видит его завершение, код выхода (перезапуск при падении) и умеет
/// остановить (`schtasks /end`). Без скриптовых обёрток (wscript/VBS отключаемы).
pub const HEADLESS_HOST: &str = r"%SystemRoot%\System32\conhost.exe";

/// Действие задачи (`WindowsTaskDefinition.Action`). Без переменных окружения — `conhost --headless "tasker-mcpd.exe" --detached`;
/// с ними (Планировщик переменных не умеет) — через `cmd /d /s /c "set "K=V" && "tasker-mcpd.exe" --detached"`.
///
/// Ошибка: значение переменной содержит `%` или `"` — cmd подставит или порвёт его.
pub fn action(daemon: &[String], environment: &[(String, String)]) -> Result<(String, String)> {
    let program = daemon.iter().map(|a| quote_argument(a)).collect::<Vec<_>>().join(" ");
    if environment.is_empty() {
        return Ok((HEADLESS_HOST.to_string(), format!("--headless {program}")));
    }

    let mut sets = String::new();
    for (key, value) in environment {
        if value.contains('%') || value.contains('"') || key.chars().any(|c| matches!(c, '%' | '"' | '=' | '&' | ' ')) {
            return Err(ServiceError::service(format!(
                "Autostart on Windows cannot pass {key}: the value must not contain % or \""
            )));
        }
        sets.push_str(&format!("set \"{key}={value}\" && "));
    }
    Ok((
        HEADLESS_HOST.to_string(),
        format!("--headless cmd.exe /d /s /c \"{sets}{program}\""),
    ))
}

/// Аргумент по правилам разбора командной строки Windows (`CommandLineToArgvW`): пробелы, кавычки и обратные косые.
pub fn quote_argument(value: &str) -> String {
    if !value.is_empty() && !value.chars().any(|c| c.is_whitespace() || c == '"') {
        return value.to_string();
    }

    let mut text = String::from("\"");
    let mut slashes = 0;
    for c in value.chars() {
        if c == '\\' {
            slashes += 1;
            continue;
        }
        if c == '"' {
            text.extend(std::iter::repeat_n('\\', slashes * 2 + 1));
            text.push('"');
        } else {
            text.extend(std::iter::repeat_n('\\', slashes));
            text.push(c);
        }
        slashes = 0;
    }
    text.extend(std::iter::repeat_n('\\', slashes * 2));
    text.push('"');
    text
}

/// XML задачи Планировщика (схема 1.2) для `schtasks /create /xml`, строки через CRLF: вход текущего пользователя, без прав
/// администратора, одна копия, работает от батареи и без остановки при простое, перезапуск при падении, без ограничения по времени.
/// `user_id` — `DOMAIN\name`; `working_directory` без кавычек (Планировщик их не разбирает).
pub fn task_xml(name: &str, user_id: &str, command: &str, arguments: &str, working_directory: &str) -> String {
    let e = xml_escape;
    let text = format!(
        r#"<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>{name}: MCP server of Tasker (starts at logon, restarts on failure)</Description>
    <URI>\{name}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure>
      <Interval>{RESTART_INTERVAL}</Interval>
      <Count>{RESTART_COUNT}</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
      <Arguments>{arguments}</Arguments>
      <WorkingDirectory>{working_directory}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"#,
        name = e(name),
        user = e(user_id),
        command = e(command),
        arguments = e(arguments),
        working_directory = e(working_directory),
    );
    text.replace('\n', "\r\n")
}

/// Байты файла определения задачи как пишет `WindowsTaskService.Enable`: UTF-16 LE с меткой порядка байтов `FF FE`.
/// Рабочий каталог — каталог данных Tasker (создаётся при включении); пути демона абсолютные.
pub fn render_windows_task(ctx: &AutostartContext) -> Result<Vec<u8>> {
    let (command, arguments) = action(&ctx.command, &ctx.environment)?;
    let xml = task_xml(&ctx.label, &ctx.user_id, &command, &arguments, &ctx.data_dir.to_string_lossy());
    Ok(utf16_le_with_bom(&xml))
}

fn utf16_le_with_bom(text: &str) -> Vec<u8> {
    let mut bytes = vec![0xFF, 0xFE];
    for unit in text.encode_utf16() {
        bytes.extend_from_slice(&unit.to_le_bytes());
    }
    bytes
}

/// Скрипт PowerShell, который печатает состояние задачи (`Running`, `Ready`, `Disabled`) — имена не переводятся, в отличие от
/// вывода schtasks.
pub fn state_script(name: &str) -> String {
    format!(
        "(Get-ScheduledTask -TaskName '{}' -ErrorAction Stop).State",
        name.replace('\'', "''")
    )
}

/// Задача выполняется: по выводу [`state_script`].
pub fn is_running(output: &str) -> bool {
    output.trim().eq_ignore_ascii_case("Running")
}

/// Имя файла для определения задачи: без символов, недопустимых в имени файла (`Path.GetInvalidFileNameChars()` текущей системы,
/// плюс `\` и `/` всегда).
pub fn windows_task_file_name(name: &str) -> String {
    let mut file = name
        .chars()
        .map(|c| {
            if is_invalid_file_name_char(c) || c == '\\' || c == '/' {
                '_'
            } else {
                c
            }
        })
        .collect::<String>();
    file.push_str(".xml");
    file
}

/// `Path.GetInvalidFileNameChars()`: на Windows — `"<>|:*?\/` и управляющие знаки 0–31; на Unix — `/` и `\0`.
fn is_invalid_file_name_char(c: char) -> bool {
    if cfg!(windows) {
        (c as u32) < 32 || tasker_core::io::INVALID_CHARS.contains(&c)
    } else {
        c == '/' || c == '\0'
    }
}

pub struct WindowsTaskService {
    runner: Box<dyn ProcessRunner>,
    directory: PathBuf,
    task_name: String,
    user_id: String,
    daemon_command: Option<Vec<String>>,
}

impl WindowsTaskService {
    /// `directory`/`task_name`/`user_id` — явные значения (тесты); иначе `TASKER_SERVICE_DIR`/`TASKER_SERVICE_LABEL`, иначе
    /// `%LOCALAPPDATA%\Tasker\service`, `Tasker MCP` и `USERDOMAIN\USERNAME`.
    pub fn new(runner: Box<dyn ProcessRunner>, directory: Option<PathBuf>, task_name: Option<String>, user_id: Option<String>) -> Self {
        let directory = directory
            .or_else(|| env_override(SERVICE_DIR_VARIABLE).map(PathBuf::from))
            .unwrap_or_else(|| local_application_data().join("Tasker").join("service"));
        let task_name = task_name
            .or_else(|| env_override(SERVICE_LABEL_VARIABLE))
            .unwrap_or_else(|| DEFAULT_TASK_NAME.to_string());
        Self {
            runner,
            directory,
            task_name,
            user_id: user_id.unwrap_or_else(default_user_id),
            daemon_command: None,
        }
    }

    /// Команда демона вместо `tasker-mcpd` рядом с текущей программой (тесты).
    pub fn with_daemon_command(mut self, command: Vec<String>) -> Self {
        self.daemon_command = Some(command);
        self
    }

    pub fn task_name(&self) -> &str {
        &self.task_name
    }

    pub fn definition_path(&self) -> PathBuf {
        self.directory.join(windows_task_file_name(&self.task_name))
    }

    /// Байты определения задачи для текущего окружения.
    pub fn definition(&self) -> Result<Vec<u8>> {
        render_windows_task(&AutostartContext::current_with(
            self.daemon_command.clone(),
            &self.task_name,
            &self.user_id,
        )?)
    }

    fn definition_arg(&self) -> String {
        self.definition_path().to_string_lossy().into_owned()
    }
}

impl ServiceManager for WindowsTaskService {
    fn name(&self) -> &'static str {
        "Task Scheduler"
    }

    fn is_enabled(&self) -> bool {
        self.definition_path().is_file()
    }

    fn enable(&self) -> Result<()> {
        std::fs::create_dir_all(&self.directory)?;
        std::fs::create_dir_all(tasker_core::settings::data_dir())?;
        // schtasks /create /xml читает UTF-16 (с меткой порядка байтов): так работают кириллица и пробелы в путях.
        std::fs::write(self.definition_path(), self.definition()?)?;

        check(
            &self.runner.run(
                "schtasks",
                &["/create", "/tn", &self.task_name, "/xml", &self.definition_arg(), "/f"],
            ),
            "schtasks /create",
        )?;
        // Триггер срабатывает только при следующем входе: запускаем сейчас (старую копию, если была, останавливаем).
        self.runner.run("schtasks", &["/end", "/tn", &self.task_name]);
        check(&self.runner.run("schtasks", &["/run", "/tn", &self.task_name]), "schtasks /run")
    }

    fn disable(&self) -> Result<()> {
        self.runner.run("schtasks", &["/end", "/tn", &self.task_name]);
        self.runner.run("schtasks", &["/delete", "/tn", &self.task_name, "/f"]); // нет задачи — не ошибка
        let path = self.definition_path();
        if path.is_file() {
            std::fs::remove_file(path)?;
        }
        Ok(())
    }

    fn start(&self) -> Result<()> {
        if !self.is_enabled() {
            return Err(ServiceError::service(NOT_ENABLED));
        }
        check(&self.runner.run("schtasks", &["/run", "/tn", &self.task_name]), "schtasks /run")
    }

    fn stop(&self) -> Result<()> {
        let result = self.runner.run("schtasks", &["/end", "/tn", &self.task_name]);
        // «Задача не выполняется» — уже остановлена.
        if !result.success() && self.is_loaded() {
            check(&result, "schtasks /end")?;
        }
        Ok(())
    }

    fn is_loaded(&self) -> bool {
        let result = self.runner.run(
            "powershell",
            &["-NoProfile", "-NonInteractive", "-Command", &state_script(&self.task_name)],
        );
        result.success() && is_running(&result.output)
    }
}

/// `Environment.GetFolderPath(LocalApplicationData, DoNotVerify)`.
fn local_application_data() -> PathBuf {
    if cfg!(windows) {
        PathBuf::from(std::env::var("LOCALAPPDATA").unwrap_or_default())
    } else if cfg!(target_os = "macos") {
        PathBuf::from(tasker_core::settings::home_dir())
            .join("Library")
            .join("Application Support")
    } else {
        match std::env::var("XDG_DATA_HOME") {
            Ok(xdg) if xdg.starts_with('/') => PathBuf::from(xdg),
            _ => PathBuf::from(tasker_core::settings::home_dir()).join(".local").join("share"),
        }
    }
}

/// `$"{Environment.UserDomainName}\\{Environment.UserName}"` (на Windows не проверено).
fn default_user_id() -> String {
    let domain = if cfg!(windows) {
        std::env::var("USERDOMAIN").unwrap_or_default()
    } else {
        std::env::var("HOSTNAME").unwrap_or_default()
    };
    format!("{domain}\\{}", tasker_core::settings::os_user_name())
}

#[cfg(test)]
mod tests {
    use super::super::test_support::{EnvGuard, FakeRunner, daemon_stub, runner, temp_dir};
    use super::*;
    use crate::autostart::ProcessResult;
    use std::rc::Rc;

    fn service(dir: &std::path::Path, r: &Rc<FakeRunner>) -> WindowsTaskService {
        WindowsTaskService::new(
            Box::new(Rc::clone(r)),
            Some(dir.join("service")),
            Some("Tasker MCP Test".to_string()),
            Some(r"PC\user".to_string()),
        )
        .with_daemon_command(daemon_stub(dir))
    }

    fn utf16(bytes: &[u8]) -> String {
        assert_eq!(&bytes[..2], &[0xFF, 0xFE]);
        let units = bytes[2..].chunks(2).map(|c| u16::from_le_bytes([c[0], c[1]])).collect::<Vec<_>>();
        String::from_utf16(&units).unwrap()
    }

    /// Эталон из `WindowsTaskServiceTests.Task_xml_matches_the_golden_sample` (.NET).
    #[test]
    fn task_xml_matches_the_dotnet_golden_sample() {
        let xml = task_xml(
            "Tasker MCP",
            r"PC\Артём",
            r"%SystemRoot%\System32\conhost.exe",
            "--headless \"C:\\Users\\Артём Иванов\\tasker\\tasker-mcpd.exe\" --detached",
            r"C:\Users\Артём Иванов\AppData\Local\Tasker",
        );
        let expected = r#"<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Tasker MCP: MCP server of Tasker (starts at logon, restarts on failure)</Description>
    <URI>\Tasker MCP</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>PC\Артём</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>PC\Артём</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>999</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>%SystemRoot%\System32\conhost.exe</Command>
      <Arguments>--headless &quot;C:\Users\Артём Иванов\tasker\tasker-mcpd.exe&quot; --detached</Arguments>
      <WorkingDirectory>C:\Users\Артём Иванов\AppData\Local\Tasker</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"#;
        assert_eq!(xml, expected.replace('\n', "\r\n"));
    }

    #[test]
    fn task_xml_escapes_special_characters() {
        let xml = task_xml("A&B <c>", r"PC\u&", "cmd", "a \"b\" & <c>", r"C:\x&y");
        assert!(xml.contains("<Arguments>a &quot;b&quot; &amp; &lt;c&gt;</Arguments>"));
        assert!(xml.contains(r"<WorkingDirectory>C:\x&amp;y</WorkingDirectory>"));
        assert!(xml.contains(r"<URI>\A&amp;B &lt;c&gt;</URI>"));
    }

    #[test]
    fn arguments_are_quoted_by_windows_rules() {
        assert_eq!(quote_argument("plain"), "plain");
        assert_eq!(quote_argument(""), "\"\"");
        assert_eq!(quote_argument(r"C:\Program Files\x.exe"), "\"C:\\Program Files\\x.exe\"");
        assert_eq!(quote_argument("C:\\Артём\\x y\\"), "\"C:\\Артём\\x y\\\\\"");
        assert_eq!(quote_argument("say \"hi\""), "\"say \\\"hi\\\"\"");
    }

    #[test]
    fn action_runs_the_daemon_headless_or_through_cmd_with_environment() {
        let daemon = vec![r"C:\Tasker App\tasker-mcpd.exe".to_string(), "--detached".to_string()];
        let (command, arguments) = action(&daemon, &[]).unwrap();
        assert_eq!(command, r"%SystemRoot%\System32\conhost.exe");
        assert_eq!(arguments, "--headless \"C:\\Tasker App\\tasker-mcpd.exe\" --detached");

        let env = vec![("TASKER_HOME".to_string(), r"C:\Данные & тест".to_string())];
        let (_, arguments) = action(&daemon, &env).unwrap();
        assert_eq!(
            arguments,
            "--headless cmd.exe /d /s /c \"set \"TASKER_HOME=C:\\Данные & тест\" && \"C:\\Tasker App\\tasker-mcpd.exe\" --detached\""
        );

        for bad in ["C:\\%TEMP%", "C:\\a\"b"] {
            let e = action(&["x.exe".to_string()], &[("TASKER_HOME".to_string(), bad.to_string())]).unwrap_err();
            assert_eq!(
                e.message(),
                "Autostart on Windows cannot pass TASKER_HOME: the value must not contain % or \""
            );
        }
    }

    #[test]
    fn state_is_read_from_powershell_output_and_script_escapes_quotes() {
        assert!(is_running("Running\r\n"));
        assert!(is_running("running"));
        assert!(!is_running("Ready\r\n"));
        assert!(!is_running("Disabled"));
        assert!(!is_running(""));
        assert_eq!(
            state_script("It's"),
            "(Get-ScheduledTask -TaskName 'It''s' -ErrorAction Stop).State"
        );
    }

    #[test]
    fn definition_file_name_is_safe() {
        assert_eq!(windows_task_file_name("Tasker MCP"), "Tasker MCP.xml");
        assert_eq!(windows_task_file_name(r"a\b/c"), "a_b_c.xml");
    }

    #[test]
    fn enable_writes_utf16_definition_creates_and_runs_the_task() {
        let dir = temp_dir();
        let home = dir.join("home");
        let _env = EnvGuard::set("TASKER_HOME", home.to_str().unwrap());
        let r = runner();
        let s = service(&dir, &r);
        s.enable().unwrap();
        assert!(s.is_enabled());
        assert!(home.is_dir());
        let bytes = std::fs::read(s.definition_path()).unwrap();
        let text = utf16(&bytes);
        assert!(text.contains("encoding=\"UTF-16\""));
        assert!(text.contains(&format!("set &quot;TASKER_HOME={}&quot;", home.display())));
        assert!(text.contains("<UserId>PC\\user</UserId>"));
        assert_eq!(
            r.calls(),
            vec![
                format!("schtasks /create /tn Tasker MCP Test /xml {} /f", s.definition_path().display()),
                "schtasks /end /tn Tasker MCP Test".to_string(),
                "schtasks /run /tn Tasker MCP Test".to_string(),
            ]
        );
        s.enable().unwrap();
        assert_eq!(bytes, std::fs::read(s.definition_path()).unwrap());
    }

    #[test]
    fn enable_reports_a_failing_schtasks() {
        let dir = temp_dir();
        let _env = EnvGuard::set("TASKER_HOME", dir.join("home").to_str().unwrap());
        let r = runner();
        r.set("schtasks /create", ProcessResult::new(1, "", "ERROR: Access is denied."));
        let e = service(&dir, &r).enable().unwrap_err();
        assert_eq!(e.message(), "schtasks /create failed (1): ERROR: Access is denied.");
    }

    #[test]
    fn disable_ends_deletes_the_task_and_removes_the_definition() {
        let dir = temp_dir();
        let _env = EnvGuard::set("TASKER_HOME", dir.join("home").to_str().unwrap());
        let r = runner();
        let s = service(&dir, &r);
        s.enable().unwrap();
        r.clear();
        s.disable().unwrap();
        assert!(!s.is_enabled());
        assert_eq!(
            r.calls(),
            vec!["schtasks /end /tn Tasker MCP Test", "schtasks /delete /tn Tasker MCP Test /f"]
        );

        r.set(
            "schtasks",
            ProcessResult::new(1, "", "ERROR: The system cannot find the file specified."),
        );
        s.disable().unwrap();
        assert!(!s.is_enabled());
    }

    #[test]
    fn start_runs_the_task_and_stop_ends_it_keeping_autostart() {
        let dir = temp_dir();
        let _env = EnvGuard::set("TASKER_HOME", dir.join("home").to_str().unwrap());
        let r = runner();
        let s = service(&dir, &r);
        s.enable().unwrap();
        r.clear();
        s.start().unwrap();
        s.stop().unwrap();
        assert!(s.is_enabled());
        assert_eq!(
            r.calls(),
            vec!["schtasks /run /tn Tasker MCP Test", "schtasks /end /tn Tasker MCP Test"]
        );
    }

    #[test]
    fn stop_of_a_stopped_task_is_not_an_error_but_a_running_one_reports() {
        let dir = temp_dir();
        let r = runner();
        let s = service(&dir, &r);
        r.set(
            "schtasks /end",
            ProcessResult::new(1, "", "ERROR: There is no running instance of the task."),
        );
        r.set("powershell", ProcessResult::new(0, "Ready\r\n", ""));
        s.stop().unwrap();

        r.set("schtasks /end", ProcessResult::new(1, "", "ERROR: Access is denied."));
        r.set("powershell", ProcessResult::new(0, "Running\r\n", ""));
        assert_eq!(
            s.stop().unwrap_err().message(),
            "schtasks /end failed (1): ERROR: Access is denied."
        );
    }

    #[test]
    fn start_without_autostart_is_an_error_and_is_loaded_follows_the_task_state() {
        let dir = temp_dir();
        let r = runner();
        let s = service(&dir, &r);
        assert_eq!(s.start().unwrap_err().message(), NOT_ENABLED);
        assert!(r.calls().is_empty());

        r.set("powershell", ProcessResult::new(0, "Running\r\n", ""));
        assert!(s.is_loaded());
        r.set("powershell", ProcessResult::new(0, "Ready\r\n", ""));
        assert!(!s.is_loaded());
        r.set(
            "powershell",
            ProcessResult::new(1, "", "Get-ScheduledTask : No MSFT_ScheduledTask objects found"),
        );
        assert!(!s.is_loaded());
        assert_eq!(
            r.calls()[0],
            "powershell -NoProfile -NonInteractive -Command (Get-ScheduledTask -TaskName 'Tasker MCP Test' -ErrorAction Stop).State"
        );
    }

    #[test]
    fn default_name_and_directory_follow_the_overrides() {
        let s = {
            let _d = EnvGuard::set("TASKER_SERVICE_DIR", "/tmp/svc");
            let _l = EnvGuard::set("TASKER_SERVICE_LABEL", "Tasker MCP Custom");
            WindowsTaskService::new(Box::new(FakeRunner::default()), None, None, None)
        };
        assert_eq!(s.task_name(), "Tasker MCP Custom");
        assert_eq!(s.definition_path(), PathBuf::from("/tmp/svc/Tasker MCP Custom.xml"));
        let _d = EnvGuard::remove("TASKER_SERVICE_DIR");
        let _l = EnvGuard::remove("TASKER_SERVICE_LABEL");
        let d = WindowsTaskService::new(Box::new(FakeRunner::default()), None, None, None);
        assert!(
            d.definition_path().ends_with("Tasker/service/Tasker MCP.xml"),
            "{}",
            d.definition_path().display()
        );
    }
}
