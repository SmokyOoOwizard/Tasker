//! Наблюдатель `watch` на реальной файловой системе (на macOS — FSEvents, с его повторами и «липкими» флагами): копия `rust/tests/golden/workspace` в `target/tmp`,
//! правки файлов → одна пачка `Changed` с относительными путями; `.cache` и `*.tmp` не приходят; 600 файлов → `FullSync`;
//! остановка не отдаёт накопленное окно (как `Stop` в .NET). Тайминги с запасом: ждём до нескольких секунд, не ровно 300 мс.
use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError};
use std::time::{Duration, Instant};
use tasker_files::watch::{WatchEvent, WatchOptions, WatcherHandle, WorkspaceWatcher};

const PROJECT: &str = "11111111-1111-4111-8111-111111111111";
const WAIT: Duration = Duration::from_secs(8);

fn temp_dir() -> PathBuf {
    let dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../target/tmp")
        .join(uuid::Uuid::new_v4().simple().to_string());
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

fn copy_dir(from: &Path, to: &Path) {
    std::fs::create_dir_all(to).unwrap();
    for entry in std::fs::read_dir(from).unwrap() {
        let entry = entry.unwrap();
        let target = to.join(entry.file_name());
        if entry.file_type().unwrap().is_dir() {
            copy_dir(&entry.path(), &target);
        } else {
            std::fs::copy(entry.path(), target).unwrap();
        }
    }
}

/// Копия golden-области; возвращает путь к её `.tasker`.
fn workspace_copy() -> PathBuf {
    let golden = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/golden/workspace/.tasker");
    let root = temp_dir().join(".tasker");
    copy_dir(&golden, &root);
    std::fs::create_dir_all(root.join(".cache")).unwrap();
    // FSEvents доносит события о только что скопированных файлах с задержкой, уже после подписки: даём им уйти до старта.
    std::thread::sleep(Duration::from_millis(1000));
    root
}

fn first_yaml(dir: &Path) -> PathBuf {
    let mut files: Vec<PathBuf> = std::fs::read_dir(dir)
        .unwrap()
        .map(|e| e.unwrap().path())
        .filter(|p| p.extension().is_some_and(|e| e == "yaml"))
        .collect();
    files.sort();
    files.remove(0)
}

fn relative(root: &Path, path: &Path) -> String {
    path.strip_prefix(root)
        .unwrap()
        .components()
        .map(|c| c.as_os_str().to_string_lossy().into_owned())
        .collect::<Vec<_>>()
        .join("/")
}

fn start(root: &Path) -> (WatcherHandle, Receiver<WatchEvent>) {
    let (tx, rx) = mpsc::channel();
    let handle = WorkspaceWatcher::start(root, move |event| {
        let _ = tx.send(event);
    })
    .unwrap();
    // FSEvents отдаёт события только после подписки, но подписка оформляется асинхронно: даём ей встать и выбрасываем хвост
    // событий о копировании, если он всё-таки дошёл.
    std::thread::sleep(Duration::from_millis(500));
    while rx.try_recv().is_ok() {}
    (handle, rx)
}

fn next(rx: &Receiver<WatchEvent>) -> WatchEvent {
    match rx.recv_timeout(WAIT) {
        Ok(event) => event,
        Err(e) => panic!("no watcher event within {WAIT:?}: {e}"),
    }
}

fn assert_silent(rx: &Receiver<WatchEvent>, for_how_long: Duration) {
    match rx.recv_timeout(for_how_long) {
        // Disconnected — обработчик (и его Sender) уже уничтожен остановкой: событий тоже нет.
        Err(RecvTimeoutError::Timeout | RecvTimeoutError::Disconnected) => {}
        Ok(event) => panic!("unexpected watcher event: {event:?}"),
    }
}

fn append(path: &Path, text: &str) {
    let mut bytes = std::fs::read(path).unwrap();
    bytes.extend_from_slice(text.as_bytes());
    std::fs::write(path, bytes).unwrap();
}

#[test]
#[cfg_attr(windows, ignore = "TSK-155: on Windows the watcher also reports changed folders")]
fn burst_of_edits_is_one_changed_event_with_relative_paths() {
    let root = workspace_copy();
    let project = root.join("projects").join(PROJECT);
    let (handle, rx) = start(&root);

    let task = first_yaml(&project.join("tasks"));
    let status = first_yaml(&project.join("statuses"));
    let created = project.join("tasks").join("new-task-deadbeef.yaml");
    let removed = first_yaml(&project.join("boards"));
    let user = first_yaml(&root.join("users"));

    append(&task, "\n# edited\n");
    append(&status, "\n# edited\n");
    std::fs::write(&created, "id: deadbeef-0000-4000-8000-000000000000\ntitle: new\n").unwrap();
    std::fs::remove_file(&removed).unwrap();
    append(&user, "\n# edited\n");

    let mut expected: Vec<String> = [&task, &status, &created, &removed, &user]
        .iter()
        .map(|p| relative(&root, p))
        .collect();
    expected.sort();

    assert_eq!(next(&rx), WatchEvent::Changed(expected));
    // Одна пачка, не несколько: следующее окно не открывается без новых правок.
    assert_silent(&rx, Duration::from_millis(1500));
    handle.stop();
}

#[test]
#[cfg_attr(windows, ignore = "TSK-155: on Windows the watcher also reports changed folders")]
fn cache_and_tmp_files_are_ignored() {
    let root = workspace_copy();
    let project = root.join("projects").join(PROJECT);
    let (handle, rx) = start(&root);

    std::fs::write(root.join(".cache").join("index-rs.db"), b"sqlite").unwrap();
    std::fs::create_dir_all(root.join(".cache").join("edit-locks")).unwrap();
    std::fs::write(root.join(".cache").join("edit-locks").join("x.json"), b"{}").unwrap();
    std::fs::write(project.join("tasks").join("task-aaaaaaaa.yaml.tmp"), b"partial").unwrap();
    std::fs::write(root.join(".gitignore"), b"/.cache/\n*.tmp\n").unwrap();
    std::fs::write(project.join("tasks").join("notes.txt"), b"not yaml").unwrap();
    assert_silent(&rx, Duration::from_millis(2500));

    // Наблюдатель жив: настоящая правка приходит, и только она.
    let task = first_yaml(&project.join("tasks"));
    append(&task, "\n# edited\n");
    assert_eq!(next(&rx), WatchEvent::Changed(vec![relative(&root, &task)]));
    handle.stop();
}

#[test]
#[cfg_attr(windows, ignore = "TSK-155: on Windows the watcher also reports changed folders")]
fn rename_reports_old_and_new_paths() {
    let root = workspace_copy();
    let tasks = root.join("projects").join(PROJECT).join("tasks");
    let (handle, rx) = start(&root);

    let old = first_yaml(&tasks);
    let new = tasks.join("renamed-task-deadbeef.yaml");
    std::fs::rename(&old, &new).unwrap();

    let mut expected = vec![relative(&root, &old), relative(&root, &new)];
    expected.sort();
    assert_eq!(next(&rx), WatchEvent::Changed(expected));
    handle.stop();
}

#[test]
fn deleted_project_folder_is_reported_as_folder_path() {
    let root = workspace_copy();
    let project = root.join("projects").join(PROJECT);
    let (handle, rx) = start(&root);

    std::fs::remove_dir_all(&project).unwrap();

    // Удалённая папка: событие о ней самой и/или о её файлах — но не FullSync (папка проекта — ~60 файлов, порог 500).
    match next(&rx) {
        WatchEvent::Changed(paths) => {
            let prefix = format!("projects/{PROJECT}");
            assert!(
                paths.iter().all(|p| p == &prefix || p.starts_with(&format!("{prefix}/"))),
                "{paths:?}"
            );
            assert!(!paths.is_empty());
        }
        other => panic!("unexpected {other:?}"),
    }
    handle.stop();
}

#[test]
fn more_than_500_files_in_one_window_is_full_sync() {
    let root = workspace_copy();
    let tasks = root.join("projects").join(PROJECT).join("tasks");
    let (handle, rx) = start(&root);

    for i in 0..600 {
        std::fs::write(tasks.join(format!("bulk-{i}-{i:08x}.yaml")), format!("id: {i}\n")).unwrap();
    }

    assert_eq!(next(&rx), WatchEvent::FullSync);
    handle.stop();
}

#[test]
fn stop_drops_pending_window_and_drop_does_not_panic() {
    let root = workspace_copy();
    let task = first_yaml(&root.join("projects").join(PROJECT).join("tasks"));
    let (handle, rx) = start(&root);

    append(&task, "\n# edited\n");
    // Останавливаем до закрытия окна (300 мс): накопленное пропадает, как при отмене Task.Delay в .NET Stop.
    handle.stop();
    assert_silent(&rx, Duration::from_millis(1500));

    // Drop вместо stop() — тоже завершает потоки, без паники; события после остановки не приходят.
    let (handle, rx) = start(&root);
    drop(handle);
    append(&task, "\n# edited again\n");
    assert_silent(&rx, Duration::from_millis(1500));
}

#[test]
fn start_fails_when_tasker_folder_is_missing() {
    let missing = temp_dir().join(".tasker");
    assert!(WorkspaceWatcher::start(&missing, |_| {}).is_err());
}

#[test]
fn custom_windows_apply() {
    let root = workspace_copy();
    let task = first_yaml(&root.join("projects").join(PROJECT).join("tasks"));
    let options = WatchOptions {
        quiet: Duration::from_millis(50),
        full_sync_threshold: 0,
        ..WatchOptions::default()
    };
    let (tx, rx) = mpsc::channel();
    let handle = WorkspaceWatcher::start_with(&root, options, move |event| {
        let _ = tx.send(event);
    })
    .unwrap();
    std::thread::sleep(Duration::from_millis(500));

    let started = Instant::now();
    append(&task, "\n# edited\n");
    // Порог 0: любой путь — FullSync.
    assert_eq!(next(&rx), WatchEvent::FullSync);
    assert!(started.elapsed() < WAIT);
    handle.stop();
}
