//! Правила полей задачи, общие для создания и правки (`TaskFieldRules`, `TaskFieldChanges`, `TaskFieldView` в .NET): какие поля есть у
//! задачи (тип + дополнительные + собственные), как применить правку полей, как проверить обязательные. Работает над уже
//! загруженными каталогом и перечислениями; ничего не читает и не пишет.
use crate::error::{Error, Result};
use std::collections::HashMap;
use tasker_core::fields;
use tasker_core::ids::guid_d;
use tasker_core::model::{FieldDefinition, FieldEnum, FieldType, OwnField, TaskField, TaskType};
use tasker_core::validate::{self, eq_ignore_case};
use uuid::Uuid;

/// Откуда у задачи поле.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TaskFieldSource {
    /// Поле её типа: есть всегда, у задачи не удаляется.
    Type,
    /// Дополнительное поле из каталога: добавлено задаче (или осталось от прежнего типа).
    Extra,
    /// Собственное поле задачи: определение хранится в ней.
    Own,
}

impl TaskFieldSource {
    pub fn name(self) -> &'static str {
        match self {
            Self::Type => "type",
            Self::Extra => "extra",
            Self::Own => "own",
        }
    }
}

/// Поле задачи с определением: из типа, каталога или самой задачи.
#[derive(Debug, Clone, PartialEq)]
pub struct Resolved {
    pub id: Uuid,
    pub name: String,
    pub field_type: FieldType,
    pub multiple: bool,
    pub enum_id: Option<Uuid>,
    pub required: bool,
    pub source: TaskFieldSource,
    pub entry: Option<TaskField>,
}

impl Resolved {
    pub fn values(&self) -> &[String] {
        self.entry.as_ref().map(|e| e.values.as_slice()).unwrap_or(&[])
    }
}

/// Поле задачи в виде для клиентов (`TaskFieldView`): определение вместе со значениями и их названиями.
#[derive(Debug, Clone, PartialEq)]
pub struct TaskFieldView {
    pub field_id: Uuid,
    pub name: String,
    pub field_type: FieldType,
    pub multiple: bool,
    pub enum_id: Option<Uuid>,
    pub required: bool,
    pub source: TaskFieldSource,
    pub values: Vec<String>,
    pub texts: Vec<String>,
}

/// Значения поля при создании и правке задачи. `values` None или пусто — убрать значения.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TaskFieldValueInput {
    pub field_id: Uuid,
    pub values: Option<Vec<String>>,
}

/// Новое собственное поле задачи.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NewOwnField {
    pub name: String,
    pub field_type: FieldType,
    pub values: Option<Vec<String>>,
    pub required: bool,
    pub multiple: bool,
    pub enum_id: Option<Uuid>,
}

impl NewOwnField {
    pub fn new(name: &str, field_type: FieldType) -> NewOwnField {
        NewOwnField {
            name: name.to_string(),
            field_type,
            values: None,
            required: false,
            multiple: false,
            enum_id: None,
        }
    }
}

/// Что менять в полях задачи; None в любом списке — ничего. Порядок применения: убрать, добавить из каталога, значения, собственные.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TaskFieldChanges {
    pub values: Option<Vec<TaskFieldValueInput>>,
    pub add_fields: Option<Vec<Uuid>>,
    pub new_own_fields: Option<Vec<NewOwnField>>,
    pub remove_fields: Option<Vec<Uuid>>,
}

impl TaskFieldChanges {
    pub fn values(items: Vec<(Uuid, Vec<&str>)>) -> TaskFieldChanges {
        TaskFieldChanges {
            values: Some(
                items
                    .into_iter()
                    .map(|(field_id, values)| TaskFieldValueInput {
                        field_id,
                        values: Some(values.into_iter().map(str::to_string).collect()),
                    })
                    .collect(),
            ),
            ..TaskFieldChanges::default()
        }
    }
}

pub type Catalog = HashMap<Uuid, FieldDefinition>;
pub type Enums = HashMap<Uuid, FieldEnum>;

/// Поля задачи: сначала поля типа (в порядке типа), затем дополнительные и собственные (в порядке записи в задаче). Запись о
/// поле, которого нет в каталоге, пропускается.
pub fn resolve(entries: &[TaskField], task_type: &TaskType, catalog: &Catalog) -> Vec<Resolved> {
    let mut result: Vec<Resolved> = Vec::new();
    let type_fields: Vec<Uuid> = task_type.fields.iter().map(|x| x.field_id).collect();

    for type_field in &task_type.fields {
        let Some(field) = catalog.get(&type_field.field_id) else {
            continue;
        };
        if result.iter().any(|x| x.id == field.id) {
            continue;
        }
        let entry = entries.iter().find(|x| x.field_id == field.id && x.own.is_none()).cloned();
        result.push(Resolved {
            id: field.id,
            name: field.name.clone(),
            field_type: field.field_type,
            multiple: field.multiple,
            enum_id: field.enum_id,
            required: type_field.required,
            source: TaskFieldSource::Type,
            entry,
        });
    }

    for entry in entries {
        if let Some(own) = &entry.own {
            result.push(Resolved {
                id: entry.field_id,
                name: own.name.clone(),
                field_type: own.field_type,
                multiple: own.multiple,
                enum_id: own.enum_id,
                required: own.required,
                source: TaskFieldSource::Own,
                entry: Some(entry.clone()),
            });
        } else if !type_fields.contains(&entry.field_id)
            && let Some(field) = catalog.get(&entry.field_id)
        {
            result.push(Resolved {
                id: field.id,
                name: field.name.clone(),
                field_type: field.field_type,
                multiple: field.multiple,
                enum_id: field.enum_id,
                required: false,
                source: TaskFieldSource::Extra,
                entry: Some(entry.clone()),
            });
        }
    }
    result
}

/// Применяет правку полей к записям задачи и возвращает новые записи.
pub fn apply(
    entries: &[TaskField],
    task_type: &TaskType,
    catalog: &Catalog,
    enums: &Enums,
    changes: Option<&TaskFieldChanges>,
) -> Result<Vec<TaskField>> {
    let mut result = entries.to_vec();
    let Some(changes) = changes else {
        return Ok(result);
    };
    remove(&mut result, task_type, changes.remove_fields.as_deref())?;
    add_from_catalog(&mut result, task_type, catalog, changes.add_fields.as_deref())?;
    // Значения — до собственных полей: поле каталога, добавленное значением, уже учитывается в уникальности имён.
    set_values(&mut result, task_type, catalog, enums, changes.values.as_deref())?;
    add_own(&mut result, task_type, catalog, enums, changes.new_own_fields.as_deref())?;
    Ok(result)
}

/// Убирает записи без значений у полей типа: поле типа есть у задачи и так.
pub fn normalize(entries: &[TaskField], task_type: &TaskType) -> Vec<TaskField> {
    entries
        .iter()
        .filter(|x| !x.values.is_empty() || x.own.is_some() || !task_type.fields.iter().any(|f| f.field_id == x.field_id))
        .cloned()
        .collect()
}

/// Обязательные поля (типа и собственные) без значения — по именам.
pub fn missing_required(entries: &[TaskField], task_type: &TaskType, catalog: &Catalog) -> Vec<String> {
    resolve(entries, task_type, catalog)
        .into_iter()
        .filter(|x| x.required && x.values().is_empty())
        .map(|x| x.name)
        .collect()
}

/// `TaskerRequiredFieldsException`.
pub fn required_fields_error(names: &[String]) -> Error {
    Error::validation(format!(
        "Required fields have no value: {}",
        names.iter().map(|x| format!("'{x}'")).collect::<Vec<_>>().join(", ")
    ))
}

fn remove(entries: &mut Vec<TaskField>, task_type: &TaskType, ids: Option<&[Uuid]>) -> Result<()> {
    let Some(ids) = ids else {
        return Ok(());
    };
    ensure_distinct(ids, "RemoveFields")?;
    for id in ids {
        if task_type.fields.iter().any(|x| x.field_id == *id) {
            return Err(Error::validation(format!(
                "RemoveFields: {} is a field of the task type '{}' and cannot be removed from the task",
                guid_d(id),
                task_type.name
            )));
        }
        let before = entries.len();
        entries.retain(|x| x.field_id != *id);
        if entries.len() == before {
            return Err(Error::validation(format!(
                "RemoveFields: the task has no additional or own field {}",
                guid_d(id)
            )));
        }
    }
    Ok(())
}

fn add_from_catalog(entries: &mut Vec<TaskField>, task_type: &TaskType, catalog: &Catalog, ids: Option<&[Uuid]>) -> Result<()> {
    let Some(ids) = ids else {
        return Ok(());
    };
    ensure_distinct(ids, "AddFields")?;
    for id in ids {
        if !catalog.contains_key(id) {
            return Err(Error::validation(format!(
                "AddFields: field not found in the project: {}",
                guid_d(id)
            )));
        }
        if task_type.fields.iter().any(|x| x.field_id == *id) || entries.iter().any(|x| x.field_id == *id && x.own.is_none()) {
            continue;
        }
        entries.push(TaskField {
            field_id: *id,
            values: vec![],
            own: None,
        });
    }
    Ok(())
}

fn add_own(
    entries: &mut Vec<TaskField>,
    task_type: &TaskType,
    catalog: &Catalog,
    enums: &Enums,
    fields: Option<&[NewOwnField]>,
) -> Result<()> {
    for field in fields.unwrap_or(&[]) {
        let name = validate::field_name(Some(&field.name), "Field name")?;
        if field.field_type != FieldType::Enum && field.enum_id.is_some() {
            return Err(Error::validation(format!("Field '{name}': only a field of type enum has an enum")));
        }
        if field.field_type == FieldType::Enum && field.enum_id.is_none() {
            return Err(Error::validation(format!(
                "Field '{name}': a field of type enum must refer to an enum of the project"
            )));
        }
        let mut enumeration = None;
        if let Some(enum_id) = field.enum_id {
            enumeration = enums.get(&enum_id);
            if enumeration.is_none() {
                return Err(Error::validation(format!(
                    "Field '{name}': enum not found in the project: {}",
                    guid_d(&enum_id)
                )));
            }
        }
        if resolve(entries, task_type, catalog).iter().any(|x| eq_ignore_case(&x.name, &name)) {
            return Err(Error::validation(format!(
                "Field '{name}': the task already has a field with this name"
            )));
        }
        let raw: Vec<Option<String>> = field.values.clone().unwrap_or_default().into_iter().map(Some).collect();
        let values = fields::normalize(&name, field.field_type, field.multiple, enumeration, &raw)?;
        entries.push(TaskField {
            field_id: Uuid::new_v4(),
            values,
            own: Some(OwnField {
                name,
                field_type: field.field_type,
                required: field.required,
                multiple: field.multiple,
                enum_id: field.enum_id,
            }),
        });
    }
    Ok(())
}

fn set_values(
    entries: &mut Vec<TaskField>,
    task_type: &TaskType,
    catalog: &Catalog,
    enums: &Enums,
    inputs: Option<&[TaskFieldValueInput]>,
) -> Result<()> {
    let Some(inputs) = inputs else {
        return Ok(());
    };
    let ids: Vec<Uuid> = inputs.iter().map(|x| x.field_id).collect();
    ensure_distinct(&ids, "Values")?;

    for input in inputs {
        // Поле задачи (типа, дополнительное, собственное) или поле каталога, которого у задачи ещё нет: значение делает его дополнительным.
        let known = resolve(entries, task_type, catalog).into_iter().find(|x| x.id == input.field_id);
        let (name, field_type, multiple, enum_id) = match known {
            Some(k) => (k.name, k.field_type, k.multiple, k.enum_id),
            None => match catalog.get(&input.field_id) {
                Some(added) => (added.name.clone(), added.field_type, added.multiple, added.enum_id),
                None => {
                    return Err(Error::validation(format!(
                        "Values: the task has no field {} and it is not in the project's catalog",
                        guid_d(&input.field_id)
                    )));
                }
            },
        };
        let enumeration = enum_id.and_then(|id| enums.get(&id));
        let raw: Vec<Option<String>> = input.values.clone().unwrap_or_default().into_iter().map(Some).collect();
        let values = fields::normalize(&name, field_type, multiple, enumeration, &raw)?;

        match entries.iter_mut().find(|x| x.field_id == input.field_id) {
            Some(entry) => entry.values = values,
            None => {
                if !values.is_empty() {
                    entries.push(TaskField {
                        field_id: input.field_id,
                        values,
                        own: None,
                    });
                }
            }
        }
    }
    Ok(())
}

fn ensure_distinct(ids: &[Uuid], argument: &str) -> Result<()> {
    let mut seen = std::collections::HashSet::new();
    if ids.iter().any(|id| !seen.insert(*id)) {
        return Err(Error::validation(format!("{argument} contains duplicates")));
    }
    Ok(())
}
