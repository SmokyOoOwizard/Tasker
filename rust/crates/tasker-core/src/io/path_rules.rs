//! `PathRules`: как сравнивать пути на этой системе — на Windows без учёта регистра, на Linux и macOS точно (на macOS том может
//! быть и чувствительным к регистру, настоящий регистр имён приводит к одному виду `CanonicalPath`). Ключи блокировок и словарей
//! по путям берут отсюда. Здесь же `full_path` — аналог `Path.GetFullPath`: абсолютный путь с лексическим разбором `.` и `..`,
//! без обращения к диску.
use crate::validate::to_upper_invariant;
use std::path::{Component, Path, PathBuf};

/// Различает ли эта система регистр в путях (`PathRules.Comparison == OrdinalIgnoreCase` — Windows).
pub const IGNORE_CASE: bool = cfg!(windows);

/// `Path.GetFullPath`: относительный путь — от текущего каталога; `.` и `..` разбираются лексически (симлинки не раскрываются),
/// повторные разделители схлопываются. Пустой путь — текущий каталог.
pub fn full_path(path: impl AsRef<Path>) -> PathBuf {
    let base = std::env::current_dir().unwrap_or_else(|_| PathBuf::from(std::path::MAIN_SEPARATOR_STR));
    full_path_in(&base, path)
}

/// `Path.GetFullPath(path, basePath)`: относительный путь — от `base` (который должен быть абсолютным).
pub fn full_path_in(base: &Path, path: impl AsRef<Path>) -> PathBuf {
    let path = path.as_ref();
    let absolute = if path.is_absolute() { path.to_path_buf() } else { base.join(path) };
    let mut result = PathBuf::new();
    for component in absolute.components() {
        match component {
            Component::Prefix(p) => result.push(p.as_os_str()),
            Component::RootDir => result.push(std::path::MAIN_SEPARATOR_STR),
            Component::CurDir => {}
            Component::ParentDir => {
                // Выше корня не поднимаемся (как Path.GetFullPath).
                let is_root = matches!(
                    result.components().next_back(),
                    Some(Component::RootDir) | Some(Component::Prefix(_)) | None
                );
                if !is_root {
                    result.pop();
                }
            }
            Component::Normal(name) => result.push(name),
        }
    }
    result
}

/// Ключ словаря или блокировки по пути: на Windows регистр не важен.
pub fn path_key(path: &str) -> String {
    if IGNORE_CASE { to_upper_invariant(path) } else { path.to_string() }
}

fn equals(a: &str, b: &str, ignore_case: bool) -> bool {
    if ignore_case {
        to_upper_invariant(a) == to_upper_invariant(b)
    } else {
        a == b
    }
}

/// Один и тот же ли это путь (оба приводятся к полному).
pub fn same_path(a: &str, b: &str) -> bool {
    equals(&full_path(a).to_string_lossy(), &full_path(b).to_string_lossy(), IGNORE_CASE)
}

/// Лежит ли `path` внутри `folder` (или это она сама); границу по разделителю учитывает.
pub fn is_inside(folder: &str, path: &str) -> bool {
    is_inside_with(folder, path, IGNORE_CASE)
}

pub fn is_inside_with(folder: &str, path: &str, ignore_case: bool) -> bool {
    let trimmed = folder.trim_end_matches(['/', '\\']);
    let (folder_units, path_units): (Vec<u16>, Vec<u16>) = if ignore_case {
        (
            to_upper_invariant(trimmed).encode_utf16().collect(),
            to_upper_invariant(path).encode_utf16().collect(),
        )
    } else {
        (trimmed.encode_utf16().collect(), path.encode_utf16().collect())
    };
    if !path_units.starts_with(&folder_units) {
        return false;
    }
    path_units.len() == folder_units.len() || matches!(path_units[folder_units.len()], 0x2F | 0x5C)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn full_path_is_lexical() {
        let cwd = std::env::current_dir().unwrap();
        assert_eq!(full_path("a/./b/../c"), cwd.join("a").join("c"));
        if cfg!(unix) {
            assert_eq!(full_path("/x/../../y//z/"), PathBuf::from("/y/z"));
            assert_eq!(full_path("/"), PathBuf::from("/"));
        }
    }

    #[test]
    fn inside_respects_separators_and_case_rule() {
        assert!(is_inside_with("/a/b/", "/a/b", false));
        assert!(is_inside_with("/a/b", "/a/b/c", false));
        assert!(!is_inside_with("/a/b", "/a/bc", false));
        assert!(!is_inside_with("/a/b", "/A/B/c", false));
        assert!(is_inside_with("/a/b", "/A/B/c", true));
    }
}
