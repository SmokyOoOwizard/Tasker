//! Условия по полям и порядок списка задач из текста клиента (`TaskService.ParseFieldFilters`, `TaskService.ParseSort`):
//! `Имя=значение`, `Имя>=3`, `Имя:set`; `status,-updated,Estimate`.
use crate::Workspace;
use crate::error::{Error, Result};
use tasker_core::fields;
use tasker_core::ids::guid_d;
use tasker_core::model::{FieldDefinition, FieldEnum, FieldOperator, FieldType, Series, Status, StatusSet, TaskType};
use tasker_core::tasks::{
    BUILTIN_SORT_NAMES, FieldCondition, SortField, SortTarget, TaskSortKey, builtin_sort, field_name_key, field_number, ranks, status_ranks,
};
use tasker_core::validate::{eq_ignore_case, to_lower_invariant};
use tasker_files::index::OwnFieldKind;
use uuid::Uuid;

/// Поле условия: имя, тип, по которому разбирается значение, перечисление и поле каталога (если есть).
struct FilterField {
    name: String,
    field_type: FieldType,
    enum_id: Option<Uuid>,
    field_id: Option<Uuid>,
}

const OPERATOR_CHARS: [char; 5] = ['=', '!', '<', '>', ':'];

// Двузнаковые раньше однозначных: «>=» не должно читаться как «>» со значением «=…».
const OPERATORS: [(&str, FieldOperator); 6] = [
    ("!=", FieldOperator::NotEqual),
    (">=", FieldOperator::GreaterOrEqual),
    ("<=", FieldOperator::LessOrEqual),
    ("=", FieldOperator::Equal),
    (">", FieldOperator::Greater),
    ("<", FieldOperator::Less),
];

/// Условия по полям из текста клиента. None — условий нет.
pub fn parse_field_filters(ws: &Workspace, project_id: &Uuid, expressions: Option<&[String]>) -> Result<Option<Vec<FieldCondition>>> {
    let Some(list) = expressions.filter(|l| !l.is_empty()) else {
        return Ok(None);
    };
    let catalog = ws.get_all::<FieldDefinition>(project_id)?;
    let mut task_types: Option<Vec<TaskType>> = None;
    let mut result = Vec::new();
    for expression in list {
        let at = expression.find(OPERATOR_CHARS);
        let Some(at) = at.filter(|a| *a > 0) else {
            return Err(Error::validation(format!(
                "Field filter '{expression}': expected 'Name=value' (also !=, >, >=, <, <=) or 'Name:set|unset|attached|detached'"
            )));
        };
        let name = expression[..at].trim();
        let target = resolve_filter_field(ws, project_id, expression, name, &catalog)?;

        if expression[at..].starts_with(':') {
            let word = expression[at + 1..].trim();
            let presence = match to_lower_invariant(word).as_str() {
                "set" => FieldOperator::Set,
                "unset" => FieldOperator::Unset,
                "attached" => FieldOperator::Attached,
                "detached" => FieldOperator::Detached,
                _ => {
                    return Err(Error::validation(format!(
                        "Field filter '{expression}': unknown ':{word}' (use :set, :unset, :attached or :detached)"
                    )));
                }
            };
            let mut type_ids = None;
            if matches!(presence, FieldOperator::Attached | FieldOperator::Detached)
                && let Some(field_id) = target.field_id
            {
                let types = match &task_types {
                    Some(t) => t,
                    None => task_types.insert(ws.get_all::<TaskType>(project_id)?),
                };
                type_ids = Some(
                    types
                        .iter()
                        .filter(|x| x.fields.iter().any(|f| f.field_id == field_id))
                        .map(|x| x.id)
                        .collect(),
                );
            }
            result.push(FieldCondition {
                name: target.name,
                field_type: target.field_type,
                operator: presence,
                value: None,
                number: None,
                field_id: target.field_id,
                type_ids,
            });
            continue;
        }
        result.push(parse_comparison(ws, project_id, expression, &target, at)?);
    }
    Ok(Some(result))
}

fn resolve_filter_field(
    ws: &Workspace,
    project_id: &Uuid,
    expression: &str,
    name: &str,
    catalog: &[FieldDefinition],
) -> Result<FilterField> {
    if let Some(field) = catalog.iter().find(|x| eq_ignore_case(&x.name, name)) {
        return Ok(FilterField {
            name: field.name.clone(),
            field_type: field.field_type,
            enum_id: field.enum_id,
            field_id: Some(field.id),
        });
    }
    let kinds = ws.index().own_field_kinds(project_id, &field_name_key(name))?;
    match kinds.len() {
        0 => Err(Error::validation(format!(
            "Field filter '{expression}': no field '{name}' in the project's catalog and no task has an own field with this name"
        ))),
        1 => Ok(FilterField {
            name: name.to_string(),
            field_type: kinds[0].field_type,
            enum_id: kinds[0].enum_id,
            field_id: None,
        }),
        _ => Err(Error::validation(format!(
            "Field filter '{expression}': tasks have own fields '{name}' of several types ({}) and the catalog has no such field, so the value cannot be read",
            kind_names(ws, project_id, &kinds)?.join(", ")
        ))),
    }
}

fn kind_names(ws: &Workspace, project_id: &Uuid, kinds: &[OwnFieldKind]) -> Result<Vec<String>> {
    let enumerations = if kinds.iter().any(|x| x.enum_id.is_some()) {
        ws.get_all::<FieldEnum>(project_id)?
    } else {
        vec![]
    };
    let mut names: Vec<String> = kinds
        .iter()
        .map(|x| match x.enum_id.and_then(|id| enumerations.iter().find(|e| e.id == id)) {
            Some(e) => format!("enum '{}'", e.name),
            None => x.field_type.name().to_string(),
        })
        .collect();
    names.sort_by(|a, b| tasker_core::tasks::ordinal(a, b));
    Ok(names)
}

fn parse_comparison(ws: &Workspace, project_id: &Uuid, expression: &str, field: &FilterField, at: usize) -> Result<FieldCondition> {
    let rest = &expression[at..];
    let Some((text, op)) = OPERATORS.iter().find(|(t, _)| rest.starts_with(t)).copied() else {
        return Err(Error::validation(format!(
            "Field filter '{expression}': expected one of = != > >= < <= after the name '{}'",
            field.name
        )));
    };
    let ordered = matches!(
        op,
        FieldOperator::Greater | FieldOperator::GreaterOrEqual | FieldOperator::Less | FieldOperator::LessOrEqual
    );
    if ordered && !matches!(field.field_type, FieldType::Int | FieldType::Float | FieldType::Date) {
        return Err(Error::validation(format!(
            "Field filter '{expression}': '{text}' does not apply to field '{}' of type {} (only int, float and date can be compared; use = or !=)",
            field.name,
            field.field_type.name()
        )));
    }
    let enumeration = match field.enum_id {
        Some(id) => ws.get_by_id::<FieldEnum>(project_id, &id)?,
        None => None,
    };
    let raw = expression[at + text.len()..].to_string();
    let value = fields::normalize(&field.name, field.field_type, false, enumeration.as_ref(), &[Some(raw)])?.remove(0);
    let number = if ordered && matches!(field.field_type, FieldType::Int | FieldType::Float) {
        field_number(&value)
    } else {
        None
    };
    Ok(FieldCondition {
        name: field.name.clone(),
        field_type: field.field_type,
        operator: op,
        value: Some(value),
        number,
        field_id: field.field_id,
        type_ids: None,
    })
}

/// Ключи упорядочивания из текста клиента. None — ключей нет.
pub fn parse_sort(ws: &Workspace, project_id: &Uuid, sort: Option<&str>) -> Result<Option<Vec<TaskSortKey>>> {
    let Some(sort) = sort.filter(|s| !s.trim().is_empty()) else {
        return Ok(None);
    };
    let mut catalog: Option<Vec<FieldDefinition>> = None;
    let mut seen: Vec<String> = Vec::new();
    let mut result = Vec::new();
    for raw in sort.split(',') {
        let text = raw.trim();
        let descending = text.starts_with('-');
        let name = if descending { &text[1..] } else { text }.trim();
        if name.is_empty() {
            return Err(Error::validation(format!(
                "Sort '{sort}': an empty key (expected keys separated by commas, '-' before a key for descending order)"
            )));
        }
        let key = field_name_key(name);
        if seen.contains(&key) {
            return Err(Error::validation(format!("Sort '{sort}': key '{name}' is listed twice")));
        }
        seen.push(key);

        if let Some((target, canonical)) = builtin_sort(name) {
            result.push(builtin_key(ws, project_id, target, descending, canonical)?);
            continue;
        }
        let catalog = match &catalog {
            Some(c) => c,
            None => catalog.insert(ws.get_all::<FieldDefinition>(project_id)?),
        };
        result.push(field_key(ws, project_id, sort, name, descending, catalog)?);
    }
    Ok(Some(result))
}

fn builtin_key(ws: &Workspace, project_id: &Uuid, target: SortTarget, descending: bool, name: &str) -> Result<TaskSortKey> {
    let mut key = TaskSortKey::new(target, descending, name);
    match target {
        SortTarget::Type => {
            let all: Vec<(Uuid, String)> = ws
                .get_all::<TaskType>(project_id)?
                .into_iter()
                .map(|x| (x.id, to_lower_invariant(&x.name)))
                .collect();
            key.type_ranks = Some(ranks(&all));
        }
        SortTarget::Series => {
            let all: Vec<(Uuid, String)> = ws.get_all::<Series>(project_id)?.into_iter().map(|x| (x.id, x.prefix)).collect();
            key.series_ranks = Some(ranks(&all));
        }
        SortTarget::Status => {
            let types = ws.get_all::<TaskType>(project_id)?;
            let sets = ws.get_all::<StatusSet>(project_id)?;
            let statuses = ws.get_all::<Status>(project_id)?;
            key.status_ranks = Some(status_ranks(&types, &sets, &statuses));
        }
        _ => {}
    }
    Ok(key)
}

fn field_key(
    ws: &Workspace,
    project_id: &Uuid,
    sort: &str,
    name: &str,
    descending: bool,
    catalog: &[FieldDefinition],
) -> Result<TaskSortKey> {
    let (mut field, enum_id) = match catalog.iter().find(|x| eq_ignore_case(&x.name, name)) {
        Some(found) => (
            SortField {
                name: found.name.clone(),
                field_type: found.field_type,
                field_id: Some(found.id),
                enum_values: None,
            },
            found.enum_id,
        ),
        None => {
            let kinds = ws.index().own_field_kinds(project_id, &field_name_key(name))?;
            match kinds.len() {
                0 => {
                    let mut names: Vec<&str> = catalog.iter().map(|x| x.name.as_str()).collect();
                    names.sort_by(|a, b| {
                        to_lower_invariant(a)
                            .cmp(&to_lower_invariant(b))
                            .then_with(|| tasker_core::tasks::ordinal(a, b))
                    });
                    let names: Vec<&str> = names.into_iter().take(30).collect();
                    return Err(Error::validation(format!(
                        "Sort '{sort}': unknown key '{name}'. Keys: {}, or the name of a field ({}or an own field of tasks); put '-' before a key for descending order, separate keys with commas",
                        BUILTIN_SORT_NAMES.join(", "),
                        if names.is_empty() {
                            String::new()
                        } else {
                            format!("catalog: {}; ", names.join(", "))
                        }
                    )));
                }
                1 => (
                    SortField {
                        name: name.to_string(),
                        field_type: kinds[0].field_type,
                        field_id: None,
                        enum_values: None,
                    },
                    kinds[0].enum_id,
                ),
                _ => {
                    return Err(Error::validation(format!(
                        "Sort '{sort}': tasks have own fields '{name}' of several types ({}) and the catalog has no such field, so it is unclear which one to sort by",
                        kind_names(ws, project_id, &kinds)?.join(", ")
                    )));
                }
            }
        }
    };
    if field.field_type == FieldType::Enum {
        let enumeration = match enum_id {
            Some(id) => ws.get_by_id::<FieldEnum>(project_id, &id)?,
            None => None,
        };
        field.enum_values = Some(
            enumeration
                .map(|e| e.values.iter().map(|v| guid_d(&v.id)).collect())
                .unwrap_or_default(),
        );
    }
    let mut key = TaskSortKey::new(SortTarget::Field, descending, &field.name);
    key.field = Some(field);
    Ok(key)
}
