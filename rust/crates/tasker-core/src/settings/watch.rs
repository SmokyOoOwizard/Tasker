//! `SettingsStore.Watcher`: слежение за `settings.json` для демона. События файловой системы склеиваются: обработчик получает уже
//! готовые настройки после того, как файл затих на [`WATCH_QUIET`] (200 мс). Тот же файл (временная запись, повторное событие)
//! изменением не считается — сравнивается отпечаток содержимого (SHA-256). Файл нечитаем (правят руками, половина JSON) —
//! обработчик не вызывается, прежние настройки остаются в силе до следующего изменения.
//!
//! Устройство: `notify` на каталоге настроек (без вложенных), события только о `settings.json` (создание, запись, переименование —
//! в том числе атомарная запись `.tmp` → `settings.json`), поток-таймер с окном тишины, как `Timer.Change(Quiet)` в .NET.
use crate::io;
use crate::settings::global_settings::GlobalSettings;
use crate::settings::store::{SettingsError, SettingsStore};
use notify::{EventKind, RecommendedWatcher, RecursiveMode, Watcher as _};
use sha2::{Digest as _, Sha256};
use std::path::Path;
use std::sync::mpsc::{self, RecvTimeoutError, Sender};
use std::thread::JoinHandle;
use std::time::Duration;

/// Окно тишины после последнего события файла (`SettingsStore.Quiet`).
pub const WATCH_QUIET: Duration = Duration::from_millis(200);

const FILE_NAME: &str = "settings.json";

enum Message {
    Touched,
    Stop,
}

/// Работающее слежение; `drop` останавливает его.
pub struct SettingsWatch {
    watcher: Option<RecommendedWatcher>,
    stop: Option<Sender<Message>>,
    thread: Option<JoinHandle<()>>,
}

impl SettingsStore {
    /// Вызывает `on_changed`, когда файл настроек меняется (в том числе другим процессом). Каталог настроек создаётся, если его нет
    /// (иначе следить не за чем). Обработчик вызывается в потоке слежения.
    pub fn watch(&self, on_changed: impl FnMut(GlobalSettings) + Send + 'static) -> Result<SettingsWatch, SettingsError> {
        let directory = self.directory().to_path_buf();
        std::fs::create_dir_all(&directory)?;
        let file = directory.join(FILE_NAME);

        let (tx, rx) = mpsc::channel::<Message>();
        let producer = tx.clone();
        let mut watcher = notify::recommended_watcher(move |result: notify::Result<notify::Event>| {
            let touched = match result {
                Ok(event) => is_change(&event.kind) && event.paths.iter().any(|p| is_settings_file(p)),
                // Потеря событий — проверим файл: отпечаток отсеет ложную тревогу.
                Err(_) => true,
            };
            if touched {
                let _ = producer.send(Message::Touched);
            }
        })
        .map_err(notify_error)?;
        watcher.watch(&directory, RecursiveMode::NonRecursive).map_err(notify_error)?;

        let store = SettingsStore::new(Some(directory));
        let mut last = fingerprint(&file);
        let thread = std::thread::Builder::new()
            .name("tasker-settings-watcher".to_string())
            .spawn(move || {
                let mut on_changed = on_changed;
                loop {
                    // Первое событие окна…
                    match rx.recv() {
                        Ok(Message::Touched) => {}
                        Ok(Message::Stop) | Err(_) => return,
                    }
                    // …и тишина после последнего: каждое новое событие сдвигает срабатывание (`Timer.Change`).
                    loop {
                        match rx.recv_timeout(WATCH_QUIET) {
                            Ok(Message::Touched) => continue,
                            Ok(Message::Stop) | Err(RecvTimeoutError::Disconnected) => return,
                            Err(RecvTimeoutError::Timeout) => break,
                        }
                    }
                    let now = fingerprint(&file);
                    if now == last {
                        continue;
                    }
                    // Файл сломан на время правки руками (ошибка загрузки) — ждём следующего изменения.
                    if let Ok(settings) = store.load() {
                        last = now;
                        on_changed(settings);
                    }
                }
            })?;

        Ok(SettingsWatch {
            watcher: Some(watcher),
            stop: Some(tx),
            thread: Some(thread),
        })
    }
}

impl Drop for SettingsWatch {
    fn drop(&mut self) {
        drop(self.watcher.take());
        if let Some(stop) = self.stop.take() {
            let _ = stop.send(Message::Stop);
        }
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

impl std::fmt::Debug for SettingsWatch {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SettingsWatch").field("running", &self.thread.is_some()).finish()
    }
}

/// `NotifyFilters.FileName | LastWrite | Size`: создание, запись, переименование; чтение и смена прав — нет.
fn is_change(kind: &EventKind) -> bool {
    !matches!(
        kind,
        EventKind::Access(_) | EventKind::Modify(notify::event::ModifyKind::Metadata(_))
    )
}

fn is_settings_file(path: &Path) -> bool {
    path.file_name().is_some_and(|n| n == FILE_NAME)
}

/// SHA-256 содержимого; None — файла нет или он не читается (сам факт тоже отпечаток: удаление файла — изменение).
fn fingerprint(file: &Path) -> Option<[u8; 32]> {
    io::read_all_text(file).ok().map(|text| Sha256::digest(text.as_bytes()).into())
}

fn notify_error(e: notify::Error) -> SettingsError {
    match e.kind {
        notify::ErrorKind::Io(io) => SettingsError::Io(io),
        _ => SettingsError::Io(std::io::Error::other(format!("cannot watch the settings file: {e}"))),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::settings::WorkspaceLocation;
    use std::sync::mpsc;
    use std::time::Instant;

    fn home() -> std::path::PathBuf {
        crate::test_support::temp_dir()
    }

    fn recv(rx: &mpsc::Receiver<GlobalSettings>, within: Duration) -> Option<GlobalSettings> {
        rx.recv_timeout(within).ok()
    }

    #[test]
    fn change_by_the_store_is_reported_once_with_the_new_settings() {
        let dir = home();
        let store = SettingsStore::new(Some(dir.join("home")));
        let (tx, rx) = mpsc::channel();
        let watch = store.watch(move |s| tx.send(s).unwrap()).unwrap();
        assert!(
            store.file_path().parent().unwrap().is_dir(),
            "the directory is created to be watched"
        );

        let folder = dir.join("a");
        std::fs::create_dir_all(&folder).unwrap();
        store.add_workspace(&WorkspaceLocation::files(folder.to_str().unwrap())).unwrap();

        let changed = recv(&rx, Duration::from_secs(5)).expect("the change is reported");
        assert_eq!(changed.mcp.workspaces.len(), 1);
        // Один вызов на одну запись (события create/modify/rename склеены).
        assert!(recv(&rx, Duration::from_millis(600)).is_none());
        drop(watch);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn same_content_broken_file_and_unrelated_files_are_ignored() {
        let dir = home();
        let store = SettingsStore::new(Some(dir.join("home")));
        store.set_port(6001).unwrap();
        let (tx, rx) = mpsc::channel();
        let _watch = store.watch(move |s| tx.send(s).unwrap()).unwrap();

        // Тот же текст — не изменение.
        let text = std::fs::read_to_string(store.file_path()).unwrap();
        io::write_all_text(&store.file_path(), &text).unwrap();
        // Другой файл в каталоге — не интересует.
        std::fs::write(store.file_path().with_file_name("other.json"), "{}").unwrap();
        // Битый файл — обработчик не вызывается, ждём следующего изменения.
        std::fs::write(store.file_path(), "{ not json").unwrap();
        assert!(recv(&rx, Duration::from_millis(800)).is_none());

        // Починили — сообщается.
        io::write_all_text(&store.file_path(), "{\"mcp\":{\"port\":6002}}").unwrap();
        let changed = recv(&rx, Duration::from_secs(5)).expect("the fix is reported");
        assert_eq!(changed.mcp.port, 6002);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn events_are_debounced_until_the_file_is_quiet() {
        let dir = home();
        let store = SettingsStore::new(Some(dir.join("home")));
        let (tx, rx) = mpsc::channel();
        let _watch = store.watch(move |s| tx.send(s).unwrap()).unwrap();

        let started = Instant::now();
        for port in 6100..6105 {
            store.set_port(port).unwrap();
            std::thread::sleep(Duration::from_millis(50));
        }
        let changed = recv(&rx, Duration::from_secs(5)).expect("the last state is reported");
        assert_eq!(changed.mcp.port, 6104);
        // Последняя запись — через 200 мс после первой; окно тишины отсчитывается от неё.
        assert!(
            started.elapsed() >= Duration::from_millis(200) + WATCH_QUIET,
            "{:?}",
            started.elapsed()
        );
        assert!(recv(&rx, Duration::from_millis(600)).is_none());
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn deleting_the_file_reports_the_defaults() {
        let dir = home();
        let store = SettingsStore::new(Some(dir.join("home")));
        store.set_port(6201).unwrap();
        let (tx, rx) = mpsc::channel();
        let _watch = store.watch(move |s| tx.send(s).unwrap()).unwrap();

        std::fs::remove_file(store.file_path()).unwrap();
        let changed = recv(&rx, Duration::from_secs(5)).expect("the removal is reported");
        assert_eq!(changed, GlobalSettings::default());
        let _ = std::fs::remove_dir_all(&dir);
    }
}
