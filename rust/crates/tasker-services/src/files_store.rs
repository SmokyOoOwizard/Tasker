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

    /// Файл сущности по пути; None — файла нет или он пуст (`YamlFile.Read`). Ошибки разбора — как в .NET: исключение.
    fn read_entity<T: Entity>(&self, project_id: &Uuid, path: &Path) -> Result<Option<T>> {
        let Some(bytes) = write::read_bytes(path)? else {
            return Ok(None);
        };
        match T::parse(*project_id, &bytes, path) {
            Ok(versioned) => Ok(Some(versioned.model)),
            Err(tasker_files::Error::Yaml(message)) if message.ends_with(": the file is empty") => Ok(None),
            Err(e) => Err(e.into()),
        }
    }

    /// Где может лежать файл: сначала то, что знает индекс, затем поиск по имени (`EntityFiles.Candidates`).
    fn candidates<T: Entity>(&self, project_id: &Uuid, id: &Uuid) -> Result<Vec<PathBuf>> {
        let mut result = Vec::new();
        let indexed = self.index.entity_path(T::KIND, project_id, id)?;
        if let Some(indexed) = &indexed
            && indexed.exists()
        {
            result.push(indexed.clone());
        }
        for path in self.directory.project(project_id).find_files(T::FOLDER, id) {
            let same = indexed
                .as_ref()
                .is_some_and(|i| tasker_core::io::same_path(&path.to_string_lossy(), &i.to_string_lossy()));
            if !same {
                result.push(path);
            }
        }
        Ok(result)
    }

    fn resolve_path<T: Entity>(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<(PathBuf, T)>> {
        for path in self.candidates::<T>(project_id, id)? {
            if let Some(entity) = self.read_entity::<T>(project_id, &path)?
                && entity.id() == *id
            {
                return Ok(Some((path, entity)));
            }
        }
        Ok(None)
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
