//! Весь golden-корпус консоли без Python: каждый снапшот `expected/cli/*` и `expected/migrate/*` — те же аргументы (`*.args` с
//! подстановкой `<ROOT>`), тот же stdout, stderr и код выхода. Как `scripts/golden/generate.py --verify --only cli,migrate`:
//! копия области `<ROOT>/golden`, пустой `TASKER_HOME` с именем `golden-user`, справка и ошибки разбора — из корня репозитория
//! (подсказки `<Tasker>` берутся из его области), директива `[suggest]` — из папки области, время блокировок — `<TIME>`.
//! `GOLDEN_FILTER=<подстрока>` ограничивает набор при отладке.
mod common;

use common::*;
use std::path::{Path, PathBuf};
use std::process::Command;

fn repository_root() -> PathBuf {
    canonical(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../.."))
}

fn names(dir: &str) -> Vec<String> {
    let mut names: Vec<String> = std::fs::read_dir(golden().join("expected").join(dir))
        .unwrap()
        .filter_map(|e| e.ok()?.file_name().into_string().ok())
        .filter(|n| n.ends_with(".args"))
        .map(|n| n.trim_end_matches(".args").to_string())
        .collect();
    names.sort();
    names
}

/// Время по часам машины (срок блокировки правки) → `<TIME>`, как `scrub` в generate.py.
fn scrub(text: &str) -> String {
    let bytes = text.as_bytes();
    let mut out = String::new();
    let mut i = 0;
    while i < bytes.len() {
        if let Some(len) = wall_clock_len(&text[i..]) {
            out.push_str("<TIME>");
            i += len;
        } else {
            let c = text[i..].chars().next().unwrap();
            out.push(c);
            i += c.len_utf8();
        }
    }
    out
}

/// `\d{4}-\d\d-\d\d[ T]\d\d:\d\d:\d\d(\.\d+)? ?\+00:00` в начале текста; длина совпадения.
fn wall_clock_len(text: &str) -> Option<usize> {
    let b = text.as_bytes();
    let digits = |from: usize, count: usize| b.len() >= from + count && b[from..from + count].iter().all(u8::is_ascii_digit);
    if !(digits(0, 4) && b.get(4) == Some(&b'-') && digits(5, 2) && b.get(7) == Some(&b'-') && digits(8, 2)) {
        return None;
    }
    if !(matches!(b.get(10), Some(b' ') | Some(b'T')) && digits(11, 2) && b.get(13) == Some(&b':') && digits(14, 2)) {
        return None;
    }
    if b.get(16) != Some(&b':') || !digits(17, 2) {
        return None;
    }
    let mut at = 19;
    if b.get(at) == Some(&b'.') {
        let mut end = at + 1;
        while end < b.len() && b[end].is_ascii_digit() {
            end += 1;
        }
        if end == at + 1 {
            return None;
        }
        at = end;
    }
    if b.get(at) == Some(&b' ') {
        at += 1;
    }
    if text[at..].starts_with("+00:00") { Some(at + 6) } else { None }
}

struct Failure {
    name: String,
    detail: String,
}

#[allow(clippy::too_many_arguments)]
fn check(
    dir: &str,
    name: &str,
    root: &Path,
    home: &Path,
    cwd: &Path,
    env: &[(&str, &str)],
    scrub_times: bool,
    failures: &mut Vec<Failure>,
) {
    let (args, out, err, code) = snapshot(dir, name, root);
    let mut command = Command::new(env!("CARGO_BIN_EXE_tasker"));
    command
        .args(&args)
        .env("TASKER_HOME", home)
        .env_remove("TASKER_PROJECT")
        .env_remove("TASKER_WIDTH")
        .env_remove("TASKER_PROFILE")
        .env_remove("TASKER_LANG")
        .env_remove("COLUMNS")
        .env_remove("LINES")
        .current_dir(cwd);
    for (key, value) in env {
        command.env(key, value);
    }
    let output = command.output().unwrap();
    let mut stdout = String::from_utf8_lossy(&output.stdout).into_owned();
    let mut stderr = String::from_utf8_lossy(&output.stderr).into_owned();
    if scrub_times {
        stdout = scrub(&stdout);
        stderr = scrub(&stderr);
    }
    let actual_code = output.status.code().unwrap_or(-1);
    if stdout != out || stderr != err || actual_code != code {
        let mut detail = format!("code {actual_code} (expected {code})");
        if stdout != out {
            detail.push_str(&format!("\n--- expected stdout\n{out}--- actual stdout\n{stdout}"));
        }
        if stderr != err {
            detail.push_str(&format!("\n--- expected stderr\n{err}--- actual stderr\n{stderr}"));
        }
        failures.push(Failure {
            name: format!("{dir}/{name}"),
            detail,
        });
    }
}

fn wanted(name: &str) -> bool {
    match std::env::var("GOLDEN_FILTER") {
        Ok(filter) if !filter.is_empty() => filter.split(',').any(|f| name.contains(f)),
        _ => true,
    }
}

#[test]
fn every_cli_and_migrate_snapshot_of_the_corpus_matches_dotnet() {
    let (root, workspace) = fresh_copy();
    let home = root.join("home");
    std::fs::create_dir_all(&home).unwrap();
    let repo = repository_root();

    // Имя держателя блокировок в снапшотах не зависит от пользователя ОС (как `t.run("whoami", "golden-user")` в generate.py).
    let status = Command::new(env!("CARGO_BIN_EXE_tasker"))
        .args(["whoami", "golden-user"])
        .env("TASKER_HOME", &home)
        .current_dir(&root)
        .status()
        .unwrap();
    assert!(status.success(), "whoami golden-user");

    let mut failures: Vec<Failure> = Vec::new();
    let mut checked = 0;
    for name in names("cli") {
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
        check("cli", &name, &root, &home, &cwd, env, scrub_times, &mut failures);
    }

    // migrate: своя копия области (`<ROOT>/migrate/golden`), как у generate.py.
    let migrate_root = root.join("migrate");
    copy_dir(&golden().join("workspace"), &migrate_root.join("golden"));
    for name in names("migrate") {
        if !wanted(&name) {
            continue;
        }
        checked += 1;
        check("migrate", &name, &migrate_root, &home, &migrate_root, &[], false, &mut failures);
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

/// `canonicalize` без префикса `\\?\` на Windows: программы печатают пути в обычном виде (`D:\…`), и сравнение идёт с ними.
fn canonical(path: impl AsRef<std::path::Path>) -> std::path::PathBuf {
    let path = std::fs::canonicalize(path).unwrap();
    match path.to_str().and_then(|text| text.strip_prefix(r"\\?\")) {
        Some(plain) if cfg!(windows) => std::path::PathBuf::from(plain),
        _ => path,
    }
}
