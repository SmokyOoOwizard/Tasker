//! Справка каждой команды и ошибки разбора — байт в байт как у .NET (System.CommandLine): снапшоты `expected/cli/*help-*` и
//! `*error-*` golden-корпуса. Справку .NET снимали из корня репозитория: подсказки значений (`<R&D|Баг|Фича>`, `<Tasker>`)
//! берутся из его области `.tasker`, поэтому и здесь бинарник запускается из корня репозитория. Глобальные настройки —
//! из пустого `TASKER_HOME`, переменные ширины и проекта сняты.
use std::path::{Path, PathBuf};
use std::process::Command;

fn golden() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/golden")
}

fn repository_root() -> PathBuf {
    std::fs::canonicalize(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../..")).unwrap()
}

fn temp_dir() -> PathBuf {
    let dir = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../target/tmp")
        .join(uuid::Uuid::new_v4().simple().to_string());
    std::fs::create_dir_all(&dir).unwrap();
    std::fs::canonicalize(dir).unwrap()
}

struct Run {
    stdout: String,
    stderr: String,
    code: i32,
}

fn tasker(cwd: &Path, home: &Path, args: &[String], env: &[(&str, &str)]) -> Run {
    let mut command = Command::new(env!("CARGO_BIN_EXE_tasker"));
    command
        .args(args)
        .env("TASKER_HOME", home)
        .env_remove("TASKER_PROJECT")
        .env_remove("TASKER_WIDTH")
        .env_remove("COLUMNS")
        .env_remove("LINES")
        .current_dir(cwd);
    for (name, value) in env {
        command.env(name, value);
    }
    let output = command.output().unwrap();
    Run {
        stdout: String::from_utf8(output.stdout).unwrap(),
        stderr: String::from_utf8(output.stderr).unwrap(),
        code: output.status.code().unwrap(),
    }
}

/// Снапшот `expected/cli/<name>`: аргументы (с подстановкой `<ROOT>`), stdout, stderr, код.
fn snapshot(name: &str, root: &Path) -> (Vec<String>, String, String, i32) {
    let base = golden().join("expected/cli").join(name);
    let read = |ext: &str| std::fs::read_to_string(format!("{}.{ext}", base.display())).unwrap();
    let root_text = root.to_string_lossy().into_owned();
    let args = read("args")
        .lines()
        .filter(|l| !l.is_empty())
        .map(|l| l.replace("<ROOT>", &root_text))
        .collect();
    (
        args,
        read("out").replace("<ROOT>", &root_text),
        read("err").replace("<ROOT>", &root_text),
        read("code").trim().parse().unwrap(),
    )
}

fn help_snapshots() -> Vec<String> {
    let mut names: Vec<String> = std::fs::read_dir(golden().join("expected/cli"))
        .unwrap()
        .filter_map(|e| e.ok()?.file_name().into_string().ok())
        .filter(|n| n.contains("-help-") && n.ends_with(".args"))
        .map(|n| n.trim_end_matches(".args").to_string())
        .collect();
    names.sort();
    names
}

#[test]
fn the_help_of_every_command_matches_the_dotnet_snapshot_byte_for_byte() {
    let home = temp_dir();
    let root = repository_root();
    let names = help_snapshots();
    assert_eq!(names.len(), 112, "help snapshots in the corpus");
    let mut failures = Vec::new();
    for name in &names {
        let (args, out, err, code) = snapshot(name, &root);
        let run = tasker(&root, &home, &args, &[]);
        if run.stdout != out || run.stderr != err || run.code != code {
            failures.push(format!(
                "{name}: code {} (expected {code}), stderr {:?}\n--- expected\n{out}--- actual\n{}",
                run.code, run.stderr, run.stdout
            ));
        }
    }
    assert!(
        failures.is_empty(),
        "{} of {} help snapshots differ:\n{}",
        failures.len(),
        names.len(),
        failures.join("\n")
    );
    std::fs::remove_dir_all(&home).unwrap();
}

/// Копия golden-области под именем `golden` в пустом каталоге (как `<ROOT>/golden` у `generate.py`).
fn golden_copy() -> PathBuf {
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
    let root = temp_dir();
    copy_dir(&golden().join("workspace"), &root.join("golden"));
    root
}

#[test]
fn parse_and_workspace_errors_match_the_dotnet_snapshots() {
    let home = temp_dir();
    let root = golden_copy();
    for name in [
        "120-error-no-project",
        "121-error-unknown-project",
        "122-error-no-workspace",
        "123-error-unknown-command",
    ] {
        let (args, out, err, code) = snapshot(name, &root);
        // Справку в ошибке разбора .NET снимал из корня репозитория (подсказка `<Tasker>`), как и `help-*`.
        let cwd = if name.contains("unknown-command") {
            repository_root()
        } else {
            root.clone()
        };
        let run = tasker(&cwd, &home, &args, &[]);
        assert_eq!(run.stdout, out, "stdout of {name}");
        assert_eq!(run.stderr, err, "stderr of {name}");
        assert_eq!(run.code, code, "exit code of {name}");
    }

    // Опечатка в переменной окружения: предупреждение в stderr перед результатом. Сам список задач (stdout) — TSK-135.
    let (args, _, err, _) = snapshot("124-error-env-typo", &root);
    let run = tasker(&root, &home, &args, &[("TASKER_PROJCET", "Golden")]);
    assert_eq!(
        run.stderr.lines().next().unwrap_or_default().to_string() + "\n",
        err,
        "stderr of 124-error-env-typo"
    );

    // Дополнение по Tab молчит: без предупреждений.
    let run = tasker(
        &root,
        &home,
        &["[suggest:0]".to_string(), String::new()],
        &[("TASKER_PROJCET", "Golden")],
    );
    assert!(!run.stderr.contains("Warning:"), "{}", run.stderr);

    std::fs::remove_dir_all(&home).unwrap();
    std::fs::remove_dir_all(&root).unwrap();
}

#[test]
fn sqlite_is_refused_by_this_build_and_version_prints_the_repository_version() {
    let home = temp_dir();
    let root = golden_copy();
    let run = tasker(&root, &home, &["task".into(), "list".into(), "--sqlite".into(), "x.db".into()], &[]);
    assert_eq!(
        (run.stdout.as_str(), run.stderr.as_str(), run.code),
        (
            "",
            "Error: --sqlite is not supported by this build: use the .NET build of Tasker\n",
            1
        )
    );
    let run = tasker(&root, &home, &["--version".into()], &[]);
    // Номер из VERSION; у сборки из исходников за ним через «+» коммит (build.rs крейта tasker-version), у релиза — ничего.
    let version = std::fs::read_to_string(repository_root().join("VERSION")).unwrap();
    assert_eq!(run.code, 0);
    let shown = run.stdout.strip_suffix('\n').unwrap();
    assert!(
        shown == version.trim() || shown.starts_with(&format!("{}+", version.trim())),
        "{shown}"
    );
    std::fs::remove_dir_all(&home).unwrap();
    std::fs::remove_dir_all(&root).unwrap();
}
