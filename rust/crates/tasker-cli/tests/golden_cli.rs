//! Весь golden-корпус консоли: каждый снапшот `expected/cli/*` и `expected/migrate/*` — те же аргументы (`*.args` с
//! подстановкой `<ROOT>`), тот же stdout, stderr и код выхода. Копия области `<ROOT>/golden`, пустой `TASKER_HOME` с именем
//! `golden-user`, справка и ошибки разбора — из корня репозитория (подсказки `<Tasker>` берутся из его области), директива
//! `[suggest]` — из папки области, время блокировок — `<TIME>`. `GOLDEN_FILTER=<подстрока>` ограничивает набор при отладке.
//! `TASKER_GOLDEN_UPDATE=1` переснимает stdout, stderr и код (см. `common`).
mod common;

use common::*;
use std::process::Command;

#[test]
fn every_cli_and_migrate_snapshot_of_the_corpus_matches() {
    let (root, workspace) = fresh_copy();
    let home = root.join("home");
    std::fs::create_dir_all(&home).unwrap();
    let repo = repository_root();

    // Имя держателя блокировок в снапшотах не зависит от пользователя ОС (`tasker whoami golden-user`).
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
    let cli = golden().join("expected/cli");
    for name in names(&cli) {
        if !wanted(&name) {
            continue;
        }
        checked += 1;
        let from_repo = name.contains("-help-") || name.contains("error-unknown-command");
        let cwd = if from_repo {
            repo.clone()
        } else if name.contains("-suggest-") {
            workspace.clone()
        } else {
            root.clone()
        };
        let env: &[(&str, &str)] = if name.contains("error-env-typo") {
            &[("TASKER_PROJCET", "Golden")]
        } else {
            &[]
        };
        let scrub_times = name.contains("lock-acquire") || name.contains("lock-show-held");
        check(&cli, "cli", &name, &root, &home, &cwd, env, scrub_times, &mut failures);
    }

    // migrate: своя копия области (`<ROOT>/migrate/golden`).
    let migrate_root = root.join("migrate");
    copy_dir(&golden().join("workspace"), &migrate_root.join("golden"));
    let migrate = golden().join("expected/migrate");
    for name in names(&migrate) {
        if !wanted(&name) {
            continue;
        }
        checked += 1;
        check(
            &migrate,
            "migrate",
            &name,
            &migrate_root,
            &home,
            &migrate_root,
            &[],
            false,
            &mut failures,
        );
    }

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
