//! `tasker migrate` на копии golden-корпуса (`rust/tests/golden`): вывод, код выхода и вся область после миграции — байт в байт как
//! у .NET (`expected/cli/*migrate*`, `expected/migrate/*`). Бинарник вызывается как процесс (`CARGO_BIN_EXE_tasker`).
mod common;

use common::*;

#[test]
fn check_and_dry_run_on_the_untouched_corpus_match_the_cli_snapshots() {
    let (root, workspace) = fresh_copy();
    let before = files(&workspace.join(".tasker"));
    assert_snapshot("cli", "007-migrate-check", &root);
    assert_snapshot("cli", "008-migrate-check-json", &root);
    assert_snapshot("cli", "009-migrate-dry-run", &root);
    // Ничего не записано: те же файлы с теми же байтами.
    assert_eq!(files(&workspace.join(".tasker")), before);
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn migrate_rewrites_and_renames_the_corpus_exactly_like_dotnet() {
    let (root, workspace) = fresh_copy();
    assert_snapshot("migrate", "001-migrate", &root);

    let actual = files(&workspace.join(".tasker"));
    let expected = files(&golden().join("expected/migrate/tasker"));
    let actual_names: Vec<&String> = actual.keys().collect();
    let expected_names: Vec<&String> = expected.keys().collect();
    assert_eq!(actual_names, expected_names, "file tree after migrate");
    for (name, bytes) in &expected {
        assert!(actual[name] == *bytes, "bytes of {name} differ after migrate");
    }

    // Версии после миграции — как в versions.json корпуса.
    let versions: serde_json::Value =
        serde_json::from_slice(&std::fs::read(golden().join("expected/migrate/versions.json")).unwrap()).unwrap();
    for (name, bytes) in &actual {
        if name.ends_with(".yaml") {
            let expected_version = versions[name].as_str().unwrap_or_else(|| panic!("{name} is not in versions.json"));
            assert_eq!(tasker_core::versioning::version_of(bytes), expected_version, "version of {name}");
        }
    }

    assert_snapshot("migrate", "002-migrate-check-after", &root);
    assert_snapshot("migrate", "003-migrate-again", &root);
    assert_eq!(files(&workspace.join(".tasker")), expected, "a second migrate changes nothing");
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn help_matches_the_system_commandline_snapshot_and_errors_use_the_dotnet_texts() {
    let (root, _workspace) = fresh_copy();
    assert_snapshot("cli", "195-help-migrate", &root);

    let nowhere = root.join("nowhere");
    let run = tasker(&root, &["migrate", "-w", nowhere.to_str().unwrap()]);
    assert_eq!(run.stdout, "");
    assert_eq!(run.stderr, format!("Error: Folder not found: {}\n", nowhere.display()));
    assert_eq!(run.code, 1);

    // Пустая папка: `.tasker` создаётся с .gitignore, мигрировать нечего.
    let empty = root.join("empty");
    std::fs::create_dir_all(&empty).unwrap();
    let run = tasker(&root, &["migrate", "-w", empty.to_str().unwrap()]);
    assert_eq!(run.stdout, "Nothing to migrate: all 0 file(s) are in the current format (9)\n");
    assert_eq!((run.stderr.as_str(), run.code), ("", 0));
    assert!(empty.join(".tasker").join("projects").is_dir());
    assert_eq!(
        std::fs::read_to_string(empty.join(".tasker").join(".gitignore")).unwrap(),
        "# Tasker: local index and locks (rebuilt, not for git) and temporary files of atomic writes\n/.cache/\n*.tmp\n"
    );
    let run = tasker(&root, &["migrate", "--check", "--json", "-w", empty.to_str().unwrap()]);
    assert_eq!(
        run.stdout,
        "{\"applicable\":true,\"dryRun\":false,\"check\":true,\"currentFormat\":9,\"scanned\":0,\"upToDate\":0,\"needsAttention\":false,\"migrated\":[],\"renamed\":[],\"newer\":[],\"unreadable\":[]}\n"
    );
    assert_eq!(run.code, 0);

    // Относительный путь области — от текущего каталога процесса.
    let run = tasker(&root, &["migrate", "--check", "-q", "--workspace", "empty"]);
    assert_eq!(
        run.stdout,
        "Nothing is written (dry run):\nNothing to migrate: all 0 file(s) are in the current format (9)\n"
    );
    assert_eq!(run.code, 0);
    std::fs::remove_dir_all(&root).unwrap();
}
