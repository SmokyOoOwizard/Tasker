//! Наблюдатель за файлами `.tasker` (`WorkspaceWatcher` в .NET, TSK-133): правки руками, `git pull`, checkout, merge — пока папка
//! открыта демоном. События файловой системы не отдаются по одному: пути копятся и отдаются пачкой, когда файловая система затихнет
//! на [`WatchOptions::quiet`] (300 мс), но не позже [`WatchOptions::max_delay`] (2 с) с первого события окна. Checkout на сотни
//! файлов — одна пачка; больше [`WatchOptions::full_sync_threshold`] (500) путей за окно, потеря событий наблюдателем
//! (переполнение очереди, ошибка, `Rescan`) или событие о самой папке `.tasker` — [`WatchEvent::FullSync`] вместо точечного
//! [`WatchEvent::Changed`].
//!
//! К индексу (TSK-131) наблюдатель не привязан: получатель — [`WatchHandler`] (любое замыкание `FnMut(WatchEvent)`), пути в
//! `Changed` — относительно `.tasker` через `/`, как в индексе; подключение `Changed` → `Refresh(paths)`, `FullSync` → `Sync` делает
//! демон.
//!
//! Устройство: `notify` (на macOS — FSEvents, на Linux — inotify) → поток-сборщик с окнами .NET; повторы одного пути схлопывает
//! сам сборщик (множество путей), оба пути переименования помечаются, как в .NET. `notify-debouncer-full` из плана не используется:
//! он считает пару `Create` + `Remove` одного файла в своём окне пустой, а FSEvents несколько секунд держит флаг `Created` на пути —
//! удаление файла, созданного 2–5 с назад, приходило бы как `Create` + `Remove` и терялось (индекс оставлял бы призрак до следующей
//! сверки). Отличия от .NET описаны у [`scope_of`] и [`WatcherHandle::stop`].
use std::collections::BTreeSet;
use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use notify::event::{MetadataKind, ModifyKind};
use notify::{Event, EventKind, RecommendedWatcher, RecursiveMode, Watcher};

use crate::layout::{self, CACHE_NAME};

/// Ошибка запуска наблюдателя (нет папки, нет прав, лимит дескрипторов) — ошибка `notify` как есть.
pub type Error = notify::Error;

/// Что изменилось за окно наблюдения.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WatchEvent {
    /// Пути относительно `.tasker` через `/`, без повторов, по возрастанию: файлы раскладки (`projects/<guid>/tasks/<slug>-<id8>.yaml`,
    /// `users/<guid>.yaml`, `projects/<guid>/project.yaml`) и папки раскладки (`projects/<guid>`, `projects/<guid>/tasks`, `users`) —
    /// удалённая или переименованная папка проекта приходит как путь папки, индекс снимает её строки по префиксу. Файл может уже
    /// не существовать (удаление, старое имя при переименовании) — получатель сверяет с диском, как `Sync` в .NET.
    Changed(Vec<String>),
    /// Сверить область целиком: за окно накопилось больше [`WatchOptions::full_sync_threshold`] путей, наблюдатель потерял события
    /// (ошибка, переполнение, `Rescan`), событие пришло о самой папке `.tasker` или о пути вне её.
    FullSync,
}

/// Получатель событий; реализован для любого `FnMut(WatchEvent) + Send + 'static`. Вызывается в потоке наблюдателя, по одному событию
/// за раз; пока обработчик работает, следующее окно копится.
pub trait WatchHandler: Send + 'static {
    fn handle(&mut self, event: WatchEvent);
}

impl<F> WatchHandler for F
where
    F: FnMut(WatchEvent) + Send + 'static,
{
    fn handle(&mut self, event: WatchEvent) {
        (self)(event)
    }
}

/// Окна наблюдения. По умолчанию — как в .NET `WorkspaceWatcher`: `Quiet` 300 мс, `MaxDelay` 2 с, `FullRescanThreshold` 500.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WatchOptions {
    /// Пачка отдаётся, когда с последнего события прошло столько времени.
    pub quiet: Duration,
    /// …но не позже, чем через столько времени после первого события окна: непрерывные правки не откладывают пачку бесконечно.
    pub max_delay: Duration,
    /// Больше стольких путей за окно — `FullSync` (сверить всё дешевле, чем разбирать каждый).
    pub full_sync_threshold: usize,
}

impl Default for WatchOptions {
    fn default() -> Self {
        WatchOptions {
            quiet: Duration::from_millis(300),
            max_delay: Duration::from_secs(2),
            full_sync_threshold: 500,
        }
    }
}

/// Наблюдатель за `.tasker`. Запускается [`WorkspaceWatcher::start`], живёт пока жив [`WatcherHandle`].
pub struct WorkspaceWatcher;

impl WorkspaceWatcher {
    /// Следит за `tasker_root` (папкой `.tasker`, она должна существовать) рекурсивно с окнами по умолчанию.
    pub fn start(tasker_root: impl AsRef<Path>, handler: impl WatchHandler) -> Result<WatcherHandle, Error> {
        Self::start_with(tasker_root, WatchOptions::default(), handler)
    }

    /// То же со своими окнами (тесты, диагностика).
    pub fn start_with(tasker_root: impl AsRef<Path>, options: WatchOptions, handler: impl WatchHandler) -> Result<WatcherHandle, Error> {
        let root = tasker_root.as_ref().to_path_buf();
        // FSEvents отдаёт пути от канонического корня (notify канонизирует путь при watch): /private/tmp вместо /tmp.
        let canonical = std::fs::canonicalize(&root).ok().filter(|c| c != &root);

        let (tx, rx) = mpsc::channel::<Message>();
        let producer = tx.clone();
        let roots = Roots { root, canonical };
        let mut watcher = notify::recommended_watcher(move |result: notify::Result<Event>| {
            // Получателя уже нет (остановка) — события некуда отдавать.
            let _ = producer.send(match result {
                Ok(event) => roots.message(&event),
                // Потеря событий (переполнение очереди, ошибка платформы) — как `Error` у FileSystemWatcher: полная сверка.
                Err(_) => Message::FullSync,
            });
        })?;
        watcher.watch(tasker_root.as_ref(), RecursiveMode::Recursive)?;

        let collector = std::thread::Builder::new()
            .name("tasker-workspace-watcher".to_string())
            .spawn(move || collect(&rx, &options, handler))
            .map_err(notify::Error::io)?;

        Ok(WatcherHandle {
            watcher: Some(watcher),
            stop: Some(tx),
            collector: Some(collector),
        })
    }
}

/// Работающий наблюдатель. [`stop`](Self::stop) или `Drop` завершает поток `notify` и сборщик и дренирует очередь.
pub struct WatcherHandle {
    watcher: Option<RecommendedWatcher>,
    stop: Option<Sender<Message>>,
    collector: Option<JoinHandle<()>>,
}

impl WatcherHandle {
    /// Останавливает наблюдение и ждёт завершения потоков. Накопленное, но ещё не отданное окно пропадает — как в .NET `Stop`
    /// (отмена `Task.Delay`): пачка догонится сверкой при следующем открытии области. Если обработчик в этот момент работает,
    /// ждёт его возврата.
    pub fn stop(mut self) {
        self.shutdown();
    }

    fn shutdown(&mut self) {
        // Сначала источник: `notify` останавливает поток событий в Drop и больше не шлёт в канал.
        drop(self.watcher.take());
        if let Some(stop) = self.stop.take() {
            let _ = stop.send(Message::Stop);
        }
        if let Some(collector) = self.collector.take() {
            // Паника обработчика уже завершила поток — повторно не падаем.
            let _ = collector.join();
        }
    }
}

impl Drop for WatcherHandle {
    fn drop(&mut self) {
        self.shutdown();
    }
}

impl std::fmt::Debug for WatcherHandle {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WatcherHandle").field("running", &self.collector.is_some()).finish()
    }
}

/// Сообщение от потока `notify` сборщику.
#[derive(Debug, Clone, PartialEq, Eq)]
enum Message {
    /// Относительные пути одного события, уже отфильтрованные ([`scope_of`]); пустой список возможен (все пути отсеяны) и окна не
    /// открывает.
    Changed(Vec<String>),
    FullSync,
    Stop,
}

struct Roots {
    root: PathBuf,
    canonical: Option<PathBuf>,
}

impl Roots {
    fn message(&self, event: &Event) -> Message {
        if event.need_rescan() {
            return Message::FullSync;
        }
        let mut paths = Vec::new();
        if is_change(&event.kind) {
            for path in &event.paths {
                match self.relative(path) {
                    // Событие о самой `.tasker` (удалили, пересоздали, переименовали) или о пути вне её — сверяем всё, как
                    // `Sync` в .NET при пути, начинающемся с `..`.
                    None => return Message::FullSync,
                    Some(relative) => match scope_of(&relative) {
                        Scope::Root => return Message::FullSync,
                        Scope::Ignored => {}
                        Scope::Path => paths.push(relative),
                    },
                }
            }
        }
        Message::Changed(paths)
    }

    /// Путь относительно `.tasker` через `/`; None — вне неё.
    fn relative(&self, path: &Path) -> Option<String> {
        let relative = path
            .strip_prefix(&self.root)
            .ok()
            .or_else(|| self.canonical.as_ref().and_then(|canonical| path.strip_prefix(canonical).ok()))?;
        Some(
            relative
                .components()
                .map(|c| c.as_os_str().to_string_lossy().into_owned())
                .collect::<Vec<_>>()
                .join("/"),
        )
    }
}

/// Что .NET считает изменением: `NotifyFilters.FileName | DirectoryName | LastWrite | Size` — создание, удаление, переименование,
/// запись. Чтение файла и смена прав/владельца — нет.
fn is_change(kind: &EventKind) -> bool {
    !matches!(
        kind,
        EventKind::Access(_)
            | EventKind::Modify(ModifyKind::Metadata(
                MetadataKind::Ownership | MetadataKind::Permissions | MetadataKind::Extended | MetadataKind::AccessTime
            ))
    )
}

/// Чем путь является для наблюдателя.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Scope {
    /// Сама папка `.tasker` (пустой путь) — сверять всё.
    Root,
    /// Не интересует индекс: `.cache` (там лежит сам индекс и блокировки), `*.tmp` атомарной записи, `.gitignore`, чужие файлы.
    Ignored,
    /// Файл или папка раскладки — в `Changed`.
    Path,
}

/// Фильтр путей наблюдателя. .NET отбрасывает только `.cache`, а остальное передаёт в `Sync`, где файлы не из раскладки отсеивает
/// `Classify`; здесь отсев делается до пачки, чтобы `*.tmp` и прочий шум не раздували окно до порога `FullSync` и не будили индекс.
/// Папки раскладки (`projects`, `projects/<guid>`, `projects/<guid>/<folder>`, `users`) проходят: удалённую или переименованную папку
/// индекс сверяет по префиксу, как `Sync` в .NET с путём папки.
pub fn scope_of(relative_path: &str) -> Scope {
    if relative_path.is_empty() {
        return Scope::Root;
    }
    if relative_path.ends_with(".tmp") {
        return Scope::Ignored;
    }
    let parts: Vec<&str> = relative_path.split('/').collect();
    if parts[0] == CACHE_NAME {
        return Scope::Ignored;
    }
    if layout::classify(relative_path).is_some() {
        return Scope::Path;
    }
    let is_folder = match parts.as_slice() {
        ["projects"] | ["users"] => true,
        ["projects", project] => uuid::Uuid::try_parse(project).is_ok(),
        ["projects", project, folder] => uuid::Uuid::try_parse(project).is_ok() && layout::folder_named(folder).is_some(),
        _ => false,
    };
    if is_folder { Scope::Path } else { Scope::Ignored }
}

/// Цикл сборщика — `Loop` в .NET: ждёт первого события, затем спит `quiet` и повторяет, пока за время сна приходили новые события
/// и с начала окна прошло меньше `max_delay`; потом отдаёт пачку. `Stop` в любой момент — выход без отдачи накопленного.
fn collect(rx: &Receiver<Message>, options: &WatchOptions, mut handler: impl WatchHandler) {
    let mut dirty = BTreeSet::new();
    let mut full_sync = false;

    loop {
        // Ждём первое событие окна.
        let Ok(message) = rx.recv() else { return };
        if !absorb(message, &mut dirty, &mut full_sync) {
            return;
        }
        if dirty.is_empty() && !full_sync {
            continue;
        }

        let started = Instant::now();
        loop {
            let mut changed = false;
            let deadline = Instant::now() + options.quiet;
            loop {
                let remaining = deadline.saturating_duration_since(Instant::now());
                match rx.recv_timeout(remaining) {
                    Ok(message) => {
                        if !absorb(message, &mut dirty, &mut full_sync) {
                            return;
                        }
                        changed = true;
                    }
                    Err(RecvTimeoutError::Timeout) => break,
                    Err(RecvTimeoutError::Disconnected) => return,
                }
            }
            if !changed || started.elapsed() >= options.max_delay {
                break;
            }
        }

        let paths: Vec<String> = std::mem::take(&mut dirty).into_iter().collect();
        if std::mem::take(&mut full_sync) || paths.len() > options.full_sync_threshold {
            handler.handle(WatchEvent::FullSync);
        } else if !paths.is_empty() {
            handler.handle(WatchEvent::Changed(paths));
        }
    }
}

/// Складывает сообщение в окно; false — пришёл `Stop`.
fn absorb(message: Message, dirty: &mut BTreeSet<String>, full_sync: &mut bool) -> bool {
    match message {
        Message::Changed(paths) => dirty.extend(paths),
        Message::FullSync => *full_sync = true,
        Message::Stop => return false,
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc;

    #[test]
    fn scope_of_layout_paths() {
        assert_eq!(scope_of(""), Scope::Root);
        assert_eq!(scope_of(".cache"), Scope::Ignored);
        assert_eq!(scope_of(".cache/index.db"), Scope::Ignored);
        assert_eq!(scope_of(".cache/edit-locks/x.json"), Scope::Ignored);
        assert_eq!(scope_of(".gitignore"), Scope::Ignored);
        assert_eq!(
            scope_of("projects/11111111-1111-4111-8111-111111111111/tasks/task-1111aaaa.yaml.tmp"),
            Scope::Ignored
        );
        assert_eq!(
            scope_of("projects/11111111-1111-4111-8111-111111111111/tasks/notes.txt"),
            Scope::Ignored
        );
        assert_eq!(scope_of("projects/not-a-guid"), Scope::Ignored);
        assert_eq!(scope_of("projects/11111111-1111-4111-8111-111111111111/unknown"), Scope::Ignored);
        assert_eq!(scope_of("stray.yaml"), Scope::Ignored);

        assert_eq!(scope_of("projects"), Scope::Path);
        assert_eq!(scope_of("users"), Scope::Path);
        assert_eq!(scope_of("projects/11111111-1111-4111-8111-111111111111"), Scope::Path);
        assert_eq!(scope_of("projects/11111111-1111-4111-8111-111111111111/tasks"), Scope::Path);
        assert_eq!(scope_of("projects/11111111-1111-4111-8111-111111111111/project.yaml"), Scope::Path);
        assert_eq!(
            scope_of("projects/11111111-1111-4111-8111-111111111111/tasks/task-1111aaaa.yaml"),
            Scope::Path
        );
        assert_eq!(
            scope_of("projects/11111111-1111-4111-8111-111111111111/tasks/11111111-1111-4111-8111-111111111111.yaml"),
            Scope::Path
        );
        assert_eq!(scope_of("users/11111111-1111-4111-8111-111111111111.yaml"), Scope::Path);
    }

    #[test]
    fn relative_paths_use_root_or_canonical_root() {
        let roots = Roots {
            root: PathBuf::from("/tmp/ws/.tasker"),
            canonical: Some(PathBuf::from("/private/tmp/ws/.tasker")),
        };
        assert_eq!(
            roots.relative(Path::new("/tmp/ws/.tasker/users/a.yaml")).as_deref(),
            Some("users/a.yaml")
        );
        assert_eq!(
            roots
                .relative(Path::new("/private/tmp/ws/.tasker/projects/p/tasks/t.yaml"))
                .as_deref(),
            Some("projects/p/tasks/t.yaml")
        );
        assert_eq!(roots.relative(Path::new("/tmp/ws/.tasker")).as_deref(), Some(""));
        assert_eq!(roots.relative(Path::new("/tmp/other/.tasker/users/a.yaml")), None);
    }

    fn options() -> WatchOptions {
        WatchOptions {
            quiet: Duration::from_millis(100),
            max_delay: Duration::from_millis(500),
            full_sync_threshold: 5,
        }
    }

    fn run(options: WatchOptions, feed: impl FnOnce(&Sender<Message>)) -> Vec<(WatchEvent, Duration)> {
        let (tx, rx) = mpsc::channel();
        let (out_tx, out_rx) = mpsc::channel();
        let started = Instant::now();
        let collector = std::thread::spawn(move || {
            collect(&rx, &options, move |event| out_tx.send((event, started.elapsed())).unwrap());
        });
        feed(&tx);
        drop(tx);
        collector.join().unwrap();
        out_rx.iter().collect()
    }

    #[test]
    fn burst_becomes_one_changed_batch_sorted_and_deduplicated() {
        let events = run(options(), |tx| {
            tx.send(Message::Changed(vec!["users/b.yaml".into()])).unwrap();
            tx.send(Message::Changed(vec!["users/a.yaml".into(), "users/b.yaml".into()]))
                .unwrap();
            tx.send(Message::Changed(vec![])).unwrap();
            std::thread::sleep(Duration::from_millis(400));
        });
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].0, WatchEvent::Changed(vec!["users/a.yaml".into(), "users/b.yaml".into()]));
    }

    #[test]
    fn continuous_changes_are_flushed_at_max_delay() {
        let events = run(options(), |tx| {
            // Правки каждые 40 мс дольше потолка (500 мс): без потолка окно не закрылось бы.
            for i in 0..30 {
                tx.send(Message::Changed(vec![format!("users/{i}.yaml")])).unwrap();
                std::thread::sleep(Duration::from_millis(40));
            }
            std::thread::sleep(Duration::from_millis(400));
        });
        assert!(events.len() >= 2, "{events:?}");
        // Первая пачка — не раньше потолка (окно продлевалось) и не намного позже него.
        assert!(events[0].1 >= Duration::from_millis(500), "{events:?}");
        assert!(events[0].1 < Duration::from_millis(1500), "{events:?}");
        assert!(
            events
                .iter()
                .all(|(e, _)| matches!(e, WatchEvent::FullSync | WatchEvent::Changed(_)))
        );
    }

    #[test]
    fn more_paths_than_threshold_is_full_sync() {
        let events = run(options(), |tx| {
            tx.send(Message::Changed((0..6).map(|i| format!("users/{i}.yaml")).collect()))
                .unwrap();
            std::thread::sleep(Duration::from_millis(300));
        });
        assert_eq!(
            events.iter().map(|(e, _)| e.clone()).collect::<Vec<_>>(),
            vec![WatchEvent::FullSync]
        );

        let events = run(options(), |tx| {
            tx.send(Message::Changed((0..5).map(|i| format!("users/{i}.yaml")).collect()))
                .unwrap();
            std::thread::sleep(Duration::from_millis(300));
        });
        assert!(
            matches!(events.as_slice(), [(WatchEvent::Changed(paths), _)] if paths.len() == 5),
            "{events:?}"
        );
    }

    #[test]
    fn error_in_window_is_full_sync_and_empty_batches_do_not_wake() {
        let events = run(options(), |tx| {
            tx.send(Message::Changed(vec![])).unwrap();
            std::thread::sleep(Duration::from_millis(250));
            tx.send(Message::Changed(vec!["users/a.yaml".into()])).unwrap();
            tx.send(Message::FullSync).unwrap();
            std::thread::sleep(Duration::from_millis(300));
        });
        assert_eq!(
            events.iter().map(|(e, _)| e.clone()).collect::<Vec<_>>(),
            vec![WatchEvent::FullSync]
        );
    }

    #[test]
    fn stop_drops_pending_window() {
        let events = run(options(), |tx| {
            tx.send(Message::Changed(vec!["users/a.yaml".into()])).unwrap();
            tx.send(Message::Stop).unwrap();
            std::thread::sleep(Duration::from_millis(300));
        });
        assert!(events.is_empty(), "{events:?}");
    }
}
