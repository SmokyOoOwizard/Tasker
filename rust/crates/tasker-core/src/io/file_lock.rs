//! `FileLock`: короткая блокировка между процессами и внутри процесса — файл, открытый эксклюзивно (на Unix `flock`, на Windows
//! `FileShare.None`). Держат только на время самой операции, поэтому с одной папкой одновременно работают демон, десктоп и
//! консоль. Занято — ждём, пока освободится (до [`DEFAULT_TIMEOUT`]). Процесс упал — ОС снимает блокировку сама, файл остаётся и
//! переиспользуется; в него ничего не пишут и не читают. Блокировка не реентерабельна для файла, но [`FileLock::run`] внутри уже
//! удерживаемой блокировки (в том же потоке) не ждёт самого себя.
use crate::io::path_rules::{full_path, path_key};
use std::collections::{HashMap, HashSet};
use std::fs::File;
use std::io;
use std::path::Path;
use std::sync::{Arc, Condvar, Mutex, OnceLock};
use std::time::{Duration, Instant};

pub const DEFAULT_TIMEOUT: Duration = Duration::from_secs(30);

// Самая длинная пауза между попытками занять блокировку, которую держит другой процесс (мс).
const MAX_POLL: u64 = 5;

// Внутри процесса очередь на файл — по одному и без опроса. Файл разводит процессы.
struct Gate {
    busy: Mutex<bool>,
    freed: Condvar,
}

fn gates() -> &'static Mutex<HashMap<String, Arc<Gate>>> {
    static GATES: OnceLock<Mutex<HashMap<String, Arc<Gate>>>> = OnceLock::new();
    GATES.get_or_init(|| Mutex::new(HashMap::new()))
}

thread_local! {
    // Блокировки, которые удерживает текущий поток: вложенный run их не берёт второй раз.
    static HELD: std::cell::RefCell<HashSet<String>> = std::cell::RefCell::new(HashSet::new());
}

pub struct FileLock {
    _file: File,
    gate: Arc<Gate>,
}

impl FileLock {
    /// Ждёт блокировку. Освобождать — `drop`.
    ///
    /// Ошибка `TimedOut`: блокировка не освободилась за `timeout` (`Lock {path} is busy: another Tasker process keeps it for more than {N} s`).
    pub fn acquire(path: &Path, timeout: Option<Duration>) -> io::Result<FileLock> {
        let full = full_path(path);
        let wait = timeout.unwrap_or(DEFAULT_TIMEOUT);
        let deadline = Instant::now() + wait;

        let gate = gates()
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .entry(path_key(&full.to_string_lossy()))
            .or_insert_with(|| {
                Arc::new(Gate {
                    busy: Mutex::new(false),
                    freed: Condvar::new(),
                })
            })
            .clone();
        {
            let mut busy = gate.busy.lock().unwrap_or_else(|e| e.into_inner());
            while *busy {
                let left = deadline.saturating_duration_since(Instant::now());
                if left.is_zero() {
                    return Err(busy_error(&full, wait));
                }
                busy = gate.freed.wait_timeout(busy, left).unwrap_or_else(|e| e.into_inner()).0;
            }
            *busy = true;
        }

        match Self::open(&full, deadline, wait) {
            Ok(file) => Ok(FileLock { _file: file, gate }),
            Err(e) => {
                release(&gate);
                Err(e)
            }
        }
    }

    fn open(full: &Path, deadline: Instant, wait: Duration) -> io::Result<File> {
        if let Some(parent) = full.parent() {
            std::fs::create_dir_all(parent)?;
        }
        // Держат блокировки доли миллисекунды, а ждущий спит между попытками: короткий шаг (1→2→4→5 мс) — те же попытки,
        // но очередь движется со скоростью самой работы.
        let mut delay = 1;
        loop {
            match try_open(full) {
                Ok(file) => return Ok(file),
                Err(e) if is_busy(&e) => {}
                Err(e) => return Err(e),
            }
            if Instant::now() >= deadline {
                return Err(busy_error(full, wait));
            }
            std::thread::sleep(Duration::from_millis(delay));
            delay = (delay * 2).min(MAX_POLL);
        }
    }

    /// Выполняет `action` под блокировкой. Если этот поток уже держит её (вложенный вызов), берёт не заново, а продолжает под той же.
    pub fn run<T>(path: &Path, timeout: Option<Duration>, action: impl FnOnce() -> T) -> io::Result<T> {
        let full = full_path(path);
        let key = path_key(&full.to_string_lossy());
        if HELD.with(|held| held.borrow().contains(&key)) {
            return Ok(action());
        }
        let lock = Self::acquire(&full, timeout)?;
        HELD.with(|held| held.borrow_mut().insert(key.clone()));
        let result = action();
        HELD.with(|held| held.borrow_mut().remove(&key));
        drop(lock);
        Ok(result)
    }
}

impl Drop for FileLock {
    fn drop(&mut self) {
        release(&self.gate);
    }
}

fn release(gate: &Gate) {
    *gate.busy.lock().unwrap_or_else(|e| e.into_inner()) = false;
    gate.freed.notify_one();
}

fn busy_error(path: &Path, timeout: Duration) -> io::Error {
    io::Error::new(
        io::ErrorKind::TimedOut,
        format!(
            "Lock {} is busy: another Tasker process keeps it for more than {:.0} s",
            path.display(),
            timeout.as_secs_f64()
        ),
    )
}

#[cfg(unix)]
fn try_open(path: &Path) -> io::Result<File> {
    let file = File::options().read(true).write(true).create(true).truncate(false).open(path)?;
    rustix::fs::flock(&file, rustix::fs::FlockOperation::NonBlockingLockExclusive)
        .map_err(|e| io::Error::from_raw_os_error(e.raw_os_error()))?;
    Ok(file)
}

#[cfg(unix)]
fn is_busy(e: &io::Error) -> bool {
    // Держит другой процесс: flock отвечает EWOULDBLOCK. .NET ждёт на любой IOException, но не на UnauthorizedAccessException
    // (EACCES) — та на Unix сразу ошибка.
    !matches!(e.kind(), io::ErrorKind::NotFound | io::ErrorKind::PermissionDenied)
}

#[cfg(windows)]
fn try_open(path: &Path) -> io::Result<File> {
    use std::os::windows::fs::OpenOptionsExt as _;
    File::options()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .share_mode(0)
        .open(path)
}

#[cfg(windows)]
fn is_busy(e: &io::Error) -> bool {
    // Держит другой процесс; так же отвечает файл, который ещё удаляется (отказ в доступе): подождём.
    e.kind() != io::ErrorKind::NotFound
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_lock(name: &str) -> std::path::PathBuf {
        crate::test_support::temp_dir().join(name)
    }

    #[test]
    fn nested_run_in_the_same_thread_does_not_wait() {
        let path = temp_lock("a.lock");
        let value = FileLock::run(&path, None, || {
            FileLock::run(&path, Some(Duration::from_millis(50)), || 42).unwrap()
        })
        .unwrap();
        assert_eq!(value, 42);
        // После выхода блокировка свободна.
        assert_eq!(FileLock::run(&path, Some(Duration::from_millis(50)), || 1).unwrap(), 1);
    }

    #[test]
    fn another_thread_waits_and_times_out_with_the_dotnet_message() {
        let path = temp_lock("b.lock");
        let lock = FileLock::acquire(&path, None).unwrap();
        let p = path.clone();
        let error = std::thread::spawn(move || FileLock::acquire(&p, Some(Duration::from_millis(30))).err().unwrap())
            .join()
            .unwrap();
        assert_eq!(error.kind(), io::ErrorKind::TimedOut);
        assert_eq!(
            error.to_string(),
            format!(
                "Lock {} is busy: another Tasker process keeps it for more than 0 s",
                full_path(&path).display()
            )
        );
        drop(lock);
        FileLock::acquire(&path, Some(Duration::from_millis(30))).unwrap();
    }

    #[test]
    fn threads_are_serialized() {
        let path = temp_lock("c.lock");
        let counter = Arc::new(Mutex::new(0));
        let handles: Vec<_> = (0..8)
            .map(|_| {
                let (p, c) = (path.clone(), counter.clone());
                std::thread::spawn(move || {
                    for _ in 0..20 {
                        FileLock::run(&p, None, || {
                            let mut n = c.lock().unwrap();
                            *n += 1;
                        })
                        .unwrap();
                    }
                })
            })
            .collect();
        for h in handles {
            h.join().unwrap();
        }
        assert_eq!(*counter.lock().unwrap(), 160);
    }
}
