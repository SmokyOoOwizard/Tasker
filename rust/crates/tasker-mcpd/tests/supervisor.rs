//! Супервизор и рабочие процессы как процессы (сценарии `DaemonUpgradeTests` .NET): `tasker-mcpd` на свободном порту с изолированными
//! `TASKER_HOME`, `TASKER_SERVICE_DIR`, `TASKER_SERVICE_LABEL`; область — копия корпуса `tests/golden/workspace`. Проверяются
//! файлы состояния и статус с рабочим процессом, перезапуск упавшего рабочего процесса с паузами и отказ после пяти падений за минуту,
//! замена на лету под непрерывной нагрузкой (`/mcp` и `/health`, новые соединения и keep-alive) без единого отказа и без потерянных
//! или задвоенных записей, откаты замены (нет программы, новый процесс упал, не успел, не открыл область), вызов в работе у старого
//! процесса, остановка запросом и сигналом, смерть супервизора, передача сокета сообщением (`TASKER_MCP_HANDOFF=message`).
use serde_json::{Value, json};
use std::io::{BufRead as _, BufReader, Read as _, Write as _};
use std::net::{TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

const BIN: &str = env!("CARGO_BIN_EXE_tasker-mcpd");
const PROJECT: &str = "11111111-1111-4111-8111-111111111111";
const FEATURE: &str = "0000002b-0000-4000-8000-00000000002b";

fn golden() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/golden/workspace")
}

fn copy_dir(from: &Path, to: &Path) {
    std::fs::create_dir_all(to).unwrap();
    for entry in std::fs::read_dir(from).unwrap() {
        let entry = entry.unwrap();
        let target = to.join(entry.file_name());
        if entry.file_type().unwrap().is_dir() {
            copy_dir(&entry.path(), &target);
        } else {
            std::fs::copy(entry.path(), target).unwrap();
        }
    }
}

fn free_port() -> u16 {
    TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port()
}

struct Fixture {
    root: PathBuf,
    port: u16,
    child: Option<Child>,
}

impl Fixture {
    fn new() -> Fixture {
        Fixture::with_env(&[])
    }

    fn with_env(env: &[(&str, &str)]) -> Fixture {
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../target/tmp")
            .join(uuid::Uuid::new_v4().simple().to_string());
        std::fs::create_dir_all(root.join("home")).unwrap();
        let root = canonical(&root);
        copy_dir(&golden(), &root.join("ws"));
        let port = free_port();
        let settings = format!(
            "{{\"mcp\":{{\"port\":{port},\"workspaces\":[{{\"kind\":\"files\",\"path\":\"{}\"}}]}}}}\n",
            root.join("ws").display()
        );
        std::fs::write(root.join("home/settings.json"), settings).unwrap();
        let mut fixture = Fixture { root, port, child: None };
        let child = fixture
            .command()
            .envs(env.iter().copied())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        fixture.child = Some(child);
        fixture.wait_until(|| fixture.status().is_some_and(|s| ready_with_one_active(&s)));
        fixture
    }

    fn command(&self) -> Command {
        let mut command = Command::new(BIN);
        command
            .env("TASKER_HOME", self.root.join("home"))
            .env("TASKER_SERVICE_DIR", self.root.join("home/service"))
            .env("TASKER_SERVICE_LABEL", format!("com.tasker.supervisor-test-{}", self.port))
            .env("TASKER_MCP_DRAIN_QUIET_MS", "300")
            .env_remove("TASKER_MCP_HANDOFF")
            .env_remove("TASKER_MCP_SUPERVISOR");
        command
    }

    fn info(&self) -> Value {
        serde_json::from_str(&std::fs::read_to_string(self.root.join("home/mcp/daemon.json")).unwrap()).unwrap()
    }

    fn token(&self) -> String {
        self.info()["token"].as_str().unwrap().to_string()
    }

    fn supervisor_pid(&self) -> u32 {
        self.child.as_ref().unwrap().id()
    }

    fn status(&self) -> Option<Value> {
        let token = std::fs::read_to_string(self.root.join("home/mcp/daemon.json"))
            .ok()
            .and_then(|t| serde_json::from_str::<Value>(&t).ok())?["token"]
            .as_str()?
            .to_string();
        let (code, body) = http(self.port, "GET", "/daemon/status", &[("X-Tasker-Control", &token)], "").ok()?;
        (code == 200).then(|| serde_json::from_str(&body).ok())?
    }

    fn workers(&self) -> Vec<Value> {
        self.status().map(|s| s["workers"].as_array().unwrap().clone()).unwrap_or_default()
    }

    fn active_worker(&self) -> u32 {
        let workers = self.workers();
        assert_eq!(workers.len(), 1, "{workers:?}");
        assert_eq!(workers[0]["role"], "active");
        workers[0]["pid"].as_u64().unwrap() as u32
    }

    fn upgrade(&self, file: &str, timeout: i64) -> Value {
        let body = json!({"file": file, "arguments": [], "timeoutSeconds": timeout}).to_string();
        let (code, text) = http(self.port, "POST", "/daemon/upgrade", &[("X-Tasker-Control", &self.token())], &body).unwrap();
        assert_eq!(code, 200, "{text}");
        serde_json::from_str(&text).unwrap()
    }

    fn wait_until(&self, condition: impl Fn() -> bool) {
        wait_until(Duration::from_secs(30), condition);
    }

    fn log(&self) -> String {
        std::fs::read_dir(self.root.join("home/logs"))
            .map(|entries| {
                entries
                    .flatten()
                    .filter(|e| e.file_name().to_string_lossy().starts_with("mcp-"))
                    .map(|e| std::fs::read_to_string(e.path()).unwrap_or_default())
                    .collect::<String>()
            })
            .unwrap_or_default()
    }

    fn task_titles(&self) -> Vec<String> {
        let tasks = self.root.join("ws/.tasker/projects").join(PROJECT).join("tasks");
        std::fs::read_dir(tasks)
            .unwrap()
            .flatten()
            .filter_map(|e| std::fs::read_to_string(e.path()).ok())
            .filter_map(|text| {
                text.lines()
                    .find_map(|l| l.strip_prefix("title: ").map(|t| t.trim_matches(['\'', '"']).to_string()))
            })
            .collect()
    }

    /// Ждёт выхода супервизора; код, stdout, stderr.
    fn wait_exit(&mut self, limit: Duration) -> (i32, String, String) {
        let mut child = self.child.take().unwrap();
        let deadline = Instant::now() + limit;
        while child.try_wait().unwrap().is_none() {
            assert!(Instant::now() < deadline, "the supervisor did not exit in {limit:?}");
            std::thread::sleep(Duration::from_millis(50));
        }
        let mut out = String::new();
        let mut err = String::new();
        child.stdout.take().unwrap().read_to_string(&mut out).unwrap();
        child.stderr.take().unwrap().read_to_string(&mut err).unwrap();
        (child.wait().unwrap().code().unwrap_or(-1), out, err)
    }

    fn script(&self, name: &str, body: &str) -> String {
        let path = self.root.join(name);
        std::fs::write(&path, format!("#!/bin/sh\n{body}\n")).unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt as _;
            std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o755)).unwrap();
        }
        path.to_string_lossy().into_owned()
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        if let Some(mut child) = self.child.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
        // Рабочие процессы выходят сами, когда супервизора нет (конец stdin): ждём, чтобы не оставить их за собой.
        wait_until(Duration::from_secs(20), || TcpListener::bind(("127.0.0.1", self.port)).is_ok());
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

fn ready_with_one_active(status: &Value) -> bool {
    status["workers"]
        .as_array()
        .is_some_and(|w| w.len() == 1 && w[0]["role"] == "active" && !w[0]["build"].as_str().unwrap_or("").is_empty())
        && status["workspaces"]
            .as_array()
            .is_some_and(|w| w.iter().all(|x| x["state"] == "open"))
}

fn wait_until(limit: Duration, condition: impl Fn() -> bool) {
    let deadline = Instant::now() + limit;
    while !condition() {
        assert!(Instant::now() < deadline, "the condition did not become true in {limit:?}");
        std::thread::sleep(Duration::from_millis(50));
    }
}

fn alive(pid: u32) -> bool {
    Command::new("kill")
        .args(["-0", &pid.to_string()])
        .stderr(Stdio::null())
        .status()
        .is_ok_and(|s| s.success())
}

/// Запрос по новому соединению с `Connection: close`: код и тело (поток SSE `/mcp` читается до конца).
fn http(port: u16, method: &str, path: &str, headers: &[(&str, &str)], body: &str) -> std::io::Result<(u16, String)> {
    let mut stream = TcpStream::connect(("127.0.0.1", port))?;
    stream.set_read_timeout(Some(Duration::from_secs(60)))?;
    let mut head = format!(
        "{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nContent-Length: {}\r\n",
        body.len()
    );
    for (name, value) in headers {
        head.push_str(&format!("{name}: {value}\r\n"));
    }
    if !body.is_empty() {
        head.push_str("Content-Type: application/json\r\nAccept: application/json, text/event-stream\r\n");
    }
    head.push_str("\r\n");
    stream.write_all(head.as_bytes())?;
    stream.write_all(body.as_bytes())?;
    let mut response = Vec::new();
    stream.read_to_end(&mut response)?;
    let text = String::from_utf8_lossy(&response).into_owned();
    let code = text
        .split(' ')
        .nth(1)
        .and_then(|c| c.parse().ok())
        .ok_or_else(|| std::io::Error::other(format!("no status line: {text:?}")))?;
    let body = text.split_once("\r\n\r\n").map(|(_, b)| b.to_string()).unwrap_or_default();
    Ok((code, body))
}

fn tool_call(name: &str, arguments: Value) -> String {
    json!({"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": name, "arguments": arguments}}).to_string()
}

/// Результат вызова инструмента из ответа SSE: (isError, text).
fn tool_result(body: &str) -> Option<(bool, String)> {
    let data = body.lines().find_map(|l| l.strip_prefix("data:"))?.trim();
    let value: Value = serde_json::from_str(data).ok()?;
    let result = &value["result"];
    Some((
        result["isError"].as_bool().unwrap_or(false),
        result["content"][0]["text"].as_str()?.to_string(),
    ))
}

/// Клиент keep-alive для `/health`: одно соединение, пока сервер не закроет его (`Connection: close` в ответе).
struct KeepAlive {
    port: u16,
    reader: Option<BufReader<TcpStream>>,
}

impl KeepAlive {
    fn get_pid(&mut self) -> Result<u32, String> {
        if self.reader.is_none() {
            let stream = TcpStream::connect(("127.0.0.1", self.port)).map_err(|e| format!("connect: {e}"))?;
            stream.set_read_timeout(Some(Duration::from_secs(30))).unwrap();
            self.reader = Some(BufReader::new(stream));
        }
        let reader = self.reader.as_mut().unwrap();
        let request = format!("GET /health HTTP/1.1\r\nHost: 127.0.0.1:{}\r\n\r\n", self.port);
        reader.get_mut().write_all(request.as_bytes()).map_err(|e| format!("write: {e}"))?;
        let mut status = String::new();
        reader.read_line(&mut status).map_err(|e| format!("read: {e}"))?;
        if !status.starts_with("HTTP/1.1 200") {
            self.reader = None;
            return Err(format!("status {status:?}"));
        }
        let (mut length, mut close) = (0usize, false);
        loop {
            let mut line = String::new();
            reader.read_line(&mut line).map_err(|e| format!("read: {e}"))?;
            let line = line.trim_end().to_ascii_lowercase();
            if line.is_empty() {
                break;
            }
            if let Some(v) = line.strip_prefix("content-length:") {
                length = v.trim().parse().unwrap_or(0);
            }
            if line == "connection: close" {
                close = true;
            }
        }
        let mut body = vec![0u8; length];
        reader.read_exact(&mut body).map_err(|e| format!("body: {e}"))?;
        if close {
            self.reader = None;
        }
        let value: Value = serde_json::from_slice(&body).map_err(|e| format!("json: {e}"))?;
        Ok(value["pid"].as_u64().unwrap() as u32)
    }
}

/// Непрерывная нагрузка: писатели `create_task` по новым соединениям и проба `/health` по keep-alive.
struct Load {
    stop: Arc<AtomicBool>,
    threads: Vec<std::thread::JoinHandle<()>>,
    acknowledged: Arc<Mutex<Vec<String>>>,
    failures: Arc<Mutex<Vec<String>>>,
    served_by: Arc<Mutex<std::collections::BTreeMap<u32, usize>>>,
    calls: Arc<AtomicUsize>,
}

impl Load {
    fn start(port: u16, writers: usize) -> Load {
        let load = Load {
            stop: Arc::new(AtomicBool::new(false)),
            threads: Vec::new(),
            acknowledged: Arc::new(Mutex::new(Vec::new())),
            failures: Arc::new(Mutex::new(Vec::new())),
            served_by: Arc::new(Mutex::new(Default::default())),
            calls: Arc::new(AtomicUsize::new(0)),
        };
        let mut threads = Vec::new();
        for client in 0..writers {
            let (stop, acknowledged, failures, calls) = (
                load.stop.clone(),
                load.acknowledged.clone(),
                load.failures.clone(),
                load.calls.clone(),
            );
            threads.push(std::thread::spawn(move || {
                let mut n = 0;
                while !stop.load(Ordering::SeqCst) {
                    let title = format!("load {client}-{n}");
                    n += 1;
                    let body = tool_call(
                        "create_task",
                        json!({"workspace": "ws", "projectId": PROJECT, "typeId": FEATURE, "title": title}),
                    );
                    calls.fetch_add(1, Ordering::SeqCst);
                    match http(port, "POST", "/mcp", &[], &body) {
                        Ok((200, text)) => match tool_result(&text) {
                            Some((false, _)) => acknowledged.lock().unwrap().push(title),
                            other => failures.lock().unwrap().push(format!("{title}: {other:?} {text}")),
                        },
                        Ok((code, text)) => failures.lock().unwrap().push(format!("{title}: HTTP {code} {text}")),
                        Err(e) => failures.lock().unwrap().push(format!("{title}: {e}")),
                    }
                }
            }));
        }
        for _ in 0..2 {
            let (stop, failures, served_by) = (load.stop.clone(), load.failures.clone(), load.served_by.clone());
            threads.push(std::thread::spawn(move || {
                let mut client = KeepAlive { port, reader: None };
                while !stop.load(Ordering::SeqCst) {
                    match client.get_pid() {
                        Ok(pid) => *served_by.lock().unwrap().entry(pid).or_default() += 1,
                        Err(e) => failures.lock().unwrap().push(format!("health: {e}")),
                    }
                    // Новое соединение тоже: принять его должен кто-то из рабочих процессов.
                    match http(port, "GET", "/health", &[], "") {
                        Ok((200, _)) => {}
                        Ok((code, text)) => failures.lock().unwrap().push(format!("health (new): HTTP {code} {text}")),
                        Err(e) => failures.lock().unwrap().push(format!("health (new): {e}")),
                    }
                    std::thread::sleep(Duration::from_millis(5));
                }
            }));
        }
        Load { threads, ..load }
    }

    fn stop(self) -> Load {
        self.stop.store(true, Ordering::SeqCst);
        for thread in self.threads {
            thread.join().unwrap();
        }
        Load {
            threads: Vec::new(),
            ..self
        }
    }
}

#[test]
fn the_supervisor_holds_the_files_and_the_worker_serves() {
    let fixture = Fixture::new();
    let supervisor = fixture.supervisor_pid();
    let info = fixture.info();
    assert_eq!(info["pid"].as_u64().unwrap() as u32, supervisor);
    assert_eq!(info["port"].as_u64().unwrap() as u16, fixture.port);

    let status = fixture.status().unwrap();
    assert_eq!(status["pid"].as_u64().unwrap() as u32, supervisor);
    assert_eq!(status["supervised"], true);
    assert_eq!(status["startedAt"], info["startedAt"]);
    let worker = fixture.active_worker();
    assert_ne!(worker, supervisor);
    let entry = &status["workers"][0];
    assert_eq!(entry["version"], tasker_mcpd::version::version());
    assert_eq!(entry["build"].as_str().unwrap().len(), 8);
    assert_eq!(
        entry.as_object().unwrap().keys().collect::<Vec<_>>(),
        ["pid", "version", "build", "role", "startedAt"]
    );

    let (code, body) = http(fixture.port, "GET", "/health", &[], "").unwrap();
    assert_eq!(
        (code, body),
        (200, format!("{{\"status\":\"ok\",\"mode\":\"McpDaemon\",\"pid\":{worker}}}"))
    );
    let (code, body) = http(fixture.port, "GET", "/ready", &[], "").unwrap();
    assert_eq!((code, body), (200, format!("{{\"ready\":true,\"pid\":{worker}}}")));
    let (code, body) = http(
        fixture.port,
        "POST",
        "/mcp",
        &[],
        &tool_call("list_projects", json!({"workspace": "ws"})),
    )
    .unwrap();
    assert_eq!(code, 200);
    assert!(tool_result(&body).unwrap().1.contains("Golden"), "{body}");

    // Второй экземпляр не запускается; без секрета управления нет.
    let second = fixture.command().output().unwrap();
    assert_eq!(second.status.code(), Some(1));
    assert_eq!(
        String::from_utf8_lossy(&second.stderr),
        "The MCP server is already running: see 'tasker mcp status'\n"
    );
    assert_eq!(http(fixture.port, "GET", "/daemon/status", &[], "").unwrap().0, 401);
    assert_eq!(http(fixture.port, "POST", "/daemon/upgrade", &[], "{}").unwrap().0, 401);
    let (code, body) = http(
        fixture.port,
        "POST",
        "/daemon/upgrade",
        &[("X-Tasker-Control", &fixture.token())],
        "{}",
    )
    .unwrap();
    assert_eq!(
        (code, body.as_str()),
        (
            400,
            "{\"error\":\"Body {\\\"file\\\", \\\"arguments\\\", \\\"timeoutSeconds\\\"} is required\"}"
        )
    );

    let log = fixture.log();
    assert!(
        log.contains(&format!("[INF] Starting the MCP server (supervisor, pid {supervisor})")),
        "{log}"
    );
    assert!(log.contains(&format!("[INF] Worker {worker} started: {BIN} \n")), "{log}");
    assert!(log.contains(&format!(
        "[INF] MCP server is running on port {} (supervisor pid {supervisor})",
        fixture.port
    )));
    assert!(log.contains("[INF] Accepting calls"));
    assert!(log.contains(&format!("[INF] MCP server worker is ready: http://127.0.0.1:{}/mcp", fixture.port)));
}

#[test]
fn a_crashed_worker_is_started_again_and_a_worker_that_keeps_crashing_ends_the_supervisor() {
    let mut fixture = Fixture::new();
    let first = fixture.active_worker();
    // Обёртка вокруг настоящей программы: падает сразу, если есть файл-метка.
    let marker = fixture.root.join("crash");
    let wrapper = fixture.script(
        "wrapper",
        &format!("if [ -e '{}' ]; then exit 7; fi\nexec '{BIN}' \"$@\"", marker.display()),
    );
    // Упал — запускается снова сразу (пауза 0 с), той же программой.
    Command::new("kill").args(["-9", &first.to_string()]).status().unwrap();
    fixture.wait_until(|| {
        fixture
            .status()
            .is_some_and(|s| ready_with_one_active(&s) && s["workers"][0]["pid"] != first)
    });
    let log = fixture.log();
    assert!(log.contains(&format!("[INF] Worker {first} exited with code 137")), "{log}");
    assert!(
        log.contains("[WRN] The MCP server worker exited unexpectedly: starting it again in 0 s"),
        "{log}"
    );

    // После замены перезапуск идёт новой программой: она падает каждый раз — паузы 1, 2, 5, 10 с, потом супервизор сдаётся.
    let result = fixture.upgrade(&wrapper, 30);
    assert_eq!(result["ok"], true, "{result}");
    let replaced = result["newPid"].as_u64().unwrap() as u32;
    fixture.wait_until(|| !alive(first) && fixture.workers().len() == 1);
    std::fs::write(&marker, "").unwrap();
    let started = Instant::now();
    Command::new("kill").args(["-9", &replaced.to_string()]).status().unwrap();
    let (code, stdout, _) = fixture.wait_exit(Duration::from_secs(60));
    let elapsed = started.elapsed();
    assert_eq!(code, 1, "{stdout}");
    assert!(elapsed >= Duration::from_secs(17), "1+2+5+10 s of pauses, got {elapsed:?}");
    for pause in [1, 2, 5, 10] {
        assert!(
            stdout.contains(&format!(
                "WRN] The MCP server worker exited unexpectedly: starting it again in {pause} s"
            )),
            "{stdout}"
        );
    }
    assert!(stdout.contains("exited with code 7"), "{stdout}");
    assert!(stdout.contains("FTL] The MCP server worker keeps crashing: giving up"), "{stdout}");
    assert!(stdout.contains("INF] MCP server stopped"), "{stdout}");
    assert!(!fixture.root.join("home/mcp/daemon.json").exists());
}

#[test]
fn upgrade_under_continuous_load_refuses_no_connection_and_loses_no_write() {
    let fixture = Fixture::new();
    let old = fixture.active_worker();
    let before = fixture.status().unwrap();
    let load = Load::start(fixture.port, 6);
    std::thread::sleep(Duration::from_millis(1500));

    let first = fixture.upgrade(BIN, 60);
    assert_eq!(first["ok"], true, "{first}");
    let message = first["message"].as_str().unwrap();
    assert!(
        message.starts_with(&format!("The MCP server process is replaced without downtime: {old} (")),
        "{message}"
    );
    assert!(
        message.ends_with("; the old process finishes the calls it has started"),
        "{message}"
    );
    assert_eq!(first["oldPid"].as_u64().unwrap() as u32, old);
    std::thread::sleep(Duration::from_millis(500));
    let second = fixture.upgrade(BIN, 60);
    assert_eq!(second["ok"], true, "{second}");
    std::thread::sleep(Duration::from_millis(1500));
    let load = load.stop();

    let failures = load.failures.lock().unwrap().clone();
    assert!(
        failures.is_empty(),
        "{} failures of {} calls:\n{}",
        failures.len(),
        load.calls.load(Ordering::SeqCst),
        failures[..failures.len().min(10)].join("\n")
    );
    let acknowledged = load.acknowledged.lock().unwrap().clone();
    let mut titles: Vec<String> = fixture.task_titles().into_iter().filter(|t| t.starts_with("load ")).collect();
    titles.sort();
    let mut expected = acknowledged.clone();
    expected.sort();
    assert_eq!(titles, expected, "every acknowledged write is there exactly once");
    assert!(acknowledged.len() > 50, "too little load to prove anything: {}", acknowledged.len());

    let served_by = load.served_by.lock().unwrap().clone();
    assert!(served_by.len() >= 3, "served by: {served_by:?}");
    assert!(served_by.contains_key(&old));
    let after = fixture.status().unwrap();
    assert_eq!(after["pid"], before["pid"]);
    assert_eq!(after["port"], before["port"]);
    fixture.wait_until(|| !alive(old) && fixture.workers().len() == 1);
    let log = fixture.log();
    assert!(
        log.contains(&format!("[INF] Upgrading: starting a new worker next to {old}")),
        "{log}"
    );
    assert!(log.contains("[INF] Draining: no new connections are accepted"), "{log}");
    assert!(log.contains("call(s) aborted): the process exits"), "{log}");
}

#[test]
fn failed_upgrades_leave_the_old_worker_serving() {
    let fixture = Fixture::new();
    let old = fixture.active_worker();

    let missing = fixture.root.join("no-such-daemon").to_string_lossy().into_owned();
    let result = fixture.upgrade(&missing, 60);
    assert_eq!(
        result,
        json!({"ok": false, "message": format!("The program of the new server process is not found: {missing}"), "oldPid": null, "newPid": null, "oldBuild": null, "newBuild": null})
    );

    let broken = fixture.script("broken-daemon", "exit 3");
    let result = fixture.upgrade(&broken, 60);
    assert_eq!(result["ok"], false);
    let message = result["message"].as_str().unwrap();
    assert!(
        message.starts_with(
            "The new MCP server process exited with code 3 before it was ready: the running server is untouched. See the log in "
        ),
        "{message}"
    );
    assert_eq!(result["oldPid"].as_u64().unwrap() as u32, old);
    assert!(result["newPid"].as_u64().is_some());

    let pid_file = fixture.root.join("stuck.pid");
    let stuck = fixture.script("stuck-daemon", &format!("echo $$ > '{}'\nexec sleep 60", pid_file.display()));
    let result = fixture.upgrade(&stuck, 2);
    assert!(
        result["message"]
            .as_str()
            .unwrap()
            .starts_with("The new MCP server process was not ready in 2 s: the running server is untouched"),
        "{result}"
    );
    let stuck_pid: u32 = std::fs::read_to_string(&pid_file).unwrap().trim().parse().unwrap();
    wait_until(Duration::from_secs(10), || !alive(stuck_pid));

    // Новый процесс не открыл область, открытую у старого (require): откат.
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt as _;
        let tasker = fixture.root.join("ws/.tasker");
        std::fs::set_permissions(&tasker, std::fs::Permissions::from_mode(0o000)).unwrap();
        let result = fixture.upgrade(BIN, 30);
        std::fs::set_permissions(&tasker, std::fs::Permissions::from_mode(0o755)).unwrap();
        let message = result["message"].as_str().unwrap();
        assert!(
            message.starts_with(&format!(
                "The new MCP server process cannot open workspaces that are open now ({}: ",
                fixture.root.join("ws").display()
            )),
            "{message}"
        );
        assert!(message.ends_with("): the running server is untouched"), "{message}");
    }

    assert_eq!(fixture.active_worker(), old);
    let (code, body) = http(
        fixture.port,
        "POST",
        "/mcp",
        &[],
        &tool_call("list_projects", json!({"workspace": "ws"})),
    )
    .unwrap();
    assert_eq!(code, 200);
    assert!(body.contains("Golden"));
    assert!(
        fixture
            .log()
            .contains("[WRN] Upgrade rolled back: The new MCP server process exited with code 3")
    );
}

#[test]
fn a_call_in_progress_is_finished_by_the_draining_worker() {
    let fixture = Fixture::new();
    let old = fixture.active_worker();

    let body = tool_call("list_projects", json!({"workspace": "ws"}));
    let mut stream = TcpStream::connect(("127.0.0.1", fixture.port)).unwrap();
    let head = format!(
        "POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{}\r\nContent-Type: application/json\r\nAccept: application/json, text/event-stream\r\nContent-Length: {}\r\n\r\n",
        fixture.port,
        body.len()
    );
    stream.write_all(head.as_bytes()).unwrap();
    stream.write_all(&body.as_bytes()[..10]).unwrap();
    std::thread::sleep(Duration::from_millis(300));

    let port = fixture.port;
    let token = fixture.token();
    let upgrade = std::thread::spawn(move || {
        let body = json!({"file": BIN, "arguments": [], "timeoutSeconds": 60}).to_string();
        http(port, "POST", "/daemon/upgrade", &[("X-Tasker-Control", &token)], &body).unwrap()
    });
    fixture.wait_until(|| {
        let workers = fixture.workers();
        workers.len() == 2
            && workers
                .iter()
                .any(|w| w["role"] == "draining" && w["pid"].as_u64() == Some(u64::from(old)))
    });
    // Тишины (300 мс) давно хватило бы — но вызов не закончен, и старый процесс его ждёт.
    std::thread::sleep(Duration::from_millis(1500));
    assert!(alive(old));
    let (code, answer) = upgrade.join().unwrap();
    assert_eq!(code, 200, "{answer}");

    stream.write_all(&body.as_bytes()[10..]).unwrap();
    stream.set_read_timeout(Some(Duration::from_secs(20))).unwrap();
    let mut response = Vec::new();
    let mut buffer = [0u8; 4096];
    // Ответ закрывает соединение (`Connection: close` при завершении): читаем до конца.
    loop {
        match stream.read(&mut buffer) {
            Ok(0) => break,
            Ok(n) => response.extend_from_slice(&buffer[..n]),
            Err(e) => panic!("{e}: {}", String::from_utf8_lossy(&response)),
        }
    }
    let response = String::from_utf8_lossy(&response);
    assert!(response.starts_with("HTTP/1.1 200"), "{response}");
    assert!(response.to_ascii_lowercase().contains("connection: close"), "{response}");
    assert!(response.contains("Golden"), "{response}");
    fixture.wait_until(|| !alive(old) && fixture.workers().len() == 1);
}

#[test]
fn stop_request_ends_the_supervisor_and_the_worker() {
    let mut fixture = Fixture::new();
    let worker = fixture.active_worker();
    let (code, body) = http(fixture.port, "POST", "/daemon/stop", &[("X-Tasker-Control", &fixture.token())], "").unwrap();
    assert_eq!((code, body.as_str()), (200, "{\"stopping\":true}"));
    let (exit, stdout, stderr) = fixture.wait_exit(Duration::from_secs(30));
    assert_eq!(exit, 0, "{stderr}");
    assert!(
        stdout.contains("INF] Stopping the MCP server: stop requested through the control interface\n"),
        "{stdout}"
    );
    assert!(stdout.contains(&format!("INF] Worker {worker} exited with code 0\n")), "{stdout}");
    assert!(stdout.contains("INF] MCP server stopped\n"), "{stdout}");
    // Журнал рабочего процесса — в stderr (stdout у него — обмен с супервизором).
    assert!(stderr.contains("INF] Stop requested\n"), "{stderr}");
    assert!(stderr.contains("INF] Stop requested by the supervisor\n"), "{stderr}");
    assert!(stderr.contains("INF] Stopping the MCP server worker\n"), "{stderr}");
    assert!(!stdout.contains("@tasker"), "{stdout}");
    assert!(!fixture.root.join("home/mcp/daemon.json").exists());
    assert!(!alive(worker));
}

#[cfg(unix)]
#[test]
fn sigterm_stops_everything_and_a_killed_supervisor_takes_its_worker_away() {
    let mut fixture = Fixture::new();
    let worker = fixture.active_worker();
    Command::new("kill")
        .args(["-TERM", &fixture.supervisor_pid().to_string()])
        .status()
        .unwrap();
    let (exit, stdout, _) = fixture.wait_exit(Duration::from_secs(30));
    assert_eq!(exit, 0);
    assert!(stdout.contains("Stopping the MCP server: SIGTERM"), "{stdout}");
    assert!(!alive(worker));

    let fixture = Fixture::new();
    let worker = fixture.active_worker();
    Command::new("kill")
        .args(["-9", &fixture.supervisor_pid().to_string()])
        .status()
        .unwrap();
    wait_until(Duration::from_secs(30), || !alive(worker));
    wait_until(Duration::from_secs(10), || TcpListener::bind(("127.0.0.1", fixture.port)).is_ok());
    assert!(!tasker_mcpd::files::DaemonFiles::new(fixture.root.join("home/mcp")).is_running());
    assert!(fixture.log().contains("[WRN] The supervisor closed the pipe: stopping"));
}

#[test]
fn the_socket_can_be_handed_over_in_hello_like_on_windows() {
    let fixture = Fixture::with_env(&[("TASKER_MCP_HANDOFF", "message")]);
    let old = fixture.active_worker();
    let (code, body) = http(
        fixture.port,
        "POST",
        "/mcp",
        &[],
        &tool_call("list_projects", json!({"workspace": "ws"})),
    )
    .unwrap();
    assert_eq!(code, 200);
    assert!(body.contains("Golden"));
    assert_eq!(fixture.upgrade(BIN, 60)["ok"], true);
    fixture.wait_until(|| !alive(old) && fixture.workers().len() == 1);
    assert!(http(fixture.port, "GET", "/health", &[], "").unwrap().0 == 200);
    let ps = Command::new("ps")
        .args(["-o", "command=", "-p", &fixture.active_worker().to_string()])
        .output()
        .unwrap();
    let command = String::from_utf8_lossy(&ps.stdout);
    assert!(command.contains("--worker --listen-handoff"), "{command}");
}

/// `canonicalize` без префикса `\\?\` на Windows: программы печатают пути в обычном виде (`D:\…`), и сравнение идёт с ними.
fn canonical(path: impl AsRef<std::path::Path>) -> std::path::PathBuf {
    let path = std::fs::canonicalize(path).unwrap();
    match path.to_str().and_then(|text| text.strip_prefix(r"\\?\")) {
        Some(plain) if cfg!(windows) => std::path::PathBuf::from(plain),
        _ => path,
    }
}
