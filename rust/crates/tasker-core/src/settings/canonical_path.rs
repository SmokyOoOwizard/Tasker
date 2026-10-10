//! `CanonicalPath`: один путь на каждый файл или папку на диске — абсолютный, с раскрытыми симлинками и с регистром букв, как он
//! записан на диске (на macOS и Windows файловая система обычно не различает регистр). По нему рабочие области и блокировки записи
//! совпадают, как бы ни открыли папку. Windows: буква диска заглавная, префикс `\\?\` снимается, junction раскрываются, UNC-путь
//! остаётся UNC.
//!
//! TODO (Windows, фаза 3): раскрытие коротких имён 8.3 (`RUNNER~1`, `GetLongPathNameW`) не перенесено.
use crate::io::{full_path, full_path_in};
use crate::validate::eq_ignore_case;
use std::collections::VecDeque;
use std::path::{Component, Path, PathBuf};

// Защита от циклов симлинков.
const MAX_LINKS: usize = 40;

pub fn canonical_path(path: &str) -> String {
    let started = crate::perf::start();
    let result = canonical(path);
    crate::perf::count("canonical-path", started);
    result
}

fn canonical(path: &str) -> String {
    let full = PathBuf::from(strip_extended_prefix(&full_path(path).to_string_lossy()));
    let (root, parts) = split(&full);
    let mut current = PathBuf::from(normalize_root(&root));
    let mut links = 0;
    let mut queue: VecDeque<String> = parts.into();
    while let Some(part) = queue.pop_front() {
        let next = current.join(actual_name(&current, &part));
        if let Some(target) = link_target(&next) {
            links += 1;
            if links <= MAX_LINKS {
                // Цель симлинка может быть относительной и сама содержать симлинки — разбираем её заново.
                let target = PathBuf::from(strip_extended_prefix(&full_path_in(&current, target).to_string_lossy()));
                let (target_root, target_parts) = split(&target);
                let rest: Vec<String> = queue.drain(..).collect();
                queue = target_parts.into_iter().chain(rest).collect();
                current = PathBuf::from(normalize_root(&target_root));
                continue;
            }
        }
        current = next;
    }
    current.to_string_lossy().into_owned()
}

/// Корень (`Path.GetPathRoot`) и остальные компоненты без пустых.
fn split(full: &Path) -> (String, Vec<String>) {
    let mut root = String::new();
    let mut parts = Vec::new();
    for component in full.components() {
        match component {
            Component::Prefix(p) => root.push_str(&p.as_os_str().to_string_lossy()),
            Component::RootDir => root.push(std::path::MAIN_SEPARATOR),
            Component::Normal(name) => parts.push(name.to_string_lossy().into_owned()),
            Component::CurDir | Component::ParentDir => parts.push(component.as_os_str().to_string_lossy().into_owned()),
        }
    }
    (root, parts)
}

fn link_target(path: &Path) -> Option<PathBuf> {
    let meta = std::fs::symlink_metadata(path).ok()?;
    if meta.file_type().is_symlink() {
        std::fs::read_link(path).ok()
    } else {
        None
    }
}

/// Снимает префикс длинных путей Windows: `\\?\C:\x` → `C:\x`, `\\?\UNC\server\share\x` → `\\server\share\x`.
/// Остальные пути (в том числе Unix) возвращает как есть. Чистая функция.
pub fn strip_extended_prefix(path: &str) -> String {
    const UNC: &str = r"\\?\UNC\";
    const EXTENDED: &str = r"\\?\";
    if path.len() >= UNC.len() && eq_ignore_case(&path[..UNC.len()], UNC) {
        return format!(r"\\{}", &path[UNC.len()..]);
    }
    // Только вид «\\?\C:\…»: другие формы (\\?\Volume{guid}\) не пути с буквой диска, их не трогаем.
    let b = path.as_bytes();
    if path.starts_with(EXTENDED)
        && b.len() >= EXTENDED.len() + 2
        && b[EXTENDED.len()].is_ascii_alphabetic()
        && b[EXTENDED.len() + 1] == b':'
    {
        return path[EXTENDED.len()..].to_string();
    }
    path.to_string()
}

/// Корень пути в единой форме: буква диска заглавная (`c:\` → `C:\`). Остальные корни (`/`, UNC) — как есть. Чистая функция.
pub fn normalize_root(root: &str) -> String {
    let b = root.as_bytes();
    if b.len() >= 2 && b[0].is_ascii_lowercase() && b[1] == b':' {
        format!("{}{}", (b[0] as char).to_ascii_uppercase(), &root[1..])
    } else {
        root.to_string()
    }
}

// Имя элемента каталога, как оно записано на диске: точное совпадение, иначе единственное без учёта регистра.
// Элемента нет (файл ещё не создан) или каталог не прочитать — имя как есть.
fn actual_name(directory: &Path, name: &str) -> String {
    if name == "." || name == ".." {
        return name.to_string();
    }
    // Быстрый путь macOS: элемент с точно таким именем есть — ответ тот же, что дал бы перебор каталога, а каталог может быть
    // огромным (системный временный — десятки тысяч записей, перебор — десятки мс на каждый вызов консоли).
    #[cfg(target_os = "macos")]
    if stored_name(&directory.join(name)).is_some_and(|stored| stored == name) {
        return name.to_string();
    }
    let Ok(entries) = std::fs::read_dir(directory) else {
        return name.to_string();
    };
    let matches: Vec<String> = entries
        .flatten()
        .map(|e| e.file_name().to_string_lossy().into_owned())
        .filter(|n| eq_ignore_case(n, name))
        .collect();
    if matches.iter().any(|m| m == name) || matches.len() != 1 {
        name.to_string()
    } else {
        matches[0].clone()
    }
}

/// Имя элемента, как оно записано в каталоге (`getattrlist(ATTR_CMN_NAME)`, симлинк не раскрывается): на файловой системе без учёта
/// регистра поиск находит элемент и по имени в другом регистре, но возвращает записанное. None — элемента нет или ошибка.
#[cfg(target_os = "macos")]
fn stored_name(path: &Path) -> Option<String> {
    use std::os::unix::ffi::OsStrExt;
    let c_path = std::ffi::CString::new(path.as_os_str().as_bytes()).ok()?;
    let mut request = libc::attrlist {
        bitmapcount: libc::ATTR_BIT_MAP_COUNT,
        reserved: 0,
        commonattr: libc::ATTR_CMN_NAME,
        volattr: 0,
        dirattr: 0,
        fileattr: 0,
        forkattr: 0,
    };
    // Длина ответа (u32), attrreference_t (смещение от самой ссылки и длина с нулём), затем имя: до 255 знаков UTF-8 по 3 байта.
    let mut buffer = [0u8; 4 + 8 + 1024];
    // SAFETY: путь — строка с нулём, запрос и буфер живут на стеке, размер буфера передаётся.
    let rc = unsafe {
        libc::getattrlist(
            c_path.as_ptr(),
            (&mut request as *mut libc::attrlist).cast(),
            buffer.as_mut_ptr().cast(),
            buffer.len(),
            libc::FSOPT_NOFOLLOW,
        )
    };
    if rc != 0 {
        return None;
    }
    let offset = i32::from_ne_bytes(buffer[4..8].try_into().ok()?);
    let length = u32::from_ne_bytes(buffer[8..12].try_into().ok()?) as usize;
    let start = usize::try_from(4 + i64::from(offset)).ok()?;
    let bytes = buffer.get(start..start.checked_add(length)?)?;
    let bytes = bytes.strip_suffix(&[0]).unwrap_or(bytes);
    String::from_utf8(bytes.to_vec()).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pure_windows_helpers() {
        assert_eq!(strip_extended_prefix(r"\\?\C:\x"), r"C:\x");
        assert_eq!(strip_extended_prefix(r"\\?\unc\server\share\x"), r"\\server\share\x");
        assert_eq!(strip_extended_prefix(r"\\?\Volume{1}\x"), r"\\?\Volume{1}\x");
        assert_eq!(strip_extended_prefix("/a/b"), "/a/b");
        assert_eq!(normalize_root(r"c:\"), r"C:\");
        assert_eq!(normalize_root("/"), "/");
        assert_eq!(normalize_root(r"\\server\share\"), r"\\server\share\");
    }

    #[cfg(unix)]
    #[test]
    fn symlinks_and_case_are_resolved() {
        let dir = crate::test_support::temp_dir();
        let real = dir.join("Real");
        std::fs::create_dir_all(&real).unwrap();
        std::os::unix::fs::symlink("Real", dir.join("link")).unwrap();
        std::os::unix::fs::symlink("link", dir.join("link2")).unwrap();
        let canonical_dir = canonical_path(dir.to_str().unwrap());
        let expected = format!("{canonical_dir}/Real");
        assert_eq!(canonical_path(real.to_str().unwrap()), expected);
        assert_eq!(canonical_path(dir.join("link").to_str().unwrap()), expected);
        assert_eq!(canonical_path(dir.join("link2").to_str().unwrap()), expected);
        assert_eq!(canonical_path(dir.join("real").to_str().unwrap()), expected);
        assert_eq!(
            canonical_path(dir.join("link2/missing/../x").to_str().unwrap()),
            format!("{expected}/x")
        );
        // Цикл не вешает: после 40 переходов ссылки остаются как есть.
        std::os::unix::fs::symlink("loop", dir.join("loop")).unwrap();
        assert_eq!(canonical_path(dir.join("loop").to_str().unwrap()), format!("{canonical_dir}/loop"));
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn the_stored_name_is_read_without_listing_the_directory() {
        let dir = crate::test_support::temp_dir();
        std::fs::create_dir_all(dir.join("Папка")).unwrap();
        std::os::unix::fs::symlink("nowhere", dir.join("Link")).unwrap();
        assert_eq!(stored_name(&dir.join("Папка")).as_deref(), Some("Папка"));
        assert_eq!(stored_name(&dir.join("Link")).as_deref(), Some("Link"));
        assert_eq!(stored_name(&dir.join("missing")), None);
        // На томе без учёта регистра поиск по другому регистру находит записанное имя — тогда решает перебор каталога, как раньше.
        if dir.join("link").symlink_metadata().is_ok() {
            assert_eq!(stored_name(&dir.join("link")).as_deref(), Some("Link"));
        }
        assert_eq!(actual_name(&dir, "Папка"), "Папка");
        assert_eq!(actual_name(&dir, "missing"), "missing");
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
