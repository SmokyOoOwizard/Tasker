//! Хранилище области-папки (`Tasker.Storage.Files`): сущности проекта поверх файлов `.tasker` и индекса (`EntityFiles<T>` и
//! `*Storage`): одна сущность — из файла (сначала там, где его знает индекс, потом по имени — старому Guid и новому суффиксу id;
//! совпадение id проверяется по содержимому), списки и подсчёты — из индекса. Запись — [`tasker_files::write`] под блокировкой
//! записи, после неё индекс догоняет файлы (`written`). Смена названия переименовывает файл; файл с новым именем уже есть —
//! запись отклоняется. Пользователи — `users/<id>.yaml`, блокировки правки — `.cache/edit-locks/*.json`, секция записи —
//! `write.lock` со сверкой папки задач проекта.
use crate::error::{Error, Result};
use crate::storage::{Entity, Storage};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use tasker_core::Timestamp;
use tasker_core::ids::guid_d;
use tasker_core::locks::{EditHolder, EditLock, LockedEntity};
use tasker_core::model::{Project, TaskItem, User, UserKind};
use tasker_core::tasks::{ListPage, Page, TaskFilter};
use tasker_files::edit_locks::EditLockStorage;
use tasker_files::files;
use tasker_files::index::{IndexQuery, LinkEdge, NumberConflict, OwnFieldKind, WorkspaceIndex, normalize_username};
use tasker_files::layout::{EntityKind, TaskerDirectory};
use tasker_files::write;
use uuid::Uuid;

/// Сколько раз искать файл сущности заново, если найденный по имени файл пропал до чтения (его переименовал или удалил другой
/// процесс).
const RESOLVE_ATTEMPTS: usize = 5;

/// Прочитанный файл-кандидат сущности.
enum Candidate<T> {
    /// Прочитан; None — файл пуст.
    Read(Option<T>),
    /// Файла (уже) нет.
    Missing,
}

/// Папка `.tasker` и её индекс.
#[derive(Debug, Clone)]
pub struct FileStore {
    directory: TaskerDirectory,
    index: WorkspaceIndex,
}

impl FileStore {
    pub fn new(directory: TaskerDirectory, index: WorkspaceIndex) -> FileStore {
        FileStore { directory, index }
    }

    pub fn directory(&self) -> &TaskerDirectory {
        &self.directory
    }

    pub fn index(&self) -> &WorkspaceIndex {
        &self.index
    }

    fn edit_locks(&self) -> EditLockStorage<'_> {
        EditLockStorage::new(&self.directory)
    }

    /// Файл-кандидат: прочитан (None — пуст) или пропал.
    fn read_candidate<T: Entity>(&self, project_id: &Uuid, path: &Path) -> Result<Candidate<T>> {
        #[cfg(test)]
        hooks::before_read(path);
        let Some(bytes) = write::read_bytes(path)? else {
            return Ok(Candidate::Missing);
        };
        match T::parse(*project_id, &bytes, path) {
            Ok(versioned) => Ok(Candidate::Read(Some(versioned.model))),
            Err(tasker_files::Error::Yaml(message)) if message.ends_with(": the file is empty") => Ok(Candidate::Read(None)),
            Err(e) => Err(e.into()),
        }
    }

    /// Файл сущности по id (`EntityFiles.ResolvePath`): сначала там, где его знает индекс, затем поиск по имени — старому Guid
    /// и суффиксу id (`ProjectDirectory.FindFiles`); совпадение id проверяется по содержимому. None — сущности нет.
    ///
    /// Путь из индекса читается до поиска по имени, как в .NET: если другой процесс переименовал файл, пока шёл запрос, поиск
    /// по имени уже видит новый файл (переименование пишет новый файл раньше, чем удаляет старый). Найденный по имени файл тоже
    /// может пропасть до чтения — тогда поиск повторяется: «нет сущности» — только если её действительно нет.
    fn resolve_path<T: Entity>(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<(PathBuf, T)>> {
        for _ in 0..RESOLVE_ATTEMPTS {
            let indexed = self.index.entity_path(T::KIND, project_id, id)?;
            if let Some(indexed) = &indexed
                && let Candidate::Read(Some(entity)) = self.read_candidate::<T>(project_id, indexed)?
                && entity.id() == *id
            {
                return Ok(Some((indexed.clone(), entity)));
            }
            let mut vanished = false;
            for path in self.directory.project(project_id).find_files(T::FOLDER, id) {
                if indexed
                    .as_ref()
                    .is_some_and(|i| tasker_core::io::same_path(&path.to_string_lossy(), &i.to_string_lossy()))
                {
                    continue;
                }
                match self.read_candidate::<T>(project_id, &path)? {
                    Candidate::Read(Some(entity)) if entity.id() == *id => return Ok(Some((path, entity))),
                    Candidate::Read(_) => {}
                    Candidate::Missing => vanished = true,
                }
            }
            if !vanished {
                break;
            }
        }
        Ok(None)
    }

    /// Ошибка чтения нечитаемого файла вида `kind`, который по имени может быть сущностью с id по ключу
    /// ([`WorkspaceIndex::unreadable_paths`]): файл читается заново, `parse` даёт ту же ошибку, что чтение по полному id. Файл
    /// пропал, пуст или теперь читается — не ошибка.
    fn read_error(
        &self,
        kind: EntityKind,
        project_id: Option<&Uuid>,
        key: &str,
        parse: impl Fn(&[u8], &Path) -> tasker_files::Result<()>,
    ) -> Result<()> {
        for path in self.index.unreadable_paths(kind, project_id, key)? {
            let Some(bytes) = write::read_bytes(&path)? else {
                continue;
            };
            match parse(&bytes, &path) {
                Ok(()) => {}
                Err(tasker_files::Error::Yaml(message)) if message.ends_with(": the file is empty") => {}
                Err(e) => return Err(e.into()),
            }
        }
        Ok(())
    }

    /// Имя уже занято другим пользователем — `Username '…' is already taken` (проверка файлового хранилища под блокировкой).
    fn ensure_unique(&self, user: &User) -> Result<()> {
        if let Some(taken) = self.user_by_username(&user.username)?
            && taken.id != user.id
        {
            return Err(Error::in_use(format!("Username '{}' is already taken", user.username)));
        }
        Ok(())
    }
}

impl Storage for FileStore {
    /// Одна сущность по id — из файла; None — нет.
    fn get_by_id<T: Entity>(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<T>> {
        Ok(self.resolve_path::<T>(project_id, id)?.map(|(_, entity)| entity))
    }

    /// Все сущности вида в проекте — из индекса, по имени (у серий — по префиксу), затем по id.
    fn get_all<T: Entity>(&self, project_id: &Uuid) -> Result<Vec<T>> {
        Ok(self.index.all::<T>(&IndexQuery::project(project_id))?)
    }

    /// Страница сущностей вида (`GetRange`).
    fn get_range<T: Entity>(&self, project_id: &Uuid, page: Page) -> Result<ListPage<T>> {
        Ok(self.index.range::<T>(&IndexQuery::project(project_id), page)?)
    }

    /// Сколько файлов этого вида в проекте не удалось прочитать (`CountUnreadable`).
    fn count_unreadable<T: Entity>(&self, project_id: &Uuid) -> Result<usize> {
        Ok(self.index().count_problems(&self.directory.project(project_id).folder(T::FOLDER))?)
    }

    fn unreadable<T: Entity>(&self, project_id: &Uuid, key: &str) -> Result<()> {
        self.read_error(T::KIND, Some(project_id), key, |bytes, path| {
            T::parse(*project_id, bytes, path).map(|_| ())
        })
    }

    fn unreadable_project(&self, key: &str) -> Result<()> {
        self.read_error(EntityKind::Project, None, key, |bytes, path| {
            files::project::parse(bytes, path).map(|_| ())
        })
    }

    fn unreadable_user(&self, key: &str) -> Result<()> {
        self.read_error(EntityKind::User, None, key, |bytes, path| {
            files::user::parse(bytes, path).map(|_| ())
        })
    }

    /// Новый файл под именем по названию. Возвращает версию.
    ///
    /// Проект удаляют под блокировкой записи, и запись файла сама создаёт недостающие папки: без проверки под той же блокировкой
    /// «поздний» писатель воскресил бы папку удалённого проекта с одним своим файлом (`EntityFiles.Add`).
    fn add<T: Entity>(&self, entity: &T) -> Result<String> {
        let project_id = entity.project_id();
        let project = self.directory.project(&project_id);
        let path = project.entity_file(T::FOLDER, &entity.id(), Some(entity.file_name_source()));
        let bytes = entity.to_bytes();
        let result: std::io::Result<Result<String>> = self.index.written(std::slice::from_ref(&path), || {
            write::locked(project.root(), || {
                if self.directory.project_accepts_writes(&project_id) {
                    Ok(write::write(&path, &bytes).map_err(Error::from))
                } else {
                    Ok(Err(Error::validation(format!(
                        "ProjectId: project not found: {}",
                        guid_d(&project_id)
                    ))))
                }
            })
        });
        result?
    }

    /// Перезапись по версии с переименованием, если название изменилось. None — файла нет или он изменён.
    fn update<T: Entity>(&self, entity: &T, expected_version: &str) -> Result<Option<String>> {
        let project_id = entity.project_id();
        let Some((current, _)) = self.resolve_path::<T>(&project_id, &entity.id())? else {
            return Ok(None);
        };
        let renamed = self
            .directory()
            .project(&project_id)
            .entity_file(T::FOLDER, &entity.id(), Some(entity.file_name_source()));
        let bytes = entity.to_bytes();
        Ok(self.index.written(&[current.clone(), renamed.clone()], || {
            write::write_if_match_renaming(&current, &renamed, &bytes, expected_version)
        })?)
    }

    /// Удаление по версии; false — файла нет или он изменён.
    fn delete<T: Entity>(&self, project_id: &Uuid, id: &Uuid, expected_version: &str) -> Result<bool> {
        let Some((path, _)) = self.resolve_path::<T>(project_id, id)? else {
            return Ok(false);
        };
        Ok(self
            .index()
            .written(std::slice::from_ref(&path), || write::delete_if_match(&path, expected_version))?)
    }

    // ---- проекты: project.yaml в папке проекта ----

    fn get_project(&self, id: &Uuid) -> Result<Option<Project>> {
        let path = self.directory.project(id).project_file();
        let Some(bytes) = write::read_bytes(&path)? else {
            return Ok(None);
        };
        match files::project::parse(&bytes, &path) {
            Ok(versioned) => Ok(Some(versioned.model)),
            Err(tasker_files::Error::Yaml(message)) if message.ends_with(": the file is empty") => Ok(None),
            Err(e) => Err(e.into()),
        }
    }

    /// Страница проектов по имени; `ids` — только эти (None — все).
    fn project_range(&self, ids: Option<&[Uuid]>, page: Page) -> Result<ListPage<Project>> {
        let query = IndexQuery {
            ids: ids.map(|x| x.to_vec()),
            ..IndexQuery::default()
        };
        Ok(self.index.range::<Project>(&query, page)?)
    }

    fn add_project(&self, project: &Project) -> Result<String> {
        let path = self.directory.project(&project.id).project_file();
        let bytes = files::project::serialize(project);
        Ok(self.index.written(std::slice::from_ref(&path), || write::write(&path, &bytes))?)
    }

    fn update_project(&self, project: &Project, expected_version: &str) -> Result<Option<String>> {
        let path = self.directory.project(&project.id).project_file();
        let bytes = files::project::serialize(project);
        Ok(self.index.written(std::slice::from_ref(&path), || {
            write::write_if_match(&path, &bytes, expected_version)
        })?)
    }

    /// Папка проекта удаляется целиком, если `project.yaml` не изменился с ожидаемой версии; ставится метка удаления.
    fn delete_project(&self, id: &Uuid, expected_version: &str) -> Result<bool> {
        let project = self.directory.project(id);
        let root = project.root().to_path_buf();
        let file = project.project_file();
        Ok(self.index.written(std::slice::from_ref(&root), || {
            write::delete_if_match_with(&file, expected_version, || {
                std::fs::remove_dir_all(&root)?;
                self.directory.mark_project_deleted(id)
            })
        })?)
    }

    fn all_projects(&self) -> Result<Vec<Project>> {
        Ok(self.index.all::<Project>(&IndexQuery::default())?)
    }

    // ---- задачи: запросы к индексу ----

    fn tasks_where(&self, project_id: &Uuid, filter: Option<&TaskFilter>) -> Result<Vec<TaskItem>> {
        Ok(self.index.all::<TaskItem>(&IndexQuery::tasks(project_id, filter))?)
    }

    fn task_range(&self, project_id: &Uuid, filter: Option<&TaskFilter>, page: Page) -> Result<ListPage<TaskItem>> {
        Ok(self.index.range::<TaskItem>(&IndexQuery::tasks(project_id, filter), page)?)
    }

    fn task_ids(&self, project_id: &Uuid, filter: Option<&TaskFilter>) -> Result<Vec<Uuid>> {
        Ok(self.index.ids(EntityKind::Task, &IndexQuery::tasks(project_id, filter))?)
    }

    fn count_tasks(&self, project_id: &Uuid, filter: &TaskFilter) -> Result<usize> {
        Ok(self.index.count(EntityKind::Task, &IndexQuery::tasks(project_id, Some(filter)))?)
    }

    fn tasks_by_id_prefix(&self, project_id: &Uuid, key: &str) -> Result<Vec<TaskItem>> {
        Ok(self.index.all(&IndexQuery::project(project_id).with_id_prefix(key))?)
    }

    fn own_field_kinds(&self, project_id: &Uuid, name_key: &str) -> Result<Vec<OwnFieldKind>> {
        Ok(self.index.own_field_kinds(project_id, name_key)?)
    }

    fn max_number(&self, project_id: &Uuid, series_id: &Uuid) -> Result<i32> {
        Ok(self.index.max_number(project_id, series_id)?)
    }

    fn tasks_by_number(&self, project_id: &Uuid, series_id: &Uuid, number: i32) -> Result<Vec<TaskItem>> {
        Ok(self.index.tasks_by_number(project_id, series_id, number)?)
    }

    fn number_conflicts(&self, project_id: &Uuid) -> Result<Vec<NumberConflict>> {
        Ok(self.index.number_conflicts(project_id)?)
    }

    fn tasks_with_series_not_in(&self, project_id: &Uuid, known_series_ids: &[Uuid]) -> Result<Vec<TaskItem>> {
        Ok(self.index.tasks_with_series_not_in(project_id, known_series_ids)?)
    }

    fn tasks_linked_to(&self, project_id: &Uuid, target_id: &Uuid) -> Result<Vec<TaskItem>> {
        Ok(self.index.tasks_linked_to(project_id, target_id)?)
    }

    fn link_targets(&self, project_id: &Uuid, type_id: &Uuid, source_ids: &[Uuid]) -> Result<HashMap<Uuid, Vec<Uuid>>> {
        Ok(self.index.link_targets(project_id, type_id, source_ids)?)
    }

    fn link_edges(&self, project_id: &Uuid, type_id: &Uuid) -> Result<Vec<LinkEdge>> {
        Ok(self.index.link_edges(project_id, type_id)?)
    }

    fn count_linked_to(&self, project_id: &Uuid, target_ids: &[Uuid]) -> Result<HashMap<Uuid, usize>> {
        Ok(self.index.count_linked_to(project_id, target_ids)?)
    }

    fn tasks_with_invalid_links(&self, project_id: &Uuid, known_type_ids: &[Uuid]) -> Result<Vec<TaskItem>> {
        Ok(self.index.tasks_with_invalid_links(project_id, known_type_ids)?)
    }

    // ---- пользователи: users/<id>.yaml ----

    fn get_user(&self, id: &Uuid) -> Result<Option<User>> {
        let path = self.directory.user_file(id);
        let Some(bytes) = write::read_bytes(&path)? else {
            return Ok(None);
        };
        match files::user::parse(&bytes, &path) {
            Ok(versioned) => Ok(Some(versioned.model)),
            Err(tasker_files::Error::Yaml(message)) if message.ends_with(": the file is empty") => Ok(None),
            Err(e) => Err(e.into()),
        }
    }

    fn user_by_username(&self, username: &str) -> Result<Option<User>> {
        Ok(self
            .index
            .first::<User>(&IndexQuery::users(None).with_sort_key(&normalize_username(username)))?)
    }

    fn user_range(&self, kind: Option<UserKind>, page: Page) -> Result<ListPage<User>> {
        Ok(self.index.range::<User>(&IndexQuery::users(kind), page)?)
    }

    fn all_users(&self, kind: Option<UserKind>) -> Result<Vec<User>> {
        Ok(self.index.all::<User>(&IndexQuery::users(kind))?)
    }

    // Проверка уникальности и запись — под одной блокировкой каталога пользователей (`UserStorage.Add` файлового хранилища).
    fn add_user(&self, user: &User) -> Result<String> {
        let path = self.directory.user_file(&user.id);
        let bytes = files::user::serialize(user);
        let version: Result<String> = write::locked(&self.directory.users(), || {
            Ok(self
                .ensure_unique(user)
                .and_then(|()| Ok(self.index.written(std::slice::from_ref(&path), || write::write(&path, &bytes))?)))
        })?;
        version
    }

    fn update_user(&self, user: &User, expected_version: &str) -> Result<Option<String>> {
        let path = self.directory.user_file(&user.id);
        let bytes = files::user::serialize(user);
        let version: Result<Option<String>> = write::locked(&self.directory.users(), || {
            Ok(self.ensure_unique(user).and_then(|()| {
                Ok(self.index.written(std::slice::from_ref(&path), || {
                    write::write_if_match(&path, &bytes, expected_version)
                })?)
            }))
        })?;
        version
    }

    fn delete_user(&self, id: &Uuid, expected_version: &str) -> Result<bool> {
        let path = self.directory.user_file(id);
        Ok(self
            .index
            .written(std::slice::from_ref(&path), || write::delete_if_match(&path, expected_version))?)
    }

    // ---- блокировки правки: .cache/edit-locks ----

    fn lock_get(&self, entity: LockedEntity, id: &Uuid, now: &Timestamp) -> Result<Option<EditLock>> {
        Ok(self.edit_locks().get(entity, id, now))
    }

    fn locks_by_project(&self, project_id: &Uuid, now: &Timestamp) -> Result<Vec<EditLock>> {
        Ok(self.edit_locks().get_by_project(project_id, now))
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
        Ok(self.edit_locks().acquire(entity, id, holder, now, expires_at, project_id)?)
    }

    fn lock_release(&self, entity: LockedEntity, id: &Uuid, holder_key: &str) -> Result<bool> {
        Ok(self.edit_locks().release(entity, id, holder_key)?)
    }

    fn lock_remove(&self, entity: LockedEntity, id: &Uuid) -> Result<()> {
        Ok(self.edit_locks().remove(entity, id)?)
    }

    /// `write.lock` между процессами плюс сверка папки задач проекта перед действием: номер серии (`max_number`) внутри действия
    /// учитывает и файлы, принесённые `git pull`. Вложенный вызов не ждёт самого себя.
    fn exclusive<T>(&self, project_id: &Uuid, action: &mut dyn FnMut() -> Result<T>) -> Result<T> {
        self.index.exclusive(project_id, || Ok(action()))?
    }
}

/// Крючок тестов: действие перед чтением файла-кандидата сущности — чтобы «другой процесс» изменил файл ровно между поиском
/// пути и чтением, без расчёта на тайминги.
#[cfg(test)]
mod hooks {
    use std::cell::RefCell;
    use std::path::Path;

    type Hook = Box<dyn FnMut(&Path)>;

    thread_local! {
        static BEFORE_READ: RefCell<Option<Hook>> = const { RefCell::new(None) };
    }

    pub(super) fn set_before_read(hook: impl FnMut(&Path) + 'static) {
        BEFORE_READ.with(|h| *h.borrow_mut() = Some(Box::new(hook)));
    }

    pub(super) fn clear() {
        BEFORE_READ.with(|h| *h.borrow_mut() = None);
    }

    /// Пока крючок работает, его нет: записи внутри него не зовут его снова.
    pub(super) fn before_read(path: &Path) {
        let Some(mut hook) = BEFORE_READ.with(|h| h.borrow_mut().take()) else {
            return;
        };
        hook(path);
        BEFORE_READ.with(|h| {
            let mut slot = h.borrow_mut();
            if slot.is_none() {
                *slot = Some(hook);
            }
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Workspace;
    use crate::project::CreateProject;
    use crate::status::{CreateStatus, UpdateStatus};
    use crate::status_set::CreateStatusSet;
    use crate::task::{CreateTask, UpdateTask};
    use crate::task_type::CreateTaskType;
    use tasker_core::ConflictCode;
    use tasker_core::model::Status;

    /// Две открытые области над одной папкой во временном каталоге системы — два процесса.
    struct Two {
        dir: PathBuf,
        first: Workspace,
        second: Workspace,
        project: Uuid,
    }

    impl Two {
        fn new() -> Two {
            let dir = std::env::temp_dir().join(format!("tasker-tsk166-{}", Uuid::new_v4().simple()));
            std::fs::create_dir_all(&dir).unwrap();
            let first = Workspace::open(&dir).unwrap();
            let second = Workspace::open(&dir).unwrap();
            let project = first.projects().create(&CreateProject { name: "Test".into() }).unwrap().id;
            Two {
                dir,
                first,
                second,
                project,
            }
        }

        fn status(&self, name: &str) -> Status {
            self.first
                .statuses()
                .create(
                    &self.project,
                    &CreateStatus {
                        name: name.into(),
                        color: "#000000".into(),
                        description: None,
                    },
                )
                .unwrap()
        }

        fn file_name(&self, path: &Path) -> String {
            path.strip_prefix(&self.dir)
                .unwrap()
                .file_name()
                .unwrap()
                .to_string_lossy()
                .into_owned()
        }

        /// Перед первым чтением файла `target` второй процесс переименовывает сущность (`rename`).
        fn rename_before_read(&self, target: PathBuf, rename: impl FnOnce(&Workspace) + 'static) {
            let second = self.second.clone();
            let mut rename = Some(rename);
            hooks::set_before_read(move |path| {
                if tasker_core::io::same_path(&path.to_string_lossy(), &target.to_string_lossy())
                    && let Some(rename) = rename.take()
                {
                    rename(&second);
                }
            });
        }
    }

    impl Drop for Two {
        fn drop(&mut self) {
            hooks::clear();
            let _ = std::fs::remove_dir_all(&self.dir);
        }
    }

    fn rename_status(project: Uuid, id: Uuid, version: String, name: &'static str) -> impl FnOnce(&Workspace) {
        move |ws: &Workspace| {
            ws.statuses()
                .update(
                    &project,
                    &id,
                    &UpdateStatus {
                        name: Some(name.into()),
                        version: Some(version),
                        ..UpdateStatus::default()
                    },
                )
                .unwrap()
                .unwrap();
        }
    }

    fn status_file(two: &Two, id: &Uuid) -> PathBuf {
        two.first
            .index()
            .entity_path(EntityKind::Status, &two.project, id)
            .unwrap()
            .unwrap()
    }

    #[test]
    fn updating_an_entity_renamed_by_another_process_is_a_version_conflict() {
        let two = Two::new();
        let status = two.status("Old");
        let old_file = status_file(&two, &status.id);
        two.rename_before_read(
            old_file.clone(),
            rename_status(two.project, status.id, status.version.clone(), "First"),
        );

        let error = two
            .first
            .statuses()
            .update(
                &two.project,
                &status.id,
                &UpdateStatus {
                    name: Some("Second".into()),
                    version: Some(status.version.clone()),
                    ..UpdateStatus::default()
                },
            )
            .unwrap_err();
        assert_eq!(error.conflict_code(), Some(ConflictCode::Modified), "{error}");
        assert!(!old_file.exists());
        let current = two.first.statuses().get_by_id(&two.project, &status.id).unwrap().unwrap();
        assert_eq!(current.name, "First");
        assert_eq!(
            two.file_name(&status_file(&two, &status.id)),
            format!("first-{}.yaml", &guid_d(&status.id)[..8])
        );
    }

    #[test]
    fn deleting_an_entity_renamed_by_another_process_is_a_version_conflict() {
        let two = Two::new();
        let status = two.status("Old");
        two.rename_before_read(
            status_file(&two, &status.id),
            rename_status(two.project, status.id, status.version.clone(), "First"),
        );

        let error = two
            .first
            .statuses()
            .delete(&two.project, &status.id, Some(&status.version))
            .unwrap_err();
        assert_eq!(error.conflict_code(), Some(ConflictCode::Modified), "{error}");
        assert!(two.first.statuses().get_by_id(&two.project, &status.id).unwrap().is_some());
    }

    #[test]
    fn a_task_renamed_by_another_process_is_found_by_its_id() {
        let two = Two::new();
        let status = two.status("Backlog");
        let set = two
            .first
            .status_sets()
            .create(
                &two.project,
                &CreateStatusSet {
                    name: "Main".into(),
                    status_ids: vec![status.id],
                },
            )
            .unwrap();
        let task_type = two
            .first
            .task_types()
            .create(
                &two.project,
                &CreateTaskType {
                    name: "Task".into(),
                    status_set_id: set.id,
                    fields: None,
                    description: None,
                },
            )
            .unwrap();
        let task = two
            .first
            .tasks()
            .create(&two.project, &CreateTask::new("Old title", task_type.id))
            .unwrap();
        let file = two
            .first
            .index()
            .entity_path(EntityKind::Task, &two.project, &task.id)
            .unwrap()
            .unwrap();
        let (project, id, version) = (two.project, task.id, task.version.clone());
        two.rename_before_read(file, move |ws| {
            ws.tasks()
                .update(
                    &project,
                    &id,
                    &UpdateTask {
                        title: Some("First".into()),
                        version: Some(version),
                        ..UpdateTask::default()
                    },
                )
                .unwrap()
                .unwrap();
        });

        let error = two
            .first
            .tasks()
            .update(
                &two.project,
                &task.id,
                &UpdateTask {
                    title: Some("Second".into()),
                    version: Some(task.version.clone()),
                    ..UpdateTask::default()
                },
            )
            .unwrap_err();
        assert_eq!(error.conflict_code(), Some(ConflictCode::Modified), "{error}");
    }

    #[test]
    fn a_file_found_by_name_and_renamed_before_reading_is_searched_again() {
        // Индекс не знает, где файл: он переложен мимо Tasker, поэтому его находит только поиск по имени, а до чтения его
        // переименовывает второй процесс.
        let two = Two::new();
        let status = two.status("Old");
        let indexed = status_file(&two, &status.id);
        let moved = indexed.with_file_name(format!("moved-{}.yaml", &guid_d(&status.id)[..8]));
        std::fs::rename(&indexed, &moved).unwrap();
        two.rename_before_read(
            moved.clone(),
            rename_status(two.project, status.id, status.version.clone(), "First"),
        );

        let files = two.first.files().unwrap();
        let found: Status = files.get_by_id(&two.project, &status.id).unwrap().unwrap();
        assert_eq!(found.name, "First");
        assert!(!moved.exists());
        // Сущности нет — «нет», без бесконечных повторов.
        assert!(files.get_by_id::<Status>(&two.project, &Uuid::new_v4()).unwrap().is_none());
    }
}
