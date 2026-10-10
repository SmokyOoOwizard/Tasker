//! Индекс области в SQLite — `.tasker/.cache/index-rs.db` (TSK-131, фаза 2 плана `docs/rust-migration-plan.md`, решение (Б)
//! раздела 3.2). Файлы `.tasker` — источник правды: индекс хранит для каждого файла его отпечаток (размер, время изменения) и
//! сущность целиком, а списки, страницы, подсчёты, поиск и `max(number)` серий идут SQL-запросами по нему.
//!
//! Свой файл и своя блокировка: индекс .NET (`index.db`, `index.lock`) не трогаем — у него другая схема и JSON PascalCase; общие
//! с .NET только `write.lock` и сами YAML. Схема (`user_version` = [`SCHEMA_VERSION`]) повторяет таблицы .NET по смыслу —
//! `files`, `task_series`, `task_links`, `task_fields`, `task_field_values`, — а колонка `data` держит модель `tasker-core` в
//! компактном JSON serde (camelCase, `Timestamp` строкой формата `O`). Смена схемы или повреждение файла — индекс удаляется и
//! строится заново: это кэш.
//!
//! Обновление ([`WorkspaceIndex::sync`], [`WorkspaceIndex::refresh`]): обход `.tasker` по [`layout::classify`], сравнение
//! `(size, mtime)` с записанным, перечитывание изменившихся, удаление пропавших — одной транзакцией под короткой блокировкой
//! `.cache/index-rs.lock` ([`FileLock`]). Чтение без блокировки. Файл, который не читается (конфликт слияния git, формат новее
//! поддерживаемого, битый YAML, несовпадение id с именем), остаётся в `files` с текстом ошибки в `error` и не попадает в списки —
//! это проблемы области ([`WorkspaceIndex::problems`]), которые показывает `tasker sync`.
//!
//! Инвариант нумерации серий: `max(number)` считается после `Refresh(tasks/)` под `write.lock` — [`WorkspaceIndex::exclusive`]
//! ([`WriteScope`] с этим индексом как [`IndexRefresh`]).
mod query;
mod sort;
mod sync;

pub use query::{IndexEntity, IndexQuery, LinkEdge, NumberConflict, OwnFieldKind, WorkspaceProblem};
pub use sync::{FileChange, SyncReport, normalize_username};

use crate::layout::{EntityKind, TaskerDirectory};
use crate::write::{IndexRefresh, WriteScope};
use rusqlite::functions::FunctionFlags;
use rusqlite::{Connection, OpenFlags};
use std::io;
use std::path::{Path, PathBuf};
use tasker_core::io::{FileLock, full_path};
use tasker_core::validate::to_lower_invariant;
use uuid::Uuid;

/// Поднять при смене схемы или формата `data`: старый индекс пересоберётся.
pub const SCHEMA_VERSION: i32 = 1;

pub const FILE_NAME: &str = "index-rs.db";
pub const LOCK_NAME: &str = "index-rs.lock";

/// Индекс одной области. Соединения с SQLite не держатся: открываются на время обращения, поэтому, пока никто не работает с
/// кэшем, файл никем не занят, и его могут делить консоль, демон и наблюдатель.
#[derive(Debug, Clone)]
pub struct WorkspaceIndex {
    directory: TaskerDirectory,
}

impl WorkspaceIndex {
    /// Открывает индекс (создаёт схему, пересобирает повреждённый) и сверяет его со всеми файлами области.
    pub fn open(directory: &TaskerDirectory) -> io::Result<WorkspaceIndex> {
        let index = Self::attach(directory)?;
        tasker_core::perf::mark("index-schema");
        index.sync()?;
        tasker_core::perf::mark("index-sync");
        Ok(index)
    }

    /// Открывает индекс без сверки с файлами: схема создаётся, повреждённый файл пересобирается, но строки не обновляются
    /// (для тестов и наблюдателя, который сверяет сам).
    pub fn attach(directory: &TaskerDirectory) -> io::Result<WorkspaceIndex> {
        let index = WorkspaceIndex {
            directory: directory.clone(),
        };
        FileLock::run(&index.lock_file(), None, || index.ensure_schema())??;
        Ok(index)
    }

    pub fn directory(&self) -> &TaskerDirectory {
        &self.directory
    }

    /// `.cache/index-rs.db`.
    pub fn file(&self) -> PathBuf {
        self.directory.cache().join(FILE_NAME)
    }

    /// `.cache/index-rs.lock` — короткая блокировка на время Sync/Refresh.
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
    /// (`max_number`) внутри действия учитывает и файлы, принесённые `git pull`. Вложенный вызов не ждёт самого себя.
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

impl IndexRefresh for WorkspaceIndex {
    fn refresh(&self, paths: &[PathBuf]) -> io::Result<()> {
        WorkspaceIndex::refresh(self, paths).map(|_| ())
    }
}

pub(crate) fn db_error(e: rusqlite::Error) -> io::Error {
    io::Error::other(format!("Index database: {e}"))
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
    connection
        .execute_batch(&format!(
            "
            PRAGMA journal_mode = WAL;
            DROP TABLE IF EXISTS task_series;
            DROP TABLE IF EXISTS task_links;
            DROP TABLE IF EXISTS task_fields;
            DROP TABLE IF EXISTS task_field_values;
            DROP TABLE IF EXISTS files;
            CREATE TABLE files (
                path         TEXT PRIMARY KEY,   -- относительно .tasker, через «/»
                size         INTEGER NOT NULL,
                mtime        INTEGER NOT NULL,   -- наносекунды от эпохи Unix
                kind         TEXT NOT NULL,      -- Project, User, Status, StatusSet, TaskType, Board, Task, Series, LinkType, Field, FieldEnum
                project_id   TEXT,
                id           TEXT NOT NULL,      -- id сущности (D); у нечитаемого файла — из имени или само имя
                sort_text    TEXT,               -- имя (у пользователя — нормализованное; у задачи — заголовок в нижнем регистре; у серии — префикс)
                sort_num     INTEGER,            -- у задачи — createdAt, тики Unix
                sort_updated INTEGER,            -- у задачи — updatedAt, тики Unix
                type_id      TEXT,
                status_id    TEXT,
                user_kind    TEXT,               -- Human, Agent
                data         TEXT,               -- сущность целиком, JSON (serde, camelCase); null, если файл не прочитан
                error        TEXT                -- почему файл не прочитан; такие файлы не попадают в списки
            );
            CREATE INDEX ix_files_by_name ON files (kind, project_id, sort_text, id);
            CREATE INDEX ix_files_by_created ON files (kind, project_id, sort_num, id);
            CREATE INDEX ix_files_by_updated ON files (kind, project_id, sort_updated, sort_num, id);
            CREATE INDEX ix_files_by_status ON files (kind, project_id, status_id);
            -- номера задач в сериях; строки файла заменяются и удаляются вместе с ним, в той же транзакции
            CREATE TABLE task_series (
                path          TEXT NOT NULL,
                project_id    TEXT NOT NULL,
                series_id     TEXT NOT NULL,
                number        INTEGER NOT NULL,
                task_id       TEXT NOT NULL,
                created_ticks INTEGER NOT NULL
            );
            CREATE INDEX ix_task_series_by_number ON task_series (project_id, series_id, number);
            CREATE INDEX ix_task_series_by_path ON task_series (path);
            -- исходящие связи задач
            CREATE TABLE task_links (
                path       TEXT NOT NULL,
                project_id TEXT NOT NULL,
                type_id    TEXT NOT NULL,
                source_id  TEXT NOT NULL,
                target_id  TEXT NOT NULL
            );
            CREATE INDEX ix_task_links_by_target ON task_links (project_id, target_id);
            CREATE INDEX ix_task_links_by_source ON task_links (project_id, type_id, source_id);
            CREATE INDEX ix_task_links_by_path ON task_links (path);
            -- поля, записанные в задаче (значения и дополнительные)
            CREATE TABLE task_fields (
                path       TEXT NOT NULL,
                project_id TEXT NOT NULL,
                field_id   TEXT NOT NULL,         -- поле каталога или собственное поле задачи
                enum_id    TEXT,                  -- перечисление собственного поля; у поля каталога null
                own_name   TEXT,                  -- собственное поле: имя в нижнем регистре; у поля каталога null
                own_type   INTEGER                -- собственное поле: тип (FieldType: string 0, int 1, float 2, bool 3, date 4, enum 5)
            );
            CREATE INDEX ix_task_fields_by_field ON task_fields (project_id, field_id);
            CREATE INDEX ix_task_fields_by_enum ON task_fields (project_id, enum_id);
            CREATE INDEX ix_task_fields_by_own_name ON task_fields (own_name);
            CREATE INDEX ix_task_fields_by_path ON task_fields (path);
            -- значения полей задач (канонический текст)
            CREATE TABLE task_field_values (
                path       TEXT NOT NULL,
                project_id TEXT NOT NULL,
                field_id   TEXT NOT NULL,
                value      TEXT NOT NULL,
                number     REAL,                  -- значение числом; null, если оно не число. Сравнения int и float идут по нему
                own_name   TEXT,
                own_type   INTEGER
            );
            CREATE INDEX ix_task_field_values_by_value ON task_field_values (field_id, value);
            CREATE INDEX ix_task_field_values_by_number ON task_field_values (field_id, number);
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
    fn relative_paths_are_slash_separated_and_outside_is_none() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        let index = WorkspaceIndex { directory: dir.clone() };
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
