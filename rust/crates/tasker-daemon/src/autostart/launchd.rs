//! macOS: агент запуска пользователя `~/Library/LaunchAgents/com.tasker.mcp.plist` (`LaunchdService` в .NET). RunAtLoad — стартует при
//! входе в систему, KeepAlive — launchd перезапускает демон, если он упал. `TASKER_SERVICE_DIR` и `TASKER_SERVICE_LABEL`
//! переопределяют каталог и имя (тесты, вторая установка).
use super::{
    AutostartContext, NOT_ENABLED, ProcessRunner, Result, SERVICE_DIR_VARIABLE, SERVICE_LABEL_VARIABLE, ServiceError, ServiceManager,
    check, env_override, xml_escape,
};
use std::path::PathBuf;

pub const DEFAULT_LABEL: &str = "com.tasker.mcp";

/// Текст plist дословно как `LaunchdService.Plist()`: строки через `\n`, отступы 4 пробела, `<key>…</key><string>…</string>` в одну строку.
pub fn render_launchd(ctx: &AutostartContext) -> String {
    let mut text = String::new();
    text.push_str("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
    text.push_str("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
    text.push_str("<plist version=\"1.0\">\n");
    text.push_str("<dict>\n");
    text.push_str(&format!("    <key>Label</key><string>{}</string>\n", xml_escape(&ctx.label)));
    text.push_str("    <key>ProgramArguments</key>\n");
    text.push_str("    <array>\n");
    for argument in &ctx.command {
        text.push_str(&format!("        <string>{}</string>\n", xml_escape(argument)));
    }
    text.push_str("    </array>\n");

    if !ctx.environment.is_empty() {
        text.push_str("    <key>EnvironmentVariables</key>\n");
        text.push_str("    <dict>\n");
        for (key, value) in &ctx.environment {
            text.push_str(&format!(
                "        <key>{}</key><string>{}</string>\n",
                xml_escape(key),
                xml_escape(value)
            ));
        }
        text.push_str("    </dict>\n");
    }

    let log = ctx.data_dir.join("logs").join("mcp-launchd.log");
    let log = xml_escape(&log.to_string_lossy());
    text.push_str("    <key>RunAtLoad</key><true/>\n");
    text.push_str("    <key>KeepAlive</key><true/>\n");
    text.push_str("    <key>ProcessType</key><string>Background</string>\n");
    text.push_str(&format!("    <key>StandardOutPath</key><string>{log}</string>\n"));
    text.push_str(&format!("    <key>StandardErrorPath</key><string>{log}</string>\n"));
    text.push_str("</dict>\n");
    text.push_str("</plist>\n");
    text
}

pub struct LaunchdService {
    runner: Box<dyn ProcessRunner>,
    directory: PathBuf,
    label: String,
    uid: u32,
    daemon_command: Option<Vec<String>>,
}

impl LaunchdService {
    /// `directory`/`label` — явные значения (тесты); иначе `TASKER_SERVICE_DIR`/`TASKER_SERVICE_LABEL`, иначе
    /// `~/Library/LaunchAgents` и `com.tasker.mcp`.
    pub fn new(runner: Box<dyn ProcessRunner>, directory: Option<PathBuf>, label: Option<String>) -> Self {
        let directory = directory
            .or_else(|| env_override(SERVICE_DIR_VARIABLE).map(PathBuf::from))
            .unwrap_or_else(|| {
                PathBuf::from(tasker_core::settings::home_dir())
                    .join("Library")
                    .join("LaunchAgents")
            });
        let label = label
            .or_else(|| env_override(SERVICE_LABEL_VARIABLE))
            .unwrap_or_else(|| DEFAULT_LABEL.to_string());
        Self {
            runner,
            directory,
            label,
            uid: current_uid(),
            daemon_command: None,
        }
    }

    /// Пользователь домена `gui/<uid>` (по умолчанию — текущий).
    pub fn with_uid(mut self, uid: u32) -> Self {
        self.uid = uid;
        self
    }

    /// Команда демона вместо `tasker-mcpd` рядом с текущей программой (тесты).
    pub fn with_daemon_command(mut self, command: Vec<String>) -> Self {
        self.daemon_command = Some(command);
        self
    }

    pub fn label(&self) -> &str {
        &self.label
    }

    pub fn plist_path(&self) -> PathBuf {
        self.directory.join(format!("{}.plist", self.label))
    }

    fn domain(&self) -> String {
        format!("gui/{}", self.uid)
    }

    fn target(&self) -> String {
        format!("{}/{}", self.domain(), self.label)
    }

    /// Текст plist для текущего окружения.
    pub fn plist(&self) -> Result<String> {
        let mut ctx = AutostartContext::current_with(self.daemon_command.clone(), &self.label, "")?;
        ctx.label = self.label.clone();
        Ok(render_launchd(&ctx))
    }

    fn plist_arg(&self) -> String {
        self.plist_path().to_string_lossy().into_owned()
    }
}

impl ServiceManager for LaunchdService {
    fn name(&self) -> &'static str {
        "launchd"
    }

    fn is_enabled(&self) -> bool {
        self.plist_path().is_file()
    }

    fn enable(&self) -> Result<()> {
        std::fs::create_dir_all(&self.directory)?;
        std::fs::create_dir_all(tasker_core::settings::logs_dir())?;
        std::fs::write(self.plist_path(), self.plist()?)?;

        // Уже загружена старая версия описания — перезагружаем.
        self.runner.run("launchctl", &["bootout", &self.target()]);
        check(
            &self.runner.run("launchctl", &["bootstrap", &self.domain(), &self.plist_arg()]),
            "launchctl bootstrap",
        )
    }

    fn disable(&self) -> Result<()> {
        self.runner.run("launchctl", &["bootout", &self.target()]);
        let path = self.plist_path();
        if path.is_file() {
            std::fs::remove_file(path)?;
        }
        Ok(())
    }

    fn start(&self) -> Result<()> {
        if !self.is_enabled() {
            return Err(ServiceError::service(NOT_ENABLED));
        }
        if self.is_loaded() {
            check(&self.runner.run("launchctl", &["kickstart", &self.target()]), "launchctl kickstart")
        } else {
            check(
                &self.runner.run("launchctl", &["bootstrap", &self.domain(), &self.plist_arg()]),
                "launchctl bootstrap",
            )
        }
    }

    // bootout выгружает службу (демон получает SIGTERM), описание остаётся: при следующем входе она загрузится снова.
    fn stop(&self) -> Result<()> {
        self.runner.run("launchctl", &["bootout", &self.target()]);
        Ok(())
    }

    fn is_loaded(&self) -> bool {
        self.runner.run("launchctl", &["print", &self.target()]).success()
    }
}

#[cfg(unix)]
fn current_uid() -> u32 {
    rustix::process::getuid().as_raw()
}

#[cfg(not(unix))]
fn current_uid() -> u32 {
    0
}

#[cfg(test)]
mod tests {
    use super::super::test_support::{EnvGuard, FakeRunner, daemon_stub, runner, temp_dir};
    use super::*;
    use crate::autostart::ProcessResult;
    use std::rc::Rc;

    fn service(dir: &std::path::Path, r: &Rc<FakeRunner>) -> LaunchdService {
        LaunchdService::new(
            Box::new(Rc::clone(r)),
            Some(dir.join("service")),
            Some("com.tasker.test".to_string()),
        )
        .with_uid(501)
        .with_daemon_command(daemon_stub(dir))
    }

    #[test]
    #[cfg_attr(windows, ignore = "launchd is macOS only: the plist is checked with Unix paths")]
    fn plist_runs_the_daemon_at_login_and_keeps_it_alive() {
        let dir = temp_dir();
        let home = dir.join("home");
        let _env = EnvGuard::set("TASKER_HOME", home.to_str().unwrap());
        let r = runner();
        let s = service(&dir, &r);
        s.enable().unwrap();
        let text = std::fs::read_to_string(s.plist_path()).unwrap();
        assert!(text.contains("<key>Label</key><string>com.tasker.test</string>"));
        assert!(text.contains("<key>RunAtLoad</key><true/>"));
        assert!(text.contains("<key>KeepAlive</key><true/>"));
        assert!(text.contains("        <string>--detached</string>\n    </array>"));
        assert!(text.contains("tasker-mcpd"));
        assert!(text.contains(&format!(
            "<key>StandardOutPath</key><string>{}</string>",
            home.join("logs/mcp-launchd.log").display()
        )));
        assert!(text.contains(&format!("<key>TASKER_HOME</key><string>{}</string>", home.display())));
        assert!(home.join("logs").is_dir());
    }

    #[test]
    #[cfg_attr(windows, ignore = "launchd is macOS only: the plist is checked with Unix paths")]
    fn plist_escapes_special_characters() {
        let dir = temp_dir();
        let special = dir.join("a&b <c>");
        let _env = EnvGuard::set("TASKER_HOME", special.to_str().unwrap());
        let r = runner();
        let s = service(&dir, &r);
        s.enable().unwrap();
        let text = std::fs::read_to_string(s.plist_path()).unwrap();
        let expected = format!("{}/a&amp;b &lt;c&gt;", dir.display());
        assert!(
            text.contains(&format!("        <key>TASKER_HOME</key><string>{expected}</string>\n")),
            "{text}"
        );
        assert!(text.contains(&format!(
            "<key>StandardErrorPath</key><string>{expected}/logs/mcp-launchd.log</string>\n"
        )));
    }

    #[test]
    fn enable_reloads_the_service_for_the_current_user() {
        let dir = temp_dir();
        let _env = EnvGuard::set("TASKER_HOME", dir.join("home").to_str().unwrap());
        let r = runner();
        let s = service(&dir, &r);
        s.enable().unwrap();
        assert!(s.is_enabled());
        assert_eq!(
            r.calls(),
            vec![
                "launchctl bootout gui/501/com.tasker.test".to_string(),
                format!("launchctl bootstrap gui/501 {}", s.plist_path().display()),
            ]
        );
    }

    #[test]
    fn enable_is_idempotent_and_reports_a_failing_launchctl() {
        let dir = temp_dir();
        let _env = EnvGuard::set("TASKER_HOME", dir.join("home").to_str().unwrap());
        let r = runner();
        let s = service(&dir, &r);
        s.enable().unwrap();
        let first = std::fs::read(s.plist_path()).unwrap();
        s.enable().unwrap();
        assert_eq!(first, std::fs::read(s.plist_path()).unwrap());

        r.set(
            "launchctl bootstrap",
            ProcessResult::new(5, "", "Bootstrap failed: 5: Input/output error"),
        );
        let e = s.enable().unwrap_err();
        assert_eq!(
            e.message(),
            "launchctl bootstrap failed (5): Bootstrap failed: 5: Input/output error"
        );
    }

    #[test]
    fn disable_unloads_and_removes_the_plist_and_is_harmless_without_it() {
        let dir = temp_dir();
        let _env = EnvGuard::set("TASKER_HOME", dir.join("home").to_str().unwrap());
        let r = runner();
        let s = service(&dir, &r);
        s.enable().unwrap();
        r.clear();
        s.disable().unwrap();
        assert!(!s.is_enabled());
        assert_eq!(r.calls(), vec!["launchctl bootout gui/501/com.tasker.test".to_string()]);
        s.disable().unwrap();
        assert!(!s.is_enabled());
    }

    #[test]
    fn start_loads_the_service_or_kicks_a_loaded_one() {
        let dir = temp_dir();
        let _env = EnvGuard::set("TASKER_HOME", dir.join("home").to_str().unwrap());
        let r = runner();
        let s = service(&dir, &r);
        s.enable().unwrap();

        r.clear();
        r.set("launchctl print", ProcessResult::new(113, "", "Could not find service"));
        s.start().unwrap();
        assert!(r.calls().iter().any(|c| c.starts_with("launchctl bootstrap")));

        r.clear();
        r.set("launchctl print", ProcessResult::ok());
        s.start().unwrap();
        assert!(r.calls().contains(&"launchctl kickstart gui/501/com.tasker.test".to_string()));
        assert!(!r.calls().iter().any(|c| c.starts_with("launchctl bootstrap")));
    }

    #[test]
    fn stop_unloads_but_keeps_autostart_enabled() {
        let dir = temp_dir();
        let _env = EnvGuard::set("TASKER_HOME", dir.join("home").to_str().unwrap());
        let r = runner();
        let s = service(&dir, &r);
        s.enable().unwrap();
        s.stop().unwrap();
        assert!(s.is_enabled());
        assert_eq!(r.calls().last().unwrap(), "launchctl bootout gui/501/com.tasker.test");
    }

    #[test]
    fn start_without_autostart_is_an_error_and_is_loaded_follows_print() {
        let dir = temp_dir();
        let r = runner();
        let s = service(&dir, &r);
        assert_eq!(s.start().unwrap_err().message(), NOT_ENABLED);
        assert!(r.calls().is_empty());

        r.set("launchctl print", ProcessResult::ok());
        assert!(s.is_loaded());
        r.set("launchctl print", ProcessResult::new(113, "", ""));
        assert!(!s.is_loaded());
        assert_eq!(r.calls()[0], "launchctl print gui/501/com.tasker.test");
    }

    #[test]
    fn defaults_follow_the_overrides() {
        let s = {
            let _d = EnvGuard::set("TASKER_SERVICE_DIR", "/tmp/svc");
            let _l = EnvGuard::set("TASKER_SERVICE_LABEL", "com.tasker.custom");
            LaunchdService::new(Box::new(FakeRunner::default()), None, None)
        };
        assert_eq!(s.plist_path(), PathBuf::from("/tmp/svc/com.tasker.custom.plist"));
        let _env = EnvGuard::set("TASKER_SERVICE_DIR", "");
        let _env2 = EnvGuard::remove("TASKER_SERVICE_LABEL");
        let d = LaunchdService::new(Box::new(FakeRunner::default()), None, Some("x".into()));
        // Пустая TASKER_SERVICE_DIR в .NET — это «задано пустым», не «не задано».
        assert_eq!(d.plist_path(), PathBuf::from("x.plist"));
        let _env3 = EnvGuard::remove("TASKER_SERVICE_DIR");
        let d = LaunchdService::new(Box::new(FakeRunner::default()), None, None);
        assert!(
            d.plist_path().ends_with("Library/LaunchAgents/com.tasker.mcp.plist"),
            "{}",
            d.plist_path().display()
        );
    }
}
