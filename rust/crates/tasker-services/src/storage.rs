//! Хранилища сущностей проекта поверх файлов и индекса (`EntityFiles<T>` и `*Storage` в `Tasker.Storage.Files`): одна сущность — из
//! файла (сначала там, где его знает индекс, потом по имени — старому Guid и новому суффиксу id; совпадение id проверяется по
//! содержимому), списки и подсчёты — из индекса. Запись — [`tasker_files::write`] под блокировкой записи, после неё индекс
//! догоняет файлы (`written`). Смена названия переименовывает файл; файл с новым именем уже есть — запись отклоняется.
use crate::error::{Error, Result};
use serde::Serialize;
use std::path::{Path, PathBuf};
use tasker_core::ids::guid_d;
use tasker_core::locks::LockedEntity;
use tasker_core::model::{Board, FieldDefinition, FieldEnum, LinkType, Project, Series, Status, StatusSet, TaskItem, TaskType};
use tasker_core::tasks::{ListPage, Page};
use tasker_files::files::{self, Versioned};
use tasker_files::index::{IndexEntity, IndexQuery};
use tasker_files::layout::{self, EntityFolder};
use tasker_files::write;
use uuid::Uuid;

/// Сущность проекта с папкой файлов, моделью файла и местом в индексе.
pub trait Entity: IndexEntity + Serialize + Clone {
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
                files::$module::parse(project_id, bytes, path)
            }

            fn to_bytes(&self) -> Vec<u8> {
                files::$module::serialize(self)
            }
        }
    };
}

entity!(TaskItem, layout::TASKS, LockedEntity::Task, task, title);
entity!(TaskType, layout::TASK_TYPES, LockedEntity::TaskType, task_type, name);
entity!(Status, layout::STATUSES, LockedEntity::Status, status, name);
entity!(StatusSet, layout::STATUS_SETS, LockedEntity::StatusSet, status_set, name);
entity!(Board, layout::BOARDS, LockedEntity::Board, board, name);
entity!(Series, layout::SERIES, LockedEntity::Series, series, name);
entity!(LinkType, layout::LINK_TYPES, LockedEntity::LinkType, link_type, name);
entity!(FieldDefinition, layout::FIELDS, LockedEntity::Field, field, name);
entity!(FieldEnum, layout::ENUMS, LockedEntity::Enum, field_enum, name);

impl crate::Workspace {
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
        let indexed = self.index().entity_path(T::KIND, project_id, id)?;
        if let Some(indexed) = &indexed
            && indexed.exists()
        {
            result.push(indexed.clone());
        }
        for path in self.directory().project(project_id).find_files(T::FOLDER, id) {
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

    /// Одна сущность по id — из файла; None — нет.
    pub fn get_by_id<T: Entity>(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<T>> {
        Ok(self.resolve_path::<T>(project_id, id)?.map(|(_, entity)| entity))
    }

    /// Все сущности вида в проекте — из индекса, по имени (у серий — по префиксу), затем по id.
    pub fn get_all<T: Entity>(&self, project_id: &Uuid) -> Result<Vec<T>> {
        Ok(self.index().all::<T>(&IndexQuery::project(project_id))?)
    }

    /// Страница сущностей вида (`GetRange`).
    pub fn get_range<T: Entity>(&self, project_id: &Uuid, page: Page) -> Result<ListPage<T>> {
        Ok(self.index().range::<T>(&IndexQuery::project(project_id), page)?)
    }

    /// Сколько файлов этого вида в проекте не удалось прочитать (`CountUnreadable`).
    pub fn count_unreadable<T: Entity>(&self, project_id: &Uuid) -> Result<usize> {
        Ok(self
            .index()
            .count_problems(&self.directory().project(project_id).folder(T::FOLDER))?)
    }

    /// Новый файл под именем по названию. Возвращает версию.
    ///
    /// Проект удаляют под блокировкой записи, и запись файла сама создаёт недостающие папки: без проверки под той же блокировкой
    /// «поздний» писатель воскресил бы папку удалённого проекта с одним своим файлом (`EntityFiles.Add`).
    pub fn add<T: Entity>(&self, entity: &T) -> Result<String> {
        let project_id = entity.project_id();
        let project = self.directory().project(&project_id);
        let path = project.entity_file(T::FOLDER, &entity.id(), Some(entity.file_name_source()));
        let bytes = entity.to_bytes();
        let result: std::io::Result<Result<String>> = self.index().written(std::slice::from_ref(&path), || {
            write::locked(project.root(), || {
                if self.directory().project_accepts_writes(&project_id) {
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
    pub fn update<T: Entity>(&self, entity: &T, expected_version: &str) -> Result<Option<String>> {
        let project_id = entity.project_id();
        let Some((current, _)) = self.resolve_path::<T>(&project_id, &entity.id())? else {
            return Ok(None);
        };
        let renamed = self
            .directory()
            .project(&project_id)
            .entity_file(T::FOLDER, &entity.id(), Some(entity.file_name_source()));
        let bytes = entity.to_bytes();
        Ok(self.index().written(&[current.clone(), renamed.clone()], || {
            write::write_if_match_renaming(&current, &renamed, &bytes, expected_version)
        })?)
    }

    /// Удаление по версии; false — файла нет или он изменён.
    pub fn delete<T: Entity>(&self, project_id: &Uuid, id: &Uuid, expected_version: &str) -> Result<bool> {
        let Some((path, _)) = self.resolve_path::<T>(project_id, id)? else {
            return Ok(false);
        };
        Ok(self
            .index()
            .written(std::slice::from_ref(&path), || write::delete_if_match(&path, expected_version))?)
    }

    // ---- проекты: project.yaml в папке проекта ----

    pub fn get_project(&self, id: &Uuid) -> Result<Option<Project>> {
        let path = self.directory().project(id).project_file();
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
    pub fn project_range(&self, ids: Option<&[Uuid]>, page: Page) -> Result<ListPage<Project>> {
        let query = IndexQuery {
            ids: ids.map(|x| x.to_vec()),
            ..IndexQuery::default()
        };
        Ok(self.index().range::<Project>(&query, page)?)
    }

    pub fn add_project(&self, project: &Project) -> Result<String> {
        let path = self.directory().project(&project.id).project_file();
        let bytes = files::project::serialize(project);
        Ok(self.index().written(std::slice::from_ref(&path), || write::write(&path, &bytes))?)
    }

    pub fn update_project(&self, project: &Project, expected_version: &str) -> Result<Option<String>> {
        let path = self.directory().project(&project.id).project_file();
        let bytes = files::project::serialize(project);
        Ok(self.index().written(std::slice::from_ref(&path), || {
            write::write_if_match(&path, &bytes, expected_version)
        })?)
    }

    /// Папка проекта удаляется целиком, если `project.yaml` не изменился с ожидаемой версии; ставится метка удаления.
    pub fn delete_project(&self, id: &Uuid, expected_version: &str) -> Result<bool> {
        let project = self.directory().project(id);
        let root = project.root().to_path_buf();
        let file = project.project_file();
        Ok(self.index().written(std::slice::from_ref(&root), || {
            write::delete_if_match_with(&file, expected_version, || {
                std::fs::remove_dir_all(&root)?;
                self.directory().mark_project_deleted(id)
            })
        })?)
    }
}
