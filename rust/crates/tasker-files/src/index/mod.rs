//! Индекс области в SQLite — `.tasker/.cache/index.db`, общий с .NET-сборкой (TSK-161; до него у Rust был
//! свой `index-rs.db`, TSK-131). Файлы `.tasker` — источник правды: индекс хранит
//! для каждого файла его отпечаток (размер, время изменения) и сущность целиком, а списки, страницы, подсчёты, поиск и
//! `max(number)` серий идут SQL-запросами по нему.
//!
//! Один файл на все процессы — десктоп и сервер .NET, консоль и демон Rust, — поэтому всё как у `WorkspaceIndex` .NET:
//! схема буква в букву (`user_version` = [`SCHEMA_VERSION`], те же таблицы, колонки, индексы и даже комментарии в CREATE), колонка
//! `data` — JSON System.Text.Json байт в байт ([`data`]: PascalCase, enum числами), `size`/`mtime` и время задач — в тиках .NET
//! (100 нс от 0001-01-01; иначе процессы перечитывали бы файлы друг друга без конца), блокировка `.cache/index.lock` тем же
//! способом ([`FileLock`]: `flock` на Unix, `FileShare.None` на Windows), журнал WAL. Чужая версия схемы — таблицы пересоздаются,
//! повреждённый файл удаляется и строится заново (как в .NET): это кэш. Старый `index-rs.db` (с `-wal`, `-shm`) и `index-rs.lock`
//! прежних Rust-сборок удаляются при открытии области.
//!
//! Обновление ([`WorkspaceIndex::sync`], [`WorkspaceIndex::refresh`]): обход `.tasker` по [`layout::classify`] (отпечаток каждого
//! файла — пачками `getattrlistbulk` на macOS, `fstatat` от дескриптора папки на остальных Unix: модуль `scan`), сравнение
//! `(size, mtime)` с записанным, перечитывание изменившихся, удаление пропавших — одной транзакцией под короткой блокировкой
//! `index.lock`. Чтение без блокировки: в режиме WAL читатель видит согласованный снимок и не мешает записи. Файл, который не
//! читается (конфликт слияния git, формат новее поддерживаемого, битый YAML, несовпадение id с именем), остаётся в `files` с
//! текстом ошибки в `error` и не попадает в списки — это проблемы области ([`WorkspaceIndex::problems`]), которые показывает
//! `tasker sync`.
//!
//! Инвариант нумерации серий: `max(number)` считается под `write.lock` по индексу, сверенному с папкой задач, — [`WorkspaceIndex::exclusive`]
//! ([`WriteScope`] с этим индексом как [`IndexRefresh`]): `Refresh(tasks/)` под блокировкой или, если блокировка досталась сразу
//! после полной сверки при открытии, — она ([`IndexRefresh::take_fresh`]).
pub mod data;
mod query;
mod scan;
mod sort;
mod sync;

pub use data::IndexEntity;
pub use query::{IndexQuery, LinkEdge, NumberConflict, OwnFieldKind, WorkspaceProblem};
pub use sync::{FileChange, SyncReport, normalize_username};

use crate::layout::{EntityKind, TaskerDirectory};
use crate::write::{IndexRefresh, WriteScope};
use rusqlite::functions::FunctionFlags;
use rusqlite::{Connection, OpenFlags};
use std::io;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use tasker_core::io::{FileLock, full_path};
use tasker_core::validate::to_lower_invariant;
use uuid::Uuid;

/// Версия схемы `WorkspaceIndex.SchemaVersion` в .NET: поднимается там и здесь вместе — при смене схемы или формата `data` старый
/// индекс пересоберётся.
pub const SCHEMA_VERSION: i32 = 10;

pub const FILE_NAME: &str = "index.db";
pub const LOCK_NAME: &str = "index.lock";

/// Свой индекс прежних Rust-сборок (TSK-131): при открытии области удаляется, чтобы не копился.
const LEGACY_FILE_NAME: &str = "index-rs.db";
const LEGACY_LOCK_NAME: &str = "index-rs.lock";

/// Индекс одной области. Соединения с SQLite не держатся: открываются на время обращения, поэтому, пока никто не работает с
/// кэшем, файл никем не занят, и его могут делить консоль, демон и наблюдатель.
#[derive(Debug, Clone)]
pub struct WorkspaceIndex {
    directory: TaskerDirectory,
    /// Когда закончилась полная сверка при открытии, если её ещё не учла секция записи ([`IndexRefresh::take_fresh`]); общее у копий.
    fresh: Arc<Mutex<Option<Instant>>>,
}

/// Сколько после сверки при открытии секция записи может её не повторять: консоль доходит до записи за миллисекунды, а долго
/// живущий процесс (демон) пишет позже — тогда сверка под блокировкой, как обычно.
const FRESH_FOR: Duration = Duration::from_secs(1);

impl WorkspaceIndex {
    /// Открывает индекс (создаёт схему, пересобирает повреждённый) и сверяет его со всеми файлами области.
    pub fn open(directory: &TaskerDirectory) -> io::Result<WorkspaceIndex> {
        let index = Self::attach(directory)?;
        tasker_core::perf::mark("index-schema");
        index.sync()?;
        *index.fresh.lock().unwrap_or_else(|e| e.into_inner()) = Some(Instant::now());
        tasker_core::perf::mark("index-sync");
        Ok(index)
    }

    /// Открывает индекс без сверки с файлами: схема создаётся, повреждённый файл пересобирается, но строки не обновляются
    /// (для тестов и наблюдателя, который сверяет сам).
    pub fn attach(directory: &TaskerDirectory) -> io::Result<WorkspaceIndex> {
        let index = WorkspaceIndex {
            directory: directory.clone(),
            fresh: Arc::default(),
        };
        FileLock::run(&index.lock_file(), None, || index.ensure_schema())??;
        Ok(index)
    }

    pub fn directory(&self) -> &TaskerDirectory {
        &self.directory
    }

    /// `.cache/index.db`.
    pub fn file(&self) -> PathBuf {
        self.directory.cache().join(FILE_NAME)
    }

    /// `.cache/index.lock` — короткая блокировка на время Sync/Refresh (общая с .NET).
    pub fn lock_file(&self) -> PathBuf {
        self.directory.cache().join(LOCK_NAME)
    }

    /// Полная сверка со всеми файлами области.
    pub fn sync(&self) -> io::Result<SyncReport> {
        self.sync_paths(&[self.directory.root().to_path_buf()])
    }

    /// Перечитывает файлы по путям (файл или папка целиком — например, удалённый проект). Хранилища зовут после каждой своей
    /// записи, наблюдатель — по событиям файловой системы. Путь вне `.tasker` — сверяется вся область.
    pub fn refresh(&self, paths: &[PathBuf]) -> io::Result<SyncReport> {
        self.sync_paths(paths)
    }

    /// Выполняет запись и затем обновляет её файлы в индексе — даже если запись не прошла (версия не совпала): значит, файл
    /// изменили снаружи, и индекс заодно догонит (`WorkspaceIndex.Written` в .NET).
    pub fn written<T>(&self, paths: &[PathBuf], write: impl FnOnce() -> io::Result<T>) -> io::Result<T> {
        let result = write();
        let refreshed = self.refresh(paths);
        let value = result?;
        refreshed?;
        Ok(value)
    }

    /// Блокировка записи между процессами (`write.lock`) плюс сверка папки задач проекта перед действием: номер серии
    /// (`max_number`) внутри действия учитывает и файлы, принесённые `git pull`. Первая секция вскоре после открытия, которой
    /// блокировка досталась сразу, папку не сверяет — её только что сверило открытие ([`IndexRefresh::take_fresh`]). Вложенный
    /// вызов не ждёт самого себя.
    pub fn exclusive<T>(&self, project_id: &Uuid, action: impl FnOnce() -> io::Result<T>) -> io::Result<T> {
        WriteScope::new(&self.directory, self).exclusive(project_id, action)
    }

    fn sync_paths(&self, paths: &[PathBuf]) -> io::Result<SyncReport> {
        let started = tasker_core::perf::start();
        let result = FileLock::run(&self.lock_file(), None, || {
            tasker_core::perf::count("index-lock", started);
            let scopes = self.scopes(paths);
            let mut connection = self.connect()?;
            sync::run(&mut connection, &self.directory, &scopes)
        })?;
        tasker_core::perf::count("index-sync", started);
        result
    }

    // Пути относительно .tasker; путь снаружи (например, через symlink) — не угадываем, сверяем всё.
    fn scopes(&self, paths: &[PathBuf]) -> Vec<String> {
        let mut scopes: Vec<String> = Vec::new();
        for path in paths {
            let scope = match self.relative(path) {
                Some(scope) => scope,
                None => return vec![String::new()],
            };
            if !scopes.contains(&scope) {
                scopes.push(scope);
            }
        }
        scopes
    }

    /// Путь относительно `.tasker` через `/`; сам `.tasker` — пустая строка; None — путь вне `.tasker`.
    pub(crate) fn relative(&self, path: &Path) -> Option<String> {
        let full = full_path(path);
        let rest = full.strip_prefix(self.directory.root()).ok()?;
        Some(
            rest.components()
                .map(|c| c.as_os_str().to_string_lossy().into_owned())
                .collect::<Vec<_>>()
                .join("/"),
        )
    }

    /// Полный путь по относительному из `files.path`.
    pub(crate) fn absolute(&self, relative: &str) -> PathBuf {
        let mut path = self.directory.root().to_path_buf();
        for part in relative.split('/').filter(|p| !p.is_empty()) {
            path.push(part);
        }
        path
    }

    /// Соединение на одно обращение: `lower()` — как `ToLowerInvariant` (встроенная знает только ASCII), чтобы порядок по тексту без
    /// учёта регистра был тем же, что у ключей индекса.
    pub(crate) fn connect(&self) -> io::Result<Connection> {
        let connection = Connection::open_with_flags(
            self.file(),
            OpenFlags::SQLITE_OPEN_READ_WRITE | OpenFlags::SQLITE_OPEN_CREATE | OpenFlags::SQLITE_OPEN_NO_MUTEX,
        )
        .map_err(db_error)?;
        connection.busy_timeout(std::time::Duration::from_secs(30)).map_err(db_error)?;
        connection
            .create_scalar_function(
                "lower",
                1,
                FunctionFlags::SQLITE_UTF8 | FunctionFlags::SQLITE_DETERMINISTIC,
                |ctx| {
                    let text: Option<String> = ctx.get(0)?;
                    Ok(text.map(|t| to_lower_invariant(&t)))
                },
            )
            .map_err(db_error)?;
        Ok(connection)
    }

    // Схема: при чужой версии таблицы пересоздаются; повреждённый или чужой файл удаляется вместе с -wal и -shm и создаётся заново.
    fn ensure_schema(&self) -> io::Result<()> {
        std::fs::create_dir_all(self.directory.cache())?;
        self.remove_legacy();
        match self.connect().and_then(|c| create_schema(&c)) {
            Ok(()) => Ok(()),
            Err(_) => {
                let file = self.file();
                for suffix in ["", "-wal", "-shm"] {
                    let path = PathBuf::from(format!("{}{suffix}", file.display()));
                    if path.exists() {
                        tasker_core::io::delete(&path)?;
                    }
                }
                create_schema(&self.connect()?)
            }
        }
    }
}

impl WorkspaceIndex {
    // Индекс прежних Rust-сборок: файл занят (Windows, старая сборка ещё работает) — не беда, удалим в следующий раз.
    fn remove_legacy(&self) {
        let cache = self.directory.cache();
        for name in [
            LEGACY_FILE_NAME.to_string(),
            format!("{LEGACY_FILE_NAME}-wal"),
            format!("{LEGACY_FILE_NAME}-shm"),
            LEGACY_LOCK_NAME.to_string(),
        ] {
            let path = cache.join(name);
            if path.exists() {
                let _ = std::fs::remove_file(&path);
            }
        }
    }
}

impl IndexRefresh for WorkspaceIndex {
    fn refresh(&self, paths: &[PathBuf]) -> io::Result<()> {
        WorkspaceIndex::refresh(self, paths).map(|_| ())
    }

    fn take_fresh(&self) -> bool {
        self.fresh
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take()
            .is_some_and(|synced| synced.elapsed() < FRESH_FOR)
    }
}

pub(crate) fn db_error(e: rusqlite::Error) -> io::Error {
    let busy = e.sqlite_error_code() == Some(rusqlite::ErrorCode::DatabaseBusy);
    io::Error::other(DbError {
        message: format!("Index database: {e}"),
        busy,
    })
}

/// Ошибка SQLite с текстом `Index database: …`; `busy` — база занята другим соединением (`SQLITE_BUSY`).
#[derive(Debug)]
struct DbError {
    message: String,
    busy: bool,
}

impl std::fmt::Display for DbError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for DbError {}

/// Ошибка от [`db_error`] — «база занята» (`SQLITE_BUSY`).
pub(crate) fn is_busy(e: &io::Error) -> bool {
    e.get_ref()
        .and_then(|inner| inner.downcast_ref::<DbError>())
        .is_some_and(|db| db.busy)
}

/// Имя вида в колонке `kind` (`IndexKind.ToString()`).
pub(crate) fn kind_name(kind: EntityKind) -> &'static str {
    match kind {
        EntityKind::Project => "Project",
        EntityKind::User => "User",
        EntityKind::Status => "Status",
        EntityKind::StatusSet => "StatusSet",
        EntityKind::TaskType => "TaskType",
        EntityKind::Board => "Board",
        EntityKind::Task => "Task",
        EntityKind::Series => "Series",
        EntityKind::LinkType => "LinkType",
        EntityKind::Field => "Field",
        EntityKind::FieldEnum => "FieldEnum",
    }
}

fn create_schema(connection: &Connection) -> io::Result<()> {
    let version: i32 = connection
        .query_row("PRAGMA user_version", [], |row| row.get(0))
        .map_err(db_error)?;
    if version == SCHEMA_VERSION {
        // Файл есть, версия своя — но убедимся, что он читается (повреждённый файл даёт ошибку уже здесь).
        connection
            .query_row("SELECT count(*) FROM files", [], |row| row.get::<_, i64>(0))
            .map_err(db_error)?;
        return Ok(());
    }
    // Текст — буква в букву из `WorkspaceIndex.CreateSchema` (.NET): SQLite хранит CREATE с комментариями, и `.schema` обеих сборок
    // совпадает (эталон — tests/golden/index/schema.sql).
    connection
        .execute_batch(&format!(
            "\
PRAGMA journal_mode = WAL;
DROP TABLE IF EXISTS task_series;
DROP TABLE IF EXISTS task_links;
DROP TABLE IF EXISTS task_fields;
DROP TABLE IF EXISTS task_field_values;
DROP TABLE IF EXISTS files;
CREATE TABLE files (
    path       TEXT PRIMARY KEY,   -- относительно .tasker, через «/»
    size       INTEGER NOT NULL,
    mtime      INTEGER NOT NULL,   -- ticks UTC
    kind       TEXT NOT NULL,
    project_id TEXT,
    id         TEXT NOT NULL,
    sort_text  TEXT,               -- имя (у пользователя — нормализованное; у задачи — заголовок в нижнем регистре, для упорядочивания)
    sort_num   INTEGER,            -- у задачи — createdAt, ticks UTC
    sort_updated INTEGER,          -- у задачи — updatedAt, ticks UTC
    type_id    TEXT,
    status_id  TEXT,
    user_kind  TEXT,
    data       TEXT,               -- сущность целиком, JSON; null, если файл не прочитан
    error      TEXT                -- почему файл не прочитан; такие файлы не попадают в списки
);
CREATE INDEX ix_files_by_name ON files (kind, project_id, sort_text, id);
CREATE INDEX ix_files_by_created ON files (kind, project_id, sort_num, id);
CREATE INDEX ix_files_by_updated ON files (kind, project_id, sort_updated, sort_num, id);
CREATE INDEX ix_files_by_status ON files (kind, project_id, status_id);
-- номера задач в сериях; строки файла заменяются и удаляются вместе с ним, в той же транзакции
CREATE TABLE task_series (
    path          TEXT NOT NULL,      -- файл задачи, как в files
    project_id    TEXT NOT NULL,
    series_id     TEXT NOT NULL,
    number        INTEGER NOT NULL,
    task_id       TEXT NOT NULL,
    created_ticks INTEGER NOT NULL    -- createdAt задачи, ticks UTC
);
CREATE INDEX ix_task_series_by_number ON task_series (project_id, series_id, number);
CREATE INDEX ix_task_series_by_path ON task_series (path);
-- исходящие связи задач; строки файла заменяются и удаляются вместе с ним, в той же транзакции
CREATE TABLE task_links (
    path       TEXT NOT NULL,         -- файл задачи-источника, как в files
    project_id TEXT NOT NULL,
    type_id    TEXT NOT NULL,         -- тип связи
    source_id  TEXT NOT NULL,
    target_id  TEXT NOT NULL
);
CREATE INDEX ix_task_links_by_target ON task_links (project_id, target_id);
CREATE INDEX ix_task_links_by_source ON task_links (project_id, type_id, source_id);
CREATE INDEX ix_task_links_by_path ON task_links (path);
-- поля, записанные в задаче (значения и дополнительные); строки файла заменяются и удаляются вместе с ним, в той же транзакции
CREATE TABLE task_fields (
    path       TEXT NOT NULL,         -- файл задачи, как в files
    project_id TEXT NOT NULL,
    field_id   TEXT NOT NULL,         -- поле каталога или собственное поле задачи
    enum_id    TEXT,                  -- перечисление собственного поля; у поля каталога null
    own_name   TEXT,                  -- собственное поле: имя в нижнем регистре (FieldNames.Key); у поля каталога null
    own_type   INTEGER                -- собственное поле: тип (FieldType); у поля каталога null
);
CREATE INDEX ix_task_fields_by_field ON task_fields (project_id, field_id);
CREATE INDEX ix_task_fields_by_enum ON task_fields (project_id, enum_id);
CREATE INDEX ix_task_fields_by_own_name ON task_fields (own_name);
CREATE INDEX ix_task_fields_by_path ON task_fields (path);
-- значения полей задач (канонический текст); строки файла заменяются и удаляются вместе с ним, в той же транзакции
CREATE TABLE task_field_values (
    path       TEXT NOT NULL,         -- файл задачи, как в files
    project_id TEXT NOT NULL,
    field_id   TEXT NOT NULL,
    value      TEXT NOT NULL,
    number     REAL,                  -- значение числом (FieldNumbers); null, если оно не число. Сравнения int и float идут по нему
    own_name   TEXT,                  -- собственное поле: имя в нижнем регистре (FieldNames.Key); у поля каталога null
    own_type   INTEGER                -- собственное поле: тип (FieldType); у поля каталога null
);
-- id поля — Guid, общий для проекта и без повторов между проектами, поэтому проект в индексы не входит
CREATE INDEX ix_task_field_values_by_value ON task_field_values (field_id, value);
CREATE INDEX ix_task_field_values_by_number ON task_field_values (field_id, number);
-- собственные поля у каждой задачи со своим id, поэтому ищутся по имени (и типу, он проверяется по строке)
CREATE INDEX ix_task_field_values_by_own_value ON task_field_values (own_name, value);
CREATE INDEX ix_task_field_values_by_own_number ON task_field_values (own_name, number);
CREATE INDEX ix_task_field_values_by_path ON task_field_values (path);
PRAGMA user_version = {SCHEMA_VERSION};
"
        ))
        .map_err(db_error)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::test_support::temp_dir;

    #[test]
    fn only_a_recent_open_marks_the_index_fresh_and_only_once() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        dir.ensure_created().unwrap();
        let index = WorkspaceIndex::open(&dir).unwrap();
        let copy = index.clone();
        assert!(copy.take_fresh(), "copies share the mark");
        assert!(!index.take_fresh());

        assert!(!WorkspaceIndex::attach(&dir).unwrap().take_fresh(), "attach does not sync");
        index.sync().unwrap();
        assert!(!index.take_fresh(), "only the sync of open marks the index");

        let index = WorkspaceIndex::open(&dir).unwrap();
        *index.fresh.lock().unwrap() = Instant::now().checked_sub(FRESH_FOR);
        assert!(
            !index.take_fresh(),
            "a long-lived process writes later: the write section syncs again"
        );
        std::fs::remove_dir_all(&ws).unwrap();
    }

    fn copy_dir(from: &Path, to: &Path) {
        std::fs::create_dir_all(to).unwrap();
        for entry in std::fs::read_dir(from).unwrap() {
            let entry = entry.unwrap();
            if entry.file_type().unwrap().is_dir() {
                copy_dir(&entry.path(), &to.join(entry.file_name()));
            } else {
                std::fs::copy(entry.path(), to.join(entry.file_name())).unwrap();
            }
        }
    }

    #[test]
    fn the_first_free_write_section_after_open_knows_the_numbers_written_by_other_sections() {
        use tasker_core::model::{TaskItem, TaskSeriesNumber};
        let ws = temp_dir();
        copy_dir(&Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../tests/golden/workspace"), &ws);
        let dir = TaskerDirectory::new(&ws);
        let index = WorkspaceIndex::open(&dir).unwrap();
        let task = index
            .all::<TaskItem>(&IndexQuery::default())
            .unwrap()
            .into_iter()
            .find(|t| !t.series_numbers.is_empty())
            .unwrap();
        let (project, series) = (task.project_id, task.series_numbers[0].series_id);
        let max = index.max_number(&project, &series).unwrap();
        let numbered = |number: i32, title: &str| {
            let item = TaskItem {
                id: Uuid::new_v4(),
                title: title.to_string(),
                series_numbers: vec![TaskSeriesNumber { series_id: series, number }],
                ..task.clone()
            };
            let path = dir.project(&project).task_file(&item.id, Some(title));
            (path, crate::files::task::serialize(&item))
        };

        // Секция записи другого процесса (свой индекс, без отметки открытия): номер выбран и файл записан под write.lock, общий
        // индекс обновлён до того, как блокировка отпущена.
        let other = WorkspaceIndex::attach(&dir).unwrap();
        let (path, bytes) = numbered(max + 1, "From another process");
        other
            .exclusive(&project, || {
                other.written(std::slice::from_ref(&path), || std::fs::write(&path, &bytes))
            })
            .unwrap();

        // Сверка при открытии «только что», блокировка свободна: первая секция папку не сверяет, но номер другой секции знает.
        *index.fresh.lock().unwrap() = Some(Instant::now());
        assert_eq!(index.exclusive(&project, || index.max_number(&project, &series)).unwrap(), max + 1);

        // Файл, который появился мимо Tasker (git pull), видит следующая секция: она сверяет папку задач под блокировкой.
        let (path, bytes) = numbered(max + 2, "Pulled");
        std::fs::write(&path, bytes).unwrap();
        assert_eq!(index.exclusive(&project, || index.max_number(&project, &series)).unwrap(), max + 2);
        std::fs::remove_dir_all(&ws).unwrap();
    }

    #[test]
    fn relative_paths_are_slash_separated_and_outside_is_none() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        let index = WorkspaceIndex {
            directory: dir.clone(),
            fresh: Arc::default(),
        };
        assert_eq!(index.relative(dir.root()).as_deref(), Some(""));
        assert_eq!(
            index.relative(&dir.project(&Uuid::nil()).tasks()).as_deref(),
            Some("projects/00000000-0000-0000-0000-000000000000/tasks")
        );
        assert_eq!(index.relative(&ws.join("notes.yaml")), None);
        assert_eq!(index.scopes(&[ws.join("x"), dir.users()]), vec![String::new()]);
        assert_eq!(index.scopes(&[dir.users(), dir.users()]), vec!["users".to_string()]);
        assert_eq!(index.absolute("users/a.yaml"), dir.root().join("users").join("a.yaml"));
        std::fs::remove_dir_all(&ws).unwrap();
    }
}
