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

/// Как после слияния веток (`LinkCleanupCliTests`): у задачи связь на удалённую задачу — сверка о ней говорит, чистка убирает.
#[test]
fn after_a_merge_sync_reports_the_dangling_link_and_cleanup_removes_it() {
    use tasker_core::model::TaskLink;
    use tasker_services::Workspace;
    use tasker_services::project::CreateProject;
    use tasker_services::series::CreateSeries;
    use tasker_services::status::CreateStatus;
    use tasker_services::status_set::CreateStatusSet;
    use tasker_services::task::CreateTask;
    use tasker_services::task_type::CreateTaskType;

    let (root, _golden) = fresh_copy();
    let folder = root.join("demo");
    std::fs::create_dir_all(&folder).unwrap();
    let ws = Workspace::open(&folder).unwrap();
    let project = ws.projects().create(&CreateProject { name: "Demo".into() }).unwrap().id;
    let todo = ws
        .statuses()
        .create(
            &project,
            &CreateStatus {
                name: "Todo".into(),
                color: "#112233".into(),
                description: None,
            },
        )
        .unwrap();
    let set = ws
        .status_sets()
        .create(
            &project,
            &CreateStatusSet {
                name: "Flow".into(),
                status_ids: vec![todo.id],
            },
        )
        .unwrap();
    let bug = ws
        .task_types()
        .create(
            &project,
            &CreateTaskType {
                name: "Bug".into(),
                status_set_id: set.id,
                fields: None,
                description: None,
            },
        )
        .unwrap();
    let series = ws.series().create(&project, &CreateSeries::new("Tasks", "TSK")).unwrap();
    let mut base = ws
        .tasks()
        .create(
            &project,
            &CreateTask {
                series_ids: Some(vec![series.id]),
                ..CreateTask::new("Base", bug.id)
            },
        )
        .unwrap();
    let blocks = ws.link_types().find(&project, "Blocks").unwrap().unwrap();
    let target = uuid::Uuid::new_v4();
    base.links.push(TaskLink {
        type_id: blocks.id,
        target_id: target,
    });
    ws.update(&base, &base.version).unwrap().unwrap();
    let folder_text = folder.to_str().unwrap();

    let sync = tasker(&root, &["sync", "-w", folder_text]);
    assert_eq!(sync.code, 0);
    assert!(sync.stdout.contains("Link problems in project 'Demo':"));
    assert!(
        sync.stdout
            .contains("1 task(s) have a link to a task or link type that does not exist (run 'tasker cleanup')")
    );
    assert!(!sync.stdout.contains("Series problems"));
    let json: serde_json::Value = serde_json::from_str(&tasker(&root, &["sync", "--json", "-w", folder_text]).stdout).unwrap();
    assert_eq!(json["series"].as_array().unwrap().len(), 1);
    assert_eq!(json["series"][0]["tasksWithInvalidLinks"], 1);

    let check = tasker(&root, &["cleanup", "--check", "-w", folder_text]);
    assert_eq!(check.code, 2);
    assert!(
        check
            .stdout
            .contains(&format!("removed link 'Blocks' to a task that does not exist ({target})"))
    );
    let dry = tasker(&root, &["cleanup", "--dry-run", "--json", "-w", folder_text]);
    assert_eq!(dry.code, 0);
    let dry: serde_json::Value = serde_json::from_str(&dry.stdout).unwrap();
    assert_eq!(dry["changeCount"], 1);
    let change = &dry["projects"][0]["changes"][0];
    assert_eq!(change["kind"], "removedInvalidLink");
    assert_eq!(change["taskId"], base.id.to_string());
    assert_eq!(change["linkTargetId"], target.to_string());
    assert_eq!(change["seriesId"], serde_json::Value::Null);

    let real = tasker(&root, &["cleanup", "-w", folder_text]);
    assert_eq!((real.code, real.stderr.as_str()), (0, ""));
    assert!(real.stdout.ends_with("1 change(s) made\n"));
    assert!(ws.tasks().get_by_id(&project, &base.id).unwrap().unwrap().links.is_empty());
    assert_eq!(tasker(&root, &["cleanup", "--check", "-w", folder_text]).code, 0);
    assert_eq!(tasker(&root, &["cleanup", "-w", folder_text]).stdout, "Nothing to clean up\n");
    assert!(!tasker(&root, &["sync", "-w", folder_text]).stdout.contains("Link problems"));

    // Нечитаемый файл типа связи: связи не трогаются, скрипту об этом говорят кодом 1. Типы по умолчанию сохраняются первой записью.
    ws.link_types().ensure_defaults(&project).unwrap();
    let link_types = ws.directory().project(&project).link_types();
    let type_file = std::fs::read_dir(&link_types).unwrap().flatten().next().unwrap().path();
    std::fs::write(&type_file, "<<<<<<< HEAD\nname: A\n=======\nname: B\n>>>>>>> branch\n").unwrap();
    let real = tasker(&root, &["cleanup", "-w", folder_text]);
    assert_eq!(real.code, 1);
    assert!(real.stdout.contains("links skipped:"));
    assert_eq!(
        real.stderr,
        "Error: the links between tasks were not checked, the rest was cleaned up (see above)\n"
    );
    std::fs::remove_dir_all(&root).unwrap();
}
