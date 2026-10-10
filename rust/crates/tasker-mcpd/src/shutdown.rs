//! Все способы остановить демон снаружи: сигналы SIGTERM и SIGINT (Ctrl+C) на Unix; на Windows — именованное событие
//! `Local\tasker-mcp-stop-<16 hex SHA-256 пути>` (.NET `ShutdownSignal`), которое взводят `tasker mcp stop` и `tasker mcp run` по
//! Ctrl+C, плюс Ctrl+C/Ctrl+Break консоли. Запрос `/daemon/stop` — отдельно, в HTTP.
//!
//! Windows-ветка написана по C# и не проверена (как и вся поддержка Windows).
use sha2::{Digest as _, Sha256};
use std::path::Path;
use tokio::sync::watch;

/// Имя события остановки для данного каталога данных: разные `TASKER_HOME` (тесты, несколько профилей) не мешают друг другу.
/// `Path.GetFullPath(dir).TrimEnd('/', '\\').ToLowerInvariant()` → `Local\tasker-mcp-stop-` + 16 hex SHA-256.
pub fn event_name(daemon_directory: &Path) -> String {
    let full = tasker_core::io::full_path(daemon_directory);
    let text = full.to_string_lossy();
    let normalized = tasker_core::validate::to_lower_invariant(text.trim_end_matches(['/', '\\']));
    let digest = Sha256::digest(normalized.as_bytes());
    let hex: String = digest[..8].iter().map(|b| format!("{b:02x}")).collect();
    format!("Local\\tasker-mcp-stop-{hex}")
}

/// Сигнал остановки процесса: [`trigger`](Self::trigger) взводится один раз любым из способов, ждать его могут несколько задач
/// (сервер и главный цикл).
pub struct Shutdown {
    tx: watch::Sender<bool>,
}

impl Shutdown {
    pub fn new() -> Shutdown {
        Shutdown {
            tx: watch::channel(false).0,
        }
    }

    pub fn trigger(&self) {
        let _ = self.tx.send(true);
    }

    /// Завершается, когда остановка запрошена (сразу, если уже).
    pub async fn triggered(&self) {
        let mut rx = self.tx.subscribe();
        let _ = rx.wait_for(|stop| *stop).await;
    }

    /// Ждёт остановки: сигнал системы или [`trigger`](Self::trigger). Возвращает, что сработало (для журнала).
    pub async fn wait(&self, daemon_directory: &Path) -> &'static str {
        tokio::select! {
            _ = self.triggered() => "request",
            reason = os_signal(daemon_directory) => reason,
        }
    }
}

impl Default for Shutdown {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(unix)]
pub async fn os_signal(_daemon_directory: &Path) -> &'static str {
    use tokio::signal::unix::{SignalKind, signal};
    let mut term = match signal(SignalKind::terminate()) {
        Ok(s) => s,
        Err(_) => return std::future::pending().await,
    };
    tokio::select! {
        _ = term.recv() => "SIGTERM",
        _ = tokio::signal::ctrl_c() => "SIGINT",
    }
}

#[cfg(windows)]
pub async fn os_signal(daemon_directory: &Path) -> &'static str {
    let name = event_name(daemon_directory);
    let event = windows_event::listen(&name);
    tokio::select! {
        _ = tokio::signal::ctrl_c() => "Ctrl+C",
        _ = event => "the stop event of 'tasker mcp stop'",
    }
}

/// Именованное событие Windows (`EventWaitHandle` с `ManualReset`): создаётся здесь, взводится консолью (`OpenExisting` + `Set`).
#[cfg(windows)]
mod windows_event {
    use std::ffi::c_void;
    use std::os::windows::ffi::OsStrExt as _;

    type Handle = *mut c_void;
    const INFINITE: u32 = 0xFFFF_FFFF;

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn CreateEventW(attributes: *const c_void, manual_reset: i32, initial_state: i32, name: *const u16) -> Handle;
        fn WaitForSingleObject(handle: Handle, milliseconds: u32) -> u32;
        fn CloseHandle(handle: Handle) -> i32;
    }

    /// Ждёт события в отдельном потоке (он живёт до конца процесса — как зарегистрированное ожидание в .NET).
    pub async fn listen(name: &str) {
        let wide: Vec<u16> = std::ffi::OsStr::new(name).encode_wide().chain(std::iter::once(0)).collect();
        let (tx, rx) = tokio::sync::oneshot::channel::<()>();
        std::thread::Builder::new()
            .name("tasker-stop-event".to_string())
            .spawn(move || {
                // SAFETY: имя — корректная строка UTF-16 с нулём на конце; дескриптор закрывается после ожидания.
                unsafe {
                    let handle = CreateEventW(std::ptr::null(), 1, 0, wide.as_ptr());
                    if handle.is_null() {
                        return;
                    }
                    WaitForSingleObject(handle, INFINITE);
                    CloseHandle(handle);
                }
                let _ = tx.send(());
            })
            .ok();
        if rx.await.is_err() {
            std::future::pending::<()>().await;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn event_name_is_a_hash_of_the_lowercased_path_without_trailing_separators() {
        let a = event_name(Path::new("/Users/Me/Library/Application Support/Tasker/mcp/"));
        let b = event_name(Path::new("/users/me/library/application support/tasker/mcp"));
        assert_eq!(a, b);
        assert!(a.starts_with("Local\\tasker-mcp-stop-"), "{a}");
        assert_eq!(a.len(), "Local\\tasker-mcp-stop-".len() + 16);
        assert!(
            a["Local\\tasker-mcp-stop-".len()..]
                .chars()
                .all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase())
        );
        // printf '/x/mcp' | shasum -a 256 | cut -c1-16
        assert_eq!(
            event_name(Path::new("/x/mcp")),
            format!("Local\\tasker-mcp-stop-{}", &sha_hex("/x/mcp")[..16])
        );
        assert_ne!(a, event_name(Path::new("/x/mcp")));
    }

    fn sha_hex(text: &str) -> String {
        Sha256::digest(text.as_bytes()).iter().map(|b| format!("{b:02x}")).collect()
    }
}
