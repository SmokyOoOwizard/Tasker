//! `AtomicFile`: чтение, запись, переименование и удаление файлов так, чтобы это работало и на Windows. Там файл, открытый
//! другим процессом, нельзя переименовать, заменить или удалить, а антивирус и индексатор открывают только что записанные файлы
//! на доли секунды — поэтому кратковременные отказы («файл занят», «доступ запрещён») повторяются с короткими паузами. Только
//! на Windows: на Unix такого отказа нет, и повтор скрыл бы настоящую ошибку. Решение «повторять ли» — чистая функция
//! [`is_transient`], повтор — [`retry`] с подставляемыми паузами: оба проверяются тестами на любой платформе.
use crate::io::line_endings::strip_bom;
use std::io::{self, Write as _};
use std::path::{Path, PathBuf};

/// Паузы между попытками (мс): в сумме около полутора секунд — дольше антивирус файл не держит.
pub const DELAYS: [u64; 16] = [5, 10, 20, 40, 80, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100];

const ERROR_ACCESS_DENIED: i32 = 5;
const ERROR_SHARING_VIOLATION: i32 = 32;
const ERROR_LOCK_VIOLATION: i32 = 33;
const ERROR_USER_MAPPED_FILE: i32 = 1224;

/// Кратковременный ли это отказ Windows (файл занят другим процессом, удаление ещё не завершилось): такую операцию стоит
/// повторить. «Файла нет», «путь не найден» — нет: повтор их не исправит.
pub fn is_transient(e: &io::Error) -> bool {
    match e.kind() {
        io::ErrorKind::PermissionDenied => true,
        io::ErrorKind::NotFound => false,
        _ => matches!(
            e.raw_os_error(),
            Some(ERROR_ACCESS_DENIED | ERROR_SHARING_VIOLATION | ERROR_LOCK_VIOLATION | ERROR_USER_MAPPED_FILE)
        ),
    }
}

/// Выполняет `operation`; при кратковременном отказе ([`is_transient`]) повторяет, пока не кончатся `delays`.
/// `retry == false` — одна попытка (не Windows). `sleep` — пауза в мс.
pub fn retry<T>(mut operation: impl FnMut() -> io::Result<T>, retry: bool, delays: &[u64], mut sleep: impl FnMut(u64)) -> io::Result<T> {
    let mut attempt = 0;
    loop {
        match operation() {
            Ok(value) => return Ok(value),
            Err(e) if retry && attempt < delays.len() && is_transient(&e) => {
                sleep(delays[attempt]);
                attempt += 1;
            }
            Err(e) => return Err(e),
        }
    }
}

fn with_retry<T>(operation: impl FnMut() -> io::Result<T>) -> io::Result<T> {
    retry(operation, cfg!(windows), &DELAYS, |ms| {
        std::thread::sleep(std::time::Duration::from_millis(ms))
    })
}

/// Переименование (`overwrite` — с заменой существующего файла: атомарно, где система это умеет).
pub fn move_file(from: &Path, to: &Path, overwrite: bool) -> io::Result<()> {
    with_retry(|| {
        if !overwrite && to.exists() {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                format!("The file '{}' already exists.", to.display()),
            ));
        }
        std::fs::rename(from, to)
    })
}

/// Удаление файла; файла нет — не ошибка.
pub fn delete(path: &Path) -> io::Result<()> {
    with_retry(|| match std::fs::remove_file(path) {
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
        other => other,
    })
}

fn temp_path(path: &Path) -> PathBuf {
    let mut temp = path.as_os_str().to_os_string();
    temp.push(".tmp");
    PathBuf::from(temp)
}

/// Атомарная запись: во временный файл рядом (`<path>.tmp`), затем переименование поверх. При обрыве посреди записи остаётся
/// только `.tmp` (он в .gitignore и ничего не значит, следующая запись его перезапишет), а не обрезанный файл. Без fsync — как в .NET.
pub fn write_all_bytes(path: &Path, bytes: &[u8]) -> io::Result<()> {
    if let Some(parent) = crate::io::full_path(path).parent() {
        std::fs::create_dir_all(parent)?;
    }
    let temp = temp_path(path);
    // Создание временного файла тоже может упереться в файл, который ещё не удалён после прошлой записи (антивирус держит).
    let mut file = with_retry(|| std::fs::File::create(&temp))?;
    file.write_all(bytes)?;
    drop(file);
    move_file(&temp, path, true)
}

pub fn write_all_text(path: &Path, text: &str) -> io::Result<()> {
    write_all_bytes(path, text.as_bytes())
}

/// Читает файл целиком (на Windows — с повторами при кратковременном отказе).
pub fn read_all_bytes(path: &Path) -> io::Result<Vec<u8>> {
    with_retry(|| std::fs::read(path))
}

/// Читает текст (UTF-8, BOM отбрасывается, недопустимые байты — U+FFFD, как у `UTF8Encoding`).
pub fn read_all_text(path: &Path) -> io::Result<String> {
    let bytes = read_all_bytes(path)?;
    let text = String::from_utf8_lossy(&bytes);
    Ok(strip_bom(&text).to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn retry_repeats_only_transient_errors() {
        let mut calls = 0;
        let mut slept = Vec::new();
        let result = retry(
            || {
                calls += 1;
                if calls < 3 {
                    Err(io::Error::from(io::ErrorKind::PermissionDenied))
                } else {
                    Ok(calls)
                }
            },
            true,
            &[1, 2, 3],
            |ms| slept.push(ms),
        );
        assert_eq!(result.unwrap(), 3);
        assert_eq!(slept, vec![1, 2]);

        let mut calls = 0;
        let result: io::Result<()> = retry(
            || {
                calls += 1;
                Err(io::Error::from(io::ErrorKind::NotFound))
            },
            true,
            &[1, 2, 3],
            |_| panic!("no sleep"),
        );
        assert_eq!(result.unwrap_err().kind(), io::ErrorKind::NotFound);
        assert_eq!(calls, 1);

        let mut calls = 0;
        let result: io::Result<()> = retry(
            || {
                calls += 1;
                Err(io::Error::from(io::ErrorKind::PermissionDenied))
            },
            false,
            &[1, 2, 3],
            |_| panic!("no sleep"),
        );
        assert!(result.is_err());
        assert_eq!(calls, 1);

        let mut calls = 0;
        let result: io::Result<()> = retry(
            || {
                calls += 1;
                Err(io::Error::from_raw_os_error(32))
            },
            true,
            &[1, 1],
            |_| {},
        );
        assert!(result.is_err());
        assert_eq!(calls, 3);
    }

    #[test]
    fn write_is_atomic_and_read_strips_bom() {
        let dir = crate::test_support::temp_dir();
        let path = dir.join("sub").join("file.txt");
        write_all_text(&path, "a\nb").unwrap();
        assert_eq!(read_all_text(&path).unwrap(), "a\nb");
        assert!(!temp_path(&path).exists());
        write_all_bytes(&path, b"\xEF\xBB\xBFx").unwrap();
        assert_eq!(read_all_text(&path).unwrap(), "x");
        assert_eq!(read_all_bytes(&path).unwrap(), b"\xEF\xBB\xBFx");
        delete(&path).unwrap();
        delete(&path).unwrap();
        assert!(read_all_text(&path).is_err());
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
