//! Несколько процессов консоли с одной папкой (сценарии `ConcurrencyTests` .NET): чтение ждёт короткую блокировку индекса
//! (`.cache/index-rs.lock`), запись — `write.lock`, общий с .NET; параллельные записи не теряются и не делят номера серии, из двух
//! изменений одной версии проходит одно; простаивающая папка блокировок не держит.
mod common;

use common::*;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::{Child, Stdio};
use std::time::{Duration, Instant};
use tasker_core::io::FileLock;

struct Folder {
    root: PathBuf,
}

impl Folder {
    fn new() -> Folder {
        let root = temp_root();
        std::fs::create_dir_all(root.join("ws")).unwrap();
        Folder { root }
    }

    fn workspace(&self) -> String {
        self.root.join("ws").to_string_lossy().into_owned()
    }

    fn cache(&self) -> PathBuf {
        self.root.join("ws/.tasker/.cache")
    }

    fn with_workspace<'a>(&self, args: &[&'a str], w: &'a str) -> Vec<&'a str> {
        let mut all = args.to_vec();
        all.extend(["-w", w]);
        all
    }

    fn run(&self, args: &[&str]) -> Run {
        let w = self.workspace();
        tasker(&self.root, &self.with_workspace(args, &w))
    }

    fn ok(&self, args: &[&str]) -> String {
        let run = self.run(args);
        assert_eq!(run.code, 0, "{}: {}", args.join(" "), run.stderr);
        run.stdout
    }

    fn start(&self, args: &[&str]) -> Child {
        let w = self.workspace();
        command(&self.root, &self.with_workspace(args, &w))
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap()
    }

    /// Проект Demo с типом задачи Bug и серией D.
    fn demo(&self) {
        self.ok(&["project", "create", "Demo"]);
        self.ok(&["status", "create", "Todo", "-p", "Demo"]);
        self.ok(&["status-set", "create", "Flow", "--status", "Todo", "-p", "Demo"]);
        self.ok(&["task-type", "create", "Bug", "--status-set", "Flow", "-p", "Demo"]);
        self.ok(&["series", "create", "Demo", "--prefix", "D", "-p", "Demo"]);
    }
}

impl Drop for Folder {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

/// Процесс ещё работает спустя `wait` — ждёт блокировку.
fn still_running_after(child: &mut Child, wait: Duration) -> bool {
    let deadline = Instant::now() + wait;
    while Instant::now() < deadline {
        if child.try_wait().unwrap().is_some() {
            return false;
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    true
}

fn finish(mut child: Child) -> Run {
    let deadline = Instant::now() + Duration::from_secs(30);
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break status;
        }
        assert!(Instant::now() < deadline, "the command did not finish after the lock was released");
        std::thread::sleep(Duration::from_millis(50));
    };
    let mut stdout = String::new();
    let mut stderr = String::new();
    child.stdout.take().unwrap().read_to_string(&mut stdout).unwrap();
    child.stderr.take().unwrap().read_to_string(&mut stderr).unwrap();
    Run {
        stdout,
        stderr,
        code: status.code().unwrap_or(-1),
    }
}

fn lock(path: &Path) -> FileLock {
    FileLock::acquire(path, Some(Duration::from_secs(10))).unwrap()
}

#[test]
fn reading_waits_for_the_index_lock_and_goes_on_after_release() {
    let folder = Folder::new();
    folder.ok(&["project", "create", "Demo"]);

    let held = lock(&folder.cache().join("index-rs.lock"));
    let mut child = folder.start(&["project", "list"]);
    assert!(
        still_running_after(&mut child, Duration::from_millis(1500)),
        "the command must wait while the index lock is held"
    );
    drop(held);
    let run = finish(child);
    assert_eq!(run.code, 0, "{}", run.stderr);
    assert!(run.stdout.contains("Demo"), "{}", run.stdout);
}

#[test]
fn writing_waits_for_the_write_lock() {
    let folder = Folder::new();
    folder.ok(&["project", "create", "Demo"]);

    let held = lock(&folder.cache().join("write.lock"));
    let mut child = folder.start(&["project", "create", "Other"]);
    assert!(
        still_running_after(&mut child, Duration::from_millis(1500)),
        "the write must wait while the write lock is held"
    );
    drop(held);
    let run = finish(child);
    assert_eq!(run.code, 0, "{}", run.stderr);
    assert!(folder.ok(&["project", "list"]).contains("Other"));
}

#[test]
fn an_idle_workspace_holds_no_locks() {
    let folder = Folder::new();
    folder.ok(&["project", "create", "Demo"]);
    folder.ok(&["project", "list"]);
    for name in ["index-rs.lock", "write.lock"] {
        drop(FileLock::acquire(&folder.cache().join(name), Some(Duration::from_millis(300))).unwrap());
    }
}

#[test]
fn parallel_processes_all_write_and_the_series_numbers_stay_unique() {
    let folder = Folder::new();
    folder.demo();

    const COUNT: usize = 8;
    let titles: Vec<String> = (0..COUNT).map(|i| format!("Task {i}")).collect();
    let children: Vec<Child> = titles
        .iter()
        .map(|t| folder.start(&["task", "create", t, "--type", "Bug", "--series", "D", "-p", "Demo"]))
        .collect();
    for run in children.into_iter().map(finish) {
        assert_eq!(run.code, 0, "{}", run.stderr);
    }

    let list: serde_json::Value = serde_json::from_str(&folder.ok(&["task", "list", "-p", "Demo", "--limit", "50", "--json"])).unwrap();
    assert_eq!(list["totalCount"], COUNT);
    let data = list["data"].as_array().unwrap();
    let mut got: Vec<&str> = data.iter().map(|x| x["title"].as_str().unwrap()).collect();
    got.sort_unstable();
    assert_eq!(got, titles.iter().map(String::as_str).collect::<Vec<_>>());

    // Каждая задача получила свой номер: D-1 … D-8 без дублей и пропусков.
    let mut references: Vec<String> = (1..=COUNT)
        .map(|n| {
            let run = folder.run(&["task", "get", &format!("D-{n}"), "-p", "Demo", "--json"]);
            assert_eq!(run.code, 0, "D-{n}: {}", run.stderr);
            serde_json::from_str::<serde_json::Value>(&run.stdout).unwrap()["title"]
                .as_str()
                .unwrap()
                .to_string()
        })
        .collect();
    references.sort_unstable();
    assert_eq!(references, titles);
    let health = folder.run(&["sync"]);
    assert_eq!(health.code, 0);
    assert!(!health.stdout.contains("Series problems"), "{}", health.stdout);
}

#[test]
fn of_two_processes_changing_the_same_version_only_one_wins() {
    let folder = Folder::new();
    folder.ok(&["project", "create", "Demo"]);
    folder.ok(&["status", "create", "Todo", "-p", "Demo"]);
    let status: serde_json::Value = serde_json::from_str(&folder.ok(&["status", "get", "Todo", "-p", "Demo", "--json"])).unwrap();
    let id = status["id"].as_str().unwrap().to_string();
    let version = status["version"].as_str().unwrap().to_string();

    let children: Vec<Child> = ["First", "Second"]
        .iter()
        .map(|name| {
            folder.start(&[
                "status",
                "update",
                &id,
                "--name",
                name,
                "--expected-version",
                &version,
                "-p",
                "Demo",
            ])
        })
        .collect();
    let runs: Vec<Run> = children.into_iter().map(finish).collect();
    assert_eq!(runs.iter().filter(|r| r.code == 0).count(), 1);
    let loser = runs.iter().find(|r| r.code == 1).expect("one of the two must lose");
    assert!(loser.stderr.contains("Modified by someone else"), "{}", loser.stderr);
}
