//! Раздел SQLite golden-корпуса: база `sqlite/workspace.sql` восстанавливается в `<ROOT>/golden.db` (журнал WAL, как у файла,
//! созданного консолью), и каждый снапшот `sqlite/expected/cli/*` — те же аргументы (`--sqlite <ROOT>/golden.db`), stdout,
//! stderr и код выхода. Пустой `TASKER_HOME` с именем `golden-user`, ошибка разбора — из корня репозитория, время блокировок —
//! `<TIME>`. `GOLDEN_FILTER=<подстрока>` ограничивает набор, `TASKER_GOLDEN_UPDATE=1` переснимает снапшоты (см. `common`).
mod common;

use common::*;
use std::process::Command;

#[test]
fn every_sqlite_cli_snapshot_of_the_corpus_matches() {
    let root = temp_root();
    let connection = rusqlite::Connection::open(root.join("golden.db")).unwrap();
    let _: String = connection.query_row("PRAGMA journal_mode = wal", [], |r| r.get(0)).unwrap();
    connection
        .execute_batch(&std::fs::read_to_string(golden().join("sqlite/workspace.sql")).unwrap())
        .unwrap();
    drop(connection);
    let home = root.join("home");
    std::fs::create_dir_all(&home).unwrap();
    let repo = repository_root();

    let status = Command::new(env!("CARGO_BIN_EXE_tasker"))
        .env_remove("TASKER_GOLDEN_UPDATE")
        .args(["whoami", "golden-user"])
        .env("TASKER_HOME", &home)
        .current_dir(&root)
        .status()
        .unwrap();
    assert!(status.success(), "whoami golden-user");

    let mut failures: Vec<Failure> = Vec::new();
    let mut checked = 0;
    let cli = golden().join("sqlite/expected/cli");
    for name in names(&cli) {
        if !wanted(&name) {
            continue;
        }
        checked += 1;
        let cwd = if name.contains("error-unknown-command") {
            repo.clone()
        } else {
            root.clone()
        };
        let env: &[(&str, &str)] = if name.contains("error-env-typo") {
            &[("TASKER_PROJCET", "Golden")]
        } else {
            &[]
        };
        let scrub_times = name.contains("lock-acquire") || name.contains("lock-show-held");
        check(&cli, "sqlite/cli", &name, &root, &home, &cwd, env, scrub_times, &mut failures);
    }
    assert!(
        checked > 100 || std::env::var("GOLDEN_FILTER").is_ok_and(|f| !f.is_empty()),
        "only {checked} snapshots"
    );

    if failures.is_empty() {
        std::fs::remove_dir_all(&root).unwrap();
    }
    let report: Vec<String> = failures.iter().map(|f| format!("{}: {}", f.name, f.detail)).collect();
    assert!(
        failures.is_empty(),
        "{} of {checked} snapshots differ:\n{}",
        failures.len(),
        report.join("\n\n")
    );
}
