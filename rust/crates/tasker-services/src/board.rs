//! Доски (`BoardService`): колонки по статусам и условиям по полям каталога; статус в двух колонках — только если их условия
//! исключают друг друга ([`crate::column_filters`]).
use crate::Workspace;
use crate::column_filters;
use crate::error::{Error, Result};
use crate::preview::TaskListItem;
use crate::task::UpdateTask;
use std::collections::{HashMap, HashSet};
use tasker_core::ids::guid_d;
use tasker_core::locks::LockedEntity;
use tasker_core::model::{Board, BoardColumn, ColumnFieldFilter, FieldDefinition, FieldEnum, StatusSet, TaskItem, TaskType};
use tasker_core::tasks::{ListPage, Page, TaskFilter};
use tasker_core::{validate, versioning};
use uuid::Uuid;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreateBoard {
    pub name: String,
    pub status_set_ids: Vec<Uuid>,
    pub columns: Vec<BoardColumnInput>,
}

/// Поля None — не меняются. `columns` заменяет колонки целиком: колонка с `id` сохраняет его, без — новая.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UpdateBoard {
    pub name: Option<String>,
    pub status_set_ids: Option<Vec<Uuid>>,
    pub columns: Option<Vec<BoardColumnInput>>,
    pub version: Option<String>,
}

/// `field_filters` — условия по полям текстом (`Имя=значение`…); None у существующей колонки — прежние, у новой — нет.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BoardColumnInput {
    pub id: Option<Uuid>,
    pub name: String,
    pub status_ids: Vec<Uuid>,
    /// Набор статусов → статус, который получает перетянутая задача.
    pub drop_statuses: Option<Vec<(Uuid, Uuid)>>,
    pub field_filters: Option<Vec<String>>,
}

impl BoardColumnInput {
    pub fn new(name: &str, status_ids: Vec<Uuid>) -> BoardColumnInput {
        BoardColumnInput {
            id: None,
            name: name.to_string(),
            status_ids,
            drop_statuses: None,
            field_filters: None,
        }
    }
}

/// Перенос задачи в колонку; `version` — версия задачи, которую видел клиент.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MoveTask {
    pub task_id: Uuid,
    pub version: Option<String>,
}

pub struct BoardService<'a> {
    ws: &'a Workspace,
}

fn subject(board: &Board) -> String {
    format!("Board '{}'", board.name)
}

impl<'a> BoardService<'a> {
    pub fn new(ws: &'a Workspace) -> BoardService<'a> {
        BoardService { ws }
    }

    pub fn get_by_id(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<Board>> {
        self.ws.get_by_id::<Board>(project_id, id)
    }

    pub fn get_all(&self, project_id: &Uuid) -> Result<Vec<Board>> {
        self.ws.get_all::<Board>(project_id)
    }

    pub fn get_range(&self, project_id: &Uuid, page: Page) -> Result<ListPage<Board>> {
        self.ws.get_range::<Board>(project_id, page)
    }

    pub fn create(&self, project_id: &Uuid, command: &CreateBoard) -> Result<Board> {
        self.ws.exclusive(project_id, || {
            let mut board = self.build(
                project_id,
                Uuid::new_v4(),
                Some(&command.name),
                Some(&command.status_set_ids),
                Some(&command.columns),
                &HashMap::new(),
            )?;
            board.version = self.ws.add(&board)?;
            Ok(board)
        })
    }

    /// None — доски нет.
    pub fn update(&self, project_id: &Uuid, id: &Uuid, command: &UpdateBoard) -> Result<Option<Board>> {
        let Some(board) = self.get_by_id(project_id, id)? else {
            return Ok(None);
        };
        let subject = subject(&board);
        self.ws.locks().ensure_writable(LockedEntity::Board, &board.id, &subject)?;
        let expected = versioning::check(&board.version, command.version.as_deref(), &subject)?;

        // Колонки не переданы — оставляем текущие (условия по полям остаются), но заново проверяем их против наборов.
        let columns: Vec<BoardColumnInput> = match &command.columns {
            Some(c) => c.clone(),
            None => board
                .columns
                .iter()
                .map(|c| BoardColumnInput {
                    id: Some(c.id),
                    name: c.name.clone(),
                    status_ids: c.status_ids.clone(),
                    drop_statuses: Some(c.drop_statuses.clone()),
                    field_filters: None,
                })
                .collect(),
        };
        let existing: HashMap<Uuid, BoardColumn> = board.columns.iter().map(|x| (x.id, x.clone())).collect();

        self.ws.exclusive(project_id, || {
            let mut updated = self.build(
                project_id,
                *id,
                Some(command.name.as_deref().unwrap_or(&board.name)),
                Some(command.status_set_ids.as_deref().unwrap_or(&board.status_set_ids)),
                Some(&columns),
                &existing,
            )?;
            let version = self.ws.update(&updated, &expected)?.ok_or_else(|| versioning::modified(&subject))?;
            updated.version = version;
            Ok(Some(updated))
        })
    }

    /// На доски ничего не ссылается. false — доски нет.
    pub fn delete(&self, project_id: &Uuid, id: &Uuid, version: Option<&str>) -> Result<bool> {
        let Some(board) = self.get_by_id(project_id, id)? else {
            return Ok(false);
        };
        let subject = subject(&board);
        self.ws.locks().ensure_writable(LockedEntity::Board, &board.id, &subject)?;
        let expected = versioning::check(&board.version, version, &subject)?;
        if !self.ws.delete::<Board>(project_id, id, &expected)? {
            return Err(versioning::modified(&subject).into());
        }
        self.ws.locks().forget(LockedEntity::Board, &board.id)?;
        Ok(true)
    }

    /// Перенос задачи в колонку: статус из правила переноса для набора её типа; задача, не подходящая под условия колонки по полям,
    /// не переносится. None — нет доски, колонки или задачи.
    pub fn move_task(&self, project_id: &Uuid, board_id: &Uuid, column_id: &Uuid, command: &MoveTask) -> Result<Option<TaskItem>> {
        let board = self.get_by_id(project_id, board_id)?;
        let column = board.as_ref().and_then(|b| b.columns.iter().find(|x| x.id == *column_id));
        let task = self.ws.get_by_id::<TaskItem>(project_id, &command.task_id)?;
        let (Some(board), Some(column), Some(task)) = (board.as_ref(), column, task) else {
            return Ok(None);
        };
        let task_type = self.ws.get_by_id::<TaskType>(project_id, &task.type_id)?.ok_or_else(|| {
            Error::not_found(format!(
                "Task type {} of task {} not found (the project may have been deleted)",
                guid_d(&task.type_id),
                guid_d(&task.id)
            ))
        })?;
        if !board.status_set_ids.contains(&task_type.status_set_id) {
            return Err(Error::validation(format!(
                "Task type '{}' is not on board '{}'",
                task_type.name, board.name
            )));
        }
        let status_id = column.drop_status(&task_type.status_set_id).ok_or_else(|| {
            Error::validation(format!(
                "Column '{}' has no drop rule for tasks of type '{}'",
                column.name, task_type.name
            ))
        })?;
        if !column.field_conditions.is_empty() {
            let types = self.ws.get_all::<TaskType>(project_id)?;
            let catalog = self.catalog(project_id)?;
            let conditions = column_filters::to_conditions(&column.field_conditions, &types, &catalog);
            if !conditions.iter().all(|x| x.matches(&task)) {
                return Err(Error::validation(format!(
                    "Task '{}' does not match the field conditions of column '{}' ({}): change the task's fields first, a move does not change them",
                    task.title,
                    column.name,
                    self.describe_filters(project_id, column)?.join(", ")
                )));
            }
        }
        self.ws.tasks().update(
            project_id,
            &task.id,
            &UpdateTask {
                status_id: Some(status_id),
                version: command.version.clone(),
                ..UpdateTask::default()
            },
        )
    }

    /// Задачи колонки: тип использует один из наборов доски, статус — один из статусов колонки, выполнены условия колонки по полям.
    /// None — нет доски или колонки.
    #[allow(clippy::too_many_arguments)]
    pub fn column_tasks(
        &self,
        project_id: &Uuid,
        board_id: &Uuid,
        column_id: &Uuid,
        page: Page,
        field_filters: Option<&[String]>,
        description_length: i32,
        sort: Option<&str>,
    ) -> Result<Option<ListPage<TaskListItem>>> {
        let Some(board) = self.get_by_id(project_id, board_id)? else {
            return Ok(None);
        };
        let Some(column) = board.columns.iter().find(|x| x.id == *column_id) else {
            return Ok(None);
        };
        let all_types = self.ws.get_all::<TaskType>(project_id)?;
        let type_ids: Vec<Uuid> = all_types
            .iter()
            .filter(|x| board.status_set_ids.contains(&x.status_set_id))
            .map(|x| x.id)
            .collect();
        let filter = TaskFilter {
            type_ids: Some(type_ids),
            status_ids: Some(column.status_ids.clone()),
            field_values: if column.field_conditions.is_empty() {
                None
            } else {
                Some(column_filters::to_conditions(
                    &column.field_conditions,
                    &all_types,
                    &self.catalog(project_id)?,
                ))
            },
            ..TaskFilter::default()
        };
        Ok(Some(self.ws.tasks().list(
            project_id,
            Some(&filter),
            field_filters,
            page,
            description_length,
            sort,
        )?))
    }

    /// Условия колонки текстом: поле и значение enum — названиями из каталога.
    pub fn describe_filters(&self, project_id: &Uuid, column: &BoardColumn) -> Result<Vec<String>> {
        if column.field_conditions.is_empty() {
            return Ok(vec![]);
        }
        let catalog = self.catalog(project_id)?;
        let enums = self.enums(project_id)?;
        Ok(column
            .field_conditions
            .iter()
            .map(|x| column_filters::text(x, &catalog, &enums))
            .collect())
    }

    fn catalog(&self, project_id: &Uuid) -> Result<HashMap<Uuid, FieldDefinition>> {
        Ok(self
            .ws
            .get_all::<FieldDefinition>(project_id)?
            .into_iter()
            .map(|x| (x.id, x))
            .collect())
    }

    fn enums(&self, project_id: &Uuid) -> Result<HashMap<Uuid, FieldEnum>> {
        Ok(self.ws.get_all::<FieldEnum>(project_id)?.into_iter().map(|x| (x.id, x)).collect())
    }

    /// Общая проверка доски для создания и изменения.
    fn build(
        &self,
        project_id: &Uuid,
        board_id: Uuid,
        name: Option<&str>,
        status_set_ids: Option<&[Uuid]>,
        columns: Option<&[BoardColumnInput]>,
        existing: &HashMap<Uuid, BoardColumn>,
    ) -> Result<Board> {
        let board_name = validate::entity_name(name, "Board name")?;
        let set_ids = validate::distinct(status_set_ids.unwrap_or(&[]), "StatusSetIds")?;
        if set_ids.is_empty() {
            return Err(Error::validation("Board must include at least one status set"));
        }
        let project_sets: HashMap<Uuid, StatusSet> = self.ws.get_all::<StatusSet>(project_id)?.into_iter().map(|x| (x.id, x)).collect();
        let known: Vec<Uuid> = project_sets.keys().copied().collect();
        validate::all_known(set_ids.iter(), &known, "StatusSetIds")?;
        let board_status_ids: Vec<Uuid> = set_ids.iter().flat_map(|x| project_sets[x].status_ids.clone()).collect();

        let Some(columns) = columns.filter(|c| !c.is_empty()) else {
            return Err(Error::validation("Board must have at least one column"));
        };

        let catalog = self.catalog(project_id)?;
        let enums = self.enums(project_id)?;
        let mut filters: Vec<Vec<ColumnFieldFilter>> = Vec::with_capacity(columns.len());
        for (i, c) in columns.iter().enumerate() {
            let where_ = format!("Columns[{i}].FieldFilters");
            let mut parsed: Vec<ColumnFieldFilter> = match &c.field_filters {
                None => {
                    c.id.and_then(|kept| existing.get(&kept))
                        .map(|kept| kept.field_conditions.clone())
                        .unwrap_or_default()
                }
                Some(text) => {
                    let conditions = self
                        .ws
                        .tasks()
                        .parse_field_filters(project_id, Some(text))
                        .map_err(|e| match e {
                            Error::Tasker(tasker_core::TaskerError::Validation(m)) => Error::validation(format!("{where_}: {m}")),
                            other => other,
                        })?
                        .unwrap_or_default();
                    let mut result = Vec::new();
                    for x in conditions {
                        // Колонка хранит условия по id поля каталога: собственные поля задач в неё не входят.
                        let field_id = x.field_id.ok_or_else(|| {
                            Error::validation(format!(
                                "{where_}: '{}' is not a field of the catalog: a column condition needs one",
                                x.name
                            ))
                        })?;
                        result.push(ColumnFieldFilter {
                            field_id,
                            operator: x.operator,
                            value: x.value,
                        });
                    }
                    result
                }
            };
            let mut distinct: Vec<ColumnFieldFilter> = Vec::new();
            for f in parsed.drain(..) {
                if !distinct.contains(&f) {
                    distinct.push(f);
                }
            }
            for filter in &distinct {
                column_filters::validate(filter, &catalog, &enums, &where_)?;
            }
            filters.push(distinct);
        }

        let mut used_column_ids: HashSet<Uuid> = HashSet::new();
        let mut built: Vec<BoardColumn> = Vec::new();
        for (i, c) in columns.iter().enumerate() {
            let field = format!("Columns[{i}]");
            if let Some(column_id) = c.id {
                if !existing.contains_key(&column_id) {
                    return Err(Error::validation(format!(
                        "{field}.Id: column {} not found on the board",
                        guid_d(&column_id)
                    )));
                }
                if !used_column_ids.insert(column_id) {
                    return Err(Error::validation(format!(
                        "{field}.Id: column {} is listed twice",
                        guid_d(&column_id)
                    )));
                }
            }
            let column_name = validate::entity_name(Some(&c.name), &format!("{field}.Name"))?;
            let status_ids = validate::distinct(&c.status_ids, &format!("{field}.StatusIds"))?;
            validate::all_known(
                status_ids.iter(),
                &board_status_ids,
                &format!("{field}.StatusIds (statuses of the board's status sets)"),
            )?;
            let drop = c.drop_statuses.clone().unwrap_or_default();
            for (set_id, status_id) in &drop {
                if !set_ids.contains(set_id) {
                    return Err(Error::validation(format!(
                        "{field}.DropStatuses: status set {} is not on the board",
                        guid_d(set_id)
                    )));
                }
                if !project_sets[set_id].status_ids.contains(status_id) {
                    return Err(Error::validation(format!(
                        "{field}.DropStatuses: status {} is not in status set {}",
                        guid_d(status_id),
                        guid_d(set_id)
                    )));
                }
                if !status_ids.contains(status_id) {
                    return Err(Error::validation(format!(
                        "{field}.DropStatuses: status {} is not one of the column's statuses",
                        guid_d(status_id)
                    )));
                }
            }
            built.push(BoardColumn {
                id: c.id.unwrap_or_else(Uuid::new_v4),
                name: column_name,
                status_ids,
                field_conditions: filters[i].clone(),
                drop_statuses: drop,
            });
        }

        if let Some(overlap) = column_filters::find_overlap(&built, &catalog) {
            return Err(Error::validation(format!(
                "Columns[{}].StatusIds: status {} is already used by column '{}', and the columns' field conditions do not exclude each other (a status may be shared only by columns with conditions that cannot hold together, such as Field=a and Field!=a)",
                overlap.second_index,
                guid_d(&overlap.status),
                overlap.first.name
            )));
        }

        Ok(Board {
            id: board_id,
            project_id: *project_id,
            name: board_name,
            status_set_ids: set_ids,
            columns: built,
            version: versioning::NEW.to_string(),
        })
    }
}
