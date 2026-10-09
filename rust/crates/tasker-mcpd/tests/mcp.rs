//! MCP демона на golden-корпусе (`tests/golden/expected/mcp`): `tasker-mcpd` на свободном порту с изолированным `TASKER_HOME`, область
//! `golden` — копия корпуса, область `broken` — несуществующая папка (как у `scripts/golden/generate.py`). Те же вызовы, что снимал
//! скрипт, сравниваются с эталонами .NET семантически: разбор JSON, нормализация `<ROOT>`, `<WSID:…>`, `<LOCAL-AGENT-*>`, `<TIME>`;
//! `isError: false` и порядок ключей конверта не считаются различием; `text` с JSON внутри сравнивается как JSON с порядком ключей,
//! а `tools/list` сверяется и по порядку ключей схем.
use serde_json::{Map, Value, json};
use std::net::TcpListener;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};
use tasker_core::locks::LockedEntity;
use tasker_core::settings::WorkspaceLocation;
use tasker_services::Workspace;
use tasker_services::locks::console_editor;

const BIN: &str = env!("CARGO_BIN_EXE_tasker-mcpd");
const GOLDEN_PROJECT: &str = "11111111-1111-4111-8111-111111111111";
const LEGACY_PROJECT: &str = "22222222-2222-4222-8222-222222222222";
const BUG_ID: &str = "00000035-0000-4000-8000-000000000035";

fn golden() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/golden")
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

/// Корень теста: `home/` (TASKER_HOME), `golden/` (копия корпуса), `broken` (нет).
struct Fixture {
    root: PathBuf,
    port: u16,
    child: Option<Child>,
    http: ureq::Agent,
}

impl Fixture {
    fn start() -> Fixture {
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../target/tmp")
            .join(uuid::Uuid::new_v4().simple().to_string());
        std::fs::create_dir_all(root.join("home")).unwrap();
        let root = std::fs::canonicalize(&root).unwrap();
        copy_dir(&golden().join("workspace"), &root.join("golden"));
        let port = free_port();
        let settings = format!(
            "{{\"mcp\":{{\"port\":{port},\"workspaces\":[{{\"kind\":\"files\",\"path\":\"{}\"}},{{\"kind\":\"files\",\"path\":\"{}\"}}]}}}}\n",
            root.join("golden").display(),
            root.join("broken").display()
        );
        std::fs::write(root.join("home/settings.json"), settings).unwrap();
        let child = Command::new(BIN)
            .env("TASKER_HOME", root.join("home"))
            .env("TASKER_SERVICE_DIR", root.join("home/service"))
            .env("TASKER_SERVICE_LABEL", format!("com.tasker.mcp-test-{port}"))
            .env_remove("TASKER_MCP_OPEN_WAIT_MS")
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        let fixture = Fixture {
            root,
            port,
            child: Some(child),
            http: ureq::Agent::new_with_config(ureq::config::Config::builder().http_status_as_error(false).build()),
        };
        let deadline = Instant::now() + Duration::from_secs(60);
        loop {
            let ready = fixture
                .http
                .get(format!("http://127.0.0.1:{port}/ready"))
                .call()
                .map(|r| r.status().as_u16() == 200)
                .unwrap_or(false);
            if ready {
                break;
            }
            assert!(Instant::now() < deadline, "the daemon did not get ready in 60 s");
            std::thread::sleep(Duration::from_millis(50));
        }
        fixture
    }

    fn golden_path(&self) -> String {
        self.root.join("golden").to_string_lossy().into_owned()
    }

    fn rpc(&self, method: &str, params: Value, headers: &[(&str, &str)]) -> Value {
        let body = json!({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).to_string();
        let mut request = self
            .http
            .post(format!("http://127.0.0.1:{}/mcp", self.port))
            .header("Content-Type", "application/json")
            .header("Accept", "application/json, text/event-stream");
        for (name, value) in headers {
            request = request.header(*name, *value);
        }
        let mut response = request.send(body).unwrap();
        assert_eq!(response.status().as_u16(), 200, "{method}");
        let text = response.body_mut().read_to_string().unwrap();
        let data = text
            .lines()
            .find_map(|line| line.strip_prefix("data:"))
            .unwrap_or_else(|| panic!("no data line in {text}"));
        serde_json::from_str(data.trim()).unwrap()
    }

    fn call(&self, tool: &str, arguments: Value) -> Value {
        self.rpc("tools/call", json!({"name": tool, "arguments": arguments}), &[])
    }

    fn call_with(&self, tool: &str, arguments: Value, headers: &[(&str, &str)]) -> Value {
        self.rpc("tools/call", json!({"name": tool, "arguments": arguments}), headers)
    }

    /// Текст результата инструмента, разобранный как JSON.
    fn result_json(&self, tool: &str, arguments: Value) -> Value {
        let text = self.call(tool, arguments)["result"]["content"][0]["text"]
            .as_str()
            .unwrap()
            .to_string();
        serde_json::from_str(&text).unwrap_or_else(|_| panic!("{tool}: {text}"))
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        if let Some(mut child) = self.child.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

/// Семантическое сравнение ответа с эталоном `expected/mcp/<name>.json`.
struct Snapshots {
    replacements: Vec<(String, String)>,
    failures: Vec<String>,
}

impl Snapshots {
    fn normalize(&self, value: &Value) -> Value {
        let mut text = serde_json::to_string(value).unwrap();
        for (from, to) in &self.replacements {
            text = text.replace(from, to);
        }
        let mut value: Value = serde_json::from_str(&text).unwrap();
        strip_false_is_error(&mut value);
        canonical_texts(&mut value);
        value
    }

    fn check(&mut self, name: &str, actual: &Value) {
        let path = golden().join("expected/mcp").join(format!("{name}.json"));
        let expected: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        let expected = self.normalize(&expected["result"]);
        let actual = self.normalize(&actual["result"]);
        if expected != actual {
            self.failures.push(format!(
                "{name}: expected {}\n       actual {}",
                tasker_core::json::to_string(&expected),
                tasker_core::json::to_string(&actual)
            ));
        }
    }
}

fn strip_false_is_error(value: &mut Value) {
    if let Value::Object(map) = value
        && map.get("isError") == Some(&Value::Bool(false))
    {
        map.remove("isError");
    }
}

/// `text` с JSON внутри — компактная запись с исходным порядком ключей (`preserve_order`): различие в пробелах и экранировании не
/// в счёт, порядок полей ответа — в счёт.
fn canonical_texts(value: &mut Value) {
    match value {
        Value::Object(map) => {
            for (key, item) in map.iter_mut() {
                match item {
                    Value::String(text) if key == "text" => {
                        if let Ok(parsed) = serde_json::from_str::<Value>(text) {
                            *text = serde_json::to_string(&parsed).unwrap();
                        }
                    }
                    _ => canonical_texts(item),
                }
            }
        }
        Value::Array(items) => items.iter_mut().for_each(canonical_texts),
        _ => {}
    }
}

fn scrub_times(value: &Value) -> Value {
    let text = serde_json::to_string(value).unwrap();
    let re = regex::Regex::new(r"\d{4}-\d\d-\d\d[ T]\d\d:\d\d:\d\d(\.\d+)? ?\+00:00").unwrap();
    serde_json::from_str(&re.replace_all(&text, "<TIME>")).unwrap()
}

#[test]
fn mcp_answers_match_the_dotnet_snapshots() {
    let f = Fixture::start();
    let root = f.root.to_string_lossy().into_owned();
    let golden_path = f.golden_path();
    let mut snapshots = Snapshots {
        replacements: vec![
            (root.clone(), "<ROOT>".into()),
            (WorkspaceLocation::files(&golden_path).id().to_string(), "<WSID:golden>".into()),
            (
                WorkspaceLocation::files(&format!("{root}/broken")).id().to_string(),
                "<WSID:broken>".into(),
            ),
        ],
        failures: Vec::new(),
    };
    let g = |extra: Value| -> Value {
        let mut map: Map<String, Value> = json!({"workspace": "golden", "projectId": GOLDEN_PROJECT})
            .as_object()
            .unwrap()
            .clone();
        map.extend(extra.as_object().unwrap().clone());
        Value::Object(map)
    };

    let me = f.result_json("whoami", json!({"workspace": "golden"}));
    for (key, marker) in [
        ("id", "<LOCAL-AGENT-ID>"),
        ("createdAt", "<LOCAL-AGENT-CREATED>"),
        ("version", "<LOCAL-AGENT-VERSION>"),
    ] {
        snapshots
            .replacements
            .push((me[key].as_str().unwrap().to_string(), marker.to_string()));
    }

    snapshots.check(
        "initialize",
        &f.rpc(
            "initialize",
            json!({"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "golden", "version": "0"}}),
            &[],
        ),
    );
    let tools = f.rpc("tools/list", json!({}), &[]);
    snapshots.check("tools-list", &tools);
    // Схемы — ещё и по порядку ключей: таблица отдаётся так, как её объявил .NET.
    let expected_tools: Value =
        serde_json::from_str(&std::fs::read_to_string(golden().join("expected/mcp/tools-list.json")).unwrap()).unwrap();
    assert_eq!(
        tasker_core::json::to_string(&tools["result"]["tools"]),
        tasker_core::json::to_string(&expected_tools["result"]["tools"])
    );
    let mut names: Vec<&str> = tools["result"]["tools"]
        .as_array()
        .unwrap()
        .iter()
        .map(|t| t["name"].as_str().unwrap())
        .collect();
    names.sort_unstable();
    assert_eq!(
        names.join("\n") + "\n",
        std::fs::read_to_string(golden().join("expected/mcp/tool-names.txt")).unwrap()
    );

    snapshots.check("list_workspaces", &f.call("list_workspaces", json!({})));
    snapshots.check("whoami", &f.call("whoami", json!({"workspace": "golden"})));
    snapshots.check("list_projects", &f.call("list_projects", json!({"workspace": "golden"})));
    snapshots.check("list_tasks", &f.call("list_tasks", g(json!({}))));
    snapshots.check(
        "list_tasks-brief",
        &f.call("list_tasks", g(json!({"descriptionLength": 0, "limit": 5}))),
    );
    let bug = f.call("get_task", g(json!({"taskId": BUG_ID})));
    snapshots.check("get_task", &bug);
    let bug_version = {
        let text: Value = serde_json::from_str(bug["result"]["content"][0]["text"].as_str().unwrap()).unwrap();
        text["version"].as_str().unwrap().to_string()
    };
    snapshots.check("get_task-by-reference", &f.call("get_task", g(json!({"taskId": "GLD-1"}))));
    snapshots.check("get_task_links", &f.call("get_task_links", g(json!({"taskId": BUG_ID}))));
    snapshots.check(
        "find_tasks_by_reference",
        &f.call("find_tasks_by_reference", g(json!({"reference": "BUG-1"}))),
    );
    snapshots.check("list_fields", &f.call("list_fields", g(json!({}))));
    snapshots.check("list_enums", &f.call("list_enums", g(json!({}))));
    let boards = f.result_json("list_boards", g(json!({})));
    snapshots.check("list_boards", &f.call("list_boards", g(json!({}))));
    let main_board = boards["data"]
        .as_array()
        .unwrap()
        .iter()
        .find(|b| b["name"] == "Main board")
        .map(|b| b["id"].as_str().unwrap().to_string())
        .unwrap();
    snapshots.check("get_board", &f.call("get_board", g(json!({"boardId": main_board}))));
    snapshots.check("list_link_types", &f.call("list_link_types", g(json!({}))));
    snapshots.check("list_series", &f.call("list_series", g(json!({}))));
    snapshots.check("series_health", &f.call("series_health", g(json!({}))));
    let statuses = f.result_json("list_statuses", g(json!({})));
    snapshots.check("list_statuses", &f.call("list_statuses", g(json!({}))));
    snapshots.check("list_status_sets", &f.call("list_status_sets", g(json!({}))));
    snapshots.check("list_task_types", &f.call("list_task_types", g(json!({}))));
    snapshots.check("list_users", &f.call("list_users", json!({"workspace": "golden"})));
    snapshots.check("list_project_members", &f.call("list_project_members", g(json!({}))));
    snapshots.check(
        "list_workspace_problems",
        &f.call("list_workspace_problems", json!({"workspace": "golden"})),
    );
    snapshots.check("get_lock", &f.call("get_lock", g(json!({"entity": "task", "entityId": BUG_ID}))));

    // По ошибке каждого кода.
    snapshots.check(
        "errors/invalid-no-project",
        &f.call("get_task", json!({"workspace": "golden", "taskId": "GLD-1"})),
    );
    snapshots.check("errors/invalid-bad-id", &f.call("get_task", g(json!({"taskId": "not-a-task"}))));
    snapshots.check("errors/invalid-missing-argument", &f.call("get_task", g(json!({}))));
    snapshots.check("errors/invalid-no-workspace", &f.call("list_projects", json!({})));
    snapshots.check("errors/not_found-task", &f.call("get_task", g(json!({"taskId": "GLD-999"}))));
    snapshots.check(
        "errors/not_found-workspace",
        &f.call("list_projects", json!({"workspace": "nowhere"})),
    );
    snapshots.check(
        "errors/modified",
        &f.call(
            "update_task",
            g(json!({"taskId": BUG_ID, "title": "x", "version": "0000000000000000"})),
        ),
    );
    let done = statuses["data"]
        .as_array()
        .unwrap()
        .iter()
        .find(|s| s["name"] == "Done")
        .cloned()
        .unwrap();
    snapshots.check(
        "errors/in_use",
        &f.call("delete_status", g(json!({"statusId": done["id"], "version": done["version"]}))),
    );
    snapshots.check(
        "errors/forbidden",
        &f.call_with(
            "list_projects",
            json!({"workspace": "golden"}),
            &[("X-Tasker-Agent", "99999999-9999-4999-8999-999999999999")],
        ),
    );
    snapshots.check(
        "errors/unsupported_format",
        &f.call(
            "get_task",
            json!({"workspace": "golden", "projectId": LEGACY_PROJECT, "taskId": "ffffffff-0000-4000-8000-ffffffffffff"}),
        ),
    );
    snapshots.check("errors/failed", &f.call("list_projects", json!({"workspace": "broken"})));

    // `tasker lock acquire task <id>` консолью от имени golden-user: блокировка правки в `.cache/edit-locks`.
    let console = Workspace::open(&golden_path).unwrap().with_editor(console_editor("golden-user"));
    let bug_uuid = uuid::Uuid::parse_str(BUG_ID).unwrap();
    let project = uuid::Uuid::parse_str(GOLDEN_PROJECT).unwrap();
    console
        .entity_locks()
        .acquire(Some(project), LockedEntity::Task, &bug_uuid)
        .unwrap()
        .unwrap();
    let locked = f.call("update_task", g(json!({"taskId": BUG_ID, "title": "x", "version": bug_version})));
    snapshots.check("errors/locked", &scrub_times(&locked));
    console.entity_locks().release(LockedEntity::Task, &bug_uuid).unwrap();

    snapshots.check("errors/unknown-tool", &f.call("no_such_tool", json!({})));

    assert!(
        snapshots.failures.is_empty(),
        "{} snapshot(s) differ:\n{}",
        snapshots.failures.len(),
        snapshots.failures.join("\n")
    );
}

/// `X-Tasker-Agent` с id агента области принимается, с чужим id — `[forbidden]`; пустое и null значение `workspace` — «не указан».
#[test]
fn agent_header_and_workspace_argument_edge_cases() {
    let f = Fixture::start();
    let me = f.result_json("whoami", json!({"workspace": "golden"}));
    let id = me["id"].as_str().unwrap().to_string();
    let same = f.call_with("whoami", json!({"workspace": "golden"}), &[("X-Tasker-Agent", id.as_str())]);
    assert_eq!(same["result"].get("isError"), Some(&Value::Bool(false)));
    let text: Value = serde_json::from_str(same["result"]["content"][0]["text"].as_str().unwrap()).unwrap();
    assert_eq!(text["id"], me["id"]);

    let number = f.call("list_projects", json!({"workspace": 5}));
    assert_eq!(number["result"]["isError"], Value::Bool(true));
    assert!(
        number["result"]["content"][0]["text"]
            .as_str()
            .unwrap()
            .starts_with("[invalid] workspace must be a string"),
        "{number}"
    );

    // Две области, одна из них не открылась: без аргумента неясно, какая нужна.
    let none = f.call("list_projects", json!({"workspace": null}));
    assert!(
        none["result"]["content"][0]["text"]
            .as_str()
            .unwrap()
            .starts_with("[invalid] workspace is required: 2 workspaces"),
        "{none}"
    );

    // Путь области вместо имени и имя без учёта регистра.
    let by_path = f.result_json("list_projects", json!({"workspace": f.golden_path()}));
    assert_eq!(by_path["totalCount"], 2);
    let upper = f.result_json("list_projects", json!({"workspace": "GOLDEN"}));
    assert_eq!(upper["totalCount"], 2);

    // Запись: создать проект и статус, увидеть их в списках; `deleted` — голый текст.
    let created = f.result_json("create_project", json!({"workspace": "golden", "name": "Из MCP «ёлочки» 🙂"}));
    assert_eq!(created["name"], "Из MCP «ёлочки» 🙂");
    let raw = f.call("get_project", json!({"workspace": "golden", "projectId": created["id"]}));
    assert!(raw.to_string().contains("Из MCP «ёлочки» 🙂"), "raw UTF-8 on the wire: {raw}");
    let status = f.result_json(
        "create_status",
        json!({"workspace": "golden", "projectId": created["id"], "name": "Todo", "color": "#112233"}),
    );
    let deleted = f.call(
        "delete_status",
        json!({"workspace": "golden", "projectId": created["id"], "statusId": status["id"], "version": status["version"]}),
    );
    assert_eq!(deleted["result"]["content"][0]["text"], "deleted");
    let removed = f.call(
        "delete_status",
        json!({"workspace": "golden", "projectId": created["id"], "statusId": status["id"], "version": status["version"]}),
    );
    assert!(
        removed["result"]["content"][0]["text"]
            .as_str()
            .unwrap()
            .starts_with("[not_found] Status "),
        "{removed}"
    );
}
