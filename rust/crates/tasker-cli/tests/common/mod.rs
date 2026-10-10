//! Общее для интеграционных тестов консоли: копия golden-корпуса в `target/tmp`, запуск бинарника, сверка со снапшотами
//! `expected/<dir>/<name>` (аргументы с подстановкой `<ROOT>`, stdout, stderr, код выхода).
#![allow(dead_code)]
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::process::Command;

pub fn golden() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/golden")
}

/// Пустой каталог в `target/tmp`; внутри — копия области под именем `golden`, как у `generate.py` (`<ROOT>/golden`).
pub fn fresh_copy() -> (PathBuf, PathBuf) {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../target/tmp")
        .join(uuid::Uuid::new_v4().simple().to_string());
    std::fs::create_dir_all(&root).unwrap();
    // Канонический путь (без `..`): так же его покажет консоль в сообщениях об ошибках.
    let root = canonical(&root);
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
pub fn snapshot(dir: &str, name: &str, root: &Path) -> (Vec<String>, String, String, i32) {
    let base = golden().join("expected").join(dir).join(name);
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

pub fn assert_snapshot(dir: &str, name: &str, root: &Path) {
    let (args, out, err, code) = snapshot(dir, name, root);
    let args: Vec<&str> = args.iter().map(String::as_str).collect();
    let run = tasker(root, &args);
    assert_eq!(run.stdout, out, "stdout of {dir}/{name}");
    assert_eq!(run.stderr, err, "stderr of {dir}/{name}");
    assert_eq!(run.code, code, "exit code of {dir}/{name}");
}

/// Все файлы под `dir` (кроме `.cache`: его .NET тоже не кладёт в корпус) с содержимым, по относительному пути.
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

/// `canonicalize` без префикса `\\?\` на Windows: программы печатают пути в обычном виде (`D:\…`), и сравнение идёт с ними.
pub fn canonical(path: impl AsRef<std::path::Path>) -> std::path::PathBuf {
    let path = std::fs::canonicalize(path).unwrap();
    match path.to_str().and_then(|text| text.strip_prefix(r"\\?\")) {
        Some(plain) if cfg!(windows) => std::path::PathBuf::from(plain),
        _ => path,
    }
}
