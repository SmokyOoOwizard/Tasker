//! Версия сборки — как `InformationalVersion` у .NET (`Directory.Build.props`, `scripts/release.sh`, `install.sh --from-source`):
//!
//! * `TASKER_VERSION` в окружении сборки — берётся как есть (так `scripts/release.sh` даёт релизу ровно номер из `VERSION`
//!   или из `--version`, как `-p:TaskerVersion` у .NET);
//! * иначе — файл `VERSION` в корне репозитория и через `+` коммит, из которого собрано: `0.1.0+f2a114d`, `0.1.0+f2a114d-dirty`
//!   при незакоммиченных правках отслеживаемых файлов (как `git describe --always --dirty` у `install.sh --from-source` в .NET);
//!   без git (архив исходников) — просто `0.1.0`.
//!
//! Коммит отдельно — `TASKER_BUILD_COMMIT` (пусто без git). Пересборка: при смене `VERSION`, `TASKER_VERSION`, текущего коммита
//! (`HEAD`, ветка, `packed-refs`) и индекса git; «-dirty» отражает состояние на момент последнего запуска этого скрипта.
use std::path::{Path, PathBuf};
use std::process::Command;

fn main() {
    let manifest = PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR"));
    let repo = manifest.join("../../..");
    let version_file = repo.join("VERSION");
    println!("cargo:rerun-if-changed={}", version_file.display());
    println!("cargo:rerun-if-env-changed=TASKER_VERSION");

    let base = std::fs::read_to_string(&version_file)
        .map(|text| text.trim().to_string())
        .unwrap_or_default();
    let base = if base.is_empty() { "0.0.0".to_string() } else { base };

    let commit = commit(&repo);
    let version = match std::env::var("TASKER_VERSION") {
        Ok(value) if !value.trim().is_empty() => value.trim().to_string(),
        _ if base.contains('+') || commit.is_empty() => base,
        _ => format!("{base}+{commit}"),
    };
    println!("cargo:rustc-env=TASKER_BUILD_VERSION={version}");
    println!("cargo:rustc-env=TASKER_BUILD_COMMIT={commit}");
}

/// Короткий хэш `HEAD` и `-dirty` при правках отслеживаемых файлов; пусто, если git нет или это не репозиторий.
fn commit(repo: &Path) -> String {
    let Some(hash) = git(repo, &["rev-parse", "--short=7", "HEAD"]) else {
        return String::new();
    };
    // Что отслеживать для пересборки: HEAD, файл текущей ветки, packed-refs и индекс (у worktree свой git-каталог).
    let mut watched = vec![
        git(repo, &["rev-parse", "--git-path", "HEAD"]),
        git(repo, &["rev-parse", "--git-path", "index"]),
    ];
    watched.push(git(repo, &["rev-parse", "--git-common-dir"]).map(|dir| format!("{dir}/packed-refs")));
    if let Some(branch) = git(repo, &["symbolic-ref", "-q", "HEAD"]) {
        watched.push(git(repo, &["rev-parse", "--git-path", &branch]));
    }
    for path in watched.into_iter().flatten() {
        let path = PathBuf::from(path);
        let path = if path.is_absolute() { path } else { repo.join(path) };
        if path.exists() {
            println!("cargo:rerun-if-changed={}", path.display());
        }
    }
    let dirty = git(repo, &["status", "--porcelain", "--untracked-files=no"]).is_some_and(|out| !out.is_empty());
    if dirty { format!("{hash}-dirty") } else { hash }
}

/// Вывод `git <args>` в каталоге `repo` без перевода строки; `None`, если git не запустился или вернул ошибку.
fn git(repo: &Path, args: &[&str]) -> Option<String> {
    let output = Command::new("git").arg("-C").arg(repo).args(args).output().ok()?;
    output
        .status
        .success()
        .then(|| String::from_utf8_lossy(&output.stdout).trim().to_string())
}
