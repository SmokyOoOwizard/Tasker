//! Сервисы домена Tasker на Rust (TSK-132, фаза 2 плана `docs/rust-migration-plan.md`, п. 3): те же операции, проверки, тексты
//! ошибок и каскады, что у `Tasker.Core.*Service` (.NET), поверх хранилища области: папки `.tasker` (файлы и индекс,
//! [`files_store::FileStore`]) или файла SQLite (та же схема, что у EF Core в `Tasker.Storage.Db`, [`db::DbStore`], TSK-159).
//! Сервисы синхронные: в домене нет async.
//!
//! Точка входа — [`Workspace`]: хранилище ([`storage::Backend`]), часы ([`Clock`]) и держатель блокировок правки ([`EditHolder`]) —
//! то, что в .NET приходит через контейнер (`I*Storage`, `IWriteScope`, `TimeProvider`, `IEditorIdentity`). Сервисы — лёгкие
//! обёртки над `&Workspace` ([`Workspace::tasks`], [`Workspace::series`] и т. д.).
//!
//! Хранилища (`*Storage` в .NET) — трейт [`storage::Storage`]: сервисы зовут его через [`Workspace`] (`Deref`), не зная, файлы
//! это или БД; секции записи проекта — [`Workspace::exclusive`].
//!
//! Правило переноса: поведение .NET воспроизводится буквально (тексты, коды ошибок, порядок захвата блокировок, число попыток
//! каскадов). Расхождения — в `README.md` крейта и в отчёте задачи.

pub mod board;
pub mod cascade;
pub mod cleanup;
pub mod clock;
pub mod column_filters;
pub mod db;
pub mod error;
pub mod field;
pub mod field_conversion;
pub mod field_enum;
pub mod files_store;
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
pub use storage::Storage;

use std::path::Path;
use std::sync::Arc;
use std::time::Duration;
use storage::Backend;
use tasker_core::locks::EditHolder;
use tasker_files::index::WorkspaceIndex;
use tasker_files::layout::TaskerDirectory;
use uuid::Uuid;

/// Рабочая область: хранилище и всё, что нужно сервисам.
#[derive(Clone)]
pub struct Workspace {
    backend: Backend,
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
        tasker_core::perf::mark("workspace-dirs");
        let index = WorkspaceIndex::open(&directory)?;
        Ok(Self::with_backend(Backend::Files(files_store::FileStore::new(directory, index))))
    }

    /// Открывает файл SQLite: создаёт его со схемой EF Core, если файла нет (`DbInitializer`), и дописывает недостающие столбцы.
    pub fn open_sqlite(file: impl AsRef<Path>) -> std::io::Result<Workspace> {
        Ok(Self::with_backend(Backend::Db(db::DbStore::open(file.as_ref())?)))
    }

    fn with_backend(backend: Backend) -> Workspace {
        Workspace {
            backend,
            clock: Arc::new(SystemClock),
            editor: EditHolder::new(tasker_core::locks::LOCAL_KEY, tasker_core::locks::LOCAL_NAME),
            cascade_timeout: locks::CASCADE_WAIT,
        }
    }

    /// Открывает папку с уже существующей областью, ничего не создавая, кроме индекса (автодополнение и справка консоли:
    /// `Session.Open(forCompletion: true)` в .NET).
    pub fn attach(folder: impl AsRef<Path>) -> std::io::Result<Workspace> {
        let directory = TaskerDirectory::new(folder);
        let index = WorkspaceIndex::open(&directory)?;
        Ok(Self::with_backend(Backend::Files(files_store::FileStore::new(directory, index))))
    }

    /// Открывает существующий файл SQLite для чтения (автодополнение): файла нет — ошибка; схема не создаётся.
    pub fn attach_sqlite(file: impl AsRef<Path>) -> std::io::Result<Workspace> {
        Ok(Self::with_backend(Backend::Db(db::DbStore::attach(file.as_ref())?)))
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

    pub fn backend(&self) -> &Backend {
        &self.backend
    }

    /// Хранилище папки; None — область в файле SQLite.
    pub fn files(&self) -> Option<&files_store::FileStore> {
        match &self.backend {
            Backend::Files(files) => Some(files),
            Backend::Db(_) => None,
        }
    }

    /// Папка `.tasker` — только у области-папки (панику у SQLite ловят тесты: вызывающий проверяет [`Workspace::files`]).
    pub fn directory(&self) -> &TaskerDirectory {
        self.files().expect("a folder workspace").directory()
    }

    /// Индекс — только у области-папки.
    pub fn index(&self) -> &WorkspaceIndex {
        self.files().expect("a folder workspace").index()
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

    /// Атомарная секция записи проекта (`IWriteScope.Exclusive`): у папки — `write.lock` между процессами плюс сверка папки задач
    /// проекта перед действием, у БД — транзакция, которая при гонке повторяется целиком (поэтому действие — `FnMut`). Вложенный
    /// вызов выполняется внутри внешнего.
    pub fn exclusive<T>(&self, project_id: &Uuid, mut action: impl FnMut() -> Result<T>) -> Result<T> {
        self.backend.exclusive(project_id, &mut action)
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

    pub fn agents(&self) -> users::AgentService<'_> {
        users::AgentService::new(self)
    }
}

/// Операции хранилища ([`Storage`]) прямо у области: `ws.get_by_id::<Status>(…)`, `ws.tasks(…)`.
impl std::ops::Deref for Workspace {
    type Target = Backend;

    fn deref(&self) -> &Backend {
        &self.backend
    }
}
