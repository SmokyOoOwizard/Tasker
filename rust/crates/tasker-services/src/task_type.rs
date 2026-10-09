//! Типы задач (`TaskTypeService`): набор статусов, поля каталога (каскад Clear/Keep при снятии поля с задач типа).
use crate::Workspace;
use crate::cascade::CascadeResult;
use crate::error::{Error, Result};
use crate::preview::{self, TaskTypeListItem};
use crate::rewrites;
use crate::usages::Usages;
use tasker_core::ids::guid_d;
use tasker_core::locks::LockedEntity;
use tasker_core::model::{FieldDefinition, StatusSet, TaskItem, TaskType, TaskTypeField};
use tasker_core::tasks::{ListPage, Page, TaskFilter};
use tasker_core::{validate, versioning};
use tasker_files::index::IndexQuery;
use tasker_files::layout::EntityKind;
use uuid::Uuid;

const CASCADE_ATTEMPTS: usize = 3;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreateTaskType {
    pub name: String,
    pub status_set_id: Uuid,
    /// Поля типа в порядке отображения (поля каталога, без повторов); None или пусто — без полей.
    pub fields: Option<Vec<TaskTypeField>>,
    pub description: Option<String>,
}

/// Что сделать со значениями поля, которое убрали из типа, у задач этого типа.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RemovedFieldValues {
    /// Убрать значения этого поля у всех задач типа.
    Clear,
    /// Оставить: поле становится дополнительным полем каждой задачи, у которой есть значения.
    Keep,
}

/// Поля None — не меняются; `description`: пустая строка — очистить. `fields` заменяет список полей целиком; убранное поле со
/// значениями у задач требует явного выбора `removed_fields`.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UpdateTaskType {
    pub name: Option<String>,
    pub status_set_id: Option<Uuid>,
    pub version: Option<String>,
    pub fields: Option<Vec<TaskTypeField>>,
    pub removed_fields: Option<RemovedFieldValues>,
    pub description: Option<String>,
}

pub struct TaskTypeService<'a> {
    ws: &'a Workspace,
}

fn subject(task_type: &TaskType) -> String {
    format!("Task type '{}'", task_type.name)
}

impl<'a> TaskTypeService<'a> {
    pub fn new(ws: &'a Workspace) -> TaskTypeService<'a> {
        TaskTypeService { ws }
    }

    pub fn get_by_id(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<TaskType>> {
        self.ws.get_by_id::<TaskType>(project_id, id)
    }

    pub fn get_all(&self, project_id: &Uuid) -> Result<Vec<TaskType>> {
        self.ws.get_all::<TaskType>(project_id)
    }

    /// Страница типов по имени; описания усечены до `description_length`.
    pub fn list(&self, project_id: &Uuid, page: Page, description_length: i32) -> Result<ListPage<TaskTypeListItem>> {
        preview::check(Some(description_length), preview::FULL)?;
        let found = self.ws.get_range::<TaskType>(project_id, page)?;
        Ok(ListPage {
            total_count: found.total_count,
            offset: found.offset,
            limit: found.limit,
            data: found
                .data
                .into_iter()
                .map(|x| TaskTypeListItem::new(x, description_length))
                .collect(),
        })
    }

    pub fn create(&self, project_id: &Uuid, command: &CreateTaskType) -> Result<TaskType> {
        let name = validate::entity_name(Some(&command.name), "Task type name")?;
        self.get_set(project_id, &command.status_set_id)?;

        self.ws.exclusive(project_id, || {
            let mut task_type = TaskType {
                id: Uuid::new_v4(),
                project_id: *project_id,
                name: name.clone(),
                description: validate::description(command.description.as_deref()).unwrap_or_default(),
                status_set_id: command.status_set_id,
                fields: self.validate_fields(project_id, command.fields.as_deref())?,
                version: versioning::NEW.to_string(),
            };
            task_type.version = self.ws.add(&task_type)?;
            Ok(task_type)
        })
    }

    /// Сменить набор можно, только если статусы всех задач типа есть в новом наборе. Убранное поле со значениями у задач — только с
    /// выбором. None — типа нет; иначе тип и число затронутых задач.
    pub fn update(&self, project_id: &Uuid, id: &Uuid, command: &UpdateTaskType) -> Result<Option<CascadeResult<TaskType>>> {
        let Some(task_type) = self.get_by_id(project_id, id)? else {
            return Ok(None);
        };
        let subject = subject(&task_type);
        self.ws.locks().ensure_writable(LockedEntity::TaskType, &task_type.id, &subject)?;
        let expected = versioning::check(&task_type.version, command.version.as_deref(), &subject)?;

        let name = match &command.name {
            None => task_type.name.clone(),
            Some(name) => validate::entity_name(Some(name), "Task type name")?,
        };
        let description = match &command.description {
            None => task_type.description.clone(),
            Some(d) => validate::description(Some(d)).unwrap_or_default(),
        };
        let set_id = command.status_set_id.unwrap_or(task_type.status_set_id);

        if set_id != task_type.status_set_id {
            let new_set = self.get_set(project_id, &set_id)?;
            let old_set = self.get_set(project_id, &task_type.status_set_id)?;
            let lost: Vec<Uuid> = old_set
                .status_ids
                .iter()
                .filter(|x| !new_set.status_ids.contains(x))
                .copied()
                .collect();
            let count = if lost.is_empty() {
                0
            } else {
                let filter = TaskFilter {
                    type_ids: Some(vec![*id]),
                    status_ids: Some(lost),
                    ..TaskFilter::default()
                };
                self.ws
                    .index()
                    .count(EntityKind::Task, &IndexQuery::tasks(project_id, Some(&filter)))?
            };
            if count > 0 {
                return Err(Error::in_use(format!(
                    "{count} task(s) of type '{}' have statuses that are not in status set '{}'; move them to other statuses first",
                    task_type.name, new_set.name
                )));
            }
        }

        // «Убрать значения»: ждём блокировки затрагиваемых задач до секции записи; внутри — проверка без ожидания.
        if command.removed_fields == Some(RemovedFieldValues::Clear)
            && let Some(fields) = &command.fields
        {
            let removed_now: Vec<Uuid> = task_type
                .fields
                .iter()
                .map(|x| x.field_id)
                .filter(|x| !fields.iter().any(|f| f.field_id == *x))
                .collect();
            if !removed_now.is_empty() {
                let holders = self.find_holders(project_id, &task_type, &removed_now)?;
                rewrites::wait_for_locks(self.ws, &holders, None)?;
            }
        }

        self.ws.exclusive(project_id, || {
            let new_fields = match &command.fields {
                None => task_type.fields.clone(),
                Some(fields) => self.validate_fields(project_id, Some(fields))?,
            };
            let removed: Vec<Uuid> = task_type
                .fields
                .iter()
                .map(|x| x.field_id)
                .filter(|x| !new_fields.iter().any(|f| f.field_id == *x))
                .collect();
            let affected = if removed.is_empty() {
                0
            } else {
                self.apply_removal(project_id, &task_type, &removed, command.removed_fields)?
            };

            let mut updated = TaskType {
                name: name.clone(),
                description: description.clone(),
                status_set_id: set_id,
                fields: new_fields,
                ..task_type.clone()
            };
            let version = self.ws.update(&updated, &expected)?.ok_or_else(|| versioning::modified(&subject))?;
            updated.version = version;
            Ok(Some(CascadeResult::new(updated, affected)))
        })
    }

    /// Нельзя удалить тип, у которого есть задачи. false — типа нет.
    pub fn delete(&self, project_id: &Uuid, id: &Uuid, version: Option<&str>) -> Result<bool> {
        let Some(task_type) = self.get_by_id(project_id, id)? else {
            return Ok(false);
        };
        let subject = subject(&task_type);
        self.ws.locks().ensure_writable(LockedEntity::TaskType, &task_type.id, &subject)?;
        let expected = versioning::check(&task_type.version, version, &subject)?;

        let mut usages = Usages::new(&subject);
        let filter = TaskFilter {
            type_ids: Some(vec![*id]),
            ..TaskFilter::default()
        };
        usages.add_tasks(
            self.ws
                .index()
                .count(EntityKind::Task, &IndexQuery::tasks(project_id, Some(&filter)))?,
        );
        usages.throw_if_any("deleted")?;

        if !self.ws.delete::<TaskType>(project_id, id, &expected)? {
            return Err(versioning::modified(&subject).into());
        }
        self.ws.locks().forget(LockedEntity::TaskType, &task_type.id)?;
        Ok(true)
    }

    fn get_set(&self, project_id: &Uuid, set_id: &Uuid) -> Result<StatusSet> {
        self.ws
            .get_by_id::<StatusSet>(project_id, set_id)?
            .ok_or_else(|| Error::validation(format!("StatusSetId: not found in the project: {}", guid_d(set_id))))
    }

    /// Поля типа: все из каталога проекта, без повторов.
    fn validate_fields(&self, project_id: &Uuid, input: Option<&[TaskTypeField]>) -> Result<Vec<TaskTypeField>> {
        let list = input.unwrap_or(&[]);
        let mut seen = std::collections::HashSet::new();
        if list.iter().any(|x| !seen.insert(x.field_id)) {
            return Err(Error::validation("Fields contains duplicates"));
        }
        let catalog: Vec<Uuid> = self.ws.get_all::<FieldDefinition>(project_id)?.into_iter().map(|x| x.id).collect();
        let missing: Vec<String> = list
            .iter()
            .map(|x| x.field_id)
            .filter(|x| !catalog.contains(x))
            .map(|x| guid_d(&x))
            .collect();
        if !missing.is_empty() {
            return Err(Error::validation(format!(
                "Fields: not found in the project: {}",
                missing.join(", ")
            )));
        }
        Ok(list.to_vec())
    }

    /// Задачи типа, у которых есть значения убираемых полей.
    fn find_holders(&self, project_id: &Uuid, task_type: &TaskType, removed: &[Uuid]) -> Result<Vec<TaskItem>> {
        let filter = TaskFilter {
            type_ids: Some(vec![task_type.id]),
            field_ids: Some(removed.to_vec()),
            ..TaskFilter::default()
        };
        let found: Vec<TaskItem> = self.ws.index().all(&IndexQuery::tasks(project_id, Some(&filter)))?;
        Ok(found.into_iter().filter(|t| holds_any(t, removed)).collect())
    }

    /// Убранные из типа поля и значения задач: есть значения — нужен выбор. «Оставить» ничего не пишет, «убрать» удаляет значения
    /// (и запись о поле) у всех задач типа. Возвращает, сколько задач затронуто.
    fn apply_removal(
        &self,
        project_id: &Uuid,
        task_type: &TaskType,
        removed: &[Uuid],
        choice: Option<RemovedFieldValues>,
    ) -> Result<usize> {
        let holders = self.find_holders(project_id, task_type, removed)?;
        if holders.is_empty() {
            return Ok(0);
        }
        let Some(choice) = choice else {
            let names: Vec<String> = self
                .ws
                .get_all::<FieldDefinition>(project_id)?
                .into_iter()
                .filter(|x| removed.contains(&x.id))
                .map(|x| format!("'{}'", x.name))
                .collect();
            return Err(Error::in_use(format!(
                "{} task(s) of type '{}' have values in the field(s) being removed ({}): choose to clear those values or to keep them as additional fields of the tasks",
                holders.len(),
                task_type.name,
                names.join(", ")
            )));
        };
        if choice == RemovedFieldValues::Keep {
            return Ok(holders.len());
        }
        let removed = removed.to_vec();
        let change = move |t: &TaskItem| -> Option<TaskItem> {
            if !holds_any(t, &removed) {
                return None;
            }
            Some(TaskItem {
                fields: t
                    .fields
                    .iter()
                    .filter(|f| !(f.own.is_none() && removed.contains(&f.field_id) && !f.values.is_empty()))
                    .cloned()
                    .collect(),
                ..t.clone()
            })
        };
        rewrites::modify_all(self.ws, &holders, &change, CASCADE_ATTEMPTS)
    }
}

fn holds_any(task: &TaskItem, removed: &[Uuid]) -> bool {
    task.fields
        .iter()
        .any(|f| f.own.is_none() && removed.contains(&f.field_id) && !f.values.is_empty())
}
