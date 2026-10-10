//! Хранилище области (`I*Storage` и `IWriteScope` из `Tasker.Core` в .NET): то, через что сервисы одинаково работают с папкой
//! `.tasker` ([`crate::files_store::FileStore`]: файлы и индекс) и с файлом SQLite ([`crate::db::DbStore`]: та же схема, что
//! создаёт EF Core в `Tasker.Storage.Db`).
//!
//! [`Storage`] — операции хранилищ .NET, собранные в один трейт: сущности проекта по виду ([`Entity`]), проекты, запросы задач
//! (`ITaskStorage`), пользователи (`IUserStorage`), блокировки правки (`IEditLockStorage`) и секция записи проекта
//! (`IWriteScope.Exclusive`). [`Backend`] — выбранная реализация; [`crate::Workspace`] передаёт ей вызовы сервисов.
use crate::error::Result;
use crate::files_store::FileStore;
use serde::Serialize;
use std::collections::HashMap;
use std::path::Path;
use tasker_core::Timestamp;
use tasker_core::locks::{EditHolder, EditLock, LockedEntity};
use tasker_core::model::{
    Board, FieldDefinition, FieldEnum, LinkType, Project, Series, Status, StatusSet, TaskItem, TaskType, User, UserKind,
};
use tasker_core::tasks::{ListPage, Page, TaskFilter};
use tasker_files::files::Versioned;
use tasker_files::index::{IndexEntity, LinkEdge, NumberConflict, OwnFieldKind};
use tasker_files::layout::EntityFolder;
use uuid::Uuid;

/// Сущность проекта: папка файлов, модель файла и место в индексе (файловое хранилище) и таблицы БД (`crate::db::DbEntity`).
pub trait Entity: IndexEntity + crate::db::DbEntity + Serialize + Clone {
    const FOLDER: &'static EntityFolder;
    const LOCK: LockedEntity;

    fn id(&self) -> Uuid;
    fn project_id(&self) -> Uuid;
    /// Название, по которому называется файл (у задачи — заголовок).
    fn file_name_source(&self) -> &str;
    fn version(&self) -> &str;
    fn set_version(&mut self, version: String);
    fn parse(project_id: Uuid, bytes: &[u8], path: &Path) -> tasker_files::Result<Versioned<Self>>;
    fn to_bytes(&self) -> Vec<u8>;
}

/// Операции хранилища области. Версия сущности — непрозрачная строка: у файлов — хэш содержимого, у БД — счётчик изменений.
/// Методы записи «по версии» возвращают None/false, если сущности нет или её уже изменили.
pub trait Storage {
    // ---- сущности проекта (`IStatusStorage`, `ITaskTypeStorage`, … по виду T) ----

    fn get_by_id<T: Entity>(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<T>>;
    /// Все сущности вида в проекте в порядке списка (по имени, у серий — по префиксу, затем по id; у задач — по созданию).
    fn get_all<T: Entity>(&self, project_id: &Uuid) -> Result<Vec<T>>;
    fn get_range<T: Entity>(&self, project_id: &Uuid, page: Page) -> Result<ListPage<T>>;
    /// Сколько записей этого вида не удалось прочитать (`CountUnreadable`); у БД таких не бывает.
    fn count_unreadable<T: Entity>(&self, project_id: &Uuid) -> Result<usize>;
    fn add<T: Entity>(&self, entity: &T) -> Result<String>;
    fn update<T: Entity>(&self, entity: &T, expected_version: &str) -> Result<Option<String>>;
    fn delete<T: Entity>(&self, project_id: &Uuid, id: &Uuid, expected_version: &str) -> Result<bool>;

    // ---- проекты (`IProjectStorage`) ----

    fn get_project(&self, id: &Uuid) -> Result<Option<Project>>;
    fn project_range(&self, ids: Option<&[Uuid]>, page: Page) -> Result<ListPage<Project>>;
    fn all_projects(&self) -> Result<Vec<Project>>;
    fn add_project(&self, project: &Project) -> Result<String>;
    fn update_project(&self, project: &Project, expected_version: &str) -> Result<Option<String>>;
    fn delete_project(&self, id: &Uuid, expected_version: &str) -> Result<bool>;

    // ---- задачи (`ITaskStorage`) ----

    fn tasks_where(&self, project_id: &Uuid, filter: Option<&TaskFilter>) -> Result<Vec<TaskItem>>;
    fn task_range(&self, project_id: &Uuid, filter: Option<&TaskFilter>, page: Page) -> Result<ListPage<TaskItem>>;
    fn task_ids(&self, project_id: &Uuid, filter: Option<&TaskFilter>) -> Result<Vec<Uuid>>;
    fn count_tasks(&self, project_id: &Uuid, filter: &TaskFilter) -> Result<usize>;
    /// Задачи, id которых начинается с ключа (Guid в форме D строчными): по созданию, затем по id.
    fn tasks_by_id_prefix(&self, project_id: &Uuid, key: &str) -> Result<Vec<TaskItem>>;
    fn own_field_kinds(&self, project_id: &Uuid, name_key: &str) -> Result<Vec<OwnFieldKind>>;
    /// Наибольший номер в серии; 0 — номеров нет. Для выдачи номера — внутри [`Storage::exclusive`].
    fn max_number(&self, project_id: &Uuid, series_id: &Uuid) -> Result<i32>;
    fn tasks_by_number(&self, project_id: &Uuid, series_id: &Uuid, number: i32) -> Result<Vec<TaskItem>>;
    fn number_conflicts(&self, project_id: &Uuid) -> Result<Vec<NumberConflict>>;
    fn tasks_with_series_not_in(&self, project_id: &Uuid, known_series_ids: &[Uuid]) -> Result<Vec<TaskItem>>;
    fn tasks_linked_to(&self, project_id: &Uuid, target_id: &Uuid) -> Result<Vec<TaskItem>>;
    fn link_targets(&self, project_id: &Uuid, type_id: &Uuid, source_ids: &[Uuid]) -> Result<HashMap<Uuid, Vec<Uuid>>>;
    fn link_edges(&self, project_id: &Uuid, type_id: &Uuid) -> Result<Vec<LinkEdge>>;
    fn count_linked_to(&self, project_id: &Uuid, target_ids: &[Uuid]) -> Result<HashMap<Uuid, usize>>;
    fn tasks_with_invalid_links(&self, project_id: &Uuid, known_type_ids: &[Uuid]) -> Result<Vec<TaskItem>>;

    // ---- пользователи (`IUserStorage`) ----

    fn get_user(&self, id: &Uuid) -> Result<Option<User>>;
    /// Без учёта регистра.
    fn user_by_username(&self, username: &str) -> Result<Option<User>>;
    fn user_range(&self, kind: Option<UserKind>, page: Page) -> Result<ListPage<User>>;
    fn all_users(&self, kind: Option<UserKind>) -> Result<Vec<User>>;
    /// Новый пользователь; имя уже занято — конфликт (у файлов — проверка под блокировкой каталога, у БД — уникальный индекс).
    fn add_user(&self, user: &User) -> Result<String>;
    fn update_user(&self, user: &User, expected_version: &str) -> Result<Option<String>>;
    fn delete_user(&self, id: &Uuid, expected_version: &str) -> Result<bool>;

    // ---- блокировки правки (`IEditLockStorage`) ----

    fn lock_get(&self, entity: LockedEntity, id: &Uuid, now: &Timestamp) -> Result<Option<EditLock>>;
    fn locks_by_project(&self, project_id: &Uuid, now: &Timestamp) -> Result<Vec<EditLock>>;
    /// Берёт или продлевает блокировку; держит другой — его блокировка.
    fn lock_acquire(
        &self,
        entity: LockedEntity,
        id: &Uuid,
        holder: &EditHolder,
        now: &Timestamp,
        expires_at: &Timestamp,
        project_id: Option<Uuid>,
    ) -> Result<EditLock>;
    fn lock_release(&self, entity: LockedEntity, id: &Uuid, holder_key: &str) -> Result<bool>;
    fn lock_remove(&self, entity: LockedEntity, id: &Uuid) -> Result<()>;

    // ---- секция записи проекта (`IWriteScope`) ----

    /// Атомарная секция записи проекта: проверки и запись внутри не пересекаются с другими писателями (у файлов — `write.lock`, у
    /// БД — транзакция `BEGIN IMMEDIATE`, которая при гонке за номер или занятой базе повторяется целиком). Вложенный вызов
    /// выполняется внутри внешнего.
    fn exclusive<T>(&self, project_id: &Uuid, action: &mut dyn FnMut() -> Result<T>) -> Result<T>;
}

/// Хранилище открытой области.
#[derive(Clone)]
pub enum Backend {
    Files(FileStore),
    Db(crate::db::DbStore),
}

/// Вызов метода [`Storage`] у выбранной реализации.
macro_rules! dispatch {
    ($self:expr, $s:ident => $call:expr) => {
        match $self {
            Backend::Files($s) => $call,
            Backend::Db($s) => $call,
        }
    };
}

impl Storage for Backend {
    fn get_by_id<T: Entity>(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<T>> {
        dispatch!(self, s => s.get_by_id(project_id, id))
    }
    fn get_all<T: Entity>(&self, project_id: &Uuid) -> Result<Vec<T>> {
        dispatch!(self, s => s.get_all(project_id))
    }
    fn get_range<T: Entity>(&self, project_id: &Uuid, page: Page) -> Result<ListPage<T>> {
        dispatch!(self, s => s.get_range(project_id, page))
    }
    fn count_unreadable<T: Entity>(&self, project_id: &Uuid) -> Result<usize> {
        dispatch!(self, s => s.count_unreadable::<T>(project_id))
    }
    fn add<T: Entity>(&self, entity: &T) -> Result<String> {
        dispatch!(self, s => s.add(entity))
    }
    fn update<T: Entity>(&self, entity: &T, expected_version: &str) -> Result<Option<String>> {
        dispatch!(self, s => s.update(entity, expected_version))
    }
    fn delete<T: Entity>(&self, project_id: &Uuid, id: &Uuid, expected_version: &str) -> Result<bool> {
        dispatch!(self, s => s.delete::<T>(project_id, id, expected_version))
    }
    fn get_project(&self, id: &Uuid) -> Result<Option<Project>> {
        dispatch!(self, s => s.get_project(id))
    }
    fn project_range(&self, ids: Option<&[Uuid]>, page: Page) -> Result<ListPage<Project>> {
        dispatch!(self, s => s.project_range(ids, page))
    }
    fn all_projects(&self) -> Result<Vec<Project>> {
        dispatch!(self, s => s.all_projects())
    }
    fn add_project(&self, project: &Project) -> Result<String> {
        dispatch!(self, s => s.add_project(project))
    }
    fn update_project(&self, project: &Project, expected_version: &str) -> Result<Option<String>> {
        dispatch!(self, s => s.update_project(project, expected_version))
    }
    fn delete_project(&self, id: &Uuid, expected_version: &str) -> Result<bool> {
        dispatch!(self, s => s.delete_project(id, expected_version))
    }
    fn tasks_where(&self, project_id: &Uuid, filter: Option<&TaskFilter>) -> Result<Vec<TaskItem>> {
        dispatch!(self, s => s.tasks_where(project_id, filter))
    }
    fn task_range(&self, project_id: &Uuid, filter: Option<&TaskFilter>, page: Page) -> Result<ListPage<TaskItem>> {
        dispatch!(self, s => s.task_range(project_id, filter, page))
    }
    fn task_ids(&self, project_id: &Uuid, filter: Option<&TaskFilter>) -> Result<Vec<Uuid>> {
        dispatch!(self, s => s.task_ids(project_id, filter))
    }
    fn count_tasks(&self, project_id: &Uuid, filter: &TaskFilter) -> Result<usize> {
        dispatch!(self, s => s.count_tasks(project_id, filter))
    }
    fn tasks_by_id_prefix(&self, project_id: &Uuid, key: &str) -> Result<Vec<TaskItem>> {
        dispatch!(self, s => s.tasks_by_id_prefix(project_id, key))
    }
    fn own_field_kinds(&self, project_id: &Uuid, name_key: &str) -> Result<Vec<OwnFieldKind>> {
        dispatch!(self, s => s.own_field_kinds(project_id, name_key))
    }
    fn max_number(&self, project_id: &Uuid, series_id: &Uuid) -> Result<i32> {
        dispatch!(self, s => s.max_number(project_id, series_id))
    }
    fn tasks_by_number(&self, project_id: &Uuid, series_id: &Uuid, number: i32) -> Result<Vec<TaskItem>> {
        dispatch!(self, s => s.tasks_by_number(project_id, series_id, number))
    }
    fn number_conflicts(&self, project_id: &Uuid) -> Result<Vec<NumberConflict>> {
        dispatch!(self, s => s.number_conflicts(project_id))
    }
    fn tasks_with_series_not_in(&self, project_id: &Uuid, known_series_ids: &[Uuid]) -> Result<Vec<TaskItem>> {
        dispatch!(self, s => s.tasks_with_series_not_in(project_id, known_series_ids))
    }
    fn tasks_linked_to(&self, project_id: &Uuid, target_id: &Uuid) -> Result<Vec<TaskItem>> {
        dispatch!(self, s => s.tasks_linked_to(project_id, target_id))
    }
    fn link_targets(&self, project_id: &Uuid, type_id: &Uuid, source_ids: &[Uuid]) -> Result<HashMap<Uuid, Vec<Uuid>>> {
        dispatch!(self, s => s.link_targets(project_id, type_id, source_ids))
    }
    fn link_edges(&self, project_id: &Uuid, type_id: &Uuid) -> Result<Vec<LinkEdge>> {
        dispatch!(self, s => s.link_edges(project_id, type_id))
    }
    fn count_linked_to(&self, project_id: &Uuid, target_ids: &[Uuid]) -> Result<HashMap<Uuid, usize>> {
        dispatch!(self, s => s.count_linked_to(project_id, target_ids))
    }
    fn tasks_with_invalid_links(&self, project_id: &Uuid, known_type_ids: &[Uuid]) -> Result<Vec<TaskItem>> {
        dispatch!(self, s => s.tasks_with_invalid_links(project_id, known_type_ids))
    }
    fn get_user(&self, id: &Uuid) -> Result<Option<User>> {
        dispatch!(self, s => s.get_user(id))
    }
    fn user_by_username(&self, username: &str) -> Result<Option<User>> {
        dispatch!(self, s => s.user_by_username(username))
    }
    fn user_range(&self, kind: Option<UserKind>, page: Page) -> Result<ListPage<User>> {
        dispatch!(self, s => s.user_range(kind, page))
    }
    fn all_users(&self, kind: Option<UserKind>) -> Result<Vec<User>> {
        dispatch!(self, s => s.all_users(kind))
    }
    fn add_user(&self, user: &User) -> Result<String> {
        dispatch!(self, s => s.add_user(user))
    }
    fn update_user(&self, user: &User, expected_version: &str) -> Result<Option<String>> {
        dispatch!(self, s => s.update_user(user, expected_version))
    }
    fn delete_user(&self, id: &Uuid, expected_version: &str) -> Result<bool> {
        dispatch!(self, s => s.delete_user(id, expected_version))
    }
    fn lock_get(&self, entity: LockedEntity, id: &Uuid, now: &Timestamp) -> Result<Option<EditLock>> {
        dispatch!(self, s => s.lock_get(entity, id, now))
    }
    fn locks_by_project(&self, project_id: &Uuid, now: &Timestamp) -> Result<Vec<EditLock>> {
        dispatch!(self, s => s.locks_by_project(project_id, now))
    }
    fn lock_acquire(
        &self,
        entity: LockedEntity,
        id: &Uuid,
        holder: &EditHolder,
        now: &Timestamp,
        expires_at: &Timestamp,
        project_id: Option<Uuid>,
    ) -> Result<EditLock> {
        dispatch!(self, s => s.lock_acquire(entity, id, holder, now, expires_at, project_id))
    }
    fn lock_release(&self, entity: LockedEntity, id: &Uuid, holder_key: &str) -> Result<bool> {
        dispatch!(self, s => s.lock_release(entity, id, holder_key))
    }
    fn lock_remove(&self, entity: LockedEntity, id: &Uuid) -> Result<()> {
        dispatch!(self, s => s.lock_remove(entity, id))
    }
    fn exclusive<T>(&self, project_id: &Uuid, action: &mut dyn FnMut() -> Result<T>) -> Result<T> {
        dispatch!(self, s => s.exclusive(project_id, action))
    }
}

macro_rules! entity {
    ($t:ty, $folder:expr, $lock:expr, $module:ident, $name:ident) => {
        impl Entity for $t {
            const FOLDER: &'static EntityFolder = &$folder;
            const LOCK: LockedEntity = $lock;

            fn id(&self) -> Uuid {
                self.id
            }

            fn project_id(&self) -> Uuid {
                self.project_id
            }

            fn file_name_source(&self) -> &str {
                &self.$name
            }

            fn version(&self) -> &str {
                &self.version
            }

            fn set_version(&mut self, version: String) {
                self.version = version;
            }

            fn parse(project_id: Uuid, bytes: &[u8], path: &Path) -> tasker_files::Result<Versioned<Self>> {
                tasker_files::files::$module::parse(project_id, bytes, path)
            }

            fn to_bytes(&self) -> Vec<u8> {
                tasker_files::files::$module::serialize(self)
            }
        }
    };
}

use tasker_files::layout;
entity!(TaskItem, layout::TASKS, LockedEntity::Task, task, title);
entity!(TaskType, layout::TASK_TYPES, LockedEntity::TaskType, task_type, name);
entity!(Status, layout::STATUSES, LockedEntity::Status, status, name);
entity!(StatusSet, layout::STATUS_SETS, LockedEntity::StatusSet, status_set, name);
entity!(Board, layout::BOARDS, LockedEntity::Board, board, name);
entity!(Series, layout::SERIES, LockedEntity::Series, series, name);
entity!(LinkType, layout::LINK_TYPES, LockedEntity::LinkType, link_type, name);
entity!(FieldDefinition, layout::FIELDS, LockedEntity::Field, field, name);
entity!(FieldEnum, layout::ENUMS, LockedEntity::Enum, field_enum, name);
