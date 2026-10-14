//! Обход папок `.tasker` для сверки индекса: все `*.yaml` папки и её подпапок с отпечатком каждого файла (размер, время
//! изменения) — те же значения, что дают `std::fs::metadata` и `FileInfo` .NET, но меньшим числом системных вызовов:
//!
//! - macOS: `getattrlistbulk` — имя, тип, размер и время изменения сразу для многих записей папки за один вызов, без `stat`
//!   каждого файла. Файловая система без него (или ответ, который не разобрать) — обход как на остальных Unix;
//! - Unix: `readdir` и `fstatat` относительно дескриптора папки — без разбора полного пути на каждом файле;
//! - Windows: `read_dir` и `metadata` по пути. Размер и время из записи папки (`FindNextFile`) NTFS обновляет с задержкой (у
//!   открытого на запись файла, у жёстких ссылок), а свежесть индекса важнее скорости, поэтому отпечаток читается из самого файла.
//!
//! Свежесть та же, что у обхода с `stat` каждого файла: отпечаток читается у каждого файла при каждой сверке. Пропускать папки
//! по их времени изменения нельзя: правка файла на месте (тот же файл, новое содержимое) время папки не меняет.
//!
//! Символические ссылки — как у `read_dir` + `metadata`: ссылка на файл `*.yaml` даёт отпечаток файла, по ссылке на папку обход
//! не идёт; недоступные папки пропускаются (`IgnoreInaccessible`).
use crate::names;
use std::path::{Path, PathBuf};

/// Отпечаток файла: размер и время изменения в тиках .NET (`FileInfo.LastWriteTimeUtc.Ticks`) — как у .NET, иначе каждая сборка
/// считала бы файлы, записанные другой, изменившимися.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) struct Stamp {
    pub size: i64,
    pub mtime: i64,
}

/// Найденный файл: путь относительно `.tasker` (через `/`), полный путь, отпечаток и то, что вернул `accept`.
pub(super) struct Found<T> {
    pub relative: String,
    pub full_path: PathBuf,
    pub stamp: Stamp,
    pub value: T,
}

/// Тики .NET (100 нс от 0001-01-01T00:00:00Z) в момент эпохи Unix.
pub(super) const NET_TICKS_AT_UNIX_EPOCH: i64 = 621_355_968_000_000_000;

/// Время изменения в тиках .NET: секунды и наносекунды stat (FILETIME на Windows), наносекунды — вниз до 100 нс, как
/// `File.GetLastWriteTimeUtc` (.NET на Unix берёт `st_mtim` и делит наносекунды на 100).
pub(super) fn net_ticks(time: std::time::SystemTime) -> Option<i64> {
    let ticks = match time.duration_since(std::time::UNIX_EPOCH) {
        Ok(d) => i64::try_from(d.as_secs()).ok()? * 10_000_000 + (d.subsec_nanos() / 100) as i64,
        Err(e) => {
            let d = e.duration();
            -(i64::try_from(d.as_secs()).ok()? * 10_000_000 + d.subsec_nanos().div_ceil(100) as i64)
        }
    };
    Some(ticks + NET_TICKS_AT_UNIX_EPOCH)
}

/// Отпечаток по `metadata`: время, которое не прочитать, — 0 (как было всегда).
fn stamp_of(metadata: &std::fs::Metadata) -> Stamp {
    Stamp {
        size: metadata.len() as i64,
        mtime: metadata.modified().ok().and_then(net_ticks).unwrap_or(0),
    }
}

/// Что лежит по пути (по ссылкам — переходя): файл с отпечатком, папка или ничего (нет, недоступно, другое).
pub(super) enum PathKind {
    File(Stamp),
    Dir,
    None,
}

pub(super) fn path_kind(path: &Path) -> PathKind {
    match std::fs::metadata(path) {
        Ok(m) if m.is_file() => PathKind::File(stamp_of(&m)),
        Ok(m) if m.is_dir() => PathKind::Dir,
        _ => PathKind::None,
    }
}

// Имя файла `*.yaml` — как `Path::extension() == "yaml"`: у «.yaml» (имя начинается с точки) расширения нет.
fn is_yaml(name: &[u8]) -> bool {
    let ext = names::EXTENSION.as_bytes();
    name.len() > ext.len() && name.ends_with(ext)
}

/// Все `*.yaml` папки `dir` и её подпапок. `relative` — путь `dir` относительно `.tasker` (пустой — сам `.tasker`). `accept`
/// получает относительный путь файла до чтения отпечатка: None — файл не нужен (и отпечаток у него не читается, если для этого
/// нужен отдельный вызов). Папку могут удалить посреди обхода (git checkout) — тогда в ответе то, что успели найти.
pub(super) fn tree<T>(dir: &Path, relative: &str, accept: &mut dyn FnMut(&str) -> Option<T>) -> Vec<Found<T>> {
    let mut out = Vec::new();
    imp::tree(dir, relative, accept, &mut out);
    out
}

fn child(relative: &str, name: &str) -> String {
    if relative.is_empty() {
        name.to_string()
    } else {
        format!("{relative}/{name}")
    }
}

#[cfg(unix)]
mod imp {
    use super::{Found, Stamp, child, is_yaml, net_ticks};
    use rustix::fs::{AtFlags, CWD, Dir, FileType, Mode, OFlags};
    use std::ffi::OsStr;
    use std::os::fd::{AsFd, BorrowedFd, OwnedFd};
    use std::os::unix::ffi::OsStrExt;
    use std::path::Path;
    use std::time::{Duration, SystemTime, UNIX_EPOCH};

    /// Запись папки: имя и что о ней известно без отдельного вызова.
    pub(super) struct Entry {
        pub name: Vec<u8>,
        pub kind: Kind,
    }

    pub(super) enum Kind {
        Dir,
        /// Обычный файл; отпечаток — если пришёл вместе с записью.
        File(Option<Stamp>),
        /// Символическая ссылка или тип неизвестен: нужен `fstatat`.
        Unknown,
        /// Сокет, канал, устройство — пропускается.
        Other,
    }

    const DIR_FLAGS: OFlags = OFlags::RDONLY.union(OFlags::DIRECTORY).union(OFlags::CLOEXEC);

    pub(super) fn tree<T>(dir: &Path, relative: &str, accept: &mut dyn FnMut(&str) -> Option<T>, out: &mut Vec<Found<T>>) {
        // Сама папка области может быть ссылкой (как у read_dir), подпапки — нет.
        if let Ok(fd) = rustix::fs::openat(CWD, dir, DIR_FLAGS, Mode::empty()) {
            walk(&fd, dir, relative, accept, out);
        }
    }

    fn walk<T>(fd: &OwnedFd, dir: &Path, relative: &str, accept: &mut dyn FnMut(&str) -> Option<T>, out: &mut Vec<Found<T>>) {
        for entry in list(fd) {
            if entry.name == b"." || entry.name == b".." {
                continue;
            }
            let name = OsStr::from_bytes(&entry.name);
            let mut kind = entry.kind;
            if matches!(kind, Kind::Unknown) {
                // Тип не пришёл с записью (ссылка или файловая система без d_type): папку видно по lstat, остальное — по stat ниже.
                match rustix::fs::statat(fd, name, AtFlags::SYMLINK_NOFOLLOW) {
                    Ok(st) => match FileType::from_raw_mode(st.st_mode as _) {
                        FileType::Directory => kind = Kind::Dir,
                        FileType::RegularFile => kind = Kind::File(Some(stamp_of_stat(&st))),
                        _ => {}
                    },
                    Err(_) => continue,
                }
            }
            match kind {
                Kind::Dir => {
                    let Ok(sub) = rustix::fs::openat(fd, name, DIR_FLAGS | OFlags::NOFOLLOW, Mode::empty()) else {
                        continue;
                    };
                    walk(&sub, &dir.join(name), &child(relative, &name.to_string_lossy()), accept, out);
                }
                Kind::Other => {}
                Kind::File(stamp) => push(fd, dir, relative, name, stamp, accept, out),
                Kind::Unknown => push(fd, dir, relative, name, None, accept, out),
            }
        }
    }

    fn push<T>(
        fd: &OwnedFd,
        dir: &Path,
        relative: &str,
        name: &OsStr,
        stamp: Option<Stamp>,
        accept: &mut dyn FnMut(&str) -> Option<T>,
        out: &mut Vec<Found<T>>,
    ) {
        if !is_yaml(name.as_bytes()) {
            return;
        }
        let path = child(relative, &name.to_string_lossy());
        let Some(value) = accept(&path) else {
            return;
        };
        let stamp = match stamp {
            Some(stamp) => stamp,
            // По ссылке — к файлу, как `std::fs::metadata`; не обычный файл (или уже удалён) — не файл области.
            None => match rustix::fs::statat(fd, name, AtFlags::empty()) {
                Ok(st) if FileType::from_raw_mode(st.st_mode as _) == FileType::RegularFile => stamp_of_stat(&st),
                _ => return,
            },
        };
        out.push(Found {
            relative: path,
            full_path: dir.join(name),
            stamp,
            value,
        });
    }

    // Типы полей stat разные на разных Unix и архитектурах (на этой могут уже быть i64).
    #[allow(clippy::unnecessary_cast)]
    fn stamp_of_stat(st: &rustix::fs::Stat) -> Stamp {
        Stamp {
            size: st.st_size as i64,
            mtime: ticks(st.st_mtime as i64, st.st_mtime_nsec as i64).unwrap_or(0),
        }
    }

    /// Время из секунд и наносекунд stat — так же, как `SystemTime` у std, затем в тики .NET.
    pub(super) fn ticks(sec: i64, nsec: i64) -> Option<i64> {
        if !(0..1_000_000_000).contains(&nsec) {
            return None;
        }
        let time: SystemTime = if sec >= 0 {
            UNIX_EPOCH.checked_add(Duration::new(sec as u64, nsec as u32))?
        } else {
            UNIX_EPOCH
                .checked_sub(Duration::from_secs(sec.unsigned_abs()))?
                .checked_add(Duration::from_nanos(nsec as u64))?
        };
        net_ticks(time)
    }

    fn list(fd: &OwnedFd) -> Vec<Entry> {
        #[cfg(target_os = "macos")]
        if !readdir_switch::readdir_only()
            && let Ok(entries) = super::bulk::list(fd.as_fd())
        {
            return entries;
        }
        read_dir(fd.as_fd())
    }

    /// Тесты проверяют на macOS и обход через readdir (как на остальных Unix).
    #[cfg(target_os = "macos")]
    pub(super) mod readdir_switch {
        #[cfg(test)]
        thread_local! {
            pub static READDIR_ONLY: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
        }

        pub fn readdir_only() -> bool {
            #[cfg(test)]
            return READDIR_ONLY.with(|x| x.get());
            #[cfg(not(test))]
            false
        }
    }

    // readdir: тип из d_type, отпечаток — отдельным fstatat.
    pub(super) fn read_dir(fd: BorrowedFd<'_>) -> Vec<Entry> {
        let Ok(mut dir) = Dir::read_from(fd) else {
            return Vec::new();
        };
        let mut entries = Vec::new();
        while let Some(entry) = dir.read() {
            let Ok(entry) = entry else {
                break;
            };
            let kind = match entry.file_type() {
                FileType::Directory => Kind::Dir,
                FileType::RegularFile => Kind::File(None),
                FileType::Symlink | FileType::Unknown => Kind::Unknown,
                _ => Kind::Other,
            };
            entries.push(Entry {
                name: entry.file_name().to_bytes().to_vec(),
                kind,
            });
        }
        entries
    }
}

/// `getattrlistbulk` (macOS): записи папки пачками с нужными атрибутами. Разбор по возвращённому набору атрибутов
/// (`ATTR_CMN_RETURNED_ATTRS`), с проверкой границ; ответ, который не разобрать, — ошибка, и папка читается через readdir.
#[cfg(target_os = "macos")]
mod bulk {
    use super::Stamp;
    use super::imp::{Entry, Kind, ticks};
    use std::io;
    use std::os::fd::{AsRawFd, BorrowedFd};

    // Нет в крейте libc: sys/attr.h и sys/vnode.h.
    const ATTR_CMN_ERROR: u32 = 0x2000_0000;
    const VREG: u32 = 1;
    const VDIR: u32 = 2;
    const VLNK: u32 = 5;

    const COMMON: u32 =
        libc::ATTR_CMN_RETURNED_ATTRS | libc::ATTR_CMN_NAME | ATTR_CMN_ERROR | libc::ATTR_CMN_OBJTYPE | libc::ATTR_CMN_MODTIME;
    const FILE: u32 = libc::ATTR_FILE_DATALENGTH;

    const BUFFER: usize = 64 * 1024;

    pub(super) fn list(fd: BorrowedFd<'_>) -> io::Result<Vec<Entry>> {
        let mut attrs = libc::attrlist {
            bitmapcount: libc::ATTR_BIT_MAP_COUNT,
            reserved: 0,
            commonattr: COMMON,
            volattr: 0,
            dirattr: 0,
            fileattr: FILE,
            forkattr: 0,
        };
        let mut buffer = vec![0u64; BUFFER / 8];
        let mut entries = Vec::new();
        loop {
            // SAFETY: attrs — корректный attrlist, буфер — BUFFER байт, выровнен на 8; ядро пишет не больше BUFFER байт.
            let count = unsafe {
                libc::getattrlistbulk(
                    fd.as_raw_fd(),
                    (&mut attrs as *mut libc::attrlist).cast(),
                    buffer.as_mut_ptr().cast(),
                    BUFFER,
                    0,
                )
            };
            if count < 0 {
                let error = io::Error::last_os_error();
                if error.kind() == io::ErrorKind::Interrupted {
                    continue;
                }
                return Err(error);
            }
            if count == 0 {
                return Ok(entries);
            }
            // SAFETY: тот же буфер как байты; читается только записанное ядром (длины записей проверяются).
            let bytes: &[u8] = unsafe { std::slice::from_raw_parts(buffer.as_ptr().cast(), BUFFER) };
            let mut offset = 0usize;
            for _ in 0..count {
                let length = u32_at(bytes, offset)? as usize;
                let record = bytes.get(offset..offset + length).ok_or_else(malformed)?;
                entries.push(entry(record)?);
                offset += length;
            }
        }
    }

    // Запись: длина (u32), возвращённые атрибуты (attribute_set_t), затем атрибуты по порядку битов — ATTR_CMN_ERROR сразу
    // после набора, затем имя, тип, время изменения, затем атрибуты файла.
    fn entry(record: &[u8]) -> io::Result<Entry> {
        let mut pos = 4;
        let common = u32_at(record, pos)?;
        let file = u32_at(record, pos + 12)?;
        pos += std::mem::size_of::<libc::attribute_set_t>();
        let mut error = 0;
        if common & ATTR_CMN_ERROR != 0 {
            error = u32_at(record, pos)?;
            pos += 4;
        }
        if common & libc::ATTR_CMN_NAME == 0 {
            return Err(malformed());
        }
        let data = i32_at(record, pos)?;
        let length = u32_at(record, pos + 4)? as usize;
        let start = usize::try_from(pos as i64 + data as i64).map_err(|_| malformed())?;
        let mut name = record.get(start..start + length).ok_or_else(malformed)?;
        if let Some(stripped) = name.strip_suffix(b"\0") {
            name = stripped;
        }
        pos += 8;
        let objtype = if common & libc::ATTR_CMN_OBJTYPE != 0 {
            pos += 4;
            Some(u32_at(record, pos - 4)?)
        } else {
            None
        };
        let modified = if common & libc::ATTR_CMN_MODTIME != 0 {
            pos += 16;
            ticks(i64_at(record, pos - 16)?, i64_at(record, pos - 8)?)
        } else {
            None
        };
        let size = if file & libc::ATTR_FILE_DATALENGTH != 0 {
            Some(i64_at(record, pos)?)
        } else {
            None
        };
        let kind = match objtype {
            // Атрибуты записи не прочитались — тип и отпечаток узнает fstatat.
            _ if error != 0 => Kind::Unknown,
            Some(VDIR) => Kind::Dir,
            Some(VREG) => Kind::File(match (size, modified) {
                (Some(size), Some(mtime)) => Some(Stamp { size, mtime }),
                _ => None,
            }),
            Some(VLNK) | None => Kind::Unknown,
            Some(_) => Kind::Other,
        };
        Ok(Entry { name: name.to_vec(), kind })
    }

    fn malformed() -> io::Error {
        io::Error::new(io::ErrorKind::InvalidData, "getattrlistbulk: malformed entry")
    }

    fn u32_at(bytes: &[u8], at: usize) -> io::Result<u32> {
        Ok(u32::from_ne_bytes(bytes.get(at..at + 4).ok_or_else(malformed)?.try_into().unwrap()))
    }

    fn i32_at(bytes: &[u8], at: usize) -> io::Result<i32> {
        Ok(i32::from_ne_bytes(bytes.get(at..at + 4).ok_or_else(malformed)?.try_into().unwrap()))
    }

    fn i64_at(bytes: &[u8], at: usize) -> io::Result<i64> {
        Ok(i64::from_ne_bytes(bytes.get(at..at + 8).ok_or_else(malformed)?.try_into().unwrap()))
    }
}

#[cfg(not(unix))]
mod imp {
    use super::{Found, child, is_yaml, stamp_of};
    use std::path::Path;

    pub(super) fn tree<T>(dir: &Path, relative: &str, accept: &mut dyn FnMut(&str) -> Option<T>, out: &mut Vec<Found<T>>) {
        let Ok(entries) = std::fs::read_dir(dir) else {
            return;
        };
        for entry in entries.flatten() {
            let Ok(file_type) = entry.file_type() else {
                continue;
            };
            let name = entry.file_name();
            let name = name.to_string_lossy();
            if file_type.is_dir() {
                tree(&entry.path(), &child(relative, &name), accept, out);
                continue;
            }
            if !is_yaml(name.as_bytes()) {
                continue;
            }
            let path = child(relative, &name);
            let Some(value) = accept(&path) else {
                continue;
            };
            let full_path = entry.path();
            if let Ok(metadata) = std::fs::metadata(&full_path)
                && metadata.is_file()
            {
                out.push(Found {
                    relative: path,
                    full_path,
                    stamp: stamp_of(&metadata),
                    value,
                });
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_support::temp_dir;
    use std::collections::BTreeMap;
    use std::time::{Duration, SystemTime, UNIX_EPOCH};

    // Прежний обход (read_dir + metadata каждого файла по полному пути) — эталон, с которым сверяется новый.
    fn reference(dir: &Path, relative: &str, out: &mut BTreeMap<String, (PathBuf, Stamp)>) {
        let Ok(entries) = std::fs::read_dir(dir) else {
            return;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            let name = entry.file_name().to_string_lossy().into_owned();
            let file_type = entry.file_type().unwrap();
            if file_type.is_dir() {
                reference(&path, &child(relative, &name), out);
            } else if path.extension().is_some_and(|e| e == "yaml")
                && let Ok(metadata) = std::fs::metadata(&path)
                && metadata.is_file()
            {
                out.insert(child(relative, &name), (path, stamp_of(&metadata)));
            }
        }
    }

    fn scanned(dir: &Path, relative: &str) -> BTreeMap<String, (PathBuf, Stamp)> {
        tree(dir, relative, &mut |_| Some(()))
            .into_iter()
            .map(|f| (f.relative, (f.full_path, f.stamp)))
            .collect()
    }

    fn set_modified(path: &Path, time: SystemTime) {
        std::fs::File::options().write(true).open(path).unwrap().set_modified(time).unwrap();
    }

    #[test]
    fn the_scan_finds_the_same_files_and_stamps_as_metadata_of_each_file() {
        let root = temp_dir();
        let ws = root.join(".tasker");
        let tasks = ws.join("projects").join("p").join("tasks");
        std::fs::create_dir_all(&tasks).unwrap();
        std::fs::create_dir_all(ws.join("users")).unwrap();
        std::fs::create_dir_all(ws.join(".cache")).unwrap();
        std::fs::write(ws.join("users").join("u.yaml"), "x").unwrap();
        std::fs::write(ws.join(".cache").join("index.db"), "db").unwrap();
        std::fs::write(tasks.join("notes.txt"), "not yaml").unwrap();
        std::fs::write(tasks.join(".yaml"), "no extension").unwrap();
        std::fs::write(tasks.join("upper.YAML"), "other extension").unwrap();
        std::fs::write(tasks.join("пример-задачи-00000001.yaml"), "unicode").unwrap();
        std::fs::write(tasks.join("empty.yaml"), "").unwrap();
        std::fs::create_dir_all(tasks.join("folder.yaml")).unwrap();
        std::fs::write(tasks.join("folder.yaml").join("inner.yaml"), "inner").unwrap();
        // Больше записей, чем входит в один ответ getattrlistbulk (64 КБ): длинные имена.
        for i in 0..1500 {
            std::fs::write(tasks.join(format!("{}-{i:08}.yaml", "t".repeat(120))), format!("task {i}")).unwrap();
        }
        // Время в прошлом, до эпохи Unix, с долями секунды и в будущем.
        set_modified(&tasks.join("empty.yaml"), UNIX_EPOCH - Duration::from_micros(1_500_250));
        set_modified(
            &ws.join("users").join("u.yaml"),
            UNIX_EPOCH + Duration::new(1_000_000_000, 123_456_789),
        );
        set_modified(
            &tasks.join("folder.yaml").join("inner.yaml"),
            SystemTime::now() + Duration::from_secs(86_400),
        );
        #[cfg(unix)]
        {
            use std::os::unix::fs::symlink;
            let outside = root.join("outside");
            std::fs::create_dir_all(&outside).unwrap();
            std::fs::write(outside.join("linked.yaml"), "outside file").unwrap();
            symlink(outside.join("linked.yaml"), tasks.join("link-to-file.yaml")).unwrap();
            symlink(&outside, tasks.join("link-to-dir")).unwrap();
            symlink(&outside, tasks.join("link-to-dir.yaml")).unwrap();
            symlink(root.join("missing.yaml"), tasks.join("dangling.yaml")).unwrap();
            let fifo = std::process::Command::new("mkfifo").arg(tasks.join("fifo.yaml")).status().unwrap();
            assert!(fifo.success());
        }

        let mut expected = BTreeMap::new();
        reference(&ws, "", &mut expected);
        let actual = scanned(&ws, "");
        // Тот же ответ у обхода readdir + fstatat (на macOS его иначе не видно: там пачки getattrlistbulk).
        #[cfg(target_os = "macos")]
        {
            imp::readdir_switch::READDIR_ONLY.with(|x| x.set(true));
            assert_eq!(scanned(&ws, ""), expected);
            imp::readdir_switch::READDIR_ONLY.with(|x| x.set(false));
        }
        assert_eq!(actual.len(), 1500 + 4 + usize::from(cfg!(unix)));
        assert_eq!(actual, expected);
        assert!(actual.contains_key("projects/p/tasks/folder.yaml/inner.yaml"));
        assert!(!actual.contains_key("projects/p/tasks/.yaml"));
        // Точность времени — как у файловой системы (до эпохи на некоторых — до секунд); равенство с metadata проверено выше.
        assert!(actual["projects/p/tasks/empty.yaml"].1.mtime < NET_TICKS_AT_UNIX_EPOCH - 10_000_000);
        assert!(actual["users/u.yaml"].1.mtime >= NET_TICKS_AT_UNIX_EPOCH + 10_000_000_000_000_000);
        #[cfg(unix)]
        assert_eq!(actual["projects/p/tasks/link-to-file.yaml"].1.size, "outside file".len() as i64);

        // Папка внутри области: относительные пути от неё.
        let tasks_only = scanned(&tasks, "projects/p/tasks");
        assert_eq!(tasks_only.len(), actual.len() - 1);
        assert!(tasks_only.keys().all(|k| k.starts_with("projects/p/tasks/")));

        // accept решает, нужен ли файл; ненужные не попадают в ответ.
        let only_inner = tree(&ws, "", &mut |r| r.ends_with("inner.yaml").then_some(7));
        assert_eq!(only_inner.len(), 1);
        assert_eq!(only_inner[0].value, 7);

        // Нет папки — пусто.
        assert!(scanned(&root.join("missing"), "missing").is_empty());
        std::fs::remove_dir_all(&root).unwrap();
    }

    // macOS: пачка getattrlistbulk и readdir + fstatat дают одни и те же записи.
    #[cfg(target_os = "macos")]
    #[test]
    fn bulk_listing_agrees_with_readdir() {
        use std::os::fd::AsFd;
        let root = temp_dir();
        std::fs::create_dir_all(root.join("sub")).unwrap();
        for i in 0..700 {
            std::fs::write(root.join(format!("{}-{i}.yaml", "n".repeat(150))), "x".repeat(i)).unwrap();
        }
        std::os::unix::fs::symlink(root.join("sub"), root.join("link")).unwrap();
        let fd = rustix::fs::openat(
            rustix::fs::CWD,
            &root,
            rustix::fs::OFlags::RDONLY | rustix::fs::OFlags::DIRECTORY,
            rustix::fs::Mode::empty(),
        )
        .unwrap();
        let describe = |entries: Vec<imp::Entry>| -> BTreeMap<Vec<u8>, String> {
            entries
                .into_iter()
                .filter(|e| e.name != b"." && e.name != b"..")
                .map(|e| {
                    let kind = match e.kind {
                        imp::Kind::Dir => "dir".to_string(),
                        imp::Kind::File(stamp) => {
                            let path = root.join(std::str::from_utf8(&e.name).unwrap());
                            let expected = stamp_of(&std::fs::metadata(&path).unwrap());
                            // readdir отпечаток не даёт, пачка — даёт, и он равен metadata.
                            assert!(stamp.is_none() || stamp == Some(expected));
                            "file".to_string()
                        }
                        imp::Kind::Unknown => "unknown".to_string(),
                        imp::Kind::Other => "other".to_string(),
                    };
                    (e.name, kind)
                })
                .collect()
        };
        let bulk = bulk::list(fd.as_fd()).unwrap();
        assert!(bulk.iter().filter(|e| matches!(e.kind, imp::Kind::File(Some(_)))).count() == 700);
        assert_eq!(describe(bulk), describe(imp::read_dir(fd.as_fd())));
        std::fs::remove_dir_all(&root).unwrap();
    }
}
