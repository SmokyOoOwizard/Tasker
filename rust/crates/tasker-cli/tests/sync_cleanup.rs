//! `tasker sync` и `tasker cleanup` на копии golden-корпуса: вывод, коды выхода и JSON — как у .NET (`expected/cli/001-006`,
//! `194-help-cleanup`, `213-help-sync`); после `migrate` сверка показывает только два известных нечитаемых файла.
mod common;

use common::*;

#[test]
fn sync_and_cleanup_check_on_the_untouched_corpus_match_the_cli_snapshots() {
    let (root, workspace) = fresh_copy();
    let before = files(&workspace.join(".tasker"));
    assert_snapshot("cli", "001-sync", &root);
    assert_snapshot("cli", "002-sync-json", &root);
    assert_snapshot("cli", "003-sync-quiet", &root);
    assert_snapshot("cli", "004-cleanup-check", &root);
    assert_snapshot("cli", "005-cleanup-check-json", &root);
    assert_snapshot("cli", "006-cleanup-dry-run", &root);
    assert_snapshot("cli", "194-help-cleanup", &root);
    assert_snapshot("cli", "213-help-sync", &root);
    // Ничего не записано: те же файлы с теми же байтами.
    assert_eq!(files(&workspace.join(".tasker")), before);
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn after_migrate_sync_reports_only_the_two_known_unreadable_files() {
    let (root, workspace) = fresh_copy();
    let run = tasker(&root, &["migrate", "-w", workspace.to_str().unwrap()]);
    assert_eq!(run.code, 1, "migrate leaves the two unreadable files");

    let run = tasker(&root, &["sync", "-w", workspace.to_str().unwrap()]);
    assert_eq!(run.code, 0);
    assert_eq!(run.stderr, "");
    let lines: Vec<&str> = run.stdout.lines().collect();
    assert_eq!(lines[0], format!("Synced {} (via direct)", workspace.display()));
    assert_eq!(lines[1], "2 file(s) cannot be read and are missing from the lists until fixed:");
    assert!(lines[2].ends_with("conflicted-title-ours-cccccccc.yaml: Unresolved git merge conflict (line 3)"));
    assert!(lines[3].contains("from-the-future-ffffffff.yaml: format version 99 is newer than this Tasker supports (9): update Tasker"));
    assert_eq!(lines[4], "Link problems in project 'Legacy':");
    assert_eq!(lines.len(), 6);

    // Чистка без записи: связи проекта Legacy не проверяются из-за нечитаемых файлов; Golden чист.
    let run = tasker(&root, &["cleanup", "--check", "--json", "-w", workspace.to_str().unwrap()]);
    assert_eq!(run.code, 2);
    let value: serde_json::Value = serde_json::from_str(&run.stdout).unwrap();
    assert_eq!(value["needsAttention"], true);
    assert_eq!(value["changeCount"], 0);
    assert_eq!(value["projects"][0]["project"], "Golden");
    assert_eq!(value["projects"][0]["linksSkipReason"], serde_json::Value::Null);
    assert!(
        value["projects"][1]["linksSkipReason"]
            .as_str()
            .unwrap()
            .starts_with("2 task or link type file(s) cannot be read")
    );
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn sync_outside_a_workspace_is_an_error_unless_quiet() {
    let (root, _workspace) = fresh_copy();
    let empty = root.join("plain");
    std::fs::create_dir_all(&empty).unwrap();
    let run = tasker(&root, &["sync", "-w", empty.to_str().unwrap()]);
    assert_eq!(run.stdout, "");
    assert_eq!(
        run.stderr,
        format!("Error: {} is not a Tasker workspace: there is no .tasker folder\n", empty.display())
    );
    assert_eq!(run.code, 1);

    // Режим хука git: молчим и выходим с 0.
    let run = tasker(&root, &["sync", "-q", "-w", empty.to_str().unwrap()]);
    assert_eq!((run.stdout.as_str(), run.stderr.as_str(), run.code), ("", "", 0));
    let run = tasker(&root, &["sync", "-q", "-w", root.join("nowhere").to_str().unwrap()]);
    assert_eq!((run.stdout.as_str(), run.stderr.as_str(), run.code), ("", "", 0));

    // Проект по -p: имя, id, несуществующий.
    let workspace = root.join("golden");
    let run = tasker(&root, &["cleanup", "--check", "-p", "golden", "-w", workspace.to_str().unwrap()]);
    assert_eq!(run.stdout, "Nothing is written (dry run):\nNothing to clean up\n");
    assert_eq!(run.code, 0);
    let run = tasker(&root, &["cleanup", "--check", "-p", "22222222", "-w", workspace.to_str().unwrap()]);
    assert!(run.stdout.contains("Project 'Legacy':"));
    assert_eq!(run.code, 2);
    let run = tasker(&root, &["cleanup", "--check", "-p", "Nope", "-w", workspace.to_str().unwrap()]);
    assert_eq!(run.stderr, "Error: No project 'Nope'\n");
    assert_eq!(run.code, 1);
    std::fs::remove_dir_all(&root).unwrap();
}
