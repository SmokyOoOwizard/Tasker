//! Мягкие сторожа скорости консоли — сценарии `PerformanceGuardTests` (.NET, TSK-92): проверяется не абсолютное время (оно зависит
//! от машины и сборки — тесты гоняют отладочную), а отношения внутри одного прогона с запасом в несколько раз: время вызова не растёт
//! с числом уже выполненных вызовов и записей и слабо зависит от числа задач. Настоящий стенд с цифрами — `scripts/perf/bench.py`.
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Instant;

struct Ws {
    root: PathBuf,
}

impl Ws {
    fn seeded(tasks: usize) -> Ws {
        let root = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../target/tmp")
            .join(format!("perf-{}", uuid::Uuid::new_v4().simple()));
        std::fs::create_dir_all(root.join("home")).unwrap();
        let ws = Ws { root };
        ws.ok(&["project", "create", "Perf"]);
        ws.ok(&["status", "create", "Todo"]);
        ws.ok(&["status-set", "create", "Main", "--status", "Todo"]);
        ws.ok(&["task-type", "create", "Bug", "--status-set", "Main"]);
        ws.ok(&["series", "create", "Tasks", "--prefix", "TSK"]);
        ws.add_tasks(0, tasks);
        ws
    }

    fn ok(&self, args: &[&str]) {
        let output = Command::new(env!("CARGO_BIN_EXE_tasker"))
            .args(args)
            .arg("-w")
            .arg(&self.root)
            .env("TASKER_HOME", self.root.join("home"))
            .env_remove("TASKER_PROJECT")
            .env_remove("TASKER_PROFILE")
            .output()
            .unwrap();
        assert!(output.status.success(), "{args:?}: {}", String::from_utf8_lossy(&output.stderr));
    }

    fn add_tasks(&self, from: usize, count: usize) {
        for i in from..from + count {
            self.ok(&["task", "create", &format!("task {i}"), "--type", "Bug", "--series", "TSK"]);
        }
    }

    /// Миллисекунды каждого из `count` подряд идущих вызовов.
    fn time(&self, count: usize, args: &[&str]) -> Vec<f64> {
        (0..count)
            .map(|_| {
                let started = Instant::now();
                self.ok(args);
                started.elapsed().as_secs_f64() * 1000.0
            })
            .collect()
    }
}

impl Drop for Ws {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

fn median(values: &[f64]) -> f64 {
    let mut sorted = values.to_vec();
    sorted.sort_by(f64::total_cmp);
    sorted[sorted.len() / 2]
}

#[test]
fn call_time_does_not_grow_with_the_number_of_calls() {
    let ws = Ws::seeded(100);
    ws.time(10, &["task", "get", "TSK-1"]);
    let times = ws.time(200, &["task", "get", "TSK-1"]);
    let (first, last) = (median(&times[..50]), median(&times[150..]));
    // В норме около 1; тройной запас — от шума соседних процессов, а не от накопления чего-либо на вызов.
    assert!(
        last < first * 3.0 + 20.0,
        "the last 50 calls took {last:.1} ms each (median), the first 50 — {first:.1} ms"
    );
}

#[test]
fn call_time_depends_weakly_on_the_number_of_tasks() {
    let ws = Ws::seeded(50);
    ws.time(10, &["task", "get", "TSK-1"]);
    let few = median(&ws.time(30, &["task", "get", "TSK-1"]));
    ws.add_tasks(50, 450);
    let many = median(&ws.time(30, &["task", "get", "TSK-1"]));
    // Каждый вызов сверяет индекс с файлами (O(число файлов) на stat): в 10 раз больше задач — не больше чем в несколько раз дольше.
    assert!(
        many < few * 4.0 + 30.0,
        "500 tasks: {many:.1} ms per call (median), 50 tasks: {few:.1} ms"
    );
}

#[test]
fn write_time_does_not_grow_with_the_number_of_writes() {
    let ws = Ws::seeded(20);
    let create = ["task", "create", "x", "--type", "Bug", "--series", "TSK"];
    ws.time(5, &create);
    let times = ws.time(120, &create);
    let (first, last) = (median(&times[..30]), median(&times[90..]));
    // Создание сверяет индекс и под блокировкой записи; растёт только число задач (до ~140), поэтому запас шире.
    assert!(
        last < first * 4.0 + 30.0,
        "the last 30 creates took {last:.1} ms each (median), the first 30 — {first:.1} ms"
    );
}
