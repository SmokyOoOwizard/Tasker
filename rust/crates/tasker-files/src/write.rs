//! Запись файлов `.tasker` на диск — операции `YamlFile` (.NET), которые работают с блокировками: запись, перезапись «если не
//! изменился» (с переименованием при смене названия), переименование, удаление по версии, апгрейд формата на диске.
//!
//! Порядок захвата общий для всех (`YamlFile.WithLock`): сначала блокировка записи папки `.tasker` между процессами
//! (`.cache/write.lock`, [`FileLock`]), затем очередь на файл внутри процесса — «сравнить версию и записать» атомарно, даже когда
//! пишут демон, десктоп и командная строка. Тот же порядок у [`WriteScope`], который держит блокировку записи и внутри пишет файлы:
//! обратный порядок дал бы взаимную блокировку. Путь вне `.tasker` — только очередь внутри процесса.
//!
//! Индекс (`.cache/index.lock`, SQLite) — задача TSK-131: где .NET зовёт `index.Refresh`, здесь крючок [`IndexRefresh`].
use crate::error::Error;
use crate::format;
use crate::layout::{NAME, TaskerDirectory, write_lock_of};
use std::collections::HashMap;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, OnceLock};
use tasker_core::io::{self as atomic, FileLock, full_path, path_key, same_path};
use tasker_core::versioning::version_of;
use uuid::Uuid;

/// Сверка индекса с файлами после записи (`WorkspaceIndex.Refresh` в .NET). Реализация — в крейте индекса (TSK-131);
/// [`NoIndex`] — заглушка, пока индекса нет.
pub trait IndexRefresh {
    /// `paths` — файлы или папки, которые изменились (или могли измениться: `git pull`).
    fn refresh(&self, paths: &[PathBuf]) -> io::Result<()>;

    /// Индекс только что целиком сверен с диском при открытии ([`crate::index::WorkspaceIndex::open`]): `true` — один раз, первой
    /// секции записи, и только вскоре после сверки. Секция, которой `write.lock` достался без ожидания, тогда не сверяет папку задач
    /// ещё раз. Номера серий в Tasker меняют только секции записи, а они обновляют общий индекс до того, как отпустить
    /// `write.lock` (и Rust, и .NET), — такую запись после сверки при открытии индекс уже знает. Файлы, которые `git pull` принёс
    /// между сверкой и записью, не видны ни так, ни сверкой под блокировкой, если пришли после неё: окно — те же миллисекунды.
    fn take_fresh(&self) -> bool {
        false
    }
}

/// Индекса нет: ничего не делает.
pub struct NoIndex;

impl IndexRefresh for NoIndex {
    fn refresh(&self, _paths: &[PathBuf]) -> io::Result<()> {
        Ok(())
    }
}

/// Блокировка записи между процессами (`.cache/write.lock`, та же, что берут операции этого модуля) плюс сверка индекса задач
/// проекта: `git pull` мог принести файлы, о которых кэш ещё не знает, а номер серии считается по индексу. Вложенный вызов и
/// записи хранилищ внутри действия не ждут самих себя (`WriteScope` в .NET).
pub struct WriteScope<'a> {
    directory: &'a TaskerDirectory,
    index: &'a dyn IndexRefresh,
}

impl<'a> WriteScope<'a> {
    pub fn new(directory: &'a TaskerDirectory, index: &'a dyn IndexRefresh) -> WriteScope<'a> {
        WriteScope { directory, index }
    }

    pub fn exclusive<T>(&self, project_id: &Uuid, action: impl FnOnce() -> io::Result<T>) -> io::Result<T> {
        FileLock::run_noting_wait(&self.directory.write_lock(), None, |waited| {
            // Сверка при открытии была только что, и блокировка досталась сразу (см. IndexRefresh::take_fresh).
            if self.index.take_fresh() && !waited {
                return action();
            }
            // Задачи всех папок проекта (вторая папка после слияния git — тоже задачи проекта).
            let mut tasks: Vec<PathBuf> = self.directory.project_dirs(project_id).iter().map(|x| x.tasks()).collect();
            if tasks.is_empty() {
                tasks.push(self.directory.project(project_id).tasks());
            }
            self.index.refresh(&tasks)?;
            action()
        })?
    }
}

// Очередь на файл внутри процесса: «сравнить версию и записать» — по одной на путь (ключ по правилам PathRules).
fn gates() -> &'static Mutex<HashMap<String, Arc<Mutex<()>>>> {
    static GATES: OnceLock<Mutex<HashMap<String, Arc<Mutex<()>>>>> = OnceLock::new();
    GATES.get_or_init(|| Mutex::new(HashMap::new()))
}

fn gated<T>(path: &Path, action: impl FnOnce() -> io::Result<T>) -> io::Result<T> {
    let gate = gates()
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .entry(path_key(&full_path(path).to_string_lossy()))
        .or_insert_with(|| Arc::new(Mutex::new(())))
        .clone();
    let _held = gate.lock().unwrap_or_else(|e| e.into_inner());
    action()
}

/// Файл блокировки записи для пути внутри `.tasker` (`TaskerDirectory.WriteLock`); None — путь не в `.tasker`.
pub fn write_lock_path(path: &Path) -> Option<PathBuf> {
    let full = full_path(path);
    full.ancestors()
        .find(|dir| dir.file_name().is_some_and(|name| name == NAME))
        .map(write_lock_of)
}

/// Выполняет действие под блокировкой по ключу (пути файла или каталога) — например, проверку уникальности и запись одним
/// шагом (`YamlFile.Locked`). Ключ отличается от путей отдельных файлов, поэтому внутри можно звать [`write`]/[`write_if_match`]
/// этих файлов.
pub fn locked<T>(key: &Path, action: impl FnOnce() -> io::Result<T>) -> io::Result<T> {
    with_lock(key, action)
}

fn with_lock<T>(path: &Path, action: impl FnOnce() -> io::Result<T>) -> io::Result<T> {
    match write_lock_path(path) {
        Some(lock) => FileLock::run(&lock, None, || gated(path, action))?,
        None => gated(path, action),
    }
}

/// Байты файла; None — файла (или его папки) нет. На Windows — с повторами при кратковременном отказе.
pub fn read_bytes(path: &Path) -> io::Result<Option<Vec<u8>>> {
    match atomic::read_all_bytes(path) {
        Ok(bytes) => Ok(Some(bytes)),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(e),
    }
}

fn current_version(path: &Path) -> io::Result<Option<String>> {
    Ok(read_bytes(path)?.map(|bytes| version_of(&bytes)))
}

/// Атомарная запись: сначала во временный файл рядом, затем переименование — при сбое посреди записи на диске не останется
/// обрезанного файла. `bytes` — уже сериализованный файл текущего формата (`files::*::serialize`).
fn write_unlocked(path: &Path, bytes: &[u8]) -> io::Result<String> {
    atomic::write_all_bytes(path, bytes)?;
    Ok(version_of(bytes))
}

/// Запись нового файла. Возвращает его версию.
pub fn write(path: &Path, bytes: &[u8]) -> io::Result<String> {
    with_lock(path, || write_unlocked(path, bytes))
}

/// Перезапись, только если файл не изменился с версии `expected_version`. Возвращает новую версию или None, если файл изменён
/// или удалён.
pub fn write_if_match(path: &Path, bytes: &[u8], expected_version: &str) -> io::Result<Option<String>> {
    with_lock(path, || {
        if current_version(path)?.as_deref() == Some(expected_version) {
            write_unlocked(path, bytes).map(Some)
        } else {
            Ok(None)
        }
    })
}

fn file_name(path: &Path) -> String {
    format::file_name(path)
}

/// Перезапись с переименованием: если файл `path` не изменился с версии `expected_version`, байты записываются в `new_path` (то же
/// имя — на месте), а прежний файл удаляется. Всё под блокировкой записи.
///
/// Ошибка `AlreadyExists` — под новым именем уже лежит другой файл (его не затираем):
/// `Cannot rename '{old}' to '{new}': the file exists`.
pub fn write_if_match_renaming(path: &Path, new_path: &Path, bytes: &[u8], expected_version: &str) -> io::Result<Option<String>> {
    with_lock(path, || {
        if current_version(path)?.as_deref() != Some(expected_version) {
            return Ok(None);
        }
        if same_path(&path.to_string_lossy(), &new_path.to_string_lossy()) {
            return write_unlocked(path, bytes).map(Some);
        }
        if new_path.exists() {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                format!("Cannot rename '{}' to '{}': the file exists", file_name(path), file_name(new_path)),
            ));
        }
        let version = write_unlocked(new_path, bytes)?;
        atomic::delete(path)?;
        Ok(Some(version))
    })
}

/// Перезапись `project.yaml` с переносом папки проекта: если файл `path` (в папке `folder`) не изменился с версии
/// `expected_version`, папка переименовывается в `new_folder` (одной операцией: другие процессы видят старую или новую папку, не
/// смесь; на Windows занятые файлы — с повторами), затем файл пишется в неё. Всё под блокировкой записи. Папка `new_folder` уже
/// есть — файл пишется на месте, папки сливает `tasker cleanup`. Возвращает новую версию или None, если файл изменён или удалён.
pub fn write_if_match_moving(
    path: &Path,
    folder: &Path,
    new_folder: &Path,
    bytes: &[u8],
    expected_version: &str,
) -> io::Result<Option<String>> {
    with_lock(path, || {
        if current_version(path)?.as_deref() != Some(expected_version) {
            return Ok(None);
        }
        let mut file = path.to_path_buf();
        if !same_path(&folder.to_string_lossy(), &new_folder.to_string_lossy())
            && !new_folder.exists()
            && let Some(name) = path.file_name()
        {
            atomic::move_file(folder, new_folder, false)?;
            file = new_folder.join(name);
        }
        write_unlocked(&file, bytes).map(Some)
    })
}

/// Переименование файла под блокировкой записи. Файл под новым именем уже есть — не затираем.
/// `false` — исходного файла уже нет или новое имя занято.
pub fn rename(path: &Path, new_path: &Path) -> io::Result<bool> {
    with_lock(path, || {
        if !path.exists() || new_path.exists() {
            return Ok(false);
        }
        atomic::move_file(path, new_path, false)?;
        Ok(true)
    })
}

/// Удаление, только если файл не изменился с версии `expected_version`.
pub fn delete_if_match(path: &Path, expected_version: &str) -> io::Result<bool> {
    delete_if_match_with(path, expected_version, || atomic::delete(path))
}

/// Выполняет `delete` (например, удаление папки проекта), только если файл `path` не изменился с версии `expected_version`.
pub fn delete_if_match_with(path: &Path, expected_version: &str, delete: impl FnOnce() -> io::Result<()>) -> io::Result<bool> {
    with_lock(path, || {
        if current_version(path)?.as_deref() != Some(expected_version) {
            return Ok(false);
        }
        delete()?;
        Ok(true)
    })
}

/// Ошибка апгрейда на диске: ввод-вывод или неподдерживаемый формат (`UnsupportedFormatException`).
#[derive(Debug)]
pub enum UpgradeError {
    Io(io::Error),
    Format(Error),
}

impl std::fmt::Display for UpgradeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Io(e) => write!(f, "{e}"),
            Self::Format(e) => write!(f, "{e}"),
        }
    }
}

impl std::error::Error for UpgradeError {}

impl From<io::Error> for UpgradeError {
    fn from(e: io::Error) -> Self {
        Self::Io(e)
    }
}

impl From<Error> for UpgradeError {
    fn from(e: Error) -> Self {
        Self::Format(e)
    }
}

/// Переписывает файл в текущий формат на диске, не меняя в нём ничего другого (текст приводится шагами [`format::upgrade`]), а
/// файл задачи (`task`) — ещё и в текущий порядок ключей ([`crate::files::task::reorder_keys`]). Под той же блокировкой, что и
/// обычная запись, — не потеряет чужую правку. Текст читается как UTF-8 без BOM, поэтому BOM при переписи уходит; окончания строк
/// остаются.
///
/// Возвращает версию, из которой переписан файл; None — файл уже в текущем формате и порядке ключей или его нет.
pub fn upgrade_on_disk(path: &Path, task: bool) -> Result<Option<u32>, UpgradeError> {
    let result: io::Result<Result<Option<u32>, UpgradeError>> = with_lock(path, || {
        let Some(bytes) = read_bytes(path)? else {
            return Ok(Ok(None));
        };
        let text = String::from_utf8_lossy(&bytes);
        let text = tasker_core::io::strip_bom(&text);
        let version = match format::version_of(text, path) {
            Ok(v) => v,
            Err(e) => return Ok(Err(e.into())),
        };
        let mut upgraded = match format::upgrade(text, path) {
            Ok(t) => t,
            Err(e) => return Ok(Err(e.into())),
        };
        if task && let Some(reordered) = crate::files::task::reorder_keys(&upgraded) {
            upgraded = reordered;
        }
        if version == format::CURRENT && upgraded == text {
            return Ok(Ok(None));
        }
        atomic::write_all_bytes(path, upgraded.as_bytes())?;
        Ok(Ok(Some(version)))
    });
    result?
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_support::temp_dir;

    fn tasker_file(ws: &Path, name: &str) -> PathBuf {
        ws.join(".tasker").join("projects").join("p").join("tasks").join(name)
    }

    #[test]
    fn the_write_lock_is_the_cache_file_of_the_enclosing_tasker_folder() {
        let ws = temp_dir();
        let path = tasker_file(&ws, "a-00000001.yaml");
        assert_eq!(
            write_lock_path(&path),
            Some(full_path(&ws).join(".tasker").join(".cache").join("write.lock"))
        );
        assert_eq!(write_lock_path(&ws.join("notes.yaml")), None);
        std::fs::remove_dir_all(&ws).unwrap();
    }

    #[test]
    fn write_if_match_checks_the_version_and_renames_without_overwriting() {
        let ws = temp_dir();
        let path = tasker_file(&ws, "a-00000001.yaml");
        let v1 = write(&path, b"formatVersion: 9\nid: 1\n").unwrap();
        assert_eq!(v1, version_of(b"formatVersion: 9\nid: 1\n"));
        assert!(write_if_match(&path, b"x", "stale").unwrap().is_none());
        let v2 = write_if_match(&path, b"formatVersion: 9\nid: 2\n", &v1).unwrap().unwrap();
        assert_ne!(v1, v2);

        let renamed = tasker_file(&ws, "b-00000001.yaml");
        let v3 = write_if_match_renaming(&path, &renamed, b"formatVersion: 9\nid: 3\n", &v2)
            .unwrap()
            .unwrap();
        assert!(!path.exists() && renamed.exists());
        assert_eq!(current_version(&renamed).unwrap().as_deref(), Some(v3.as_str()));

        // Новое имя занято — ошибка с текстом .NET, оба файла целы.
        std::fs::write(&path, b"other").unwrap();
        let error = write_if_match_renaming(&renamed, &path, b"z", &v3).unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::AlreadyExists);
        assert_eq!(
            error.to_string(),
            "Cannot rename 'b-00000001.yaml' to 'a-00000001.yaml': the file exists"
        );
        assert_eq!(std::fs::read(&path).unwrap(), b"other");
        // То же имя — запись на месте.
        assert!(
            write_if_match_renaming(&renamed, &renamed, b"formatVersion: 9\nid: 4\n", &v3)
                .unwrap()
                .is_some()
        );

        assert!(!rename(&renamed, &path).unwrap());
        let moved = tasker_file(&ws, "c-00000001.yaml");
        assert!(rename(&renamed, &moved).unwrap());
        assert!(!rename(&renamed, &moved).unwrap());

        let version = current_version(&moved).unwrap().unwrap();
        assert!(!delete_if_match(&moved, "stale").unwrap());
        assert!(delete_if_match(&moved, &version).unwrap());
        assert!(!moved.exists());
        assert!(!delete_if_match(&moved, &version).unwrap());
        assert!(ws.join(".tasker").join(".cache").join("write.lock").exists());
        std::fs::remove_dir_all(&ws).unwrap();
    }

    #[test]
    fn upgrade_on_disk_rewrites_only_the_version_line_and_the_task_key_order() {
        let ws = temp_dir();
        let path = tasker_file(&ws, "a-00000001.yaml");
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, b"\xEF\xBB\xBFformatVersion: 3\r\nid: 1\r\ntitle: x\r\n").unwrap();
        assert_eq!(upgrade_on_disk(&path, false).unwrap(), Some(3));
        assert_eq!(std::fs::read(&path).unwrap(), b"formatVersion: 10\r\nid: 1\r\ntitle: x\r\n");
        assert_eq!(upgrade_on_disk(&path, false).unwrap(), None);
        std::fs::write(&path, b"id: 1\n").unwrap();
        assert_eq!(upgrade_on_disk(&path, false).unwrap(), Some(0));
        assert_eq!(std::fs::read(&path).unwrap(), b"formatVersion: 10\nid: 1\n");
        std::fs::write(&path, b"formatVersion: 99\nid: 1\n").unwrap();
        let error = upgrade_on_disk(&path, false).unwrap_err();
        assert_eq!(
            error.to_string(),
            "a-00000001.yaml: format version 99 is newer than this Tasker supports (10): update Tasker"
        );
        assert_eq!(upgrade_on_disk(&tasker_file(&ws, "missing.yaml"), false).unwrap(), None);
        // Файл задачи текущего формата в прежнем порядке ключей: описание уходит в конец.
        std::fs::write(&path, b"formatVersion: 10\r\nid: 1\r\ndescription: d\r\ntitle: x\r\n").unwrap();
        assert_eq!(upgrade_on_disk(&path, false).unwrap(), None);
        assert_eq!(upgrade_on_disk(&path, true).unwrap(), Some(10));
        assert_eq!(
            std::fs::read(&path).unwrap(),
            b"formatVersion: 10\r\nid: 1\r\ntitle: x\r\ndescription: d\r\n"
        );
        assert_eq!(upgrade_on_disk(&path, true).unwrap(), None);
        std::fs::write(&path, b"id: 1\ndescription: d\ntitle: x\n").unwrap();
        assert_eq!(upgrade_on_disk(&path, true).unwrap(), Some(0));
        assert_eq!(
            std::fs::read(&path).unwrap(),
            b"formatVersion: 10\nid: 1\ntitle: x\ndescription: d\n"
        );
        std::fs::remove_dir_all(&ws).unwrap();
    }

    // Индекс, который «только что сверен при открытии» (take_fresh), и запись сверок.
    struct Fresh {
        fresh: Mutex<bool>,
        refreshed: Mutex<Vec<PathBuf>>,
    }

    impl IndexRefresh for Fresh {
        fn refresh(&self, paths: &[PathBuf]) -> io::Result<()> {
            self.refreshed.lock().unwrap().extend_from_slice(paths);
            Ok(())
        }

        fn take_fresh(&self) -> bool {
            std::mem::take(&mut *self.fresh.lock().unwrap())
        }
    }

    impl Fresh {
        fn new() -> Fresh {
            Fresh {
                fresh: Mutex::new(true),
                refreshed: Mutex::new(Vec::new()),
            }
        }

        fn refreshes(&self) -> usize {
            self.refreshed.lock().unwrap().len()
        }
    }

    #[test]
    fn right_after_a_fresh_open_the_first_free_write_scope_does_not_refresh_again() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        let project = Uuid::new_v4();

        // Блокировка свободна: первая секция после открытия не сверяет, следующая — сверяет.
        let index = Fresh::new();
        let scope = WriteScope::new(&dir, &index);
        assert_eq!(scope.exclusive(&project, || Ok(1)).unwrap(), 1);
        assert_eq!(index.refreshes(), 0);
        scope.exclusive(&project, || Ok(())).unwrap();
        assert_eq!(index.refreshes(), 1);

        // Вложенная секция внутри первой — сверяет (блокировку держали до неё).
        let index = Fresh::new();
        let scope = WriteScope::new(&dir, &index);
        scope.exclusive(&project, || scope.exclusive(&project, || Ok(()))).unwrap();
        assert_eq!(*index.refreshed.lock().unwrap(), vec![dir.project(&project).tasks()]);

        // Блокировку держит другой поток (как другой процесс, который пишет), — секция её ждёт и затем сверяет. Пока ждущий не
        // дошёл до блокировки, её могут отпустить раньше — тогда он её не ждал; такой прогон повторяется.
        let mut waited_and_refreshed = false;
        for _ in 0..50 {
            let index = Fresh::new();
            let held = FileLock::acquire(&dir.write_lock(), None).unwrap();
            std::thread::scope(|threads| {
                let waiter = threads.spawn(|| WriteScope::new(&dir, &index).exclusive(&project, || Ok(())).unwrap());
                std::thread::sleep(std::time::Duration::from_millis(30));
                drop(held);
                waiter.join().unwrap();
            });
            assert!(!*index.fresh.lock().unwrap(), "the first section takes the mark either way");
            if index.refreshes() == 1 {
                waited_and_refreshed = true;
                break;
            }
        }
        assert!(waited_and_refreshed);
        std::fs::remove_dir_all(&ws).unwrap();
    }

    #[test]
    fn write_scope_refreshes_the_tasks_folder_and_nests_without_waiting() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        struct Recorder(Mutex<Vec<PathBuf>>);
        impl IndexRefresh for Recorder {
            fn refresh(&self, paths: &[PathBuf]) -> io::Result<()> {
                self.0.lock().unwrap().extend_from_slice(paths);
                Ok(())
            }
        }
        let recorder = Recorder(Mutex::new(Vec::new()));
        let project = Uuid::new_v4();
        let scope = WriteScope::new(&dir, &recorder);
        let value = scope
            .exclusive(&project, || {
                // Внутри — запись файла той же области: берёт ту же блокировку, не ждёт самого себя.
                write(&dir.project(&project).task_file(&Uuid::nil(), Some("x")), b"formatVersion: 10\n")?;
                scope.exclusive(&project, || Ok(7))
            })
            .unwrap();
        assert_eq!(value, 7);
        assert_eq!(
            *recorder.0.lock().unwrap(),
            vec![dir.project(&project).tasks(), dir.project(&project).tasks()]
        );
        std::fs::remove_dir_all(&ws).unwrap();
    }
}
