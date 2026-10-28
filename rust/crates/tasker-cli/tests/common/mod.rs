//! Общее для интеграционных тестов консоли: копия golden-корпуса в `target/tmp`, запуск бинарника, сверка со снапшотами
//! `expected/<dir>/<name>` (аргументы с подстановкой `<ROOT>`, stdout, stderr, код выхода).
//! `TASKER_GOLDEN_UPDATE=1 cargo test` переснимает снапшоты текущей сборкой (`.args` не меняются): путь корня теста в выводе
//! заменяется обратно на `<ROOT>`. На Windows обратная замена теряет вид разделителей — снапшоты переснимаются на macOS/Linux.
#![allow(dead_code)]
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::process::Command;

pub fn golden() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../tests/golden")
}

pub fn golden_update() -> bool {
    std::env::var("TASKER_GOLDEN_UPDATE").is_ok_and(|v| !v.is_empty() && v != "0")
}

pub fn repository_root() -> PathBuf {
    canonical(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../.."))
}

/// Пустой каталог в `target/tmp`; внутри — копия области под именем `golden` (`<ROOT>/golden`).
pub fn fresh_copy() -> (PathBuf, PathBuf) {
    // Канонический путь (без `..`): так же его покажет консоль в сообщениях об ошибках.
    let root = temp_root();
    let workspace = root.join("golden");
    copy_dir(&golden().join("workspace"), &workspace);
    (root, workspace)
}

pub fn copy_dir(from: &Path, to: &Path) {
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

pub struct Run {
    pub stdout: String,
    pub stderr: String,
    pub code: i32,
}

pub fn tasker(root: &Path, args: &[&str]) -> Run {
    run(command(root, args))
}

/// Команда консоли в `root`: свои `TASKER_HOME` (`<root>/.home`), место службы и её имя — настоящая установка и её демон
/// (`tasker sync` спрашивает работающий демон) тестов не касаются.
pub fn command(root: &Path, args: &[&str]) -> Command {
    let mut command = Command::new(env!("CARGO_BIN_EXE_tasker"));
    command
        .env_remove("TASKER_GOLDEN_UPDATE")
        .args(args)
        .env_remove("TASKER_PROJECT")
        .env("TASKER_HOME", root.join(".home"))
        .env("TASKER_SERVICE_DIR", root.join(".home").join("service"))
        .env("TASKER_SERVICE_LABEL", format!("com.tasker.cli-test-{}", std::process::id()))
        .current_dir(root);
    command
}

pub fn run(mut command: Command) -> Run {
    let output = command.output().unwrap();
    Run {
        stdout: String::from_utf8(output.stdout).unwrap(),
        stderr: String::from_utf8(output.stderr).unwrap(),
        code: output.status.code().unwrap_or(-1),
    }
}

/// Пустой каталог в `target/tmp` (канонический путь).
pub fn temp_root() -> PathBuf {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../target/tmp")
        .join(uuid::Uuid::new_v4().simple().to_string());
    std::fs::create_dir_all(&root).unwrap();
    canonical(&root)
}

/// Снапшот `expected/<dir>/<name>`: аргументы (с подстановкой `<ROOT>`), stdout, stderr, код.
pub fn snapshot(dir: &str, name: &str, root: &Path) -> (Vec<String>, String, String, i32) {
    snapshot_in(&golden().join("expected").join(dir), name, root)
}

pub fn snapshot_in(dir: &Path, name: &str, root: &Path) -> (Vec<String>, String, String, i32) {
    let base = dir.join(name);
    let read = |ext: &str| std::fs::read_to_string(format!("{}.{ext}", base.display())).unwrap();
    let root_text = root.to_string_lossy().into_owned();
    let args = read("args")
        .lines()
        .filter(|l| !l.is_empty())
        .map(|l| l.replace("<ROOT>", &root_text))
        .collect();
    let out = with_root(&read("out"), &root_text);
    let err = with_root(&read("err"), &root_text);
    let code = read("code").trim().parse().unwrap();
    (args, out, err, code)
}

pub fn write_snapshot(dir: &Path, name: &str, root: &Path, stdout: &str, stderr: &str, code: i32) {
    let root = root.to_string_lossy();
    let base = dir.join(name);
    let write = |ext: &str, text: &str| std::fs::write(format!("{}.{ext}", base.display()), text).unwrap();
    write("out", &without_root(stdout, &root));
    write("err", &without_root(stderr, &root));
    write("code", &format!("{code}\n"));
}

pub fn assert_snapshot(dir: &str, name: &str, root: &Path) {
    let (args, _, _, _) = snapshot(dir, name, root);
    let args: Vec<&str> = args.iter().map(String::as_str).collect();
    let run = tasker(root, &args);
    if golden_update() {
        write_snapshot(&golden().join("expected").join(dir), name, root, &run.stdout, &run.stderr, run.code);
    }
    let (_, out, err, code) = snapshot(dir, name, root);
    assert_eq!(run.stdout, out, "stdout of {dir}/{name}");
    assert_eq!(run.stderr, err, "stderr of {dir}/{name}");
    assert_eq!(run.code, code, "exit code of {dir}/{name}");
}

/// Все файлы под `dir` (кроме `.cache`: индекс и блокировки в корпус не входят) с содержимым, по относительному пути.
pub fn files(dir: &Path) -> BTreeMap<String, Vec<u8>> {
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

/// Подстановка `<ROOT>` в снапшот. Эталоны сняты на Unix (`<ROOT>/golden`); на Windows программа печатает путь с `\`, а в JSON —
/// с экранированным `\\`: разделители в пути сразу после `<ROOT>` приводятся к виду Windows.
fn with_root(text: &str, root: &str) -> String {
    if !cfg!(windows) {
        return text.replace("<ROOT>", root);
    }
    let mut out = String::new();
    for line in text.split_inclusive('\n') {
        let json = line.starts_with('{') || line.starts_with('[');
        let (root, separator) = if json {
            (root.replace('\\', "\\\\"), "\\\\")
        } else {
            (root.to_string(), "\\")
        };
        let mut rest = line;
        while let Some(at) = rest.find("<ROOT>") {
            out.push_str(&rest[..at]);
            out.push_str(&root);
            rest = &rest[at + "<ROOT>".len()..];
            let end = rest
                .find(|c: char| c.is_whitespace() || matches!(c, '"' | ',' | ')' | '\''))
                .unwrap_or(rest.len());
            out.push_str(&rest[..end].replace('/', separator));
            rest = &rest[end..];
        }
        out.push_str(rest);
    }
    out
}

pub fn without_root(text: &str, root: &str) -> String {
    text.replace(root, "<ROOT>")
}

pub fn names(dir: &Path) -> Vec<String> {
    let mut names: Vec<String> = std::fs::read_dir(dir)
        .unwrap()
        .filter_map(|e| e.ok()?.file_name().into_string().ok())
        .filter(|n| n.ends_with(".args"))
        .map(|n| n.trim_end_matches(".args").to_string())
        .collect();
    names.sort();
    names
}

/// Время по часам машины (срок блокировки правки) → `<TIME>`.
pub fn scrub(text: &str) -> String {
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

pub struct Failure {
    pub name: String,
    pub detail: String,
}

#[allow(clippy::too_many_arguments)]
pub fn check(
    dir: &Path,
    label: &str,
    name: &str,
    root: &Path,
    home: &Path,
    cwd: &Path,
    env: &[(&str, &str)],
    scrub_times: bool,
    failures: &mut Vec<Failure>,
) {
    let (args, _, _, _) = snapshot_in(dir, name, root);
    let mut command = Command::new(env!("CARGO_BIN_EXE_tasker"));
    command
        .env_remove("TASKER_GOLDEN_UPDATE")
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
    if golden_update() {
        write_snapshot(dir, name, root, &stdout, &stderr, actual_code);
    }
    let (_, out, err, code) = snapshot_in(dir, name, root);
    if stdout != out || stderr != err || actual_code != code {
        let mut detail = format!("code {actual_code} (expected {code})");
        if stdout != out {
            detail.push_str(&format!("\n--- expected stdout\n{out}--- actual stdout\n{stdout}"));
        }
        if stderr != err {
            detail.push_str(&format!("\n--- expected stderr\n{err}--- actual stderr\n{stderr}"));
        }
        failures.push(Failure {
            name: format!("{label}/{name}"),
            detail,
        });
    }
}

pub fn wanted(name: &str) -> bool {
    match std::env::var("GOLDEN_FILTER") {
        Ok(filter) if !filter.is_empty() => filter.split(',').any(|f| name.contains(f)),
        _ => true,
    }
}

/// `canonicalize` без префикса `\\?\` на Windows: программы печатают пути в обычном виде (`D:\…`), и сравнение идёт с ними.
pub fn canonical(path: impl AsRef<std::path::Path>) -> std::path::PathBuf {
    let path = std::fs::canonicalize(path).unwrap();
    match path.to_str().and_then(|text| text.strip_prefix(r"\\?\")) {
        Some(plain) if cfg!(windows) => std::path::PathBuf::from(plain),
        _ => path,
    }
}
