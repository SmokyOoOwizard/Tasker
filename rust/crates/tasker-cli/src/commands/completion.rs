//! `tasker completion zsh|bash|pwsh` (`CompletionCommands`, `Shells`, `PowerShellProfile*` в .NET): скрипт автодополнения по
//! Tab (встроен из `src/Tasker.Cli/Completion/Shells/`, печатается байт в байт); для PowerShell — `--install`/`--uninstall`
//! помеченного блока в `$PROFILE`.
use crate::context::Context;
use crate::errors::{CliError, Result};
use crate::kit;
use clap::ArgMatches;
use std::path::{Path, PathBuf};
use tasker_core::io::full_path;
use tasker_core::settings::expand_user_path;

const ZSH: &str = include_str!("../../../../../src/Tasker.Cli/Completion/Shells/tasker.zsh");
const BASH: &str = include_str!("../../../../../src/Tasker.Cli/Completion/Shells/tasker.bash");
const PWSH: &str = include_str!("../../../../../src/Tasker.Cli/Completion/Shells/tasker.ps1");

/// Оболочка по имени или псевдониму (`powershell` — то же, что `pwsh`).
pub fn shell(name: &str) -> Option<(&'static str, &'static str)> {
    match name {
        "zsh" => Some(("zsh", ZSH)),
        "bash" => Some(("bash", BASH)),
        "pwsh" | "powershell" => Some(("pwsh", PWSH)),
        _ => None,
    }
}

pub const BEGIN: &str = "# >>> tasker completion >>>";
pub const END: &str = "# <<< tasker completion <<<";
pub const BACKUP_SUFFIX: &str = ".tasker-backup";

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProfileChange {
    Added,
    Replaced,
    Unchanged,
    Removed,
    Absent,
    Broken,
}

pub fn quote(value: &str) -> String {
    let mut out = String::from("'");
    for c in value.chars() {
        out.push(c);
        if matches!(c, '\'' | '\u{2018}' | '\u{2019}' | '\u{201A}' | '\u{201B}') {
            out.push(c);
        }
    }
    out.push('\'');
    out
}

pub fn block(script_path: &str, newline: &str) -> String {
    [
        BEGIN.to_string(),
        "# Tab completion for tasker, added by 'tasker completion pwsh --install'. To undo: delete this block or run 'tasker completion pwsh --uninstall'.".to_string(),
        format!("if (Test-Path -LiteralPath {}) {{ . {} }}", quote(script_path), quote(script_path)),
        END.to_string(),
    ]
    .join(newline)
}

pub fn newline_of(content: &str, fallback: &str) -> String {
    if content.contains("\r\n") {
        "\r\n".into()
    } else if content.contains('\n') {
        "\n".into()
    } else {
        fallback.into()
    }
}

fn split(content: &str) -> Vec<String> {
    content.replace("\r\n", "\n").split('\n').map(str::to_string).collect()
}

/// Границы блока: Some((начало, конец)); None — блока нет; Err — есть только одна метка или они в неправильном порядке.
fn find(lines: &[String]) -> std::result::Result<Option<(usize, usize)>, ()> {
    let begin = lines.iter().position(|x| x.trim_end() == BEGIN);
    let end = lines.iter().position(|x| x.trim_end() == END);
    match (begin, end) {
        (None, None) => Ok(None),
        (Some(b), Some(e)) if e >= b => Ok(Some((b, e))),
        _ => Err(()),
    }
}

pub fn install(content: &str, block: &str, newline: &str) -> (String, ProfileChange) {
    let lines = split(content);
    match find(&lines) {
        Err(()) => return (content.to_string(), ProfileChange::Broken),
        Ok(Some((begin, end))) => {
            let existing = lines[begin..=end].join(newline);
            if existing == block {
                return (content.to_string(), ProfileChange::Unchanged);
            }
            let mut replaced: Vec<String> = lines[..begin].to_vec();
            replaced.push(block.to_string());
            replaced.extend(lines[end + 1..].iter().cloned());
            return (replaced.join(newline), ProfileChange::Replaced);
        }
        Ok(None) => {}
    }
    if content.is_empty() {
        return (format!("{block}{newline}"), ProfileChange::Added);
    }
    let separator = if content.ends_with('\n') {
        newline.to_string()
    } else {
        format!("{newline}{newline}")
    };
    (format!("{content}{separator}{block}{newline}"), ProfileChange::Added)
}

pub fn uninstall(content: &str, newline: &str) -> (String, ProfileChange) {
    let lines = split(content);
    let (begin, end) = match find(&lines) {
        Err(()) => return (content.to_string(), ProfileChange::Broken),
        Ok(None) => return (content.to_string(), ProfileChange::Absent),
        Ok(Some(found)) => found,
    };
    let from = if begin > 0 && lines[begin - 1].is_empty() && begin - 1 > 0 {
        begin - 1
    } else {
        begin
    };
    let mut rest: Vec<String> = lines[..from].to_vec();
    rest.extend(lines[end + 1..].iter().cloned());
    let mut text = rest.join(newline);
    // Блок стоял в самом конце: завершающий перевод строки остаётся у предыдущей строки.
    if rest.last().is_some_and(|x| !x.is_empty()) && end == lines.len() - 1 {
        text.push_str(newline);
    }
    (
        if rest.iter().all(|x| x.is_empty()) { String::new() } else { text },
        ProfileChange::Removed,
    )
}

pub fn default_profiles(windows: bool, documents: &Path, home: &Path, config_home: Option<&str>) -> Vec<PathBuf> {
    const FILE: &str = "Microsoft.PowerShell_profile.ps1";
    if windows {
        return vec![
            documents.join("PowerShell").join(FILE),
            documents.join("WindowsPowerShell").join(FILE),
        ];
    }
    let config = match config_home {
        Some(c) if !c.is_empty() => PathBuf::from(c),
        _ => home.join(".config"),
    };
    vec![config.join("powershell").join(FILE)]
}

/// Правит один профиль: отчёт строками `  backup: …`, `  changed: …`, `  unchanged: …`, `  skipped: …`.
pub fn apply(path: &Path, script_path: Option<&str>, newline_fallback: &str) -> Result<(ProfileChange, String)> {
    let exists = path.exists();
    let bytes = if exists { std::fs::read(path)? } else { Vec::new() };
    let has_bom = bytes.starts_with(&[0xEF, 0xBB, 0xBF]);
    let body = if has_bom { &bytes[3..] } else { &bytes[..] };
    let Ok(content) = std::str::from_utf8(body) else {
        return Ok((
            ProfileChange::Broken,
            format!(
                "  skipped:   {} is not UTF-8 (probably the ANSI code page): add the block yourself, see 'tasker manual windows'",
                path.display()
            ),
        ));
    };
    let newline = newline_of(content, newline_fallback);
    let (updated, change) = match script_path {
        None => uninstall(content, &newline),
        Some(script) => install(content, &block(script, &newline), &newline),
    };
    match change {
        ProfileChange::Broken => {
            return Ok((
                change,
                format!(
                    "  skipped:   {} has only one of the '{BEGIN}' / '{END}' lines: fix or delete it, then run this command again",
                    path.display()
                ),
            ));
        }
        ProfileChange::Unchanged => {
            return Ok((
                change,
                format!("  unchanged: {} already has the tasker completion block", path.display()),
            ));
        }
        ProfileChange::Absent => {
            return Ok((change, format!("  unchanged: {} has no tasker completion block", path.display())));
        }
        _ => {}
    }
    let mut report = String::new();
    let backup = PathBuf::from(format!("{}{BACKUP_SUFFIX}", path.display()));
    if exists && !backup.exists() {
        std::fs::copy(path, &backup)?;
        report.push_str(&format!(
            "  backup:    {}{BACKUP_SUFFIX} (the file as it was before)\n",
            path.display()
        ));
    }
    if let Some(parent) = full_path(path).parent() {
        std::fs::create_dir_all(parent)?;
    }
    let with_bom = has_bom || updated.chars().any(|c| c as u32 > 0x7F);
    let mut out: Vec<u8> = Vec::new();
    if with_bom {
        out.extend_from_slice(&[0xEF, 0xBB, 0xBF]);
    }
    out.extend_from_slice(updated.as_bytes());
    std::fs::write(path, out)?;
    report.push_str(&match change {
        ProfileChange::Added => format!("  changed:   {} (the tasker completion block is added at the end)", path.display()),
        ProfileChange::Replaced => format!("  changed:   {} (the tasker completion block is updated)", path.display()),
        _ => format!("  changed:   {} (the tasker completion block is removed)", path.display()),
    });
    Ok((change, report))
}

pub fn write_script(path: &Path, script: &str) -> Result<()> {
    if let Some(parent) = full_path(path).parent() {
        std::fs::create_dir_all(parent)?;
    }
    let mut out: Vec<u8> = vec![0xEF, 0xBB, 0xBF];
    out.extend_from_slice(script.as_bytes());
    std::fs::write(path, out)?;
    Ok(())
}

pub fn run(ctx: &mut Context<'_>, leaf: &ArgMatches) -> Result<()> {
    let (name, script) = shell(&kit::required(leaf, "shell")).expect("a shell accepted by the parser");
    let installing = kit::flag(leaf, "install");
    let uninstalling = kit::flag(leaf, "uninstall");
    if !installing && !uninstalling {
        if !kit::values_or_empty(leaf, "profile").is_empty() || kit::text(leaf, "script").is_some() {
            return Err(CliError::new("--profile and --script need --install or --uninstall"));
        }
        let _ = ctx.out().write_all(script.as_bytes());
        return Ok(());
    }
    if installing && uninstalling {
        return Err(CliError::new("Use either --install or --uninstall, not both"));
    }
    if name != "pwsh" {
        return Err(CliError::new(format!(
            "--install and --uninstall are for pwsh only: {name} is connected by scripts/install.sh (or print the script with 'tasker completion {name}')"
        )));
    }

    let script_path = match kit::text(leaf, "script") {
        Some(given) => full_path(expand_user_path(&given)),
        None => tasker_core::settings::data_dir().join("completions").join("tasker.ps1"),
    };
    let given = kit::values_or_empty(leaf, "profile");
    let profiles: Vec<PathBuf> = if given.is_empty() {
        let home = PathBuf::from(tasker_core::settings::expand_user_path("~"));
        default_profiles(
            cfg!(windows),
            &home.join("Documents"),
            &home,
            std::env::var("XDG_CONFIG_HOME").ok().as_deref(),
        )
    } else {
        given.iter().map(|x| full_path(expand_user_path(x))).collect()
    };
    let newline = if cfg!(windows) { "\r\n" } else { "\n" };
    let script_text = script_path.to_string_lossy().into_owned();

    if installing {
        write_script(&script_path, script)?;
        let _ = writeln!(ctx.out(), "Tab completion script: {script_text}");
    }
    for file in &profiles {
        let (_, report) = apply(file, installing.then_some(script_text.as_str()), newline)?;
        let _ = writeln!(ctx.out(), "{report}");
    }
    if uninstalling && script_path.exists() {
        std::fs::remove_file(&script_path)?;
        let _ = writeln!(ctx.out(), "Removed the script {script_text}");
    }
    if installing {
        let _ = writeln!(
            ctx.out(),
            "Open a new PowerShell window to use it. cmd.exe has no programmable completion: it is not supported there."
        );
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_profile_block_is_added_replaced_and_removed() {
        let b = block("C:\\x\\tasker.ps1", "\n");
        assert!(b.contains("Test-Path -LiteralPath 'C:\\x\\tasker.ps1'"));
        let (added, change) = install("", &b, "\n");
        assert_eq!((added.as_str(), change), (format!("{b}\n").as_str(), ProfileChange::Added));
        let (same, change) = install(&added, &b, "\n");
        assert_eq!((same == added, change), (true, ProfileChange::Unchanged));
        let other = block("other.ps1", "\n");
        let (replaced, change) = install(&added, &other, "\n");
        assert_eq!((replaced.contains("other.ps1"), change), (true, ProfileChange::Replaced));
        let (removed, change) = uninstall(&replaced, "\n");
        assert_eq!((removed.as_str(), change), ("", ProfileChange::Removed));
        // Файл кончается переводом строки: блок отделяется одной пустой строкой (как у .NET).
        let (text, change) = install("echo hi\n", &b, "\n");
        assert_eq!(
            (text.as_str(), change),
            (format!("echo hi\n\n{b}\n").as_str(), ProfileChange::Added)
        );
        let (back, change) = uninstall(&text, "\n");
        assert_eq!((back.as_str(), change), ("echo hi\n", ProfileChange::Removed));
        assert_eq!(uninstall("x\n", "\n").1, ProfileChange::Absent);
        assert_eq!(install(BEGIN, &b, "\n").1, ProfileChange::Broken);
        assert_eq!(quote("it's"), "'it''s'");
    }
}
