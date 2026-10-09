//! Демон как процесс: `tasker-mcpd` на свободном порту с изолированным `TASKER_HOME` — файлы состояния, HTTP управления,
//! области из `settings.json` (открытая, несуществующая, добавленная на лету), остановка запросом и сигналом, второй экземпляр,
//! занятый порт. Повторяет сценарии `DaemonTests` (.NET), которым не нужен `/mcp`.
use std::net::TcpListener;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

const BIN: &str = env!("CARGO_BIN_EXE_tasker-mcpd");

struct Home {
    root: PathBuf,
    port: u16,
}

impl Home {
    fn new() -> Home {
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../target/tmp")
            .join(uuid::Uuid::new_v4().simple().to_string());
        std::fs::create_dir_all(root.join("home")).unwrap();
        let port = free_port();
        let home = Home { root, port };
        home.write_settings(&[]);
        home
    }

    fn home(&self) -> PathBuf {
        self.root.join("home")
    }

    fn workspace(&self, name: &str) -> String {
        let dir = self.root.join(name);
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::canonicalize(&dir).unwrap().to_string_lossy().into_owned()
    }

    /// `settings.json` руками, атомарно — как `SettingsStore.Update` (`.tmp` + rename).
    fn write_settings(&self, workspaces: &[&str]) {
        let list: Vec<String> = workspaces
            .iter()
            .map(|w| format!("{{\"kind\":\"files\",\"path\":\"{w}\"}}"))
            .collect();
        let text = format!("{{\"mcp\":{{\"port\":{},\"workspaces\":[{}]}}}}\n", self.port, list.join(","));
        let file = self.home().join("settings.json");
        let temp = self.home().join("settings.json.tmp");
        std::fs::write(&temp, text).unwrap();
        std::fs::rename(&temp, &file).unwrap();
    }

    fn command(&self) -> Command {
        let mut command = Command::new(BIN);
        command
            .env("TASKER_HOME", self.home())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        command
    }

    fn start(&self) -> Daemon {
        let child = self.command().spawn().unwrap();
        let daemon = Daemon {
            child: Some(child),
            http: ureq::Agent::new_with_config(ureq::config::Config::builder().http_status_as_error(false).build()),
            port: self.port,
            home: self.home(),
        };
        daemon.wait_until(|| daemon.get("/health", None).map(|(code, _)| code == 200).unwrap_or(false));
        daemon
    }

    fn info_file(&self) -> PathBuf {
        self.home().join("mcp").join("daemon.json")
    }

    fn token(&self) -> String {
        let text = std::fs::read_to_string(self.info_file()).unwrap();
        let value: serde_json::Value = serde_json::from_str(&text).unwrap();
        value["token"].as_str().unwrap().to_string()
    }

    fn log(&self) -> String {
        std::fs::read_dir(self.home().join("logs"))
            .map(|entries| {
                entries
                    .flatten()
                    .filter(|e| e.file_name().to_string_lossy().starts_with("mcp-"))
                    .map(|e| std::fs::read_to_string(e.path()).unwrap_or_default())
                    .collect::<String>()
            })
            .unwrap_or_default()
    }
}

impl Drop for Home {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

struct Daemon {
    child: Option<Child>,
    http: ureq::Agent,
    port: u16,
    home: PathBuf,
}

impl Daemon {
    fn url(&self, path: &str) -> String {
        format!("http://127.0.0.1:{}{path}", self.port)
    }

    fn get(&self, path: &str, token: Option<&str>) -> Result<(u16, String), ureq::Error> {
        let mut request = self.http.get(self.url(path));
        if let Some(token) = token {
            request = request.header("X-Tasker-Control", token);
        }
        let mut response = request.call()?;
        Ok((response.status().as_u16(), response.body_mut().read_to_string()?))
    }

    fn post(&self, path: &str, token: Option<&str>, body: &str) -> Result<(u16, String), ureq::Error> {
        let mut request = self.http.post(self.url(path));
        if let Some(token) = token {
            request = request.header("X-Tasker-Control", token);
        }
        let mut response = request.header("Content-Type", "application/json").send(body)?;
        Ok((response.status().as_u16(), response.body_mut().read_to_string()?))
    }

    fn status(&self, token: &str) -> serde_json::Value {
        let (code, body) = self.get("/daemon/status", Some(token)).unwrap();
        assert_eq!(code, 200, "{body}");
        serde_json::from_str(&body).unwrap()
    }

    fn wait_until(&self, condition: impl Fn() -> bool) {
        let deadline = Instant::now() + Duration::from_secs(30);
        while !condition() {
            assert!(Instant::now() < deadline, "the condition did not become true in 30 s");
            std::thread::sleep(Duration::from_millis(50));
        }
    }

    fn wait_workspaces(&self, token: &str, condition: impl Fn(&[serde_json::Value]) -> bool) -> serde_json::Value {
        self.wait_until(|| condition(self.status(token)["workspaces"].as_array().unwrap()));
        self.status(token)
    }

    fn pid(&self) -> u32 {
        self.child.as_ref().unwrap().id()
    }

    /// Ждёт завершения процесса; возвращает код, stdout и stderr.
    fn wait_exit(&mut self) -> (i32, String, String) {
        let child = self.child.take().unwrap();
        let output = child.wait_with_output().unwrap();
        (
            output.status.code().unwrap_or(-1),
            String::from_utf8_lossy(&output.stdout).into_owned(),
            String::from_utf8_lossy(&output.stderr).into_owned(),
        )
    }

    fn lock_is_held(&self) -> bool {
        tasker_mcpd::files::DaemonFiles::new(self.home.join("mcp")).is_running()
    }
}

impl Drop for Daemon {
    fn drop(&mut self) {
        if let Some(mut child) = self.child.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

fn free_port() -> u16 {
    TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port()
}

fn states(status: &serde_json::Value) -> Vec<(String, String)> {
    status["workspaces"]
        .as_array()
        .unwrap()
        .iter()
        .map(|w| (w["path"].as_str().unwrap().to_string(), w["state"].as_str().unwrap().to_string()))
        .collect()
}

#[test]
fn start_serves_the_workspaces_and_writes_daemon_json_for_the_owner_only() {
    let home = Home::new();
    let a = home.workspace("a");
    let b = home.workspace("b");
    home.write_settings(&[&a, &b]);
    let daemon = home.start();

    let text = std::fs::read_to_string(home.info_file()).unwrap();
    let info: serde_json::Value = serde_json::from_str(&text).unwrap();
    assert_eq!(info["pid"].as_u64().unwrap() as u32, daemon.pid());
    assert_eq!(info["port"].as_u64().unwrap() as u16, home.port);
    assert_eq!(info["token"].as_str().unwrap().len(), 43);
    assert!(text.starts_with("{\n  \"pid\": "), "{text}");
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt as _;
        assert_eq!(std::fs::metadata(home.info_file()).unwrap().permissions().mode() & 0o777, 0o600);
    }
    assert!(daemon.lock_is_held());

    let token = home.token();
    let status = daemon.wait_workspaces(&token, |w| w.iter().all(|x| x["state"] == "open"));
    assert_eq!(status["port"], home.port);
    assert_eq!(status["settingsPort"], home.port);
    assert_eq!(status["pid"].as_u64().unwrap() as u32, daemon.pid());
    assert_eq!(status["supervised"], false);
    assert_eq!(status["workers"].as_array().unwrap().len(), 0);
    assert_eq!(status["mcpUrl"], format!("http://127.0.0.1:{}/mcp", home.port));
    assert_eq!(status["workspaces"][0]["key"], "a");
    assert_eq!(status["workspaces"][1]["key"], "b");
    assert_eq!(status["workspaces"][0]["kind"], "files");
    assert_eq!(status["workspaces"][0]["error"], serde_json::Value::Null);
    assert!(Path::new(&a).join(".tasker").is_dir());

    let (code, body) = daemon.get("/ready", None).unwrap();
    assert_eq!(code, 200);
    assert_eq!(body, format!("{{\"ready\":true,\"pid\":{}}}", daemon.pid()));
    let (code, body) = daemon.get("/health", None).unwrap();
    assert_eq!(
        (code, body),
        (
            200,
            format!("{{\"status\":\"ok\",\"mode\":\"McpDaemon\",\"pid\":{}}}", daemon.pid())
        )
    );
    assert_eq!(
        daemon.get("/api/health", None).unwrap(),
        (200, "{\"status\":\"ok\",\"mode\":\"McpDaemon\"}".into())
    );
}

#[test]
fn control_endpoints_need_the_secret_and_a_local_host() {
    let home = Home::new();
    let daemon = home.start();
    let token = home.token();

    assert_eq!(daemon.get("/daemon/status", None).unwrap().0, 401);
    assert_eq!(daemon.post("/daemon/stop", None, "").unwrap().0, 401);
    assert_eq!(daemon.get("/daemon/status", Some("not-the-secret")).unwrap().0, 401);
    assert_eq!(daemon.post("/daemon/upgrade", None, "").unwrap().0, 401);

    // Страница на чужом домене, указывающем на 127.0.0.1, — DNS rebinding: Host не локальный.
    let mut response = daemon
        .http
        .get(daemon.url("/daemon/status"))
        .header("X-Tasker-Control", &token)
        .header("Host", "evil.example.com")
        .call()
        .unwrap();
    assert_eq!(response.status().as_u16(), 403);
    assert_eq!(
        response.body_mut().read_to_string().unwrap(),
        "{\"error\":\"Only local requests are allowed\"}"
    );
    let response = daemon
        .http
        .get(daemon.url("/health"))
        .header("Origin", "http://evil.example.com")
        .call()
        .unwrap();
    assert_eq!(response.status().as_u16(), 403);
    let response = daemon
        .http
        .get(daemon.url("/health"))
        .header("Origin", "http://localhost:3000")
        .call()
        .unwrap();
    assert_eq!(response.status().as_u16(), 200);

    // Заменить на лету нельзя: демон в одном процессе (как .NET без супервизора).
    let (code, body) = daemon
        .post(
            "/daemon/upgrade",
            Some(&token),
            "{\"file\":\"/x\",\"arguments\":[],\"timeoutSeconds\":1}",
        )
        .unwrap();
    assert_eq!(code, 409);
    assert_eq!(
        body,
        "{\"error\":\"The MCP server runs as a single process and cannot be replaced on the fly: restart it\"}"
    );

    // MCP ещё нет; адреса десктопа отвечают как .NET.
    assert_eq!(daemon.post("/mcp", None, "{}").unwrap().0, 501);
    let (code, body) = daemon.post("/w/a/mcp", None, "{}").unwrap();
    assert_eq!(code, 404);
    assert!(body.contains("MCP has no per-workspace address"), "{body}");
    assert_eq!(
        daemon.get("/w/nothing/api/tasks", None).unwrap(),
        (404, "{\"error\":\"Workspace 'nothing' is not open\"}".into())
    );
    assert_eq!(daemon.get("/api/tasks", None).unwrap().0, 404);
    assert_eq!(daemon.get("/nothing", None).unwrap(), (404, String::new()));

    // Сервер всё это пережил.
    assert_eq!(daemon.status(&token)["pid"].as_u64().unwrap() as u32, daemon.pid());
}

#[test]
fn sync_checks_an_open_workspace_and_rejects_unknown_or_missing_bodies() {
    let home = Home::new();
    let a = home.workspace("a");
    home.write_settings(&[&a]);
    let daemon = home.start();
    let token = home.token();
    daemon.wait_workspaces(&token, |w| w.iter().all(|x| x["state"] == "open"));

    let (code, body) = daemon
        .post("/daemon/sync", Some(&token), &format!("{{\"kind\":\"files\",\"path\":\"{a}\"}}"))
        .unwrap();
    assert_eq!(code, 200, "{body}");
    assert_eq!(
        body,
        format!("{{\"path\":\"{a}\",\"problemCount\":0,\"problems\":[],\"series\":[]}}")
    );

    // Файл, который не читается, — проблема области.
    let tasker = Path::new(&a).join(".tasker");
    let project = tasker.join("projects").join("11111111-1111-4111-8111-111111111111");
    std::fs::create_dir_all(&project).unwrap();
    std::fs::write(project.join("project.yaml"), "id: not-a-guid\nname: [broken\n").unwrap();
    let (code, body) = daemon
        .post("/daemon/sync", Some(&token), &format!("{{\"kind\":\"files\",\"path\":\"{a}\"}}"))
        .unwrap();
    assert_eq!(code, 200, "{body}");
    let result: serde_json::Value = serde_json::from_str(&body).unwrap();
    assert_eq!(result["problemCount"], 1, "{body}");
    assert_eq!(
        result["problems"][0]["path"],
        "projects/11111111-1111-4111-8111-111111111111/project.yaml"
    );

    let (code, body) = daemon
        .post("/daemon/sync", Some(&token), "{\"kind\":\"files\",\"path\":\"/nowhere\"}")
        .unwrap();
    assert_eq!(code, 404);
    assert_eq!(body, "{\"error\":\"The daemon does not serve /nowhere\"}");
    let (code, body) = daemon.post("/daemon/sync", Some(&token), "").unwrap();
    assert_eq!(code, 400);
    assert_eq!(body, "{\"error\":\"Body {\\\"kind\\\", \\\"path\\\"} is required\"}");
    assert_eq!(daemon.post("/daemon/sync", None, "{}").unwrap().0, 401);
}

#[test]
fn a_workspace_that_cannot_be_opened_is_reported_and_does_not_stop_the_others() {
    let home = Home::new();
    let gone = home.workspace("gone");
    let kept = home.workspace("kept");
    home.write_settings(&[&gone, &kept]);
    std::fs::remove_dir_all(&gone).unwrap();
    let daemon = home.start();
    let token = home.token();

    let status = daemon.wait_workspaces(&token, |w| w.iter().all(|x| x["state"] != "opening"));
    assert_eq!(
        states(&status),
        [(gone.clone(), "failed".to_string()), (kept.clone(), "open".to_string())]
    );
    assert_eq!(status["workspaces"][0]["error"], format!("{gone} does not exist"));
    assert_eq!(status["workspaces"][0]["key"], serde_json::Value::Null);
    assert_eq!(status["workspaces"][1]["key"], "kept");
    assert!(!Path::new(&gone).exists(), "a failed workspace must not be created on disk");
    assert_eq!(daemon.get("/ready", None).unwrap().0, 200);

    // Папка появилась, и настройки изменились — область открывается следующим изменением (повтор по таймеру — 30 с).
    std::fs::create_dir_all(&gone).unwrap();
    let other = home.workspace("other");
    home.write_settings(&[&gone, &kept, &other]);
    let status = daemon.wait_workspaces(&token, |w| w.len() == 3 && w.iter().all(|x| x["state"] == "open"));
    assert_eq!(status["workspaces"][0]["key"], "gone");
    assert_eq!(status["workspaces"][2]["key"], "other");

    // Убрали из настроек — закрыта; одноимённые папки различаются суффиксом.
    let other2 = home.workspace("x/other");
    home.write_settings(&[&kept, &other, &other2]);
    let status = daemon.wait_workspaces(&token, |w| {
        w.len() == 3 && w.iter().all(|x| x["state"] == "open") && w[2]["path"] == other2.as_str()
    });
    let keys: Vec<&str> = status["workspaces"]
        .as_array()
        .unwrap()
        .iter()
        .map(|w| w["key"].as_str().unwrap())
        .collect();
    assert_eq!(keys, ["kept", "other", "other-2"], "{status}");
    assert!(
        home.log().contains(&format!("[INF] Workspace removed from MCP: {gone}")),
        "{}",
        home.log()
    );
    assert!(
        home.log()
            .contains(&format!("[WRN] Cannot open workspace {gone}: {gone} does not exist"))
    );
}

#[test]
fn stop_request_ends_the_process_and_cleans_up() {
    let home = Home::new();
    let a = home.workspace("a");
    home.write_settings(&[&a]);
    let mut daemon = home.start();
    let token = home.token();
    daemon.wait_workspaces(&token, |w| w.iter().all(|x| x["state"] == "open"));

    let (code, body) = daemon.post("/daemon/stop", Some(&token), "").unwrap();
    assert_eq!((code, body), (200, "{\"stopping\":true}".to_string()));
    let (exit, stdout, stderr) = daemon.wait_exit();
    assert_eq!(exit, 0, "{stderr}");
    assert!(stdout.contains("] Stop requested\n"), "{stdout}");
    assert!(stdout.contains("] Stopping the MCP server\n"), "{stdout}");
    assert!(stderr.is_empty(), "{stderr}");
    assert!(!home.info_file().exists());
    assert!(!daemon.lock_is_held());
    assert!(daemon.get("/health", None).is_err());

    // Журнал: формат Serilog, только файл за сегодня.
    let log = home.log();
    let first = log.lines().next().unwrap();
    assert!(first.ends_with(" [INF] Starting the MCP server"), "{first}");
    assert_eq!(
        first.len(),
        "2026-10-09 13:58:27.056 +02:00 [INF] Starting the MCP server".len(),
        "{first}"
    );
    assert!(log.contains("[INF] Workspace closed: Files "), "{log}");
}

#[cfg(unix)]
#[test]
fn sigterm_stops_the_daemon_gracefully() {
    let home = Home::new();
    let a = home.workspace("a");
    home.write_settings(&[&a]);
    let mut daemon = home.start();
    let token = home.token();
    daemon.wait_workspaces(&token, |w| w.iter().all(|x| x["state"] == "open"));

    let killed = Command::new("kill").arg("-TERM").arg(daemon.pid().to_string()).status().unwrap();
    assert!(killed.success());
    let (exit, stdout, _) = daemon.wait_exit();
    assert_eq!(exit, 0);
    assert!(stdout.contains("Stopping the MCP server"), "{stdout}");
    assert!(!home.info_file().exists());
    assert!(!daemon.lock_is_held());
}

#[test]
fn only_one_server_runs_at_a_time() {
    let home = Home::new();
    let _daemon = home.start();

    let second = home.command().output().unwrap();
    assert_eq!(second.status.code(), Some(1));
    assert_eq!(
        String::from_utf8_lossy(&second.stderr),
        "The MCP server is already running: see 'tasker mcp status'\n"
    );
    assert!(home.info_file().exists(), "the second instance must not touch daemon.json");
}

#[test]
fn busy_port_is_reported_in_the_log_and_nothing_is_left_running() {
    let home = Home::new();
    let listener = TcpListener::bind(("127.0.0.1", home.port)).unwrap();

    let started = Instant::now();
    let output = home.command().output().unwrap();
    assert!(started.elapsed() >= Duration::from_secs(5), "waits for the port up to 5 s");
    assert_eq!(output.status.code(), Some(1));
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(stderr.starts_with(&format!("Port {} is busy", home.port)), "{stderr}");
    assert!(!home.info_file().exists());
    assert!(!tasker_mcpd::files::DaemonFiles::new(home.home().join("mcp")).is_running());
    // Консоль читает последнюю строку [ERR]/[FTL] после «Starting the MCP server».
    let log = home.log();
    assert!(log.contains(&format!("[ERR] Port {} is busy", home.port)), "{log}");
    assert!(
        log.contains("[INF] Port") && log.contains("waiting up to 5 s for the desktop to release it"),
        "{log}"
    );
    drop(listener);
}

#[test]
fn a_port_released_right_after_the_start_is_taken() {
    let home = Home::new();
    let listener = TcpListener::bind(("127.0.0.1", home.port)).unwrap();
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(1500));
        drop(listener);
    });

    let daemon = home.start();
    assert_eq!(daemon.status(&home.token())["port"], home.port);
}

#[test]
fn unknown_and_unsupported_arguments_are_refused() {
    let home = Home::new();
    let output = home.command().arg("--what").output().unwrap();
    assert_eq!(output.status.code(), Some(2));
    assert_eq!(
        String::from_utf8_lossy(&output.stderr),
        "Unknown argument '--what': see 'tasker-mcpd --help'\n"
    );
    for flag in ["--supervised", "--worker"] {
        let output = home.command().arg(flag).output().unwrap();
        assert_eq!(output.status.code(), Some(2), "{flag}");
        assert!(String::from_utf8_lossy(&output.stderr).contains("TSK-139"), "{flag}");
    }
    let output = home.command().arg("--help").output().unwrap();
    assert_eq!(output.status.code(), Some(0));
    assert!(String::from_utf8_lossy(&output.stdout).starts_with("tasker-mcpd — the Tasker MCP server (daemon)."));
    assert!(!home.info_file().exists());
}
