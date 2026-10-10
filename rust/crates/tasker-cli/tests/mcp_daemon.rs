//! `tasker mcp run | upgrade` с демоном `tasker-mcpd` из того же каталога сборки (сценарии `DaemonTests`/`DaemonUpgradeTests` .NET):
//! изолированные `TASKER_HOME`, `TASKER_SERVICE_DIR`, `TASKER_SERVICE_LABEL`, свободный порт. Демона рядом нет (собран только
//! `tasker-cli`) — тест ничего не проверяет и сообщает об этом.
use std::net::TcpListener;
use std::path::PathBuf;
#[cfg(unix)]
use std::process::Stdio;
use std::process::{Command, Output};
#[cfg(unix)]
use std::time::{Duration, Instant};

const BIN: &str = env!("CARGO_BIN_EXE_tasker");

struct Home {
    root: PathBuf,
}

impl Home {
    fn new() -> Home {
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../target/tmp")
            .join(uuid::Uuid::new_v4().simple().to_string());
        std::fs::create_dir_all(root.join("home")).unwrap();
        let port = TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port();
        std::fs::write(
            root.join("home/settings.json"),
            format!("{{\"mcp\":{{\"port\":{port},\"workspaces\":[]}}}}\n"),
        )
        .unwrap();
        Home { root }
    }

    fn command(&self, args: &[&str]) -> Command {
        let mut command = Command::new(BIN);
        command
            .args(args)
            .env("TASKER_HOME", self.root.join("home"))
            .env("TASKER_SERVICE_DIR", self.root.join("service"))
            .env("TASKER_SERVICE_LABEL", format!("com.tasker.cli-test-{}", std::process::id()))
            .env("TASKER_MCP_DRAIN_QUIET_MS", "300");
        command
    }

    fn run(&self, args: &[&str]) -> (i32, String, String) {
        let Output { status, stdout, stderr } = self.command(args).output().unwrap();
        (
            status.code().unwrap_or(-1),
            String::from_utf8_lossy(&stdout).into_owned(),
            String::from_utf8_lossy(&stderr).into_owned(),
        )
    }
}

impl Drop for Home {
    fn drop(&mut self) {
        let _ = self.command(&["mcp", "stop"]).output();
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

fn daemon_is_built() -> bool {
    let daemon = PathBuf::from(BIN).with_file_name(format!("tasker-mcpd{}", std::env::consts::EXE_SUFFIX));
    if !daemon.is_file() {
        eprintln!("skipped: {} is not built (cargo build -p tasker-mcpd)", daemon.display());
    }
    daemon.is_file()
}

#[test]
fn upgrade_replaces_the_worker_and_reports_failures_like_dotnet() {
    if !daemon_is_built() {
        return;
    }
    let home = Home::new();
    let (code, out, _) = home.run(&["mcp", "upgrade"]);
    assert_eq!(code, 3);
    assert_eq!(
        out,
        "The MCP server is not running: nothing to upgrade (start it with 'tasker mcp start')\n"
    );

    assert_eq!(home.run(&["mcp", "start"]).0, 0);
    let (code, out, err) = home.run(&["mcp", "upgrade"]);
    assert_eq!(code, 0, "{err}");
    assert!(out.starts_with("The MCP server process is replaced without downtime: "), "{out}");
    assert_eq!(out.matches("\nworker pid ").count(), 1, "{out}");

    let missing = home.root.join("no-such-daemon");
    let (code, out, err) = home.run(&["mcp", "upgrade", "--daemon", missing.to_str().unwrap()]);
    assert_eq!((code, out.as_str()), (1, ""));
    assert_eq!(
        err,
        format!(
            "Error: The program of the new server process is not found: {}\nThe running MCP server is not changed. Look at the log or restart it with 'tasker mcp upgrade --restart'.\n",
            missing.display()
        )
    );

    let (code, out, _) = home.run(&["mcp", "upgrade", "--json"]);
    assert_eq!(code, 0);
    let value: serde_json::Value = serde_json::from_str(&out).unwrap();
    assert_eq!(value["upgraded"], "replaced");
    assert_eq!(value["status"]["supervised"], true);
    assert_eq!(
        value.as_object().unwrap().keys().collect::<Vec<_>>(),
        ["upgraded", "message", "status"]
    );
}

#[cfg(unix)]
#[test]
fn foreground_run_stops_on_interrupt_and_cleans_up() {
    if !daemon_is_built() {
        return;
    }
    let home = Home::new();
    let child = home
        .command(&["mcp", "run"])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let deadline = Instant::now() + Duration::from_secs(30);
    while home.run(&["mcp", "status"]).0 != 0 {
        assert!(Instant::now() < deadline, "the daemon did not start");
        std::thread::sleep(Duration::from_millis(100));
    }
    Command::new("kill").args(["-INT", &child.id().to_string()]).status().unwrap();
    let output = child.wait_with_output().unwrap();
    assert_eq!(output.status.code(), Some(0));
    let stdout = String::from_utf8_lossy(&output.stdout);
    assert!(stdout.contains("Stopping the MCP server"), "{stdout}");
    assert!(!home.root.join("home/mcp/daemon.json").exists());
    assert_eq!(home.run(&["mcp", "status"]).0, 3);
}
