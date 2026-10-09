//! Каталог полей проекта (`FieldService`): уникальность имени и существование перечисления проверяются под секцией записи; смена
//! типа, множественности и перечисления преобразует значения задач ([`FieldConversion`]) в той же секции.
use crate::Workspace;
use crate::error::{Error, Result};
use crate::field_conversion::{FieldChangeChoice, FieldConversion};
use crate::rewrites;
use crate::usages::Usages;
use std::collections::{HashMap, HashSet};
use tasker_core::ids::{guid_d, parse_guid};
use tasker_core::locks::LockedEntity;
use tasker_core::model::{Board, FieldDefinition, FieldEnum, FieldType, TaskField, TaskItem, TaskType};
use tasker_core::tasks::{ListPage, Page, TaskFilter};
use tasker_core::validate::{self, eq_ignore_case};
use tasker_core::versioning;
use tasker_files::index::IndexQuery;
use tasker_files::layout::EntityKind;
use uuid::Uuid;

const CASCADE_ATTEMPTS: usize = 3;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreateField {
    pub name: String,
    pub field_type: FieldType,
    /// None — одно значение.
    pub multiple: Option<bool>,
    /// Перечисление проекта — обязательно для enum и запрещено для остальных.
    pub enum_id: Option<Uuid>,
}

/// Поля None — не меняются. Тип, множественность и перечисление меняются вместе со значениями задач; без нужного выбора — ошибка.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UpdateField {
    pub name: Option<String>,
    pub version: Option<String>,
    pub field_type: Option<FieldType>,
    pub multiple: Option<bool>,
    pub enum_id: Option<Uuid>,
    pub choice: Option<FieldChangeChoice>,
}

pub struct FieldService<'a> {
    ws: &'a Workspace,
}

fn subject(field: &FieldDefinition) -> String {
    format!("Field '{}'", field.name)
}

fn holds(field_id: &Uuid, f: &TaskField) -> bool {
    f.own.is_none() && f.field_id == *field_id && !f.values.is_empty()
}

type Change = Box<dyn Fn(&TaskItem) -> Option<TaskItem>>;

struct ValuesPlan {
    affected: Vec<TaskItem>,
    problems: Option<String>,
    change: Change,
}

impl<'a> FieldService<'a> {
    pub fn new(ws: &'a Workspace) -> FieldService<'a> {
        FieldService { ws }
    }

    pub fn count_unreadable(&self, project_id: &Uuid) -> Result<usize> {
        self.ws.count_unreadable::<FieldDefinition>(project_id)
    }

    pub fn get_all(&self, project_id: &Uuid) -> Result<Vec<FieldDefinition>> {
        self.ws.get_all::<FieldDefinition>(project_id)
    }

    pub fn get_range(&self, project_id: &Uuid, page: Page) -> Result<ListPage<FieldDefinition>> {
        self.ws.get_range::<FieldDefinition>(project_id, page)
    }

    pub fn get_by_id(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<FieldDefinition>> {
        self.ws.get_by_id::<FieldDefinition>(project_id, id)
    }

    /// Поле по id или по имени (без учёта регистра); None — нет.
    pub fn find(&self, project_id: &Uuid, reference: &str) -> Result<Option<FieldDefinition>> {
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
        let by_name: Vec<&FieldDefinition> = all.iter().filter(|x| eq_ignore_case(&x.name, text)).collect();
        match by_name.len() {
            0 => Ok(None),
            1 => Ok(Some(by_name[0].clone())),
            _ => Err(Error::validation(format!(
                "Several fields are named '{text}', use the id: {}",
                by_name.iter().map(|x| guid_d(&x.id)).collect::<Vec<_>>().join(", ")
            ))),
        }
    }

    pub fn create(&self, project_id: &Uuid, command: &CreateField) -> Result<FieldDefinition> {
        let name = validate::field_name(Some(&command.name), "Field name")?;
        if command.field_type != FieldType::Enum && command.enum_id.is_some() {
            return Err(Error::validation("EnumId: only a field of type enum has an enum"));
        }
        if command.field_type == FieldType::Enum && command.enum_id.is_none() {
            return Err(Error::validation(
                "EnumId: a field of type enum must refer to an enum of the project",
            ));
        }

        self.ws.exclusive(project_id, || {
            if let Some(enum_id) = command.enum_id
                && self.ws.get_by_id::<FieldEnum>(project_id, &enum_id)?.is_none()
            {
                return Err(Error::validation(format!(
                    "EnumId: enum not found in the project: {}",
                    guid_d(&enum_id)
                )));
            }
            self.ensure_name_free(project_id, &name, None)?;
            let mut field = FieldDefinition {
                id: Uuid::new_v4(),
                project_id: *project_id,
                name: name.clone(),
                field_type: command.field_type,
                multiple: command.multiple.unwrap_or(false),
                enum_id: command.enum_id,
                version: versioning::NEW.to_string(),
            };
            field.version = self.ws.add(&field)?;
            Ok(field)
        })
    }

    /// None — поля нет. Значения задач, которые нельзя сохранить без выбора пользователя, — конфликт с числом задач.
    pub fn update(&self, project_id: &Uuid, id: &Uuid, command: &UpdateField) -> Result<Option<FieldDefinition>> {
        let Some(field) = self.get_by_id(project_id, id)? else {
            return Ok(None);
        };
        let subject = subject(&field);
        self.ws.locks().ensure_writable(LockedEntity::Field, &field.id, &subject)?;
        let expected = versioning::check(&field.version, command.version.as_deref(), &subject)?;

        // Поле, созданное до правила «имя из букв и цифр», с прежним именем правится как раньше; новое имя проверяется целиком.
        let name = match &command.name {
            None => field.name.clone(),
            Some(n) if n.trim() == field.name => field.name.clone(),
            Some(n) => validate::field_name(Some(n), "Field name")?,
        };
        let field_type = command.field_type.unwrap_or(field.field_type);
        if field_type != FieldType::Enum && command.enum_id.is_some() {
            return Err(Error::validation("EnumId: only a field of type enum has an enum"));
        }
        let enum_id = if field_type == FieldType::Enum {
            command.enum_id.or(field.enum_id)
        } else {
            None
        };
        if field_type == FieldType::Enum && enum_id.is_none() {
            return Err(Error::validation(
                "EnumId: a field of type enum must refer to an enum of the project",
            ));
        }
        let changed = FieldDefinition {
            name: name.clone(),
            field_type,
            multiple: command.multiple.unwrap_or(field.multiple),
            enum_id,
            ..field.clone()
        };

        // Значения задач меняются: ждём блокировки затрагиваемых задач до секции записи; внутри — проверка без ожидания.
        let plan = self.plan(project_id, &field, &changed, command.choice.as_ref())?;
        if plan.problems.is_none() {
            rewrites::wait_for_locks(self.ws, &plan.affected, None)?;
        }

        self.ws.exclusive(project_id, || {
            // Поле перечитано под блокировкой записи: между проверкой версии и записью его никто не изменил.
            if self.get_by_id(project_id, id)?.map(|x| x.version) != Some(field.version.clone()) {
                return Err(versioning::modified(&subject).into());
            }
            if name != field.name {
                self.ensure_name_free(project_id, &name, Some(id))?;
            }

            // Условие колонки хранит значение в виде прежнего типа и перечисления; от множественности зависит, исключают ли друг друга
            // условия колонок с общим статусом.
            if changed.field_type != field.field_type || changed.enum_id != field.enum_id || changed.multiple != field.multiple {
                let mut usages = Usages::new(&subject);
                self.add_board_columns(project_id, id, &mut usages)?;
                usages.throw_if_any("changed to another type, enum or multiplicity (remove its conditions from the columns first)")?;
            }

            self.apply_values(project_id, &field, &changed, command.choice.as_ref())?;

            let mut result = changed.clone();
            let version = self.ws.update(&result, &expected)?.ok_or_else(|| versioning::modified(&subject))?;
            result.version = version;
            Ok(Some(result))
        })
    }

    fn plan(
        &self,
        project_id: &Uuid,
        field: &FieldDefinition,
        changed: &FieldDefinition,
        choice: Option<&FieldChangeChoice>,
    ) -> Result<ValuesPlan> {
        let old_enum = match field.enum_id {
            Some(id) => self.ws.get_by_id::<FieldEnum>(project_id, &id)?,
            None => None,
        };
        let new_enum = match changed.enum_id {
            Some(id) => self.ws.get_by_id::<FieldEnum>(project_id, &id)?,
            None => None,
        };
        if let Some(id) = changed.enum_id
            && new_enum.is_none()
        {
            return Err(Error::validation(format!("EnumId: enum not found in the project: {}", guid_d(&id))));
        }

        let conversion = FieldConversion::create(field, old_enum.as_ref(), changed, new_enum.as_ref(), choice)?;
        if !conversion.changes_values() {
            return Ok(ValuesPlan {
                affected: vec![],
                problems: None,
                change: Box::new(|_| None),
            });
        }

        let type_fields: HashMap<Uuid, HashSet<Uuid>> = self
            .ws
            .get_all::<TaskType>(project_id)?
            .into_iter()
            .map(|x| (x.id, x.fields.iter().map(|f| f.field_id).collect()))
            .collect();
        let field_id = field.id;
        let conversion = std::rc::Rc::new(conversion);

        let change_with = {
            let conversion = conversion.clone();
            move |task: &TaskItem| -> Option<TaskItem> {
                if !task.fields.iter().any(|f| holds(&field_id, f)) {
                    return None;
                }
                let mut entries: Vec<TaskField> = Vec::new();
                let mut differs = false;
                for entry in &task.fields {
                    if !holds(&field_id, entry) {
                        entries.push(entry.clone());
                        continue;
                    }
                    let values = conversion.convert(&entry.values).values;
                    differs |= values != entry.values;
                    // Поле типа без значения в задаче не записывается: пустое — убираем; дополнительное остаётся.
                    let in_type = type_fields.get(&task.type_id).is_some_and(|own| own.contains(&field_id));
                    if !values.is_empty() || !in_type {
                        entries.push(TaskField { values, ..entry.clone() });
                    }
                }
                differs.then(|| TaskItem {
                    fields: entries,
                    ..task.clone()
                })
            }
        };

        let filter = TaskFilter {
            field_ids: Some(vec![field.id]),
            ..TaskFilter::default()
        };
        let mut holders: Vec<TaskItem> = self
            .ws
            .index()
            .all::<TaskItem>(&IndexQuery::tasks(project_id, Some(&filter)))?
            .into_iter()
            .filter(|x| x.fields.iter().any(|f| holds(&field_id, f)))
            .collect();
        holders.sort_by(|a, b| {
            a.created_at
                .unix_ticks()
                .cmp(&b.created_at.unix_ticks())
                .then_with(|| a.id.cmp(&b.id))
        });

        let results: Vec<_> = holders
            .iter()
            .map(|x| conversion.convert(&x.fields.iter().find(|f| holds(&field_id, f)).unwrap().values))
            .collect();
        let mut problems: Vec<String> = Vec::new();
        let bad = results.iter().filter(|x| !x.unconvertible.is_empty()).count();
        if bad > 0 && !choice.is_some_and(|c| c.clear_unconvertible) {
            let mut examples: Vec<String> = Vec::new();
            for x in results.iter().flat_map(|r| r.unconvertible.iter()) {
                if !examples.contains(x) {
                    examples.push(x.clone());
                }
                if examples.len() == 5 {
                    break;
                }
            }
            problems.push(format!(
                "{bad} task(s) have values that do not fit the new definition (e.g. {}): choose to clear such values (the others are converted){}",
                examples.iter().map(|x| format!("'{x}'")).collect::<Vec<_>>().join(", "),
                if changed.field_type == FieldType::Enum { " or map them to values of the enum" } else { "" }
            ));
        }
        let several = results.iter().filter(|x| x.several).count();
        if several > 0 && choice.and_then(|c| c.several).is_none() {
            problems.push(format!(
                "{several} task(s) have several values, but the field will hold one: choose to keep the first value or to clear them"
            ));
        }

        let affected: Vec<TaskItem> = holders.into_iter().filter(|x| change_with(x).is_some()).collect();
        Ok(ValuesPlan {
            affected,
            problems: (!problems.is_empty()).then(|| format!("{} cannot be changed: {}", subject(field), problems.join("; "))),
            change: Box::new(change_with),
        })
    }

    fn apply_values(
        &self,
        project_id: &Uuid,
        field: &FieldDefinition,
        changed: &FieldDefinition,
        choice: Option<&FieldChangeChoice>,
    ) -> Result<()> {
        let plan = self.plan(project_id, field, changed, choice)?;
        if let Some(problems) = plan.problems {
            return Err(Error::in_use(problems));
        }
        rewrites::modify_all(self.ws, &plan.affected, plan.change.as_ref(), CASCADE_ATTEMPTS)?;
        Ok(())
    }

    /// Нельзя удалить поле, на которое есть ссылки: типы задач, задачи, колонки досок. false — поля нет.
    pub fn delete(&self, project_id: &Uuid, id: &Uuid, version: Option<&str>) -> Result<bool> {
        let Some(field) = self.get_by_id(project_id, id)? else {
            return Ok(false);
        };
        let subject = subject(&field);
        self.ws.locks().ensure_writable(LockedEntity::Field, &field.id, &subject)?;
        let expected = versioning::check(&field.version, version, &subject)?;

        self.ws.exclusive(project_id, || {
            let mut usages = Usages::new(&subject);
            for task_type in self.ws.get_all::<TaskType>(project_id)? {
                if task_type.fields.iter().any(|x| x.field_id == *id) {
                    usages.add(format!("task type '{}'", task_type.name));
                }
            }
            let filter = TaskFilter {
                field_ids: Some(vec![*id]),
                ..TaskFilter::default()
            };
            usages.add_tasks(
                self.ws
                    .index()
                    .count(EntityKind::Task, &IndexQuery::tasks(project_id, Some(&filter)))?,
            );
            self.add_board_columns(project_id, id, &mut usages)?;
            usages.throw_if_any("deleted")?;

            if !self.ws.delete::<FieldDefinition>(project_id, id, &expected)? {
                return Err(versioning::modified(&subject).into());
            }
            self.ws.locks().forget(LockedEntity::Field, &field.id)?;
            Ok(true)
        })
    }

    /// Колонки досок, у которых есть условие по этому полю.
    fn add_board_columns(&self, project_id: &Uuid, field_id: &Uuid, usages: &mut Usages) -> Result<()> {
        for board in self.ws.get_all::<Board>(project_id)? {
            for column in &board.columns {
                if column.field_conditions.iter().any(|x| x.field_id == *field_id) {
                    usages.add_board_column(&board, column);
                }
            }
        }
        Ok(())
    }

    fn ensure_name_free(&self, project_id: &Uuid, name: &str, except_id: Option<&Uuid>) -> Result<()> {
        if self
            .get_all(project_id)?
            .iter()
            .any(|x| Some(&x.id) != except_id && eq_ignore_case(&x.name, name))
        {
            return Err(Error::in_use(format!("Field '{name}' already exists in the project")));
        }
        Ok(())
    }
}
