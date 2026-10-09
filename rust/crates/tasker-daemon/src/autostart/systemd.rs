//! Linux: пользовательская служба systemd `~/.config/systemd/user/tasker-mcp.service` (`SystemdService` в .NET).
//! WantedBy=default.target — стартует при входе (чтобы без входа — `loginctl enable-linger`), Restart=always — перезапуск при падении.
//! `TASKER_SERVICE_DIR` и `TASKER_SERVICE_LABEL` переопределяют каталог и имя.
use super::{
    AutostartContext, NOT_ENABLED, ProcessResult, ProcessRunner, Result, SERVICE_DIR_VARIABLE, SERVICE_LABEL_VARIABLE, ServiceError,
    ServiceManager, env_override,
};
use std::path::PathBuf;

pub const DEFAULT_UNIT: &str = "tasker-mcp.service";

/// Текст unit-файла дословно как `SystemdService.UnitFile()`: `Environment=` по строке на переменную перед `Restart=always`.
pub fn render_systemd(ctx: &AutostartContext) -> String {
    let command = ctx.command.iter().map(|a| quote(a)).collect::<Vec<_>>().join(" ");
    let environment = ctx
        .environment
        .iter()
        .map(|(k, v)| format!("Environment={}\n", quote(&format!("{k}={v}"))))
        .collect::<String>();
    format!(
        "[Unit]\nDescription=Tasker MCP server\n\n[Service]\nExecStart={command}\n{environment}Restart=always\nRestartSec=5\n\n[Install]\nWantedBy=default.target\n"
    )
}

/// Аргументы с пробелами и спецсимволами — в кавычках по правилам systemd (`SystemdService.Quote`).
pub fn quote(value: &str) -> String {
    if value
        .chars()
        .any(|c| c.is_whitespace() || matches!(c, '"' | '\'' | '\\' | '$' | '%'))
    {
        format!(
            "\"{}\"",
            value
                .replace('\\', "\\\\")
                .replace('"', "\\\"")
                .replace('%', "%%")
                .replace('$', "$$")
        )
    } else {
        value.to_string()
    }
}

pub struct SystemdService {
    runner: Box<dyn ProcessRunner>,
    directory: PathBuf,
    unit: String,
    daemon_command: Option<Vec<String>>,
}

impl SystemdService {
    /// `directory`/`unit` — явные значения (тесты); иначе `TASKER_SERVICE_DIR`/`TASKER_SERVICE_LABEL`, иначе
    /// `~/.config/systemd/user` и `tasker-mcp.service`.
    pub fn new(runner: Box<dyn ProcessRunner>, directory: Option<PathBuf>, unit: Option<String>) -> Self {
        let directory = directory
            .or_else(|| env_override(SERVICE_DIR_VARIABLE).map(PathBuf::from))
            .unwrap_or_else(|| {
                PathBuf::from(tasker_core::settings::home_dir())
                    .join(".config")
                    .join("systemd")
                    .join("user")
            });
        let unit = unit
            .or_else(|| env_override(SERVICE_LABEL_VARIABLE))
            .unwrap_or_else(|| DEFAULT_UNIT.to_string());
        Self {
            runner,
            directory,
            unit,
            daemon_command: None,
        }
    }

    /// Команда демона вместо `tasker-mcpd` рядом с текущей программой (тесты).
    pub fn with_daemon_command(mut self, command: Vec<String>) -> Self {
        self.daemon_command = Some(command);
        self
    }

    pub fn unit(&self) -> &str {
        &self.unit
    }

    pub fn unit_path(&self) -> PathBuf {
        self.directory.join(&self.unit)
    }

    /// Текст unit-файла для текущего окружения.
    pub fn unit_file(&self) -> Result<String> {
        Ok(render_systemd(&AutostartContext::current_with(
            self.daemon_command.clone(),
            &self.unit,
            "",
        )?))
    }

    fn systemctl(&self, arguments: &[&str]) -> ProcessResult {
        let mut all = vec!["--user"];
        all.extend_from_slice(arguments);
        self.runner.run("systemctl", &all)
    }
}

/// `SystemdService.Check`: с подсказкой про сеанс пользователя, если systemctl не достучался до шины (TSK-111).
fn check(result: &ProcessResult, what: &str) -> Result<()> {
    if result.success() {
        return Ok(());
    }
    let text = format!("{}{}", result.error, result.output);
    let text = text.trim();
    let hint = if text.contains("Failed to connect to bus") {
        " (systemctl --user needs a login session of the user: log in directly or over ssh with a session, or run 'loginctl enable-linger' and set XDG_RUNTIME_DIR=/run/user/<uid>)"
    } else {
        ""
    };
    Err(ServiceError::service(format!("{what} failed ({}): {text}{hint}", result.exit_code)))
}

impl ServiceManager for SystemdService {
    fn name(&self) -> &'static str {
        "systemd"
    }

    fn is_enabled(&self) -> bool {
        self.unit_path().is_file()
    }

    fn enable(&self) -> Result<()> {
        std::fs::create_dir_all(&self.directory)?;
        std::fs::write(self.unit_path(), self.unit_file()?)?;
        let result = check(&self.systemctl(&["daemon-reload"]), "systemctl daemon-reload")
            .and_then(|()| check(&self.systemctl(&["enable", "--now", &self.unit]), "systemctl enable"));
        if let Err(e @ ServiceError::Service(_)) = result {
            // Без сеанса пользователя (su, sudo -u, ssh без logind) systemctl --user не достучится до шины: не оставляем описание,
            // которое выглядело бы как «автозапуск включён» (is_enabled — это файл).
            std::fs::remove_file(self.unit_path())?;
            return Err(e);
        }
        result
    }

    fn disable(&self) -> Result<()> {
        self.systemctl(&["disable", "--now", &self.unit]);
        let path = self.unit_path();
        if path.is_file() {
            std::fs::remove_file(path)?;
        }
        self.systemctl(&["daemon-reload"]);
        Ok(())
    }

    fn start(&self) -> Result<()> {
        if !self.is_enabled() {
            return Err(ServiceError::service(NOT_ENABLED));
        }
        check(&self.systemctl(&["start", &self.unit]), "systemctl start")
    }

    fn stop(&self) -> Result<()> {
        check(&self.systemctl(&["stop", &self.unit]), "systemctl stop")
    }

    fn is_loaded(&self) -> bool {
        self.systemctl(&["is-active", "--quiet", &self.unit]).success()
    }
}

#[cfg(test)]
mod tests {
    use super::super::test_support::{EnvGuard, FakeRunner, daemon_stub, runner, temp_dir};
    use super::*;
    use std::rc::Rc;

    fn service(dir: &std::path::Path, r: &Rc<FakeRunner>) -> SystemdService {
        SystemdService::new(
            Box::new(Rc::clone(r)),
            Some(dir.join("service")),
            Some("tasker-test.service".to_string()),
        )
        .with_daemon_command(daemon_stub(dir))
    }

    #[test]
    fn unit_restarts_the_daemon_and_starts_at_login() {
        let dir = temp_dir();
        let home = dir.join("home");
        let _env = EnvGuard::set("TASKER_HOME", home.to_str().unwrap());
        let r = runner();
        let s = service(&dir, &r);
        s.enable().unwrap();
        let text = std::fs::read_to_string(s.unit_path()).unwrap();
        assert!(text.contains("Restart=always"));
        assert!(text.contains("WantedBy=default.target"));
        let exec = text.lines().find(|l| l.starts_with("ExecStart=")).unwrap();
        assert!(exec.ends_with("--detached"));
        assert!(exec.contains("tasker-mcpd"));
        assert!(text.contains(&format!("Environment=TASKER_HOME={}\n", home.display())));
    }

    #[test]
    fn unit_quotes_arguments_with_spaces() {
        let dir = temp_dir();
        let special = dir.join("with space");
        let _env = EnvGuard::set("TASKER_HOME", special.to_str().unwrap());
        let r = runner();
        let s = service(&dir, &r);
        s.enable().unwrap();
        let text = std::fs::read_to_string(s.unit_path()).unwrap();
        assert!(
            text.contains(&format!("Environment=\"TASKER_HOME={}\"\n", special.display())),
            "{text}"
        );
    }

    #[test]
    fn quote_follows_systemd_rules() {
        assert_eq!(quote("plain"), "plain");
        assert_eq!(quote("a b"), "\"a b\"");
        assert_eq!(quote("x\\y\"z$1%2'"), "\"x\\\\y\\\"z$$1%%2'\"");
        assert_eq!(quote(""), "");
    }

    #[test]
    fn enable_reloads_and_enables_the_user_unit_and_is_idempotent() {
        let dir = temp_dir();
        let _env = EnvGuard::set("TASKER_HOME", dir.join("home").to_str().unwrap());
        let r = runner();
        let s = service(&dir, &r);
        s.enable().unwrap();
        assert_eq!(
            r.calls(),
            vec![
                "systemctl --user daemon-reload",
                "systemctl --user enable --now tasker-test.service"
            ]
        );
        let first = std::fs::read(s.unit_path()).unwrap();
        s.enable().unwrap();
        assert_eq!(first, std::fs::read(s.unit_path()).unwrap());
    }

    #[test]
    fn disable_stops_removes_and_reloads() {
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
            vec![
                "systemctl --user disable --now tasker-test.service",
                "systemctl --user daemon-reload"
            ]
        );
    }

    #[test]
    fn start_stop_and_state() {
        let dir = temp_dir();
        let _env = EnvGuard::set("TASKER_HOME", dir.join("home").to_str().unwrap());
        let r = runner();
        let s = service(&dir, &r);
        s.enable().unwrap();
        r.clear();
        s.start().unwrap();
        s.stop().unwrap();
        r.set("systemctl --user is-active", ProcessResult::new(3, "", ""));
        assert!(!s.is_loaded());
        assert_eq!(
            r.calls(),
            vec![
                "systemctl --user start tasker-test.service",
                "systemctl --user stop tasker-test.service",
                "systemctl --user is-active --quiet tasker-test.service",
            ]
        );
        r.set("systemctl --user stop", ProcessResult::new(5, "", "Failed to stop"));
        assert_eq!(s.stop().unwrap_err().message(), "systemctl stop failed (5): Failed to stop");
    }

    #[test]
    fn start_without_autostart_is_an_error() {
        let dir = temp_dir();
        let r = runner();
        let s = service(&dir, &r);
        assert_eq!(s.start().unwrap_err().message(), NOT_ENABLED);
        assert!(r.calls().is_empty());
    }

    #[test]
    fn failure_is_reported_with_the_command_output_and_removes_the_unit() {
        let dir = temp_dir();
        let _env = EnvGuard::set("TASKER_HOME", dir.join("home").to_str().unwrap());
        let r = runner();
        r.set("systemctl --user enable", ProcessResult::new(1, "", "Failed to connect to bus"));
        let s = service(&dir, &r);
        let e = s.enable().unwrap_err();
        assert_eq!(
            e.message(),
            "systemctl enable failed (1): Failed to connect to bus (systemctl --user needs a login session of the user: log in directly or over ssh with a session, or run 'loginctl enable-linger' and set XDG_RUNTIME_DIR=/run/user/<uid>)"
        );
        assert!(
            !s.is_enabled(),
            "A failed enable must not leave the unit file that looks like enabled autostart"
        );
    }

    #[test]
    fn defaults_follow_the_overrides() {
        let s = {
            let _d = EnvGuard::set("TASKER_SERVICE_DIR", "/tmp/svc");
            let _l = EnvGuard::set("TASKER_SERVICE_LABEL", "tasker-custom.service");
            SystemdService::new(Box::new(FakeRunner::default()), None, None)
        };
        assert_eq!(s.unit_path(), PathBuf::from("/tmp/svc/tasker-custom.service"));
        let _d = EnvGuard::remove("TASKER_SERVICE_DIR");
        let _l = EnvGuard::remove("TASKER_SERVICE_LABEL");
        let d = SystemdService::new(Box::new(FakeRunner::default()), None, None);
        assert!(
            d.unit_path().ends_with(".config/systemd/user/tasker-mcp.service"),
            "{}",
            d.unit_path().display()
        );
    }
}
