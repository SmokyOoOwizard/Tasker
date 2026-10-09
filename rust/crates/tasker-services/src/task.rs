//! Задачи (`TaskService`): создание и правка с полями, списки (плоские и деревом), серии и номера, поиск по ссылке.
use crate::Workspace;
use crate::error::{Error, Result};
use crate::hierarchy::TaskHierarchy;
use crate::links::{self, TaskLinkView};
use crate::preview::{self, TaskListItem, TaskTreeItem, TaskTreeList};
use crate::task_fields::{self, Catalog, Enums, TaskFieldChanges, TaskFieldView};
use crate::task_filters;
use std::collections::HashMap;
use tasker_core::ids::guid_d;
use tasker_core::locks::LockedEntity;
use tasker_core::model::{FieldDefinition, FieldEnum, Series, StatusSet, TaskItem, TaskSeriesNumber, TaskType};
use tasker_core::tasks::{ListPage, Page, TaskFilter};
use tasker_core::{ShortId, TaskReference, validate, versioning};
use tasker_files::index::{IndexQuery, LinkEdge};
use tasker_files::layout::EntityKind;
use uuid::Uuid;

pub const MAX_TITLE_LENGTH: usize = 500;

/// `status_id` None — первый статус набора; `series_ids` — серии, в которые сразу включить задачу.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CreateTask {
    pub title: String,
    pub description: Option<String>,
    pub type_id: Uuid,
    pub status_id: Option<Uuid>,
    pub series_ids: Option<Vec<Uuid>>,
    pub fields: Option<TaskFieldChanges>,
}

impl CreateTask {
    pub fn new(title: &str, type_id: Uuid) -> CreateTask {
        CreateTask {
            title: title.to_string(),
            type_id,
            ..CreateTask::default()
        }
    }
}

/// Поля None — не меняются. `description`: пустая строка — очистить. При смене типа статус должен быть в наборе нового типа.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UpdateTask {
    pub title: Option<String>,
    pub description: Option<String>,
    pub type_id: Option<Uuid>,
    pub status_id: Option<Uuid>,
    pub version: Option<String>,
    pub fields: Option<TaskFieldChanges>,
}

/// Задача вместе с видом её полей и связями с обеих сторон (`TaskDetails`).
#[derive(Debug, Clone, PartialEq)]
pub struct TaskDetails {
    pub task: TaskItem,
    pub field_views: Vec<TaskFieldView>,
    /// Не больше [`links::MAX_VIEWED`].
    pub link_views: Vec<TaskLinkView>,
    pub link_count: usize,
}

pub struct TaskService<'a> {
    ws: &'a Workspace,
}

fn subject(task: &TaskItem) -> String {
    format!("Task '{}'", task.title)
}

impl<'a> TaskService<'a> {
    pub fn new(ws: &'a Workspace) -> TaskService<'a> {
        TaskService { ws }
    }

    pub fn get_by_id(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<TaskItem>> {
        self.ws.get_by_id::<TaskItem>(project_id, id)
    }

    /// Все задачи проекта по фильтру, в порядке списка (`ITaskStorage.GetAll`).
    pub fn get_all(&self, project_id: &Uuid, filter: Option<&TaskFilter>) -> Result<Vec<TaskItem>> {
        Ok(self.ws.index().all::<TaskItem>(&IndexQuery::tasks(project_id, filter))?)
    }

    pub fn get_range(&self, project_id: &Uuid, filter: Option<&TaskFilter>, page: Page) -> Result<ListPage<TaskItem>> {
        Ok(self.ws.index().range::<TaskItem>(&IndexQuery::tasks(project_id, filter), page)?)
    }

    pub fn count(&self, project_id: &Uuid, filter: &TaskFilter) -> Result<usize> {
        Ok(self
            .ws
            .index()
            .count(EntityKind::Task, &IndexQuery::tasks(project_id, Some(filter)))?)
    }

    pub fn create(&self, project_id: &Uuid, command: &CreateTask) -> Result<TaskItem> {
        let title = validate::name(Some(&command.title), "Title", MAX_TITLE_LENGTH)?;
        let (task_type, set) = self.get_type(project_id, &command.type_id)?;
        let status_id = command.status_id.or_else(|| set.status_ids.first().copied()).unwrap_or_default();
        ensure_status_in_set(&status_id, &task_type, &set)?;

        let series_ids = validate::distinct(command.series_ids.as_deref().unwrap_or(&[]), "SeriesIds")?;
        if command
            .fields
            .as_ref()
            .and_then(|f| f.remove_fields.as_ref())
            .is_some_and(|r| !r.is_empty())
        {
            return Err(Error::validation("RemoveFields: a new task has no fields to remove"));
        }

        let now = self.ws.now();
        let mut task = TaskItem {
            id: Uuid::new_v4(),
            project_id: *project_id,
            title,
            description: validate::description(command.description.as_deref()),
            type_id: task_type.id,
            status_id,
            series_numbers: vec![],
            links: vec![],
            fields: vec![],
            created_at: now,
            updated_at: now,
            version: versioning::NEW.to_string(),
        };

        // Поля проверяются и задача записывается в одной атомарной секции; там же выбираются номера серий.
        if series_ids.is_empty() && command.fields.is_none() && !task_type.fields.iter().any(|x| x.required) {
            task.version = self.ws.add(&task)?;
            return Ok(task);
        }

        self.ws.exclusive(project_id, || {
            task.fields = self.build_fields(project_id, &task, &task_type, command.fields.as_ref(), true)?;
            let mut numbers = Vec::new();
            for series_id in &series_ids {
                if self.ws.get_by_id::<Series>(project_id, series_id)?.is_none() {
                    return Err(Error::validation(format!(
                        "SeriesIds: not found in the project: {}",
                        guid_d(series_id)
                    )));
                }
                numbers.push(TaskSeriesNumber {
                    series_id: *series_id,
                    number: self.ws.index().max_number(project_id, series_id)? + 1,
                });
            }
            task.series_numbers = numbers;
            task.version = self.ws.add(&task)?;
            Ok(task)
        })
    }

    /// None — задачи нет.
    pub fn update(&self, project_id: &Uuid, id: &Uuid, command: &UpdateTask) -> Result<Option<TaskItem>> {
        let Some(task) = self.get_by_id(project_id, id)? else {
            return Ok(None);
        };
        let subject = subject(&task);
        self.ws.locks().ensure_writable(LockedEntity::Task, &task.id, &subject)?;
        let expected = versioning::check(&task.version, command.version.as_deref(), &subject)?;

        let (task_type, set) = self.get_type(project_id, &command.type_id.unwrap_or(task.type_id))?;
        let status_id = command.status_id.unwrap_or(task.status_id);
        ensure_status_in_set(&status_id, &task_type, &set)?;

        let updated = TaskItem {
            title: match &command.title {
                None => task.title.clone(),
                Some(t) => validate::name(Some(t), "Title", MAX_TITLE_LENGTH)?,
            },
            description: match &command.description {
                None => task.description.clone(),
                Some(d) => validate::description(Some(d)),
            },
            type_id: task_type.id,
            status_id,
            updated_at: self.ws.now(),
            ..task.clone()
        };

        // Правка только статуса содержимого не меняет и обязательные поля не проверяет.
        let content_changed =
            command.title.is_some() || command.description.is_some() || command.fields.is_some() || task_type.id != task.type_id;

        let write = || -> Result<Option<TaskItem>> {
            let mut with_fields = updated.clone();
            if content_changed {
                with_fields.fields = self.build_fields(project_id, &task, &task_type, command.fields.as_ref(), content_changed)?;
            }
            let version = self
                .ws
                .update(&with_fields, &expected)?
                .ok_or_else(|| versioning::modified(&subject))?;
            with_fields.version = version;
            Ok(Some(with_fields))
        };

        if command.fields.is_some() {
            self.ws.exclusive(project_id, write)
        } else {
            write()
        }
    }

    /// Поля задачи после правки: применённая правка, значения проверены, обязательные заполнены (если правка меняет содержимое).
    fn build_fields(
        &self,
        project_id: &Uuid,
        task: &TaskItem,
        task_type: &TaskType,
        changes: Option<&TaskFieldChanges>,
        content_changed: bool,
    ) -> Result<Vec<tasker_core::model::TaskField>> {
        let needs_required_check = content_changed
            && (task_type.fields.iter().any(|x| x.required) || task.fields.iter().any(|x| x.own.as_ref().is_some_and(|o| o.required)));
        if changes.is_none() && !needs_required_check {
            return Ok(task_fields::normalize(&task.fields, task_type));
        }
        let catalog = self.catalog(project_id)?;
        let enumerations: Enums = if changes.is_none() {
            HashMap::new()
        } else {
            self.enums(project_id)?
        };

        let entries = task_fields::normalize(
            &task_fields::apply(&task.fields, task_type, &catalog, &enumerations, changes)?,
            task_type,
        );
        if content_changed {
            let missing = task_fields::missing_required(&entries, task_type, &catalog);
            if !missing.is_empty() {
                return Err(task_fields::required_fields_error(&missing));
            }
        }
        Ok(entries)
    }

    fn catalog(&self, project_id: &Uuid) -> Result<Catalog> {
        Ok(self
            .ws
            .get_all::<FieldDefinition>(project_id)?
            .into_iter()
            .map(|x| (x.id, x))
            .collect())
    }

    fn enums(&self, project_id: &Uuid) -> Result<Enums> {
        Ok(self.ws.get_all::<FieldEnum>(project_id)?.into_iter().map(|x| (x.id, x)).collect())
    }

    /// Поля задачи с определениями; None — задачи нет.
    pub fn get_fields(&self, project_id: &Uuid, task_id: &Uuid) -> Result<Option<Vec<TaskFieldView>>> {
        match self.get_by_id(project_id, task_id)? {
            None => Ok(None),
            Some(task) => Ok(Some(self.fields_of(project_id, &task)?)),
        }
    }

    /// То же для уже прочитанной задачи.
    pub fn fields_of(&self, project_id: &Uuid, task: &TaskItem) -> Result<Vec<TaskFieldView>> {
        let task_type = self.ws.get_by_id::<TaskType>(project_id, &task.type_id)?.ok_or_else(|| {
            Error::not_found(format!(
                "Task type {} of task {} not found (the project may have been deleted)",
                guid_d(&task.type_id),
                guid_d(&task.id)
            ))
        })?;
        if task_type.fields.is_empty() && task.fields.is_empty() {
            return Ok(vec![]);
        }
        let catalog = self.catalog(project_id)?;
        let enumerations = self.enums(project_id)?;
        Ok(task_fields::resolve(&task.fields, &task_type, &catalog)
            .into_iter()
            .map(|x| {
                let texts = tasker_core::fields::texts(x.field_type, x.enum_id.and_then(|e| enumerations.get(&e)), x.values());
                TaskFieldView {
                    field_id: x.id,
                    name: x.name.clone(),
                    field_type: x.field_type,
                    multiple: x.multiple,
                    enum_id: x.enum_id,
                    required: x.required,
                    source: x.source,
                    values: x.values().to_vec(),
                    texts,
                }
            })
            .collect())
    }

    /// Условия по полям из текста клиента (см. [`task_filters::parse_field_filters`]).
    pub fn parse_field_filters(
        &self,
        project_id: &Uuid,
        expressions: Option<&[String]>,
    ) -> Result<Option<Vec<tasker_core::tasks::FieldCondition>>> {
        task_filters::parse_field_filters(self.ws, project_id, expressions)
    }

    /// Ключи упорядочивания из текста клиента (см. [`task_filters::parse_sort`]).
    pub fn parse_sort(&self, project_id: &Uuid, sort: Option<&str>) -> Result<Option<Vec<tasker_core::tasks::TaskSortKey>>> {
        task_filters::parse_sort(self.ws, project_id, sort)
    }

    /// Страница задач проекта по фильтру, условиям по полям и порядку — для списков клиентам.
    pub fn list(
        &self,
        project_id: &Uuid,
        filter: Option<&TaskFilter>,
        field_filters: Option<&[String]>,
        page: Page,
        description_length: i32,
        sort: Option<&str>,
    ) -> Result<ListPage<TaskListItem>> {
        preview::check(Some(description_length), preview::FULL)?;
        let filter = self.list_filter(project_id, filter, field_filters, sort)?;
        let found = self.get_range(project_id, filter.as_ref(), page)?;
        let hierarchy = self.load_hierarchy(project_id)?;
        Ok(ListPage {
            total_count: found.total_count,
            offset: found.offset,
            limit: found.limit,
            data: self.preview_with(project_id, found.data, description_length, &hierarchy)?,
        })
    }

    fn list_filter(
        &self,
        project_id: &Uuid,
        filter: Option<&TaskFilter>,
        field_filters: Option<&[String]>,
        sort: Option<&str>,
    ) -> Result<Option<TaskFilter>> {
        let conditions = self.parse_field_filters(project_id, field_filters)?;
        let keys = self.parse_sort(project_id, sort)?;
        if conditions.is_none() && keys.is_none() {
            return Ok(filter.cloned());
        }
        let base = filter.cloned().unwrap_or_default();
        Ok(Some(TaskFilter {
            field_values: match conditions {
                None => base.field_values,
                Some(c) => Some(base.field_values.into_iter().flatten().chain(c).collect()),
            },
            sort: keys.or(base.sort),
            ..base
        }))
    }

    /// Список задач деревом: эпик и под ним его дочерние задачи по иерархическим связям (см. [`TaskHierarchy`]).
    pub fn list_tree(
        &self,
        project_id: &Uuid,
        filter: Option<&TaskFilter>,
        field_filters: Option<&[String]>,
        page: Page,
        description_length: i32,
        sort: Option<&str>,
    ) -> Result<TaskTreeList> {
        preview::check(Some(description_length), preview::FULL)?;
        let filter = self.list_filter(project_id, filter, field_filters, sort)?;
        let hierarchy = self.load_hierarchy(project_id)?;

        if hierarchy.is_empty() {
            let flat = self.get_range(project_id, filter.as_ref(), page)?;
            return Ok(TaskTreeList {
                total_count: flat.total_count,
                top_level_count: flat.total_count,
                offset: flat.offset,
                limit: flat.limit,
                data: self
                    .preview_with(project_id, flat.data, description_length, &hierarchy)?
                    .into_iter()
                    .map(|item| TaskTreeItem {
                        item,
                        depth: 0,
                        repeated: false,
                    })
                    .collect(),
            });
        }

        let ordered = self
            .ws
            .index()
            .ids(EntityKind::Task, &IndexQuery::tasks(project_id, filter.as_ref()))?;
        let top = hierarchy.top_level(&ordered);
        let position: HashMap<Uuid, usize> = ordered.iter().enumerate().map(|(i, id)| (*id, i)).collect();
        let roots: Vec<Uuid> = top.iter().skip(page.offset).take(page.limit).copied().collect();
        let rows = hierarchy.rows(&roots, &position);

        let mut loaded: HashMap<Uuid, TaskItem> = HashMap::new();
        let mut ids: Vec<Uuid> = Vec::new();
        for row in &rows {
            if !ids.contains(&row.id) {
                ids.push(row.id);
            }
        }
        for chunk in ids.chunks(500) {
            let filter = TaskFilter {
                ids: Some(chunk.to_vec()),
                ..TaskFilter::default()
            };
            for task in self.get_all(project_id, Some(&filter))? {
                loaded.insert(task.id, task);
            }
        }
        let items: HashMap<Uuid, TaskListItem> = self
            .preview_with(project_id, loaded.into_values().collect(), description_length, &hierarchy)?
            .into_iter()
            .map(|x| (x.task.id, x))
            .collect();
        Ok(TaskTreeList {
            total_count: ordered.len(),
            top_level_count: top.len(),
            offset: page.offset,
            limit: page.limit,
            // Задачу, удалённую между запросами, пропускаем вместе с её строкой.
            data: rows
                .iter()
                .filter_map(|row| {
                    items.get(&row.id).map(|item| TaskTreeItem {
                        item: item.clone(),
                        depth: row.depth,
                        repeated: row.repeated,
                    })
                })
                .collect(),
        })
    }

    /// Иерархические связи проекта: пусто, если иерархических типов нет или связей по ним ещё нет.
    fn load_hierarchy(&self, project_id: &Uuid) -> Result<TaskHierarchy> {
        let mut edges: Vec<LinkEdge> = Vec::new();
        for type_id in self.ws.links().hierarchical_type_ids(project_id)? {
            edges.extend(self.ws.index().link_edges(project_id, &type_id)?);
        }
        Ok(if edges.is_empty() {
            TaskHierarchy::default()
        } else {
            TaskHierarchy::new(&edges)
        })
    }

    /// Задачи в виде записей списка: описание усечено, число связей, родители и число дочерних.
    pub fn preview(&self, project_id: &Uuid, items: Vec<TaskItem>, description_length: i32) -> Result<Vec<TaskListItem>> {
        preview::check(Some(description_length), preview::FULL)?;
        let hierarchy = self.load_hierarchy(project_id)?;
        self.preview_with(project_id, items, description_length, &hierarchy)
    }

    fn preview_with(
        &self,
        project_id: &Uuid,
        items: Vec<TaskItem>,
        description_length: i32,
        hierarchy: &TaskHierarchy,
    ) -> Result<Vec<TaskListItem>> {
        let ids: Vec<Uuid> = items.iter().map(|x| x.id).collect();
        let inbound = self.ws.index().count_linked_to(project_id, &ids)?;
        Ok(items
            .into_iter()
            .map(|x| {
                let links_count = x.links.len() + inbound.get(&x.id).copied().unwrap_or(0);
                let parents = hierarchy.parents_of(&x.id);
                let children = hierarchy.child_count(&x.id);
                TaskListItem::new(x, description_length, links_count, parents, children)
            })
            .collect())
    }

    /// Задача с видом её полей и связями (`TaskDetails`).
    pub fn describe(&self, project_id: &Uuid, task: &TaskItem) -> Result<TaskDetails> {
        let field_views = self.fields_of(project_id, task)?;
        let all = self.ws.links().links_of(project_id, task)?;
        let link_count = all.len();
        Ok(TaskDetails {
            task: task.clone(),
            field_views,
            link_views: all.into_iter().take(links::MAX_VIEWED).collect(),
            link_count,
        })
    }

    /// None — задачи нет.
    pub fn describe_by_id(&self, project_id: &Uuid, task_id: &Uuid) -> Result<Option<TaskDetails>> {
        match self.get_by_id(project_id, task_id)? {
            None => Ok(None),
            Some(task) => Ok(Some(self.describe(project_id, &task)?)),
        }
    }

    /// На задачи ничего не ссылается, поэтому удаление без проверок ссылок; входящие связи других задач убираются. false — задачи нет.
    pub fn delete(&self, project_id: &Uuid, id: &Uuid, version: Option<&str>) -> Result<bool> {
        let Some(task) = self.get_by_id(project_id, id)? else {
            return Ok(false);
        };
        let subject = subject(&task);
        self.ws.locks().ensure_writable(LockedEntity::Task, &task.id, &subject)?;
        let expected = versioning::check(&task.version, version, &subject)?;

        if !self.ws.delete::<TaskItem>(project_id, id, &expected)? {
            return Err(versioning::modified(&subject).into());
        }
        self.ws.locks().forget(LockedEntity::Task, &task.id)?;
        links::remove_inbound(self.ws, project_id, &task.id)?;
        Ok(true)
    }

    fn get_type(&self, project_id: &Uuid, type_id: &Uuid) -> Result<(TaskType, StatusSet)> {
        let task_type = self
            .ws
            .get_by_id::<TaskType>(project_id, type_id)?
            .ok_or_else(|| Error::validation(format!("TypeId: not found in the project: {}", guid_d(type_id))))?;
        let set = self
            .ws
            .get_by_id::<StatusSet>(project_id, &task_type.status_set_id)?
            .ok_or_else(|| {
                Error::not_found(format!(
                    "Status set {} of task type {} not found (the project may have been deleted)",
                    guid_d(&task_type.status_set_id),
                    guid_d(&task_type.id)
                ))
            })?;
        Ok((task_type, set))
    }

    // ---- серии и номера ----

    /// Включает задачу в серию: следующий номер (максимум + 1) под секцией записи. Уже в серии — задача как есть. None — нет задачи или серии.
    pub fn add_to_series(&self, project_id: &Uuid, task_id: &Uuid, series_id: &Uuid, version: Option<&str>) -> Result<Option<TaskItem>> {
        self.ws.exclusive(project_id, || {
            let Some(task) = self.get_by_id(project_id, task_id)? else {
                return Ok(None);
            };
            if self.ws.get_by_id::<Series>(project_id, series_id)?.is_none() {
                return Ok(None);
            }
            let subject = subject(&task);
            self.ws.locks().ensure_writable(LockedEntity::Task, &task.id, &subject)?;
            let expected = versioning::check(&task.version, version, &subject)?;
            if task.series_numbers.iter().any(|x| x.series_id == *series_id) {
                return Ok(Some(task));
            }
            let number = self.ws.index().max_number(project_id, series_id)? + 1;
            let mut numbers = task.series_numbers.clone();
            numbers.push(TaskSeriesNumber {
                series_id: *series_id,
                number,
            });
            Ok(Some(self.save(&task, numbers, &expected)?))
        })
    }

    /// Убирает задачу из серии. Не в серии — задача как есть. None — задачи нет.
    pub fn remove_from_series(
        &self,
        project_id: &Uuid,
        task_id: &Uuid,
        series_id: &Uuid,
        version: Option<&str>,
    ) -> Result<Option<TaskItem>> {
        self.ws.exclusive(project_id, || {
            let Some(task) = self.get_by_id(project_id, task_id)? else {
                return Ok(None);
            };
            let subject = subject(&task);
            self.ws.locks().ensure_writable(LockedEntity::Task, &task.id, &subject)?;
            let expected = versioning::check(&task.version, version, &subject)?;
            if task.series_numbers.iter().all(|x| x.series_id != *series_id) {
                return Ok(Some(task));
            }
            let numbers = task.series_numbers.iter().filter(|x| x.series_id != *series_id).copied().collect();
            Ok(Some(self.save(&task, numbers, &expected)?))
        })
    }

    /// Убирает из серии задачу с этим номером. Несколько задач с номером — ошибка со списком id. None — такой задачи в серии нет.
    pub fn remove_from_series_by_number(&self, project_id: &Uuid, series_id: &Uuid, number: i32) -> Result<Option<TaskItem>> {
        self.ws.exclusive(project_id, || {
            let found = self.ws.index().tasks_by_number(project_id, series_id, number)?;
            if found.is_empty() {
                return Ok(None);
            }
            if found.len() > 1 {
                return Err(Error::validation(format!(
                    "Several tasks have number {number} in the series: {}; remove by task id instead",
                    found.iter().map(|x| guid_d(&x.id)).collect::<Vec<_>>().join(", ")
                )));
            }
            let task = &found[0];
            self.ws.locks().ensure_writable(LockedEntity::Task, &task.id, &subject(task))?;
            let numbers = task.series_numbers.iter().filter(|x| x.series_id != *series_id).copied().collect();
            Ok(Some(self.save(task, numbers, &task.version)?))
        })
    }

    /// Меняет номер задачи в серии: `to` — этот номер (должен быть свободен), None — следующий свободный. None — нет задачи, серии или
    /// задача не в серии. Конфликт — `to` меньше 1 или занят другой задачей.
    pub fn renumber(
        &self,
        project_id: &Uuid,
        task_id: &Uuid,
        series_id: &Uuid,
        to: Option<i32>,
        version: Option<&str>,
    ) -> Result<Option<TaskItem>> {
        self.ws.exclusive(project_id, || {
            let Some(task) = self.get_by_id(project_id, task_id)? else {
                return Ok(None);
            };
            if self.ws.get_by_id::<Series>(project_id, series_id)?.is_none() {
                return Ok(None);
            }
            let subject = subject(&task);
            self.ws.locks().ensure_writable(LockedEntity::Task, &task.id, &subject)?;
            let expected = versioning::check(&task.version, version, &subject)?;

            let Some(current) = task.series_numbers.iter().find(|x| x.series_id == *series_id).copied() else {
                return Ok(None);
            };
            let number = match to {
                None => self.ws.index().max_number(project_id, series_id)? + 1,
                Some(number) => {
                    if number < 1 {
                        return Err(Error::in_use(format!("Series number must be 1 or greater, got {number}")));
                    }
                    if self
                        .ws
                        .index()
                        .tasks_by_number(project_id, series_id, number)?
                        .iter()
                        .any(|x| x.id != *task_id)
                    {
                        return Err(Error::in_use(format!("Number {number} is already taken in the series")));
                    }
                    if number == current.number {
                        return Ok(Some(task));
                    }
                    number
                }
            };
            let numbers = task
                .series_numbers
                .iter()
                .map(|x| {
                    if x.series_id == *series_id {
                        TaskSeriesNumber { number, ..*x }
                    } else {
                        *x
                    }
                })
                .collect();
            Ok(Some(self.save(&task, numbers, &expected)?))
        })
    }

    /// Задачи по ссылке: Guid — одна или ни одной; `ПРЕФИКС-номер` — все с этим номером (дубликат — несколько); префикс id (если
    /// разрешён) — одна или ни одной. Нет серии с таким префиксом — пусто.
    pub fn resolve(&self, project_id: &Uuid, reference: &str, allow_id_prefix: bool) -> Result<Vec<TaskItem>> {
        let parsed = TaskReference::parse(Some(reference), allow_id_prefix)?;
        match parsed {
            TaskReference::Id(id) => Ok(self.get_by_id(project_id, &id)?.into_iter().collect()),
            TaskReference::IdPrefix(key) => self.find_by_id_prefix(project_id, &key),
            TaskReference::Series { prefix, number } => {
                // Серий с одним префиксом должно быть не больше одной; если после слияния их несколько — ищем во всех.
                let mut found: HashMap<Uuid, TaskItem> = HashMap::new();
                for series in self.ws.get_all::<Series>(project_id)?.into_iter().filter(|x| x.prefix == prefix) {
                    for task in self.ws.index().tasks_by_number(project_id, &series.id, number)? {
                        found.insert(task.id, task);
                    }
                }
                let mut result: Vec<TaskItem> = found.into_values().collect();
                result.sort_by(|a, b| {
                    a.created_at
                        .unix_ticks()
                        .cmp(&b.created_at.unix_ticks())
                        .then_with(|| a.id.cmp(&b.id))
                });
                Ok(result)
            }
        }
    }

    /// Id задачи из текста клиента: полный Guid (не проверяется) или префикс id ровно одной задачи. None — нет такой.
    pub fn resolve_id(&self, project_id: &Uuid, id: &str) -> Result<Option<Uuid>> {
        match TaskReference::try_parse(Some(id), true) {
            Some(TaskReference::Id(guid)) => Ok(Some(guid)),
            Some(TaskReference::IdPrefix(key)) => Ok(self.find_by_id_prefix(project_id, &key)?.first().map(|x| x.id)),
            _ => Err(Error::validation(format!(
                "'{id}' is not a task id: give the full id or its first {}+ hex characters",
                ShortId::LENGTH
            ))),
        }
    }

    fn find_by_id_prefix(&self, project_id: &Uuid, key: &str) -> Result<Vec<TaskItem>> {
        let found: Vec<TaskItem> = self.ws.index().all(&IndexQuery::project(project_id).with_id_prefix(key))?;
        if found.len() > 1 {
            return Err(Error::validation(format!(
                "Several tasks start with '{}', use a longer prefix or the full id: {}",
                key.replace('-', ""),
                found.iter().map(|x| guid_d(&x.id)).collect::<Vec<_>>().join(", ")
            )));
        }
        Ok(found)
    }

    fn save(&self, task: &TaskItem, numbers: Vec<TaskSeriesNumber>, expected: &str) -> Result<TaskItem> {
        let mut updated = TaskItem {
            series_numbers: numbers,
            updated_at: self.ws.now(),
            ..task.clone()
        };
        let version = self
            .ws
            .update(&updated, expected)?
            .ok_or_else(|| versioning::modified(&subject(task)))?;
        updated.version = version;
        Ok(updated)
    }
}

fn ensure_status_in_set(status_id: &Uuid, task_type: &TaskType, set: &StatusSet) -> Result<()> {
    if !set.status_ids.contains(status_id) {
        return Err(Error::validation(format!(
            "StatusId: status {} is not in the status set of task type '{}'",
            guid_d(status_id),
            task_type.name
        )));
    }
    Ok(())
}
