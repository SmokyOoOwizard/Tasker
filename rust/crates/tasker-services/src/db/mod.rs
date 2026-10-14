//! Хранилище области в файле SQLite (TSK-159): та же схема и те же форматы значений, что у `Tasker.Storage.Db` (.NET, EF Core),
//! — один файл одновременно открывают .NET-десктоп, .NET-сервер, Rust-консоль и Rust-демон.
//!
//! Схема — ровно та, что создаёт `EnsureCreated` (`schema.sql` снят с файла, созданного .NET; эталон `.schema` —
//! `rust/tests/golden/sqlite/schema.sql`). Своих таблиц нет, схему не меняем, миграций нет (TSK-1): как `DbInitializer`, только
//! создаём схему в пустом файле и дописываем два столбца `link_types`, если их нет.
//!
//! Значения — как их пишет EF Core SQLite: `Guid` — TEXT в форме D **заглавными** (`'1C5289E1-1AF3-…'`), `DateTime` (UTC) — TEXT
//! `yyyy-MM-dd HH:mm:ss.FFFFFFF` (хвостовые нули дроби отброшены, без смещения), `bool` — INTEGER 0/1, перечисления — числом
//! (`FieldType`), вид пользователя — строкой (`Human`/`Agent`), оператор условия колонки — именем (`GreaterOrEqual`). Версия
//! сущности — счётчик (`DbVersion`): 1 при создании, +1 при каждом изменении; наружу — строкой.
//!
//! Соединения — как у Microsoft.Data.Sqlite под EF Core: `foreign_keys = 1` (каскады удаления делает БД), ожидание занятой базы
//! до 30 с, `lower()` подменена на `ToLowerInvariant` (`SqliteUnicodeLower`), журнал — тот, что записан в файле (новый файл
//! создаётся в WAL, как у EF). Запись из нескольких команд — в транзакции `BEGIN IMMEDIATE`; секция записи проекта
//! ([`Storage::exclusive`]) — одна транзакция на всё действие, при нарушении уникальности (гонка за номер серии) или занятой базе
//! откатывается и повторяется целиком, до 5 раз (`WriteScope`). Соединения не держат блокировок между обращениями: берутся из
//! пула на время вызова; внутри секции записи все обращения потока идут через её соединение.
mod entities;
mod tasks;
mod values;

pub use entities::DbEntity;

use crate::error::{Error, Result};
use crate::storage::{Entity, Storage};
use rusqlite::functions::FunctionFlags;
use rusqlite::{Connection, OpenFlags, OptionalExtension, params};
use std::cell::RefCell;
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tasker_core::Timestamp;
use tasker_core::locks::{EditHolder, EditLock, LockedEntity};
use tasker_core::model::{Project, TaskItem, User, UserKind};
use tasker_core::tasks::{ListPage, Page, TaskFilter};
use tasker_core::validate::to_lower_invariant;
use tasker_files::index::{LinkEdge, NumberConflict, OwnFieldKind};
use uuid::Uuid;
use values::{db_time, g, guid_col, parse_version, time_col};

/// DDL схемы в том виде, в каком её хранит `sqlite_master` после `EnsureCreated` (порядок таблиц и индексов — как у EF).
pub const SCHEMA: &str = include_str!("schema.sql");

/// Сколько раз секция записи повторяет транзакцию (`WriteScope.MaxAttempts`).
const MAX_ATTEMPTS: u32 = 5;

/// Ожидание занятой базы (`SqliteCommand.CommandTimeout` по умолчанию — 30 с).
const BUSY_TIMEOUT: Duration = Duration::from_secs(30);

/// Файл SQLite области. Клоны делят пул соединений.
#[derive(Clone)]
pub struct DbStore {
    inner: Arc<Inner>,
}

struct Inner {
    file: PathBuf,
    /// Ключ секции записи потока ([`ACTIVE`]).
    id: u64,
    pool: Mutex<Vec<Connection>>,
}

impl std::fmt::Debug for DbStore {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DbStore").field("file", &self.inner.file).finish()
    }
}

thread_local! {
    /// Соединения открытых в этом потоке секций записи: (id хранилища, соединение с начатой транзакцией).
    static ACTIVE: RefCell<Vec<(u64, Connection)>> = const { RefCell::new(Vec::new()) };
}

static NEXT_ID: AtomicU64 = AtomicU64::new(1);

/// Ошибка SQLite: текст как у Microsoft.Data.Sqlite (`SQLite Error 19: 'UNIQUE constraint failed: …'.`), код и расширенный код.
#[derive(Debug)]
pub struct DbError {
    message: String,
    code: Option<rusqlite::ErrorCode>,
    extended: i32,
}

impl std::fmt::Display for DbError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for DbError {}

impl DbError {
    /// Нарушение уникального индекса или первичного ключа (`DbErrors.IsUniqueViolation`: 2067 и 1555).
    pub fn is_unique_violation(&self) -> bool {
        matches!(self.extended, 2067 | 1555)
    }

    /// База занята другим писателем (5) или таблица заблокирована (6).
    pub fn is_busy(&self) -> bool {
        matches!(
            self.code,
            Some(rusqlite::ErrorCode::DatabaseBusy | rusqlite::ErrorCode::DatabaseLocked)
        )
    }
}

pub(crate) fn db_error(e: rusqlite::Error) -> Error {
    let (code, extended) = match &e {
        rusqlite::Error::SqliteFailure(failure, _) => (Some(failure.code), failure.extended_code),
        _ => (None, 0),
    };
    let message = match &e {
        rusqlite::Error::SqliteFailure(failure, Some(text)) => format!("SQLite Error {}: '{text}'.", failure.extended_code & 0xff),
        rusqlite::Error::SqliteFailure(failure, None) => format!("SQLite Error {}: '{failure}'.", failure.extended_code & 0xff),
        other => format!("SQLite: {other}"),
    };
    Error::Io(std::io::Error::other(DbError { message, code, extended }))
}

/// Ошибка SQLite внутри ошибки сервиса; None — ошибка другого рода.
pub fn db_error_of(e: &Error) -> Option<&DbError> {
    match e {
        Error::Io(io) => io.get_ref().and_then(|inner| inner.downcast_ref::<DbError>()),
        _ => None,
    }
}

fn is_retryable(e: &Error) -> bool {
    db_error_of(e).is_some_and(|db| db.is_unique_violation() || db.is_busy())
}

fn connect(file: &Path, create: bool) -> rusqlite::Result<Connection> {
    let mut flags = OpenFlags::SQLITE_OPEN_READ_WRITE | OpenFlags::SQLITE_OPEN_NO_MUTEX;
    if create {
        flags |= OpenFlags::SQLITE_OPEN_CREATE;
    }
    let connection = Connection::open_with_flags(file, flags)?;
    connection.busy_timeout(BUSY_TIMEOUT)?;
    // Как e_sqlite3 у Microsoft.Data.Sqlite (SQLITE_DQS=0): имя в двойных кавычках — только идентификатор, не строка. Без этого
    // проверка `SELECT "AllowCycles" …` у старой БД «прошла» бы на строковом литерале.
    connection.set_db_config(rusqlite::config::DbConfig::SQLITE_DBCONFIG_DQS_DML, false)?;
    connection.set_db_config(rusqlite::config::DbConfig::SQLITE_DBCONFIG_DQS_DDL, false)?;
    // EF Core включает внешние ключи на каждом соединении: каскадные удаления и запрет удалить то, на что ссылаются, делает БД.
    connection.execute_batch("PRAGMA foreign_keys = 1;")?;
    connection.create_scalar_function(
        "lower",
        1,
        FunctionFlags::SQLITE_UTF8 | FunctionFlags::SQLITE_DETERMINISTIC,
        |ctx| {
            let text: Option<String> = ctx.get(0)?;
            Ok(text.map(|t| to_lower_invariant(&t)))
        },
    )?;
    Ok(connection)
}

impl DbStore {
    /// Открывает файл, создавая его со схемой, если его нет (`DbInitializer.Start`): гонку двух создающих процессов ловит
    /// транзакция, проигравший повторяет (до 5 раз).
    pub fn open(file: &Path) -> std::io::Result<DbStore> {
        let file = tasker_core::io::full_path(file);
        let store = DbStore {
            inner: Arc::new(Inner {
                file: file.clone(),
                id: NEXT_ID.fetch_add(1, Ordering::Relaxed),
                pool: Mutex::new(Vec::new()),
            }),
        };
        let mut attempt = 1;
        loop {
            match store.initialize() {
                Ok(()) => return Ok(store),
                Err(e) if attempt < 5 && db_error_of(&e).is_some() => {
                    std::thread::sleep(Duration::from_millis(100 * attempt as u64));
                    attempt += 1;
                }
                Err(Error::Io(e)) => return Err(e),
                Err(e) => return Err(std::io::Error::other(e.to_string())),
            }
        }
    }

    /// Открывает существующий файл (автодополнение); файла нет — ошибка.
    pub fn attach(file: &Path) -> std::io::Result<DbStore> {
        if !file.is_file() {
            return Err(std::io::Error::new(
                std::io::ErrorKind::NotFound,
                format!("No workspace at {}", file.display()),
            ));
        }
        Self::open(file)
    }

    /// Полный путь файла.
    pub fn file(&self) -> &Path {
        &self.inner.file
    }

    // EnsureCreated: файла нет — создать его в WAL; таблиц нет — создать схему. Затем — недостающие столбцы (AddMissingColumns).
    fn initialize(&self) -> Result<()> {
        let existed = self.inner.file.exists();
        let connection = connect(&self.inner.file, true).map_err(db_error)?;
        if !existed {
            connection
                .query_row("PRAGMA journal_mode = 'wal';", [], |_| Ok(()))
                .map_err(db_error)?;
        }
        if !has_tables(&connection).map_err(db_error)? {
            connection.execute_batch("BEGIN IMMEDIATE;").map_err(db_error)?;
            let created = (|| {
                if !has_tables(&connection)? {
                    connection.execute_batch(SCHEMA)?;
                }
                connection.execute_batch("COMMIT;")
            })();
            if let Err(e) = created {
                let _ = connection.execute_batch("ROLLBACK;");
                return Err(db_error(e));
            }
        }
        add_missing_columns(&connection).map_err(db_error)?;
        self.give(connection);
        Ok(())
    }

    fn take(&self) -> Result<Connection> {
        let pooled = self.inner.pool.lock().unwrap_or_else(|e| e.into_inner()).pop();
        match pooled {
            Some(connection) => Ok(connection),
            None => connect(&self.inner.file, false).map_err(db_error),
        }
    }

    fn give(&self, connection: Connection) {
        // Соединение с незавершённой транзакцией (ошибка посреди отката) в пул не возвращаем.
        if connection.is_autocommit() {
            self.inner.pool.lock().unwrap_or_else(|e| e.into_inner()).push(connection);
        }
    }

    /// Чтение или одна команда записи: соединение секции записи потока, если она открыта, иначе из пула.
    pub(crate) fn with<T>(&self, f: impl FnOnce(&Connection) -> rusqlite::Result<T>) -> Result<T> {
        let mut f = Some(f);
        let id = self.inner.id;
        let active = ACTIVE.with(|a| {
            a.borrow()
                .iter()
                .find(|(owner, _)| *owner == id)
                .map(|(_, c)| (f.take().expect("called once"))(c))
        });
        if let Some(result) = active {
            return result.map_err(db_error);
        }
        let connection = self.take()?;
        let result = (f.take().expect("called once"))(&connection);
        self.give(connection);
        result.map_err(db_error)
    }

    /// Запись из нескольких команд атомарно: внутри секции записи — в её транзакции, иначе в своей (`BEGIN IMMEDIATE`, как
    /// `BeginTransaction` и неявная транзакция `SaveChanges` у Microsoft.Data.Sqlite).
    pub(crate) fn write<T>(&self, f: impl FnOnce(&Connection) -> Result<T>) -> Result<T> {
        let mut f = Some(f);
        let id = self.inner.id;
        let active = ACTIVE.with(|a| {
            a.borrow()
                .iter()
                .find(|(owner, _)| *owner == id)
                .map(|(_, c)| (f.take().expect("called once"))(c))
        });
        if let Some(result) = active {
            return result;
        }
        let connection = self.take()?;
        if let Err(e) = connection.execute_batch("BEGIN IMMEDIATE;") {
            self.give(connection);
            return Err(db_error(e));
        }
        let result = (f.take().expect("called once"))(&connection);
        let result = match result {
            Ok(value) => connection.execute_batch("COMMIT;").map(|()| value).map_err(db_error),
            Err(e) => Err(e),
        };
        if result.is_err() {
            let _ = connection.execute_batch("ROLLBACK;");
        }
        self.give(connection);
        result
    }

    fn in_section(&self) -> bool {
        let id = self.inner.id;
        ACTIVE.with(|a| a.borrow().iter().any(|(owner, _)| *owner == id))
    }

    /// Одна попытка секции записи: транзакция `BEGIN IMMEDIATE` на соединении, которое на время действия видят все обращения
    /// потока к этому хранилищу.
    fn attempt<T>(&self, action: &mut dyn FnMut() -> Result<T>) -> Result<T> {
        let connection = self.take()?;
        if let Err(e) = connection.execute_batch("BEGIN IMMEDIATE;") {
            self.give(connection);
            return Err(db_error(e));
        }
        let id = self.inner.id;
        ACTIVE.with(|a| a.borrow_mut().push((id, connection)));
        let result = action();
        let connection = ACTIVE.with(|a| {
            let mut a = a.borrow_mut();
            let at = a.iter().rposition(|(owner, _)| *owner == id).expect("the section's connection");
            a.remove(at).1
        });
        let result = match result {
            Ok(value) => connection.execute_batch("COMMIT;").map(|()| value).map_err(db_error),
            Err(e) => Err(e),
        };
        if result.is_err() {
            let _ = connection.execute_batch("ROLLBACK;");
        }
        self.give(connection);
        result
    }
}

fn has_tables(connection: &Connection) -> rusqlite::Result<bool> {
    let count: i64 = connection.query_row(
        "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"rootpage\" IS NOT NULL;",
        [],
        |row| row.get(0),
    )?;
    Ok(count > 0)
}

/// `DbInitializer.AddMissingColumns`: у БД, созданной до признаков типов связей, — столбцы со значениями «как было» (циклы
/// допускаются, кроме Blocks по умолчанию; тип не иерархический).
fn add_missing_columns(connection: &Connection) -> rusqlite::Result<()> {
    if add_column(connection, "AllowCycles", "1")? {
        let mut blocks: Vec<String> = Vec::new();
        {
            let mut statement = connection.prepare("SELECT \"Id\", \"ProjectId\" FROM \"link_types\"")?;
            let rows = statement.query_map([], |row| Ok((guid_col(row, 0)?, guid_col(row, 1)?)))?;
            for row in rows {
                let (id, project_id) = row?;
                if id == tasker_core::ids::default_link_type_id(&project_id, "blocks") {
                    blocks.push(g(&id));
                }
            }
        }
        for id in blocks {
            connection.execute("UPDATE \"link_types\" SET \"AllowCycles\" = 0 WHERE \"Id\" = ?1", params![id])?;
        }
    }
    add_column(connection, "Hierarchical", "0")?;
    Ok(())
}

fn add_column(connection: &Connection, column: &str, default: &str) -> rusqlite::Result<bool> {
    if connection
        .prepare(&format!("SELECT \"{column}\" FROM link_types WHERE 1 = 0"))
        .is_ok()
    {
        return Ok(false);
    }
    connection.execute_batch(&format!(
        "ALTER TABLE link_types ADD COLUMN \"{column}\" INTEGER NOT NULL DEFAULT {default}"
    ))?;
    Ok(true)
}

fn page_of<T>(total: usize, page: Page, data: Vec<T>) -> ListPage<T> {
    ListPage {
        total_count: total,
        offset: page.offset,
        limit: page.limit,
        data,
    }
}

const PROJECT_COLUMNS: &str = "\"Id\", \"Name\", \"CreatedAt\", \"Version\"";

fn project_row(row: &rusqlite::Row<'_>) -> rusqlite::Result<Project> {
    Ok(Project {
        id: guid_col(row, 0)?,
        name: row.get(1)?,
        created_at: time_col(row, 2)?,
        version: row.get::<_, i64>(3)?.to_string(),
    })
}

const USER_COLUMNS: &str = "\"Id\", \"Username\", \"Email\", \"IsAdmin\", \"Kind\", \"OwnerId\", \"CreatedAt\", \"Version\"";

fn user_row(row: &rusqlite::Row<'_>) -> rusqlite::Result<User> {
    let kind: String = row.get(4)?;
    Ok(User {
        id: guid_col(row, 0)?,
        username: row.get(1)?,
        email: row.get(2)?,
        is_admin: row.get(3)?,
        kind: if kind == "Agent" { UserKind::Agent } else { UserKind::Human },
        owner_id: values::optional_guid_col(row, 5)?,
        created_at: time_col(row, 6)?,
        version: row.get::<_, i64>(7)?.to_string(),
    })
}

fn kind_text(kind: UserKind) -> &'static str {
    match kind {
        UserKind::Human => "Human",
        UserKind::Agent => "Agent",
    }
}

/// Нормализация имени для уникального индекса (`UserStorage.Normalize`: `Trim().ToUpperInvariant()`, на стороне приложения).
fn normalize(value: &str) -> String {
    tasker_core::validate::to_upper_invariant(value.trim())
}

fn taken() -> Error {
    Error::in_use("Username or email is already taken")
}

fn unique_to_taken(e: Error) -> Error {
    if db_error_of(&e).is_some_and(DbError::is_unique_violation) {
        taken()
    } else {
        e
    }
}

const LOCK_COLUMNS: &str = "\"Entity\", \"Id\", \"HolderKey\", \"HolderName\", \"ProjectId\", \"AcquiredAt\", \"ExpiresAt\"";

fn lock_row(row: &rusqlite::Row<'_>) -> rusqlite::Result<EditLock> {
    let entity: String = row.get(0)?;
    let entity = LockedEntity::ALL
        .into_iter()
        .find(|x| x.name() == entity)
        .ok_or_else(|| values::conversion(0, format!("unknown entity '{entity}'")))?;
    Ok(EditLock {
        entity,
        id: guid_col(row, 1)?,
        holder: EditHolder::new(row.get::<_, String>(2)?, row.get::<_, String>(3)?),
        project_id: values::optional_guid_col(row, 4)?,
        acquired_at: time_col(row, 5)?,
        expires_at: time_col(row, 6)?,
    })
}

impl DbStore {
    fn find_lock(&self, entity: LockedEntity, id: &Uuid) -> Result<Option<EditLock>> {
        self.with(|c| {
            c.query_row(
                &format!("SELECT {LOCK_COLUMNS} FROM \"edit_locks\" WHERE \"Entity\" = ?1 AND \"Id\" = ?2 LIMIT 1"),
                params![entity.name(), g(id)],
                lock_row,
            )
            .optional()
        })
    }
}

impl Storage for DbStore {
    fn get_by_id<T: Entity>(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<T>> {
        self.with(|c| T::db_get(c, project_id, id))
    }

    fn get_all<T: Entity>(&self, project_id: &Uuid) -> Result<Vec<T>> {
        self.with(|c| T::db_list(c, project_id, None)).map(|(_, data)| data)
    }

    fn get_range<T: Entity>(&self, project_id: &Uuid, page: Page) -> Result<ListPage<T>> {
        let (total, data) = self.with(|c| T::db_list(c, project_id, Some(page)))?;
        Ok(page_of(total, page, data))
    }

    /// В БД нечитаемых записей не бывает.
    fn count_unreadable<T: Entity>(&self, _project_id: &Uuid) -> Result<usize> {
        Ok(0)
    }

    fn unreadable<T: Entity>(&self, _project_id: &Uuid, _key: &str) -> Result<()> {
        Ok(())
    }

    fn unreadable_project(&self, _key: &str) -> Result<()> {
        Ok(())
    }

    fn unreadable_user(&self, _key: &str) -> Result<()> {
        Ok(())
    }

    fn add<T: Entity>(&self, entity: &T) -> Result<String> {
        self.write(|c| entity.db_insert(c).map_err(db_error))?;
        Ok(values::INITIAL_VERSION.to_string())
    }

    fn update<T: Entity>(&self, entity: &T, expected_version: &str) -> Result<Option<String>> {
        let Some(expected) = parse_version(expected_version) else {
            return Ok(None);
        };
        let updated = self.write(|c| entity.db_update(c, expected).map_err(db_error))?;
        Ok(updated.then(|| (i64::from(expected) + 1).to_string()))
    }

    fn delete<T: Entity>(&self, project_id: &Uuid, id: &Uuid, expected_version: &str) -> Result<bool> {
        let Some(expected) = parse_version(expected_version) else {
            return Ok(false);
        };
        self.write(|c| T::db_delete(c, project_id, id, expected).map_err(db_error))
    }

    fn get_project(&self, id: &Uuid) -> Result<Option<Project>> {
        self.with(|c| {
            c.query_row(
                &format!("SELECT {PROJECT_COLUMNS} FROM \"projects\" WHERE \"Id\" = ?1 LIMIT 1"),
                params![g(id)],
                project_row,
            )
            .optional()
        })
    }

    fn project_range(&self, ids: Option<&[Uuid]>, page: Page) -> Result<ListPage<Project>> {
        let filter = match ids {
            Some(_) => " WHERE \"Id\" IN (SELECT value FROM json_each(?1))",
            None => " WHERE ?1 IS NULL",
        };
        let ids = ids.map(values::json_ids);
        let (total, data) = self.with(|c| {
            let total: i64 = c.query_row(&format!("SELECT COUNT(*) FROM \"projects\"{filter}"), params![ids], |row| {
                row.get(0)
            })?;
            let mut statement = c.prepare(&format!(
                "SELECT {PROJECT_COLUMNS} FROM \"projects\"{filter} ORDER BY \"Name\", \"Id\" LIMIT ?2 OFFSET ?3"
            ))?;
            let data = statement
                .query_map(params![ids, page.limit as i64, page.offset as i64], project_row)?
                .collect::<rusqlite::Result<Vec<_>>>()?;
            Ok((total.max(0) as usize, data))
        })?;
        Ok(page_of(total, page, data))
    }

    fn all_projects(&self) -> Result<Vec<Project>> {
        self.with(|c| {
            let mut statement = c.prepare(&format!("SELECT {PROJECT_COLUMNS} FROM \"projects\" ORDER BY \"Name\", \"Id\""))?;
            statement.query_map([], project_row)?.collect()
        })
    }

    fn add_project(&self, project: &Project) -> Result<String> {
        self.with(|c| {
            c.execute(
                "INSERT INTO \"projects\" (\"Id\", \"CreatedAt\", \"Name\", \"Version\") VALUES (?1, ?2, ?3, ?4)",
                params![g(&project.id), db_time(&project.created_at), project.name, values::INITIAL_VERSION],
            )
        })?;
        Ok(values::INITIAL_VERSION.to_string())
    }

    fn update_project(&self, project: &Project, expected_version: &str) -> Result<Option<String>> {
        let Some(expected) = parse_version(expected_version) else {
            return Ok(None);
        };
        let updated = self.with(|c| {
            c.execute(
                "UPDATE \"projects\" SET \"Name\" = ?1, \"Version\" = ?2 WHERE \"Id\" = ?3 AND \"Version\" = ?4",
                params![project.name, i64::from(expected) + 1, g(&project.id), expected],
            )
        })?;
        Ok((updated > 0).then(|| (i64::from(expected) + 1).to_string()))
    }

    /// Содержимое проекта удаляется каскадом на уровне БД.
    fn delete_project(&self, id: &Uuid, expected_version: &str) -> Result<bool> {
        let Some(expected) = parse_version(expected_version) else {
            return Ok(false);
        };
        let deleted = self.with(|c| {
            c.execute(
                "DELETE FROM \"projects\" WHERE \"Id\" = ?1 AND \"Version\" = ?2",
                params![g(id), expected],
            )
        })?;
        Ok(deleted > 0)
    }

    fn tasks_where(&self, project_id: &Uuid, filter: Option<&TaskFilter>) -> Result<Vec<TaskItem>> {
        self.with(|c| tasks::list(c, project_id, filter, None)).map(|(_, data)| data)
    }

    fn task_range(&self, project_id: &Uuid, filter: Option<&TaskFilter>, page: Page) -> Result<ListPage<TaskItem>> {
        let (total, data) = self.with(|c| tasks::list(c, project_id, filter, Some(page)))?;
        Ok(page_of(total, page, data))
    }

    fn task_ids(&self, project_id: &Uuid, filter: Option<&TaskFilter>) -> Result<Vec<Uuid>> {
        self.with(|c| tasks::ids(c, project_id, filter))
    }

    fn count_tasks(&self, project_id: &Uuid, filter: &TaskFilter) -> Result<usize> {
        self.with(|c| tasks::count(c, project_id, filter))
    }

    fn tasks_by_id_prefix(&self, project_id: &Uuid, key: &str) -> Result<Vec<TaskItem>> {
        self.with(|c| tasks::by_id_prefix(c, project_id, key))
    }

    fn own_field_kinds(&self, project_id: &Uuid, name_key: &str) -> Result<Vec<OwnFieldKind>> {
        self.with(|c| tasks::own_field_kinds(c, project_id, name_key))
    }

    fn max_number(&self, project_id: &Uuid, series_id: &Uuid) -> Result<i32> {
        self.with(|c| {
            c.query_row(
                "SELECT MAX(\"Number\") FROM \"task_series_numbers\" WHERE \"ProjectId\" = ?1 AND \"SeriesId\" = ?2",
                params![g(project_id), g(series_id)],
                |row| row.get::<_, Option<i32>>(0),
            )
        })
        .map(|max| max.unwrap_or(0))
    }

    fn tasks_by_number(&self, project_id: &Uuid, series_id: &Uuid, number: i32) -> Result<Vec<TaskItem>> {
        self.with(|c| tasks::by_number(c, project_id, series_id, number))
    }

    fn number_conflicts(&self, project_id: &Uuid) -> Result<Vec<NumberConflict>> {
        self.with(|c| tasks::number_conflicts(c, project_id))
    }

    fn tasks_with_series_not_in(&self, project_id: &Uuid, known_series_ids: &[Uuid]) -> Result<Vec<TaskItem>> {
        self.with(|c| tasks::with_series_not_in(c, project_id, known_series_ids))
    }

    fn tasks_linked_to(&self, project_id: &Uuid, target_id: &Uuid) -> Result<Vec<TaskItem>> {
        self.with(|c| tasks::linked_to(c, project_id, target_id))
    }

    fn link_targets(&self, project_id: &Uuid, type_id: &Uuid, source_ids: &[Uuid]) -> Result<HashMap<Uuid, Vec<Uuid>>> {
        if source_ids.is_empty() {
            return Ok(HashMap::new());
        }
        self.with(|c| tasks::link_targets(c, project_id, type_id, source_ids))
    }

    fn link_edges(&self, project_id: &Uuid, type_id: &Uuid) -> Result<Vec<LinkEdge>> {
        self.with(|c| tasks::link_edges(c, project_id, type_id))
    }

    fn count_linked_to(&self, project_id: &Uuid, target_ids: &[Uuid]) -> Result<HashMap<Uuid, usize>> {
        if target_ids.is_empty() {
            return Ok(HashMap::new());
        }
        self.with(|c| tasks::count_linked_to(c, project_id, target_ids))
    }

    fn tasks_with_invalid_links(&self, project_id: &Uuid, known_type_ids: &[Uuid]) -> Result<Vec<TaskItem>> {
        self.with(|c| tasks::with_invalid_links(c, project_id, known_type_ids))
    }

    fn get_user(&self, id: &Uuid) -> Result<Option<User>> {
        self.with(|c| {
            c.query_row(
                &format!("SELECT {USER_COLUMNS} FROM \"users\" WHERE \"Id\" = ?1 LIMIT 1"),
                params![g(id)],
                user_row,
            )
            .optional()
        })
    }

    fn user_by_username(&self, username: &str) -> Result<Option<User>> {
        self.with(|c| {
            c.query_row(
                &format!("SELECT {USER_COLUMNS} FROM \"users\" WHERE \"NormalizedUsername\" = ?1 LIMIT 1"),
                params![normalize(username)],
                user_row,
            )
            .optional()
        })
    }

    fn user_range(&self, kind: Option<UserKind>, page: Page) -> Result<ListPage<User>> {
        let kind = kind.map(kind_text);
        let (total, data) = self.with(|c| {
            let total: i64 = c.query_row(
                "SELECT COUNT(*) FROM \"users\" WHERE ?1 IS NULL OR \"Kind\" = ?1",
                params![kind],
                |row| row.get(0),
            )?;
            let mut statement = c.prepare(&format!(
                "SELECT {USER_COLUMNS} FROM \"users\" WHERE ?1 IS NULL OR \"Kind\" = ?1 \
                 ORDER BY \"NormalizedUsername\", \"Id\" LIMIT ?2 OFFSET ?3"
            ))?;
            let data = statement
                .query_map(params![kind, page.limit as i64, page.offset as i64], user_row)?
                .collect::<rusqlite::Result<Vec<_>>>()?;
            Ok((total.max(0) as usize, data))
        })?;
        Ok(page_of(total, page, data))
    }

    fn all_users(&self, kind: Option<UserKind>) -> Result<Vec<User>> {
        Ok(self.user_range(kind, Page::new(0, i32::MAX as usize))?.data)
    }

    fn add_user(&self, user: &User) -> Result<String> {
        self.with(|c| {
            c.execute(
                "INSERT INTO \"users\" (\"Id\", \"CreatedAt\", \"Email\", \"IsAdmin\", \"Kind\", \"NormalizedEmail\", \
                 \"NormalizedUsername\", \"OwnerId\", \"PasswordHash\", \"SecurityStamp\", \"Username\", \"Version\") \
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, NULL, ?9, ?10, ?11)",
                params![
                    g(&user.id),
                    db_time(&user.created_at),
                    user.email,
                    user.is_admin,
                    kind_text(user.kind),
                    user.email.as_deref().map(normalize),
                    normalize(&user.username),
                    user.owner_id.as_ref().map(g),
                    g(&Uuid::new_v4()),
                    user.username,
                    values::INITIAL_VERSION,
                ],
            )
        })
        .map_err(unique_to_taken)?;
        Ok(values::INITIAL_VERSION.to_string())
    }

    fn update_user(&self, user: &User, expected_version: &str) -> Result<Option<String>> {
        let Some(expected) = parse_version(expected_version) else {
            return Ok(None);
        };
        let updated = self
            .with(|c| {
                c.execute(
                    "UPDATE \"users\" SET \"Username\" = ?1, \"NormalizedUsername\" = ?2, \"Email\" = ?3, \
                     \"NormalizedEmail\" = ?4, \"IsAdmin\" = ?5, \"Version\" = ?6 WHERE \"Id\" = ?7 AND \"Version\" = ?8",
                    params![
                        user.username,
                        normalize(&user.username),
                        user.email,
                        user.email.as_deref().map(normalize),
                        user.is_admin,
                        i64::from(expected) + 1,
                        g(&user.id),
                        expected
                    ],
                )
            })
            .map_err(unique_to_taken)?;
        Ok((updated > 0).then(|| (i64::from(expected) + 1).to_string()))
    }

    /// Участие в проектах, сессии, агенты пользователя и их токены удаляются каскадом на уровне БД.
    fn delete_user(&self, id: &Uuid, expected_version: &str) -> Result<bool> {
        let Some(expected) = parse_version(expected_version) else {
            return Ok(false);
        };
        let deleted = self.with(|c| {
            c.execute(
                "DELETE FROM \"users\" WHERE \"Id\" = ?1 AND \"Version\" = ?2",
                params![g(id), expected],
            )
        })?;
        Ok(deleted > 0)
    }

    fn lock_get(&self, entity: LockedEntity, id: &Uuid, now: &Timestamp) -> Result<Option<EditLock>> {
        Ok(self.find_lock(entity, id)?.filter(|held| held.is_active(now)))
    }

    fn locks_by_project(&self, project_id: &Uuid, now: &Timestamp) -> Result<Vec<EditLock>> {
        self.with(|c| {
            let mut statement = c.prepare(&format!(
                "SELECT {LOCK_COLUMNS} FROM \"edit_locks\" WHERE \"ProjectId\" = ?1 AND \"ExpiresAt\" > ?2 \
                 ORDER BY \"AcquiredAt\", \"Id\""
            ))?;
            statement.query_map(params![g(project_id), db_time(now)], lock_row)?.collect()
        })
    }

    /// Атомарно — условным `UPDATE … WHERE (держатель — я ИЛИ срок вышел)` и первичным ключом при вставке: из двух одновременных
    /// запросов выигрывает один, второй видит чужую блокировку (`EditLockStorage` БД, до 3 попыток).
    fn lock_acquire(
        &self,
        entity: LockedEntity,
        id: &Uuid,
        holder: &EditHolder,
        now: &Timestamp,
        expires_at: &Timestamp,
        project_id: Option<Uuid>,
    ) -> Result<EditLock> {
        const MAX_LOCK_ATTEMPTS: u32 = 3;
        let now_db = db_time(now);
        let expires_db = db_time(expires_at);
        let mut attempt = 1;
        loop {
            let Some(current) = self.find_lock(entity, id)? else {
                let inserted = self.with(|c| {
                    c.execute(
                        "INSERT INTO \"edit_locks\" (\"Entity\", \"Id\", \"AcquiredAt\", \"ExpiresAt\", \"HolderKey\", \
                         \"HolderName\", \"ProjectId\") VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
                        params![
                            entity.name(),
                            g(id),
                            now_db,
                            expires_db,
                            holder.key,
                            holder.name,
                            project_id.as_ref().map(g)
                        ],
                    )
                });
                match inserted {
                    Ok(_) => {
                        return Ok(EditLock {
                            entity,
                            id: *id,
                            holder: holder.clone(),
                            acquired_at: values::utc(now),
                            expires_at: values::utc(expires_at),
                            project_id,
                        });
                    }
                    // Другой запрос успел вставить строку: смотрим, чья она.
                    Err(e) if attempt < MAX_LOCK_ATTEMPTS && db_error_of(&e).is_some_and(DbError::is_unique_violation) => {
                        attempt += 1;
                        continue;
                    }
                    Err(e) => return Err(e),
                }
            };

            let active = current.is_active(now);
            let mine = current.holder.key == holder.key && active;
            if !mine && active {
                return Ok(current);
            }
            // Своя (продлеваем, момент взятия сохраняется) или истёкшая (берём заново).
            let acquired_at = if mine { current.acquired_at } else { values::utc(now) };
            let updated = self.with(|c| {
                c.execute(
                    "UPDATE \"edit_locks\" SET \"HolderKey\" = ?1, \"HolderName\" = ?2, \"ProjectId\" = ?3, \
                     \"AcquiredAt\" = ?4, \"ExpiresAt\" = ?5 WHERE \"Entity\" = ?6 AND \"Id\" = ?7 \
                     AND (\"HolderKey\" = ?1 OR \"ExpiresAt\" <= ?8)",
                    params![
                        holder.key,
                        holder.name,
                        project_id.as_ref().map(g),
                        db_time(&acquired_at),
                        expires_db,
                        entity.name(),
                        g(id),
                        now_db
                    ],
                )
            })?;
            if updated > 0 {
                return Ok(EditLock {
                    entity,
                    id: *id,
                    holder: holder.clone(),
                    acquired_at,
                    expires_at: *expires_at,
                    project_id,
                });
            }
            // Между чтением и записью блокировку взял другой — читаем заново.
            if attempt >= MAX_LOCK_ATTEMPTS {
                return Err(Error::Io(std::io::Error::other(format!(
                    "Could not acquire the lock of {} {}: it keeps changing",
                    entity.name(),
                    tasker_core::ids::guid_d(id)
                ))));
            }
            attempt += 1;
        }
    }

    fn lock_release(&self, entity: LockedEntity, id: &Uuid, holder_key: &str) -> Result<bool> {
        let deleted = self.with(|c| {
            c.execute(
                "DELETE FROM \"edit_locks\" WHERE \"Entity\" = ?1 AND \"Id\" = ?2 AND \"HolderKey\" = ?3",
                params![entity.name(), g(id), holder_key],
            )
        })?;
        Ok(deleted > 0)
    }

    fn lock_remove(&self, entity: LockedEntity, id: &Uuid) -> Result<()> {
        self.with(|c| {
            c.execute(
                "DELETE FROM \"edit_locks\" WHERE \"Entity\" = ?1 AND \"Id\" = ?2",
                params![entity.name(), g(id)],
            )
        })?;
        Ok(())
    }

    /// `WriteScope.Exclusive` для SQLite: транзакция `BEGIN IMMEDIATE` (блокировка записи берётся сразу — иначе второй писатель
    /// не смог бы повысить блокировку и сразу получил бы «database is locked»). Нарушение уникальности (другой процесс взял тот
    /// же номер) или занятая база — откат и повтор действия целиком через 10–30 мс × попытка, до 5 попыток. Вложенный вызов —
    /// внутри внешней транзакции, повторяет внешний.
    fn exclusive<T>(&self, _project_id: &Uuid, action: &mut dyn FnMut() -> Result<T>) -> Result<T> {
        if self.in_section() {
            return action();
        }
        let mut attempt = 1;
        loop {
            match self.attempt(action) {
                Err(e) if attempt < MAX_ATTEMPTS && is_retryable(&e) => {
                    std::thread::sleep(Duration::from_millis(u64::from(values::jitter(10, 30) * attempt)));
                    attempt += 1;
                }
                result => return result,
            }
        }
    }
}
