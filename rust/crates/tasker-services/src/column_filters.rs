//! Условия колонки по полям (`ColumnFilters` в .NET): проверка, перевод в условия фильтра задач, запись текстом и проверка
//! «колонки не пересекаются».
use crate::error::{Error, Result};
use std::collections::HashMap;
use tasker_core::fields;
use tasker_core::ids::guid_d;
use tasker_core::model::{BoardColumn, ColumnFieldFilter, FieldDefinition, FieldEnum, FieldOperator, FieldType, TaskType};
use tasker_core::tasks::{FieldCondition, field_number};
use uuid::Uuid;

fn is_ordered(op: FieldOperator) -> bool {
    matches!(
        op,
        FieldOperator::Greater | FieldOperator::GreaterOrEqual | FieldOperator::Less | FieldOperator::LessOrEqual
    )
}

/// У условия есть значение для сравнения.
fn has_operand(op: FieldOperator) -> bool {
    matches!(op, FieldOperator::Equal | FieldOperator::NotEqual) || is_ordered(op)
}

/// Условию нужно хотя бы одно значение у поля.
fn requires_value(op: FieldOperator) -> bool {
    matches!(op, FieldOperator::Equal | FieldOperator::Set) || is_ordered(op)
}

/// Условие колонки как условие фильтра задач: типы, в которых есть поле, и число для сравнений считаются при каждом чтении.
pub fn to_conditions(filters: &[ColumnFieldFilter], types: &[TaskType], fields: &HashMap<Uuid, FieldDefinition>) -> Vec<FieldCondition> {
    filters
        .iter()
        .map(|f| {
            let field = &fields[&f.field_id];
            let base = FieldCondition {
                name: field.name.clone(),
                field_type: field.field_type,
                operator: f.operator,
                value: None,
                number: None,
                field_id: Some(f.field_id),
                type_ids: None,
            };
            match f.operator {
                FieldOperator::Attached | FieldOperator::Detached => FieldCondition {
                    type_ids: Some(
                        types
                            .iter()
                            .filter(|t| t.fields.iter().any(|x| x.field_id == f.field_id))
                            .map(|t| t.id)
                            .collect(),
                    ),
                    ..base
                },
                op if is_ordered(op) => FieldCondition {
                    value: f.value.clone(),
                    number: f.value.as_deref().and_then(field_number),
                    ..base
                },
                _ => FieldCondition {
                    value: f.value.clone(),
                    ..base
                },
            }
        })
        .collect()
}

/// Условие в виде, понятном клиенту (`Имя=значение`, `Имя>=3`, `Имя:set`).
pub fn text(filter: &ColumnFieldFilter, fields: &HashMap<Uuid, FieldDefinition>, enums: &HashMap<Uuid, FieldEnum>) -> String {
    let Some(field) = fields.get(&filter.field_id) else {
        return format!(
            "{}{}{}",
            guid_d(&filter.field_id),
            symbol(filter.operator),
            filter.value.as_deref().unwrap_or("")
        );
    };
    let value = filter.value.as_ref().map(|v| {
        fields::texts(field.field_type, field.enum_id.and_then(|e| enums.get(&e)), std::slice::from_ref(v))
            .into_iter()
            .next()
            .unwrap_or_default()
    });
    format!("{}{}{}", field.name, symbol(filter.operator), value.unwrap_or_default())
}

pub fn symbol(op: FieldOperator) -> &'static str {
    match op {
        FieldOperator::Equal => "=",
        FieldOperator::NotEqual => "!=",
        FieldOperator::Greater => ">",
        FieldOperator::GreaterOrEqual => ">=",
        FieldOperator::Less => "<",
        FieldOperator::LessOrEqual => "<=",
        FieldOperator::Set => ":set",
        FieldOperator::Unset => ":unset",
        FieldOperator::Attached => ":attached",
        FieldOperator::Detached => ":detached",
    }
}

/// Проверка условия против каталога: поле есть, оператор подходит типу, значение есть где нужно и в канонической форме.
pub fn validate(
    filter: &ColumnFieldFilter,
    fields: &HashMap<Uuid, FieldDefinition>,
    enums: &HashMap<Uuid, FieldEnum>,
    where_: &str,
) -> Result<()> {
    let Some(field) = fields.get(&filter.field_id) else {
        return Err(Error::validation(format!(
            "{where_}: field {} not found in the project's catalog",
            guid_d(&filter.field_id)
        )));
    };
    if !has_operand(filter.operator) {
        if filter.value.is_some() {
            return Err(Error::validation(format!(
                "{where_}: '{}' on field '{}' takes no value",
                symbol(filter.operator),
                field.name
            )));
        }
        return Ok(());
    }
    let Some(value) = &filter.value else {
        return Err(Error::validation(format!(
            "{where_}: '{}' on field '{}' needs a value",
            symbol(filter.operator),
            field.name
        )));
    };
    if is_ordered(filter.operator) && !matches!(field.field_type, FieldType::Int | FieldType::Float | FieldType::Date) {
        return Err(Error::validation(format!(
            "{where_}: '{}' does not apply to field '{}' of type {} (only int, float and date can be compared; use = or !=)",
            symbol(filter.operator),
            field.name,
            field.field_type.name()
        )));
    }
    let enumeration = field.enum_id.and_then(|e| enums.get(&e));
    let canonical = fields::normalize(&field.name, field.field_type, false, enumeration, &[Some(value.clone())])
        .map_err(|e| Error::validation(format!("{where_}: {}", e.message())))?
        .remove(0);
    // Хранится только каноническая запись (у enum — id значения).
    if canonical != *value {
        return Err(Error::validation(format!(
            "{where_}: value '{value}' of field '{}' is not in canonical form ('{canonical}')",
            field.name
        )));
    }
    Ok(())
}

/// Заведомо ли ни одна задача не попадёт в обе колонки: хотя бы одна пара их условий по одному полю не выполняется вместе.
pub fn are_exclusive(a: &BoardColumn, b: &BoardColumn, fields: &HashMap<Uuid, FieldDefinition>) -> bool {
    a.field_conditions.iter().any(|x| {
        b.field_conditions
            .iter()
            .any(|y| x.field_id == y.field_id && fields.get(&x.field_id).is_some_and(|f| one_way(x, y, f) || one_way(y, x, f)))
    })
}

/// Первая пара колонок с общим статусом и неисключающими условиями.
pub struct Overlap<'c> {
    pub first: &'c BoardColumn,
    pub second: &'c BoardColumn,
    pub status: Uuid,
    pub second_index: usize,
}

pub fn find_overlap<'c>(columns: &'c [BoardColumn], fields: &HashMap<Uuid, FieldDefinition>) -> Option<Overlap<'c>> {
    for j in 1..columns.len() {
        for i in 0..j {
            let shared = columns[i].status_ids.iter().find(|s| columns[j].status_ids.contains(s));
            if let Some(shared) = shared
                && !are_exclusive(&columns[i], &columns[j], fields)
            {
                return Some(Overlap {
                    first: &columns[i],
                    second: &columns[j],
                    status: *shared,
                    second_index: j,
                });
            }
        }
    }
    None
}

fn one_way(x: &ColumnFieldFilter, y: &ColumnFieldFilter, field: &FieldDefinition) -> bool {
    if matches!(x.operator, FieldOperator::Unset | FieldOperator::Detached) && requires_value(y.operator) {
        return true;
    }
    if x.operator == FieldOperator::Detached && y.operator == FieldOperator::Attached {
        return true;
    }
    if x.operator == FieldOperator::Equal && y.operator == FieldOperator::NotEqual && x.value == y.value {
        return true;
    }
    // Дальше — только поле с одним значением и условия «равно» и сравнения.
    let comparable = |op: FieldOperator| op == FieldOperator::Equal || is_ordered(op);
    if field.multiple || !comparable(x.operator) || !comparable(y.operator) {
        return false;
    }
    let numeric = matches!(field.field_type, FieldType::Int | FieldType::Float);
    if !numeric && field.field_type != FieldType::Date {
        return x.operator == FieldOperator::Equal && y.operator == FieldOperator::Equal && x.value != y.value;
    }

    let compare = |left: &str, right: &str| -> std::cmp::Ordering {
        if numeric {
            field_number(left)
                .unwrap_or(0.0)
                .partial_cmp(&field_number(right).unwrap_or(0.0))
                .unwrap_or(std::cmp::Ordering::Equal)
        } else {
            tasker_core::tasks::ordinal(left, right)
        }
    };
    let (low_x, high_x) = bounds(x);
    let (low_y, high_y) = bounds(y);
    let low = tighter(low_x, low_y, &compare, true);
    let high = tighter(high_x, high_y, &compare, false);
    let (Some(low), Some(high)) = (low, high) else {
        return false;
    };
    match compare(&low.0, &high.0) {
        std::cmp::Ordering::Greater => true,
        std::cmp::Ordering::Equal => low.1 || high.1,
        std::cmp::Ordering::Less => false,
    }
}

/// Граница: значение и признак «открытая».
type Bound = (String, bool);

fn bounds(f: &ColumnFieldFilter) -> (Option<Bound>, Option<Bound>) {
    let v = f.value.clone().unwrap_or_default();
    match f.operator {
        FieldOperator::Equal => (Some((v.clone(), false)), Some((v, false))),
        FieldOperator::Greater => (Some((v, true)), None),
        FieldOperator::GreaterOrEqual => (Some((v, false)), None),
        FieldOperator::Less => (None, Some((v, true))),
        _ => (None, Some((v, false))),
    }
}

/// Из двух границ одной стороны — более тугая (нижняя — большая, верхняя — меньшая; при равенстве — открытая).
fn tighter(a: Option<Bound>, b: Option<Bound>, compare: &dyn Fn(&str, &str) -> std::cmp::Ordering, higher: bool) -> Option<Bound> {
    match (a, b) {
        (None, b) => b,
        (a, None) => a,
        (Some(a), Some(b)) => match compare(&a.0, &b.0) {
            std::cmp::Ordering::Equal => Some(if a.1 { a } else { b }),
            std::cmp::Ordering::Greater => Some(if higher { a } else { b }),
            std::cmp::Ordering::Less => Some(if higher { b } else { a }),
        },
    }
}
