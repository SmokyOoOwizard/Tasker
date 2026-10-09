//! Перечисления проекта (`FieldEnumService`): удаление значения, выбранного у задач или в условиях колонок досок, — только с
//! явным выбором (убрать или переназначить), задачи и колонки переписываются в той же секции записи.
use crate::Workspace;
use crate::cascade::CascadeResult;
use crate::column_filters;
use crate::error::{Error, Result};
use crate::locks::LockTarget;
use crate::rewrites;
use crate::usages::Usages;
use std::collections::{HashMap, HashSet};
use std::time::Duration;
use tasker_core::ids::{guid_d, parse_guid};
use tasker_core::locks::LockedEntity;
use tasker_core::model::{
    Board, BoardColumn, ColumnFieldFilter, FieldDefinition, FieldEnum, FieldEnumValue, TaskField, TaskItem, TaskType,
};
use tasker_core::tasks::{ListPage, Page, TaskFilter};
use tasker_core::validate::{self, eq_ignore_case};
use tasker_core::versioning;
use tasker_files::index::IndexQuery;
use tasker_files::layout::EntityKind;
use uuid::Uuid;

const CASCADE_ATTEMPTS: usize = 3;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreateFieldEnum {
    pub name: String,
    /// Названия значений в порядке отображения; хотя бы одно, без повторов (без учёта регистра).
    pub values: Vec<String>,
}

/// Значение в правке перечисления: `id` — существующее значение (переименование), None — новое.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FieldEnumValueInput {
    pub id: Option<Uuid>,
    pub name: String,
}

/// Что сделать со значением, которое удаляют, пока оно выбрано: ровно один вариант.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct RemovedEnumValues {
    pub clear: bool,
    pub reassign_to: Option<Uuid>,
}

impl RemovedEnumValues {
    pub fn clear() -> RemovedEnumValues {
        RemovedEnumValues {
            clear: true,
            reassign_to: None,
        }
    }

    pub fn reassign(to: Uuid) -> RemovedEnumValues {
        RemovedEnumValues {
            clear: false,
            reassign_to: Some(to),
        }
    }
}

/// Поля None — не меняются. `values` заменяет список целиком (и порядок).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UpdateFieldEnum {
    pub name: Option<String>,
    pub values: Option<Vec<FieldEnumValueInput>>,
    pub version: Option<String>,
    pub removed: Option<RemovedEnumValues>,
}

pub struct FieldEnumService<'a> {
    ws: &'a Workspace,
}

fn subject(value: &FieldEnum) -> String {
    format!("Enum '{}'", value.name)
}

fn board_subject(board: &Board) -> String {
    format!("Board '{}'", board.name)
}

fn uses_value(filter: &ColumnFieldFilter, field_ids: &HashSet<Uuid>, removed_keys: &HashSet<String>) -> bool {
    field_ids.contains(&filter.field_id) && filter.value.as_ref().is_some_and(|v| removed_keys.contains(v))
}

impl<'a> FieldEnumService<'a> {
    pub fn new(ws: &'a Workspace) -> FieldEnumService<'a> {
        FieldEnumService { ws }
    }

    pub fn count_unreadable(&self, project_id: &Uuid) -> Result<usize> {
        self.ws.count_unreadable::<FieldEnum>(project_id)
    }

    pub fn get_all(&self, project_id: &Uuid) -> Result<Vec<FieldEnum>> {
        self.ws.get_all::<FieldEnum>(project_id)
    }

    pub fn get_range(&self, project_id: &Uuid, page: Page) -> Result<ListPage<FieldEnum>> {
        self.ws.get_range::<FieldEnum>(project_id, page)
    }

    pub fn get_by_id(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<FieldEnum>> {
        self.ws.get_by_id::<FieldEnum>(project_id, id)
    }

    /// Перечисление по id или по имени (без учёта регистра); None — нет.
    pub fn find(&self, project_id: &Uuid, reference: &str) -> Result<Option<FieldEnum>> {
        let text = reference.trim();
        if text.is_empty() {
            return Ok(None);
        }
        let all = self.get_all(project_id)?;
        if let Some(id) = parse_guid(text)
            && let Some(by_id) = all.iter().find(|x| x.id == id)
        {
            return Ok(Some(by_id.clone()));
        }
        let by_name: Vec<&FieldEnum> = all.iter().filter(|x| eq_ignore_case(&x.name, text)).collect();
        match by_name.len() {
            0 => Ok(None),
            1 => Ok(Some(by_name[0].clone())),
            _ => Err(Error::validation(format!(
                "Several enums are named '{text}', use the id: {}",
                by_name.iter().map(|x| guid_d(&x.id)).collect::<Vec<_>>().join(", ")
            ))),
        }
    }

    pub fn create(&self, project_id: &Uuid, command: &CreateFieldEnum) -> Result<FieldEnum> {
        let name = validate::entity_name(Some(&command.name), "Enum name")?;
        let input: Vec<FieldEnumValueInput> = command
            .values
            .iter()
            .map(|x| FieldEnumValueInput { id: None, name: x.clone() })
            .collect();
        let values = validate_values(&input, &[])?;

        self.ws.exclusive(project_id, || {
            self.ensure_name_free(project_id, &name, None)?;
            let mut value = FieldEnum {
                id: Uuid::new_v4(),
                project_id: *project_id,
                name: name.clone(),
                values: values.clone(),
                version: versioning::NEW.to_string(),
            };
            value.version = self.ws.add(&value)?;
            Ok(value)
        })
    }

    /// None — перечисления нет. Иначе перечисление, число переписанных задач и колонок.
    pub fn update(&self, project_id: &Uuid, id: &Uuid, command: &UpdateFieldEnum) -> Result<Option<CascadeResult<FieldEnum>>> {
        let Some(current) = self.get_by_id(project_id, id)? else {
            return Ok(None);
        };
        let subject = subject(&current);
        self.ws.locks().ensure_writable(LockedEntity::Enum, &current.id, &subject)?;
        let expected = versioning::check(&current.version, command.version.as_deref(), &subject)?;

        let name = match &command.name {
            None => current.name.clone(),
            Some(n) => validate::entity_name(Some(n), "Enum name")?,
        };
        let values = match &command.values {
            None => current.values.clone(),
            Some(input) => validate_values(input, &current.values)?,
        };
        let removed: Vec<Uuid> = current
            .values
            .iter()
            .map(|x| x.id)
            .filter(|x| !values.iter().any(|v| v.id == *x))
            .collect();
        validate_choice(command.removed.as_ref(), &values, &removed)?;

        // Выбор сделан и значения где-то выбраны: ждём блокировки затрагиваемых задач и досок до секции записи.
        if !removed.is_empty() && command.removed.is_some() {
            let removed_keys: HashSet<String> = removed.iter().map(guid_d).collect();
            let (holders, _) = self.find_holders(project_id, &current, &removed_keys)?;
            rewrites::wait_for_locks(self.ws, holders.values(), None)?;
            let boards: Vec<LockTarget> = self
                .find_boards(project_id, &current, &removed_keys)?
                .iter()
                .map(|b| LockTarget::new(LockedEntity::Board, b.id, board_subject(b)))
                .collect();
            self.ws.locks().wait_until_writable(&boards, None)?;
        }

        self.ws.exclusive(project_id, || {
            if name != current.name {
                self.ensure_name_free(project_id, &name, Some(id))?;
            }
            let (affected, columns) = if removed.is_empty() {
                (0, 0)
            } else {
                self.apply_removal(project_id, &current, &removed, command.removed.as_ref())?
            };
            let mut updated = FieldEnum {
                name: name.clone(),
                values: values.clone(),
                ..current.clone()
            };
            let version = self.ws.update(&updated, &expected)?.ok_or_else(|| versioning::modified(&subject))?;
            updated.version = version;
            Ok(Some(CascadeResult::new(updated, affected).with_columns(columns)))
        })
    }

    /// Нельзя удалить перечисление, на которое ссылаются поля (каталога или собственные поля задач). false — перечисления нет.
    pub fn delete(&self, project_id: &Uuid, id: &Uuid, version: Option<&str>) -> Result<bool> {
        let Some(current) = self.get_by_id(project_id, id)? else {
            return Ok(false);
        };
        let subject = subject(&current);
        self.ws.locks().ensure_writable(LockedEntity::Enum, &current.id, &subject)?;
        let expected = versioning::check(&current.version, version, &subject)?;

        self.ws.exclusive(project_id, || {
            let mut usages = Usages::new(&subject);
            for field in self.ws.get_all::<FieldDefinition>(project_id)? {
                if field.enum_id == Some(*id) {
                    usages.add(format!("field '{}'", field.name));
                }
            }
            let filter = TaskFilter {
                enum_ids: Some(vec![*id]),
                ..TaskFilter::default()
            };
            let own = self
                .ws
                .index()
                .count(EntityKind::Task, &IndexQuery::tasks(project_id, Some(&filter)))?;
            if own > 0 {
                usages.add(format!("own fields of {own} task(s)"));
            }
            usages.throw_if_any("deleted")?;

            if !self.ws.delete::<FieldEnum>(project_id, id, &expected)? {
                return Err(versioning::modified(&subject).into());
            }
            self.ws.locks().forget(LockedEntity::Enum, &current.id)?;
            Ok(true)
        })
    }

    fn enum_field_ids(&self, project_id: &Uuid, current: &FieldEnum) -> Result<HashSet<Uuid>> {
        Ok(self
            .ws
            .get_all::<FieldDefinition>(project_id)?
            .into_iter()
            .filter(|x| x.enum_id == Some(current.id))
            .map(|x| x.id)
            .collect())
    }

    /// Задачи, где выбраны убираемые значения, и признак «поле использует их».
    #[allow(clippy::type_complexity)]
    fn find_holders(
        &self,
        project_id: &Uuid,
        current: &FieldEnum,
        removed_keys: &HashSet<String>,
    ) -> Result<(HashMap<Uuid, TaskItem>, Box<dyn Fn(&TaskField) -> bool>)> {
        let catalog_fields = self.enum_field_ids(project_id, current)?;
        let enum_id = current.id;
        let keys = removed_keys.clone();
        let uses = move |field: &TaskField| -> bool {
            let related = match &field.own {
                None => catalog_fields.contains(&field.field_id),
                Some(own) => own.enum_id == Some(enum_id),
            };
            related && field.values.iter().any(|v| keys.contains(v))
        };

        let mut holders: HashMap<Uuid, TaskItem> = HashMap::new();
        let filters = [
            TaskFilter {
                field_ids: Some(self.enum_field_ids(project_id, current)?.into_iter().collect()),
                ..TaskFilter::default()
            },
            TaskFilter {
                enum_ids: Some(vec![current.id]),
                ..TaskFilter::default()
            },
        ];
        for filter in &filters {
            for task in self.ws.index().all::<TaskItem>(&IndexQuery::tasks(project_id, Some(filter)))? {
                if task.fields.iter().any(&uses) {
                    holders.insert(task.id, task);
                }
            }
        }
        Ok((holders, Box::new(uses)))
    }

    /// Доски, у колонок которых есть условие на убираемые значения.
    fn find_boards(&self, project_id: &Uuid, current: &FieldEnum, removed_keys: &HashSet<String>) -> Result<Vec<Board>> {
        let field_ids = self.enum_field_ids(project_id, current)?;
        Ok(self
            .ws
            .get_all::<Board>(project_id)?
            .into_iter()
            .filter(|b| {
                b.columns
                    .iter()
                    .any(|c| c.field_conditions.iter().any(|f| uses_value(f, &field_ids, removed_keys)))
            })
            .collect())
    }

    fn apply_removal(
        &self,
        project_id: &Uuid,
        current: &FieldEnum,
        removed: &[Uuid],
        choice: Option<&RemovedEnumValues>,
    ) -> Result<(usize, usize)> {
        let removed_keys: HashSet<String> = removed.iter().map(guid_d).collect();
        let (holders, uses) = self.find_holders(project_id, current, &removed_keys)?;
        let affected_boards = self.find_boards(project_id, current, &removed_keys)?;
        if holders.is_empty() && affected_boards.is_empty() {
            return Ok((0, 0));
        }

        let Some(choice) = choice else {
            let field_ids = self.enum_field_ids(project_id, current)?;
            let names: Vec<String> = current
                .values
                .iter()
                .filter(|x| removed.contains(&x.id))
                .map(|x| format!("'{}'", x.name))
                .collect();
            let mut places: Vec<String> = Vec::new();
            if !holders.is_empty() {
                places.push(format!("selected in {} task(s)", holders.len()));
            }
            if !affected_boards.is_empty() {
                let columns: Vec<String> = affected_boards
                    .iter()
                    .flat_map(|b| {
                        b.columns
                            .iter()
                            .filter(|c| c.field_conditions.iter().any(|f| uses_value(f, &field_ids, &removed_keys)))
                            .map(move |c| format!("board '{}' (column '{}')", b.name, c.name))
                    })
                    .collect();
                places.push(format!("used in the field conditions of {}", columns.join(", ")));
            }
            return Err(Error::in_use(format!(
                "Value(s) {} of {} are {} and cannot be removed: choose to clear them (from the tasks, and the conditions from the columns) or to reassign them to another value of the enum",
                names.join(", "),
                subject(current),
                places.join(" and ")
            )));
        };

        let tasks_rewritten = if holders.is_empty() {
            0
        } else {
            self.rewrite_tasks(project_id, &removed_keys, choice, &holders, uses.as_ref())?
        };
        let columns_rewritten = if affected_boards.is_empty() {
            0
        } else {
            self.rewrite_boards(project_id, current, &removed_keys, choice, &affected_boards)?
        };
        Ok((tasks_rewritten, columns_rewritten))
    }

    /// Колонки досок: условие на убираемое значение убирается («убрать») или получает значение-замену («переназначить»). Доска после
    /// правки должна остаться корректной: иначе отказ без записи.
    fn rewrite_boards(
        &self,
        project_id: &Uuid,
        current: &FieldEnum,
        removed_keys: &HashSet<String>,
        choice: &RemovedEnumValues,
        affected: &[Board],
    ) -> Result<usize> {
        let field_ids = self.enum_field_ids(project_id, current)?;
        let catalog: HashMap<Uuid, FieldDefinition> = self
            .ws
            .get_all::<FieldDefinition>(project_id)?
            .into_iter()
            .map(|x| (x.id, x))
            .collect();
        let target = choice.reassign_to.map(|x| guid_d(&x));

        let rewrite = |column: &BoardColumn| -> BoardColumn {
            if !column.field_conditions.iter().any(|f| uses_value(f, &field_ids, removed_keys)) {
                return column.clone();
            }
            let mut conditions: Vec<ColumnFieldFilter> = Vec::new();
            for f in &column.field_conditions {
                let next = if uses_value(f, &field_ids, removed_keys) {
                    target.as_ref().map(|t| ColumnFieldFilter {
                        value: Some(t.clone()),
                        ..f.clone()
                    })
                } else {
                    Some(f.clone())
                };
                if let Some(next) = next
                    && !conditions.contains(&next)
                {
                    conditions.push(next);
                }
            }
            BoardColumn {
                field_conditions: conditions,
                ..column.clone()
            }
        };

        let mut rewritten: Vec<Board> = Vec::new();
        for board in affected {
            let columns: Vec<BoardColumn> = board.columns.iter().map(rewrite).collect();
            if let Some(overlap) = column_filters::find_overlap(&columns, &catalog) {
                return Err(Error::in_use(format!(
                    "{}: after the change columns '{}' and '{}' would show the same tasks (the same status and conditions that no longer exclude each other): change their conditions on the board first",
                    board_subject(board),
                    overlap.first.name,
                    overlap.second.name
                )));
            }
            rewritten.push(Board { columns, ..board.clone() });
        }

        // Блокировки досок проверяются до первой записи (без ожидания: ожидание — снаружи секции записи).
        let targets: Vec<LockTarget> = rewritten
            .iter()
            .map(|b| LockTarget::new(LockedEntity::Board, b.id, board_subject(b)))
            .collect();
        self.ws.locks().wait_until_writable(&targets, Some(Duration::ZERO))?;

        for board in &rewritten {
            let mut next = board.clone();
            let mut attempt = 0;
            loop {
                if self.ws.update(&next, &next.version)?.is_some() {
                    break;
                }
                // Доску тем временем изменили: колонки переписываются заново по свежей версии.
                let Some(fresh) = self.ws.get_by_id::<Board>(project_id, &board.id)? else {
                    break;
                };
                if attempt + 1 >= CASCADE_ATTEMPTS {
                    return Err(versioning::modified(&board_subject(board)).into());
                }
                next = Board {
                    columns: fresh.columns.iter().map(rewrite).collect(),
                    ..fresh
                };
                attempt += 1;
            }
        }
        Ok(affected
            .iter()
            .map(|b| {
                b.columns
                    .iter()
                    .filter(|c| c.field_conditions.iter().any(|f| uses_value(f, &field_ids, removed_keys)))
                    .count()
            })
            .sum())
    }

    fn rewrite_tasks(
        &self,
        project_id: &Uuid,
        removed_keys: &HashSet<String>,
        choice: &RemovedEnumValues,
        holders: &HashMap<Uuid, TaskItem>,
        uses: &dyn Fn(&TaskField) -> bool,
    ) -> Result<usize> {
        let type_fields: HashMap<Uuid, HashSet<Uuid>> = self
            .ws
            .get_all::<TaskType>(project_id)?
            .into_iter()
            .map(|x| (x.id, x.fields.iter().map(|f| f.field_id).collect()))
            .collect();
        let target = choice.reassign_to.map(|x| guid_d(&x));
        let rewrite = |values: &[String]| -> Vec<String> {
            let mut result: Vec<String> = Vec::new();
            for value in values {
                let replaced = if removed_keys.contains(value) {
                    target.clone()
                } else {
                    Some(value.clone())
                };
                if let Some(replaced) = replaced
                    && !result.contains(&replaced)
                {
                    result.push(replaced);
                }
            }
            result
        };

        let change = |t: &TaskItem| -> Option<TaskItem> {
            if !t.fields.iter().any(uses) {
                return None;
            }
            let fields: Vec<TaskField> = t
                .fields
                .iter()
                .map(|f| {
                    if uses(f) {
                        TaskField {
                            values: rewrite(&f.values),
                            ..f.clone()
                        }
                    } else {
                        f.clone()
                    }
                })
                // Поле типа без значения в задаче не записывается: пустое — убираем; дополнительное остаётся.
                .filter(|f| {
                    !f.values.is_empty() || f.own.is_some() || !type_fields.get(&t.type_id).is_some_and(|own| own.contains(&f.field_id))
                })
                .collect();
            Some(TaskItem { fields, ..t.clone() })
        };

        let mut ordered: Vec<&TaskItem> = holders.values().collect();
        ordered.sort_by(|a, b| {
            a.created_at
                .unix_ticks()
                .cmp(&b.created_at.unix_ticks())
                .then_with(|| a.id.cmp(&b.id))
        });
        rewrites::modify_all(self.ws, ordered, &change, CASCADE_ATTEMPTS)
    }

    fn ensure_name_free(&self, project_id: &Uuid, name: &str, except_id: Option<&Uuid>) -> Result<()> {
        if self
            .get_all(project_id)?
            .iter()
            .any(|x| Some(&x.id) != except_id && eq_ignore_case(&x.name, name))
        {
            return Err(Error::in_use(format!("Enum '{name}' already exists in the project")));
        }
        Ok(())
    }
}

/// Выбор «что с задачами» — ровно один вариант; «переназначить» — на значение, которое остаётся.
fn validate_choice(choice: Option<&RemovedEnumValues>, remaining: &[FieldEnumValue], removed: &[Uuid]) -> Result<()> {
    let Some(choice) = choice else {
        return Ok(());
    };
    if choice.clear == choice.reassign_to.is_some() {
        return Err(Error::validation(
            "Removed: choose exactly one of clear (remove the values from the tasks) or reassign (give another value)",
        ));
    }
    if let Some(target) = choice.reassign_to
        && (!remaining.iter().any(|x| x.id == target) || removed.contains(&target))
    {
        return Err(Error::validation(format!(
            "Removed: the value to reassign to {} is not among the values the enum keeps",
            guid_d(&target)
        )));
    }
    Ok(())
}

/// Хотя бы одно значение; названия без повторов; id — только существующих значений и по разу.
fn validate_values(input: &[FieldEnumValueInput], existing: &[FieldEnumValue]) -> Result<Vec<FieldEnumValue>> {
    if input.is_empty() {
        return Err(Error::validation("Enum must contain at least one value"));
    }
    let mut ids: Vec<Uuid> = Vec::new();
    let mut names: Vec<String> = Vec::new();
    let mut result = Vec::new();
    for item in input {
        let value_name = validate::entity_name(Some(&item.name), "Enum value")?;
        if names.iter().any(|n| eq_ignore_case(n, &value_name)) {
            return Err(Error::validation(format!(
                "Enum value '{value_name}' is repeated: values must be unique"
            )));
        }
        names.push(value_name.clone());
        let value_id = item.id.unwrap_or_else(Uuid::new_v4);
        if item.id.is_some() && !existing.iter().any(|x| x.id == value_id) {
            return Err(Error::validation(format!(
                "Enum value id not found in the enum: {}",
                guid_d(&value_id)
            )));
        }
        if ids.contains(&value_id) {
            return Err(Error::validation(format!("Enum value id {} is repeated", guid_d(&value_id))));
        }
        ids.push(value_id);
        result.push(FieldEnumValue {
            id: value_id,
            name: value_name,
        });
    }
    Ok(result)
}
