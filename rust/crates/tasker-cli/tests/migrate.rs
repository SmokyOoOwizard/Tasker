//! `tasker migrate` на копии golden-корпуса (`rust/tests/golden`): вывод, код выхода и вся область после миграции — байт в байт как
//! у .NET (`expected/cli/*migrate*`, `expected/migrate/*`). Бинарник вызывается как процесс (`CARGO_BIN_EXE_tasker`).
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::process::Command;

fn golden() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/golden")
}

/// Пустой каталог в `target/tmp`; внутри — копия области под именем `golden`, как у `generate.py` (`<ROOT>/golden`).
fn fresh_copy() -> (PathBuf, PathBuf) {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../target/tmp")
        .join(uuid::Uuid::new_v4().simple().to_string());
    std::fs::create_dir_all(&root).unwrap();
    // Канонический путь (без `..`): так же его покажет консоль в сообщениях об ошибках.
    let root = std::fs::canonicalize(&root).unwrap();
    let workspace = root.join("golden");
    copy_dir(&golden().join("workspace"), &workspace);
    (root, workspace)
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

struct Run {
    stdout: String,
    stderr: String,
    code: i32,
}

fn tasker(root: &Path, args: &[&str]) -> Run {
    let output = Command::new(env!("CARGO_BIN_EXE_tasker"))
        .args(args)
        .env_remove("TASKER_PROJECT")
        .current_dir(root)
        .output()
        .unwrap();
    Run {
        stdout: String::from_utf8(output.stdout).unwrap(),
        stderr: String::from_utf8(output.stderr).unwrap(),
        code: output.status.code().unwrap(),
    }
}

/// Снапшот `expected/<dir>/<name>`: аргументы (с подстановкой `<ROOT>`), stdout, stderr, код.
fn snapshot(dir: &str, name: &str, root: &Path) -> (Vec<String>, String, String, i32) {
    let base = golden().join("expected").join(dir).join(name);
    let read = |ext: &str| std::fs::read_to_string(format!("{}.{ext}", base.display())).unwrap();
    let root_text = root.to_string_lossy().into_owned();
    let args = read("args")
        .lines()
        .filter(|l| !l.is_empty())
        .map(|l| l.replace("<ROOT>", &root_text))
        .collect();
    let out = read("out").replace("<ROOT>", &root_text);
    let err = read("err").replace("<ROOT>", &root_text);
    let code = read("code").trim().parse().unwrap();
    (args, out, err, code)
}

fn assert_snapshot(dir: &str, name: &str, root: &Path) {
    let (args, out, err, code) = snapshot(dir, name, root);
    let args: Vec<&str> = args.iter().map(String::as_str).collect();
    let run = tasker(root, &args);
    assert_eq!(run.stdout, out, "stdout of {dir}/{name}");
    assert_eq!(run.stderr, err, "stderr of {dir}/{name}");
    assert_eq!(run.code, code, "exit code of {dir}/{name}");
}

/// Все файлы под `dir` (кроме `.cache`: его .NET тоже не кладёт в корпус) с содержимым, по относительному пути.
fn files(dir: &Path) -> BTreeMap<String, Vec<u8>> {
    fn walk(root: &Path, dir: &Path, out: &mut BTreeMap<String, Vec<u8>>) {
        for entry in std::fs::read_dir(dir).unwrap() {
            let path = entry.unwrap().path();
            if path.file_name().is_some_and(|n| n == ".cache") {
                continue;
            }
            if path.is_dir() {
                walk(root, &path, out);
            } else {
                let relative = path.strip_prefix(root).unwrap().to_string_lossy().replace('\\', "/");
                out.insert(relative, std::fs::read(&path).unwrap());
            }
        }
    }
    let mut out = BTreeMap::new();
    walk(dir, dir, &mut out);
    out
}

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

// Справка `migrate --help` сверяется со снапшотом в `tests/help.rs` (из корня репозитория: подсказки значений берутся из его области).
#[test]
fn errors_use_the_dotnet_texts() {
    let (root, _workspace) = fresh_copy();

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
