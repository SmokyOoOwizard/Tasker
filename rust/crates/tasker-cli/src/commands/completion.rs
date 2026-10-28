//! `tasker completion zsh|bash|pwsh`: скрипт автодополнения по
//! Tab (встроен из `completion/`, печатается байт в байт); для PowerShell — `--install`/`--uninstall`
//! помеченного блока в `$PROFILE`.
use crate::context::Context;
use crate::errors::{CliError, Result};
use crate::kit;
use clap::ArgMatches;
use std::path::{Path, PathBuf};
use tasker_core::io::full_path;
use tasker_core::settings::expand_user_path;

const ZSH: &str = include_str!("../../completion/tasker.zsh");
const BASH: &str = include_str!("../../completion/tasker.bash");
const PWSH: &str = include_str!("../../completion/tasker.ps1");

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

/// Папка «Документы» Windows, где PowerShell ищет `$PROFILE` (`[Environment]::GetFolderPath('MyDocuments')`): её можно перенести
/// (на другой диск, в OneDrive), и тогда она не `%USERPROFILE%\Documents`. `None` — не удалось узнать или не Windows.
#[cfg(windows)]
fn documents_dir() -> Option<PathBuf> {
    use std::os::windows::ffi::OsStringExt;
    use windows_sys::Win32::System::Com::CoTaskMemFree;
    use windows_sys::Win32::UI::Shell::{FOLDERID_Documents, KF_FLAG_DEFAULT, SHGetKnownFolderPath};
    let mut raw: windows_sys::core::PWSTR = std::ptr::null_mut();
    // SAFETY: SHGetKnownFolderPath пишет в raw строку UTF-16 с нулём на конце (её освобождает CoTaskMemFree, даже при ошибке).
    unsafe {
        let hr = SHGetKnownFolderPath(&FOLDERID_Documents, KF_FLAG_DEFAULT as u32, std::ptr::null_mut(), &mut raw);
        let path = (hr >= 0 && !raw.is_null()).then(|| {
            let len = (0..).take_while(|&i| *raw.add(i) != 0).count();
            PathBuf::from(std::ffi::OsString::from_wide(std::slice::from_raw_parts(raw, len)))
        });
        CoTaskMemFree(raw as *const _);
        path.filter(|p| !p.as_os_str().is_empty())
    }
}

#[cfg(not(windows))]
fn documents_dir() -> Option<PathBuf> {
    None
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
            &documents_dir().unwrap_or_else(|| home.join("Documents")),
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
        // Файл кончается переводом строки: блок отделяется одной пустой строкой.
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

    const SCRIPT: &str = "C:\\Users\\Иван\\AppData\\Local\\Tasker\\completions\\tasker.ps1";

    #[test]
    fn the_block_loads_the_script_if_it_exists() {
        assert_eq!(
            block("C:\\Users\\O'Brien\\tasker.ps1", "\n"),
            "# >>> tasker completion >>>\n\
             # Tab completion for tasker, added by 'tasker completion pwsh --install'. To undo: delete this block or run 'tasker completion pwsh --uninstall'.\n\
             if (Test-Path -LiteralPath 'C:\\Users\\O''Brien\\tasker.ps1') { . 'C:\\Users\\O''Brien\\tasker.ps1' }\n\
             # <<< tasker completion <<<"
        );
        let crlf = block(SCRIPT, "\r\n");
        assert_eq!(install("", &crlf, "\r\n"), (format!("{crlf}\r\n"), ProfileChange::Added));
    }

    #[test]
    fn the_block_is_appended_after_the_existing_content_and_removed_without_a_trace() {
        for (original, newline) in [
            ("Set-Alias g git\n", "\n"),
            ("Set-Alias g git\r\n", "\r\n"),
            ("Set-Alias g git", "\n"),
        ] {
            let b = block(SCRIPT, newline);
            let (installed, added) = install(original, &b, newline);
            let (removed, change) = uninstall(&installed, newline);
            assert_eq!(added, ProfileChange::Added);
            assert!(
                installed.starts_with(original) && installed.ends_with(&format!("{b}{newline}")),
                "{installed:?}"
            );
            assert_eq!(change, ProfileChange::Removed);
            let expected = if original.ends_with('\n') {
                original.to_string()
            } else {
                format!("{original}{newline}")
            };
            assert_eq!(removed, expected);
        }
    }

    #[test]
    fn installing_twice_changes_nothing_and_a_new_path_replaces_the_block() {
        let first = block(SCRIPT, "\n");
        let (installed, _) = install("# mine\n", &first, "\n");
        let (again, unchanged) = install(&installed, &first, "\n");
        let (moved, replaced) = install(&format!("{installed}# after\n"), &block("D:\\t.ps1", "\n"), "\n");
        assert_eq!((again == installed, unchanged), (true, ProfileChange::Unchanged));
        assert_eq!(replaced, ProfileChange::Replaced);
        assert_eq!(moved.matches(BEGIN).count(), 1);
        assert!(moved.contains("D:\\t.ps1") && !moved.contains("Иван"));
        assert!(moved.starts_with("# mine\n") && moved.ends_with("# after\n"));
    }

    #[test]
    fn a_file_with_only_one_mark_is_left_alone_and_a_profile_without_the_block_is_not_changed() {
        let broken = format!("x\n{BEGIN}\ny\n");
        assert_eq!(install(&broken, "block", "\n"), (broken.clone(), ProfileChange::Broken));
        assert_eq!(uninstall(&broken, "\n"), (broken.clone(), ProfileChange::Broken));
        assert_eq!(
            uninstall("Set-Alias g git\n", "\n"),
            ("Set-Alias g git\n".to_string(), ProfileChange::Absent)
        );
    }

    #[test]
    fn smart_quotes_in_a_path_are_doubled_for_powershell() {
        assert_eq!(quote("a'b\u{2019}c"), "'a''b\u{2019}\u{2019}c'");
    }

    #[test]
    fn profiles_are_found_by_platform() {
        let file = "Microsoft.PowerShell_profile.ps1";
        assert_eq!(
            default_profiles(true, Path::new("D:\\Docs"), Path::new("C:\\Users\\u"), None),
            [
                Path::new("D:\\Docs").join("PowerShell").join(file),
                Path::new("D:\\Docs").join("WindowsPowerShell").join(file)
            ]
        );
        assert_eq!(
            default_profiles(false, Path::new(""), Path::new("/home/u"), None),
            [Path::new("/home/u").join(".config").join("powershell").join(file)]
        );
        assert_eq!(
            default_profiles(false, Path::new(""), Path::new("/home/u"), Some("/cfg")),
            [Path::new("/cfg").join("powershell").join(file)]
        );
    }

    /// Папка «Документы» — та же, что у PowerShell (`$PROFILE`), даже если её перенесли с `%USERPROFILE%\Documents`.
    #[cfg(windows)]
    #[test]
    fn documents_are_the_folder_powershell_uses() {
        let out = std::process::Command::new("powershell.exe")
            .args([
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "[Environment]::GetFolderPath('MyDocuments')",
            ])
            .output()
            .expect("powershell.exe");
        let expected = String::from_utf8_lossy(&out.stdout).trim().to_string();
        assert_eq!(documents_dir(), Some(PathBuf::from(expected)));
    }

    fn folder() -> PathBuf {
        let path = std::env::temp_dir().join(format!("tasker-pwsh-{}", uuid::Uuid::new_v4().simple()));
        std::fs::create_dir_all(&path).unwrap();
        path
    }

    #[test]
    fn a_profile_is_backed_up_once_and_keeps_its_byte_order_mark() {
        let dir = folder();
        let profile = dir.join("p.ps1");
        std::fs::write(&profile, b"\xEF\xBB\xBFSet-Alias g git\r\n").unwrap();
        let (change, report) = apply(&profile, Some("C:\\t\\tasker.ps1"), "\r\n").unwrap();
        let (second, _) = apply(&profile, Some("C:\\t\\tasker.ps1"), "\r\n").unwrap();
        assert_eq!((change, second), (ProfileChange::Added, ProfileChange::Unchanged));
        assert!(report.contains("backup:"), "{report}");
        let backup = PathBuf::from(format!("{}{BACKUP_SUFFIX}", profile.display()));
        assert_eq!(std::fs::read(&backup).unwrap(), b"\xEF\xBB\xBFSet-Alias g git\r\n");
        let bytes = std::fs::read(&profile).unwrap();
        assert_eq!(&bytes[..3], b"\xEF\xBB\xBF");
        assert!(String::from_utf8_lossy(&bytes).contains("\r\n# >>> tasker completion >>>\r\n"));
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn a_new_profile_gets_a_byte_order_mark_only_when_it_has_non_ascii_text() {
        let dir = folder();
        let ascii = dir.join("a").join("p.ps1");
        let cyrillic = dir.join("b").join("p.ps1");
        apply(&ascii, Some("C:\\t\\tasker.ps1"), "\n").unwrap();
        apply(&cyrillic, Some("C:\\Users\\Иван\\tasker.ps1"), "\n").unwrap();
        assert_ne!(std::fs::read(&ascii).unwrap()[0], 0xEF);
        assert_eq!(&std::fs::read(&cyrillic).unwrap()[..3], b"\xEF\xBB\xBF");
        assert!(!PathBuf::from(format!("{}{BACKUP_SUFFIX}", ascii.display())).exists());
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn a_profile_in_another_code_page_is_not_touched() {
        let dir = folder();
        let profile = dir.join("p.ps1");
        let ansi = [0x23, 0x20, 0xC8, 0xE2, 0xE0, 0xED, 0x0A]; // «# Иван» в Windows-1251
        std::fs::write(&profile, ansi).unwrap();
        let (change, report) = apply(&profile, Some("C:\\t\\tasker.ps1"), "\n").unwrap();
        assert_eq!(change, ProfileChange::Broken);
        assert!(report.contains("not UTF-8"), "{report}");
        assert_eq!(std::fs::read(&profile).unwrap(), ansi);
        std::fs::remove_dir_all(dir).unwrap();
    }
}
