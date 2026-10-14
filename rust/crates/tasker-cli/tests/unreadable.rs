//! Ссылка на файл, который не удалось прочитать (конфликт слияния git, формат новее), — по полному id и по короткому id — даёт
//! ошибку его чтения с кодом 1, а не «нет такой»: задачи (файлы корпуса в проекте Legacy) и все остальные сущности (файлы с
//! конфликтом, добавленные в копию корпуса).
mod common;

use common::*;
use std::path::Path;

const LEGACY: &str = "22222222-2222-4222-8222-222222222222";
const CONFLICT: &str = "Error: Unresolved git merge conflict (line 3)\n";

/// Файл с маркерами конфликта слияния в строках 3–7.
fn conflicted(path: &Path, id: &str) {
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(
        path,
        format!("formatVersion: 5\nid: {id}\n<<<<<<< HEAD\nname: Ours\n=======\nname: Theirs\n>>>>>>> feature\n"),
    )
    .unwrap();
}

fn assert_error(root: &Path, args: &[&str], stderr: &str) {
    let run = tasker(root, args);
    assert_eq!((run.stdout.as_str(), run.stderr.as_str(), run.code), ("", stderr, 1), "{args:?}");
}

#[test]
fn tasks_by_full_and_short_id_report_the_read_error() {
    let (root, workspace) = fresh_copy();
    let ws = workspace.to_str().unwrap();
    let future =
        "Unsupported format: from-the-future-ffffffff.yaml: format version 99 is newer than this Tasker supports (9): update Tasker\n";
    for reference in ["cccccccc-0000-4000-8000-cccccccccccc", "cccccccc", "CCCCCCCC0000"] {
        assert_error(&root, &["task", "get", reference, "-w", ws, "-p", "Legacy"], CONFLICT);
        assert_error(&root, &["task", "get", reference, "--json", "-w", ws, "-p", "Legacy"], CONFLICT);
        assert_error(
            &root,
            &["task", "update", reference, "--title", "x", "-w", ws, "-p", "Legacy"],
            CONFLICT,
        );
    }
    for reference in ["ffffffff-0000-4000-8000-ffffffffffff", "ffffffff"] {
        assert_error(&root, &["task", "get", reference, "-w", ws, "-p", "Legacy"], future);
    }
    // Короткий id, которому не подходит ни один файл, — по-прежнему «нет такой».
    assert_error(
        &root,
        &["task", "get", "abcdef12", "-w", ws, "-p", "Legacy"],
        "Error: No task 'abcdef12'\n",
    );
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn other_entities_by_full_and_short_id_report_the_read_error() {
    let (root, workspace) = fresh_copy();
    let ws = workspace.to_str().unwrap();
    let project = workspace.join(".tasker/projects").join(LEGACY);
    let entities = [
        ("status", "statuses", "d0000001"),
        ("status-set", "status-sets", "d0000002"),
        ("task-type", "task-types", "d0000003"),
        ("link-type", "link-types", "d0000004"),
        ("field", "fields", "d0000005"),
        ("enum", "enums", "d0000006"),
        ("board", "boards", "d0000007"),
        ("series", "series", "d0000008"),
    ];
    for (_, folder, short) in entities {
        conflicted(
            &project.join(folder).join(format!("broken-{short}.yaml")),
            &format!("{short}-0000-4000-8000-000000000000"),
        );
    }
    conflicted(
        &workspace.join(".tasker/projects/d0000009-0000-4000-8000-000000000000/project.yaml"),
        "d0000009-0000-4000-8000-000000000000",
    );
    conflicted(
        &workspace.join(".tasker/users/d000000a-0000-4000-8000-000000000000.yaml"),
        "d000000a-0000-4000-8000-000000000000",
    );

    for (command, _, short) in entities {
        let full = format!("{short}-0000-4000-8000-000000000000");
        for reference in [full.as_str(), short] {
            assert_error(&root, &[command, "get", reference, "-w", ws, "-p", "Legacy"], CONFLICT);
        }
        // Нет ни прочитанной сущности, ни файла — «нет такой».
        let run = tasker(&root, &[command, "get", "abcdef12", "-w", ws, "-p", "Legacy"]);
        assert_eq!(run.code, 1, "{command}");
        assert!(run.stderr.starts_with("Error: No "), "{command}: {}", run.stderr);
    }
    for reference in ["d0000009-0000-4000-8000-000000000000", "d0000009"] {
        assert_error(&root, &["project", "get", reference, "-w", ws], CONFLICT);
    }
    for command in ["user", "agent"] {
        for reference in ["d000000a-0000-4000-8000-000000000000", "d000000a"] {
            assert_error(&root, &[command, "get", reference, "-w", ws], CONFLICT);
        }
    }
    std::fs::remove_dir_all(&root).unwrap();
}

/// Стандартные типы связей, ещё не сохранённые в файлы, находятся по имени и по id: проверка нечитаемых файлов их не прячет.
#[test]
fn default_link_types_without_files_are_found() {
    let root = temp_root();
    let ok = |args: &[&str]| {
        let run = tasker(&root, args);
        assert_eq!((run.stderr.as_str(), run.code), ("", 0), "{args:?}");
        run.stdout
    };
    ok(&["project", "create", "P"]);
    assert!(
        !root
            .join(".tasker/projects")
            .read_dir()
            .unwrap()
            .any(|project| { project.unwrap().path().join("link-types").exists() })
    );
    let id = ok(&["link-type", "get", "Blocks", "--json"]);
    let id = serde_json::from_str::<serde_json::Value>(&id).unwrap()["id"]
        .as_str()
        .unwrap()
        .to_string();
    ok(&["link-type", "get", &id]);
    ok(&["link-type", "get", &id[..8]]);
}
