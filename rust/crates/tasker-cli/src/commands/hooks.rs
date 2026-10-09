//! `tasker hooks …` (`HooksCommands`, `GitHooks` в .NET): git-хуки `post-merge`, `post-checkout`, `post-rewrite`, которые после
//! `pull`, `checkout`, `merge` и `rebase` запускают `tasker sync` для рабочей папки. Блок Tasker помечен маркерами и вставляется
//! в начало хука, чужие команды остаются; установка идемпотентна, удаление убирает только блок.
use crate::context::Context;
use crate::errors::{CliError, Result};
use crate::json::object;
use clap::ArgMatches;
use serde_json::Value;
use std::path::{Path, PathBuf};
use tasker_core::io::full_path;
use tasker_core::settings::canonical_path;
use tasker_files::layout::NAME;

pub const NAMES: [&str; 3] = ["post-merge", "post-checkout", "post-rewrite"];

const BEGIN: &str = "# >>> tasker (managed by 'tasker hooks install', do not edit) >>>";
const END: &str = "# <<< tasker <<<";

/// Состояние одного git-хука: есть ли блок Tasker и есть ли чужие команды.
pub struct HookState {
    pub name: &'static str,
    pub path: PathBuf,
    pub installed: bool,
    pub foreign: bool,
}

/// Где лежит рабочая папка в репозитории git и куда ставить хуки.
pub struct HookTarget {
    /// Путь рабочей папки от корня через «/»; пусто — сама корневая папка.
    pub relative: String,
    pub hooks_directory: PathBuf,
}

fn git(directory: &str, arguments: &[&str]) -> Result<String> {
    let output = std::process::Command::new("git")
        .arg("-C")
        .arg(directory)
        .args(arguments)
        .output()
        .map_err(|e| CliError::new(format!("git {} failed: {e}", arguments.join(" "))))?;
    if !output.status.success() {
        let message = String::from_utf8_lossy(&output.stderr).trim().to_string();
        return Err(CliError::new(if message.to_lowercase().contains("not a git repository") {
            format!("{directory} is not in a git repository")
        } else {
            format!("git {} failed: {message}", arguments.join(" "))
        }));
    }
    Ok(String::from_utf8_lossy(&output.stdout).trim().to_string())
}

pub fn locate(workspace_folder: &str) -> Result<HookTarget> {
    let workspace = canonical_path(workspace_folder);
    if !Path::new(&workspace).join(NAME).is_dir() {
        return Err(CliError::new(format!(
            "{workspace} is not a Tasker workspace: there is no {NAME} folder"
        )));
    }
    let top = canonical_path(&git(&workspace, &["rev-parse", "--show-toplevel"])?);
    // Абсолютный путь: core.hooksPath может быть относительным, а из подпапки git отдал бы путь от неё.
    let hooks = git(&workspace, &["rev-parse", "--path-format=absolute", "--git-path", "hooks"])?;
    let relative = relative_path(&top, &workspace);
    Ok(HookTarget {
        relative,
        hooks_directory: tasker_core::io::full_path_in(Path::new(&workspace), &hooks),
    })
}

/// `Path.GetRelativePath(top, workspace)` через «/»; сама папка — пусто.
fn relative_path(top: &str, workspace: &str) -> String {
    let top = Path::new(top);
    let workspace = Path::new(workspace);
    match workspace.strip_prefix(top) {
        Ok(rest) if rest.as_os_str().is_empty() => String::new(),
        Ok(rest) => rest.to_string_lossy().replace('\\', "/"),
        Err(_) => {
            // Папка вне корня (в другом рабочем дереве): столько `..`, сколько нужно.
            let mut ups = Vec::new();
            let mut current = top.to_path_buf();
            while !workspace.starts_with(&current) {
                ups.push("..");
                if !current.pop() {
                    break;
                }
            }
            let rest = workspace
                .strip_prefix(&current)
                .map(|p| p.to_string_lossy().replace('\\', "/"))
                .unwrap_or_default();
            let mut parts = ups;
            if !rest.is_empty() {
                parts.push(&rest);
            }
            parts.join("/")
        }
    }
}

fn quote(value: &str) -> String {
    format!("'{}'", value.replace('\'', "'\\''"))
}

/// Путь для скрипта sh: на Windows разделители «/».
fn to_shell_path(path: &str) -> String {
    if cfg!(windows) { path.replace('\\', "/") } else { path.to_string() }
}

/// Блок хука: запускает `tasker sync` для этой папки (путь от корня репозитория — хук работает и в других рабочих деревьях).
pub fn block(target: &HookTarget) -> Result<String> {
    let exe = full_path(std::env::current_exe().map_err(|_| CliError::new("Cannot find the path of the running program"))?);
    let command = to_shell_path(&exe.to_string_lossy());
    let exists = format!("[ -e {} ]", quote(&command));
    let run = quote(&command);
    let workspace = if target.relative.is_empty() {
        "\"$(git rev-parse --show-toplevel)\"".to_string()
    } else {
        format!("\"$(git rev-parse --show-toplevel)\"/{}", quote(&target.relative))
    };
    Ok(format!(
        "{BEGIN}\nif {exists}; then\n  {run} sync --quiet --workspace {workspace} || true\nfi\n{END}\n"
    ))
}

/// Чужие команды остаются; наш блок — сразу после строки `#!`, чтобы завершение чужого скрипта его не отменило.
pub fn with_block(text: &str, block: &str) -> String {
    let text = if has_block(text) { without_block(text) } else { text.to_string() };
    if is_empty(&text) {
        return format!("#!/bin/sh\n{block}");
    }
    if !text.starts_with("#!") {
        return format!("#!/bin/sh\n{block}{text}");
    }
    match text.find('\n') {
        None => format!("{text}\n{block}"),
        Some(first_line) => format!("{}{block}{}", &text[..first_line + 1], &text[first_line + 1..]),
    }
}

pub fn has_block(text: &str) -> bool {
    text.contains(BEGIN)
}

pub fn without_block(text: &str) -> String {
    let Some(start) = text.find(BEGIN) else {
        return text.to_string();
    };
    let Some(end) = text[start..].find(END).map(|at| start + at) else {
        return text.to_string();
    };
    let mut after = end + END.len();
    if text[after..].starts_with('\r') {
        after += 1;
    }
    if text[after..].starts_with('\n') {
        after += 1;
    }
    format!("{}{}", &text[..start], &text[after..])
}

/// Пусто или только строка `#!` — хуку больше нечего выполнять.
fn is_empty(text: &str) -> bool {
    text.split('\n').all(|x| x.trim().is_empty() || x.starts_with("#!"))
}

fn read(path: &Path) -> Result<String> {
    if path.exists() {
        Ok(String::from_utf8_lossy(&std::fs::read(path)?).into_owned())
    } else {
        Ok(String::new())
    }
}

fn make_executable(path: &Path) -> Result<()> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let mut permissions = std::fs::metadata(path)?.permissions();
        permissions.set_mode(permissions.mode() | 0o111);
        std::fs::set_permissions(path, permissions)?;
    }
    let _ = path;
    Ok(())
}

pub fn install(workspace_folder: &str) -> Result<Vec<HookState>> {
    let target = locate(workspace_folder)?;
    let block = block(&target)?;
    std::fs::create_dir_all(&target.hooks_directory)?;
    for name in NAMES {
        let path = target.hooks_directory.join(name);
        let text = read(&path)?;
        // Хуки читает sh: окончания строк только LF.
        std::fs::write(&path, tasker_core::io::to_lf(&with_block(&text, &block)).as_bytes())?;
        make_executable(&path)?;
    }
    status_of(&target)
}

/// Хуки, из которых убран блок Tasker.
pub fn uninstall(workspace_folder: &str) -> Result<Vec<&'static str>> {
    let target = locate(workspace_folder)?;
    let mut removed = Vec::new();
    for name in NAMES {
        let path = target.hooks_directory.join(name);
        if !path.exists() {
            continue;
        }
        let text = read(&path)?;
        if !has_block(&text) {
            continue;
        }
        let rest = without_block(&text);
        if is_empty(&rest) {
            std::fs::remove_file(&path)?;
        } else {
            std::fs::write(&path, tasker_core::io::to_lf(&rest).as_bytes())?;
        }
        removed.push(name);
    }
    Ok(removed)
}

pub fn status(workspace_folder: &str) -> Result<Vec<HookState>> {
    status_of(&locate(workspace_folder)?)
}

fn status_of(target: &HookTarget) -> Result<Vec<HookState>> {
    let mut states = Vec::new();
    for name in NAMES {
        let path = target.hooks_directory.join(name);
        let text = read(&path)?;
        let installed = has_block(&text);
        let rest = if installed { without_block(&text) } else { text };
        states.push(HookState {
            name,
            path,
            installed,
            foreign: !is_empty(&rest),
        });
    }
    Ok(states)
}

fn state_json(x: &HookState) -> Value {
    object(vec![
        ("name", Value::String(x.name.into())),
        ("path", Value::String(x.path.to_string_lossy().into_owned())),
        ("installed", Value::Bool(x.installed)),
        ("foreign", Value::Bool(x.foreign)),
    ])
}

pub fn run(ctx: &mut Context<'_>, command: &str, _leaf: &ArgMatches, workspace: Option<&str>) -> Result<()> {
    let folder = match workspace {
        Some(folder) => folder.to_string(),
        None => std::env::current_dir()?.to_string_lossy().into_owned(),
    };
    match command {
        "install" => {
            let states = install(&folder)?;
            let directory = states[0]
                .path
                .parent()
                .map(|p| p.to_string_lossy().into_owned())
                .unwrap_or_default();
            let names: Vec<String> = states.iter().map(|x| format!("  {}", x.name)).collect();
            ctx.print(
                &Value::Array(states.iter().map(state_json).collect()),
                &format!("Installed in {directory}:\n{}", names.join("\n")),
            );
        }
        "uninstall" => {
            let removed = uninstall(&folder)?;
            ctx.print(
                &object(vec![(
                    "removed",
                    Value::Array(removed.iter().map(|x| Value::String((*x).into())).collect()),
                )]),
                &if removed.is_empty() {
                    "No Tasker hooks found".to_string()
                } else {
                    format!("Removed from: {}", removed.join(", "))
                },
            );
        }
        "status" => {
            let states = status(&folder)?;
            let lines: Vec<String> = states
                .iter()
                .map(|x| {
                    format!(
                        "{:<14} {}{}",
                        x.name,
                        if x.installed { "installed" } else { "not installed" },
                        if x.foreign { " (has other commands)" } else { "" }
                    )
                })
                .collect();
            ctx.print(&Value::Array(states.iter().map(state_json).collect()), &lines.join("\n"));
        }
        _ => return Err(CliError::new("not implemented in this build")),
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_block_goes_after_the_shebang_and_is_removed_cleanly() {
        let block = "# >>> tasker (managed by 'tasker hooks install', do not edit) >>>\nrun\n# <<< tasker <<<\n";
        assert_eq!(with_block("", block), format!("#!/bin/sh\n{block}"));
        assert_eq!(with_block("echo hi\n", block), format!("#!/bin/sh\n{block}echo hi\n"));
        let installed = with_block("#!/bin/bash\nexit 0\n", block);
        assert_eq!(installed, format!("#!/bin/bash\n{block}exit 0\n"));
        assert_eq!(without_block(&installed), "#!/bin/bash\nexit 0\n");
        assert_eq!(with_block(&installed, block), installed);
        assert!(is_empty("#!/bin/sh\n\n"));
    }
}
