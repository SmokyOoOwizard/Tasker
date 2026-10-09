//! Сервисы домена Tasker на Rust (TSK-132, фаза 2 плана `docs/rust-migration-plan.md`, п. 3): те же операции, проверки, тексты
//! ошибок и каскады, что у `Tasker.Core.*Service` (.NET), поверх файлов `.tasker` ([`tasker_files::write`]) и индекса
//! ([`tasker_files::index::WorkspaceIndex`]). Сервисы синхронные: в домене нет async.
//!
//! Точка входа — [`Workspace`]: папка `.tasker`, индекс, часы ([`Clock`]) и держатель блокировок правки ([`EditHolder`]) — то, что в
//! .NET приходит через контейнер (`TaskerDirectory`, `WorkspaceIndex`, `TimeProvider`, `IEditorIdentity`). Сервисы — лёгкие
//! обёртки над `&Workspace` ([`Workspace::tasks`], [`Workspace::series`] и т. д.).
//!
//! Хранилища (`*Storage` в .NET) здесь — методы [`Workspace`] над общим [`storage::Entity`]: одна сущность читается из файла,
//! списки и подсчёты идут из индекса, запись — через [`tasker_files::write`] под `write.lock` и с обновлением индекса
//! (`written`), секции записи проекта — [`Workspace::exclusive`].
//!
//! Правило переноса: поведение .NET воспроизводится буквально (тексты, коды ошибок, порядок захвата блокировок, число попыток
//! каскадов). Расхождения — в `README.md` крейта и в отчёте задачи.

pub mod board;
pub mod cascade;
pub mod cleanup;
pub mod clock;
pub mod column_filters;
pub mod error;
pub mod field;
pub mod field_conversion;
pub mod field_enum;
pub mod health;
pub mod hierarchy;
pub mod link_cycles;
pub mod link_type;
pub mod links;
pub mod locks;
pub mod preview;
pub mod project;
pub mod rewrites;
pub mod series;
pub mod status;
pub mod status_set;
pub mod storage;
pub mod task;
pub mod task_fields;
pub mod task_filters;
pub mod task_type;
pub mod usages;
pub mod users;

pub use cascade::CascadeResult;
pub use clock::{Clock, FakeClock, SystemClock};
pub use error::{Error, Result};

use std::path::Path;
use std::sync::Arc;
use std::time::Duration;
use tasker_core::locks::EditHolder;
use tasker_files::edit_locks::EditLockStorage;
use tasker_files::index::WorkspaceIndex;
use tasker_files::layout::TaskerDirectory;
use uuid::Uuid;

/// Рабочая область в файловом режиме: всё, что нужно сервисам.
#[derive(Clone)]
pub struct Workspace {
    directory: TaskerDirectory,
    index: WorkspaceIndex,
    clock: Arc<dyn Clock>,
    editor: EditHolder,
    cascade_timeout: Duration,
}

impl Workspace {
    /// Открывает папку: создаёт `projects/`, `users/`, `.gitignore` (как `FileStorageModule`) и индекс со сверкой файлов
    /// (`WorkspaceIndex::open`). Держатель блокировок — консольный (`cli`, «Local user (console)»), часы системные.
    pub fn open(folder: impl AsRef<Path>) -> std::io::Result<Workspace> {
        let directory = TaskerDirectory::new(folder);
        directory.ensure_created()?;
        let index = WorkspaceIndex::open(&directory)?;
        Ok(Workspace {
            directory,
            index,
            clock: Arc::new(SystemClock),
            editor: EditHolder::new(tasker_core::locks::LOCAL_KEY, tasker_core::locks::LOCAL_NAME),
            cascade_timeout: locks::CASCADE_WAIT,
        })
    }

    /// Та же папка, другой держатель блокировок (другой пользователь или клиент).
    pub fn with_editor(mut self, editor: EditHolder) -> Workspace {
        self.editor = editor;
        self
    }

    /// Часы: тесты подставляют [`FakeClock`].
    pub fn with_clock(mut self, clock: Arc<dyn Clock>) -> Workspace {
        self.clock = clock;
        self
    }

    /// Сколько массовая правка ждёт чужие блокировки (`EditLockService.CascadeTimeout`).
    pub fn with_cascade_timeout(mut self, timeout: Duration) -> Workspace {
        self.cascade_timeout = timeout;
        self
    }

    pub fn directory(&self) -> &TaskerDirectory {
        &self.directory
    }

    pub fn index(&self) -> &WorkspaceIndex {
        &self.index
    }

    pub fn clock(&self) -> &dyn Clock {
        self.clock.as_ref()
    }

    pub fn now(&self) -> tasker_core::Timestamp {
        self.clock.now()
    }

    pub fn editor(&self) -> &EditHolder {
        &self.editor
    }

    pub fn cascade_timeout(&self) -> Duration {
        self.cascade_timeout
    }

    pub(crate) fn edit_locks(&self) -> EditLockStorage<'_> {
        EditLockStorage::new(&self.directory)
    }

    /// Атомарная секция записи проекта (`IWriteScope.Exclusive`): `write.lock` между процессами плюс сверка папки задач проекта
    /// перед действием. Вложенный вызов не ждёт самого себя.
    pub fn exclusive<T>(&self, project_id: &Uuid, action: impl FnOnce() -> Result<T>) -> Result<T> {
        self.index.exclusive(project_id, || Ok(action()))?
    }

    pub fn projects(&self) -> project::ProjectService<'_> {
        project::ProjectService::new(self)
    }

    pub fn statuses(&self) -> status::StatusService<'_> {
        status::StatusService::new(self)
    }

    pub fn status_sets(&self) -> status_set::StatusSetService<'_> {
        status_set::StatusSetService::new(self)
    }

    pub fn task_types(&self) -> task_type::TaskTypeService<'_> {
        task_type::TaskTypeService::new(self)
    }

    pub fn fields(&self) -> field::FieldService<'_> {
        field::FieldService::new(self)
    }

    pub fn enums(&self) -> field_enum::FieldEnumService<'_> {
        field_enum::FieldEnumService::new(self)
    }

    pub fn boards(&self) -> board::BoardService<'_> {
        board::BoardService::new(self)
    }

    pub fn tasks(&self) -> task::TaskService<'_> {
        task::TaskService::new(self)
    }

    pub fn series(&self) -> series::SeriesService<'_> {
        series::SeriesService::new(self)
    }

    pub fn links(&self) -> links::TaskLinkService<'_> {
        links::TaskLinkService::new(self)
    }

    pub fn link_types(&self) -> link_type::LinkTypeService<'_> {
        link_type::LinkTypeService::new(self)
    }

    pub fn locks(&self) -> locks::EditLockService<'_> {
        locks::EditLockService::new(self)
    }

    pub fn entity_locks(&self) -> locks::EntityLockService<'_> {
        locks::EntityLockService::new(self)
    }

    pub fn cleanup(&self) -> cleanup::CleanupService<'_> {
        cleanup::CleanupService::new(self)
    }

    pub fn series_health(&self) -> health::SeriesHealthService<'_> {
        health::SeriesHealthService::new(self)
    }

    pub fn link_health(&self) -> health::LinkHealthService<'_> {
        health::LinkHealthService::new(self)
    }

    pub fn users(&self) -> users::UserService<'_> {
        users::UserService::new(self)
    }
}
