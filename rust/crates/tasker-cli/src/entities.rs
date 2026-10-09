//! JSON сущностей для `--json` — как `JsonSerializer.Serialize(x, TaskerJson.Options)` в .NET: camelCase, порядок полей как в
//! записях C#, enum строками, даты как System.Text.Json (`Timestamp::format_json`).
use crate::json::{id, ids, object, opt_id, opt_str};
use serde_json::Value;
use tasker_core::Timestamp;
use tasker_core::model::{
    Board, BoardColumn, FieldDefinition, FieldEnum, LinkType, OwnField, Project, Series, Status, StatusSet, TaskField, TaskItem, TaskLink,
    TaskSeriesNumber, TaskType, TaskTypeField, User, UserKind,
};
use tasker_services::links::TaskLinkView;
use tasker_services::locks::LockInfo;
use tasker_services::preview::{StatusListItem, TaskListItem, TaskTypeListItem};
use tasker_services::task_fields::TaskFieldView;

pub fn time(value: &Timestamp) -> Value {
    Value::String(value.format_json())
}

pub fn text(value: &str) -> Value {
    Value::String(value.to_string())
}

pub fn project(x: &Project) -> Value {
    object(vec![
        ("id", id(&x.id)),
        ("name", text(&x.name)),
        ("createdAt", time(&x.created_at)),
        ("version", text(&x.version)),
    ])
}

pub fn status(x: &Status) -> Value {
    object(vec![
        ("id", id(&x.id)),
        ("projectId", id(&x.project_id)),
        ("name", text(&x.name)),
        ("color", text(&x.color)),
        ("description", text(&x.description)),
        ("version", text(&x.version)),
    ])
}

/// Запись списка с усечённым описанием: сначала `descriptionTruncated`, `descriptionLength`, затем поля сущности.
fn with_preview(truncated: bool, length: usize, mut entity: Value) -> Value {
    let mut map = serde_json::Map::new();
    map.insert("descriptionTruncated".into(), Value::Bool(truncated));
    map.insert("descriptionLength".into(), Value::from(length));
    if let Value::Object(fields) = &mut entity {
        for (key, value) in std::mem::take(fields) {
            map.insert(key, value);
        }
    }
    Value::Object(map)
}

pub fn status_list_item(x: &StatusListItem) -> Value {
    with_preview(x.description_truncated, x.description_length, status(&x.status))
}

pub fn status_set(x: &StatusSet) -> Value {
    object(vec![
        ("id", id(&x.id)),
        ("projectId", id(&x.project_id)),
        ("name", text(&x.name)),
        ("statusIds", ids(&x.status_ids)),
        ("version", text(&x.version)),
    ])
}

fn task_type_field(x: &TaskTypeField) -> Value {
    object(vec![("fieldId", id(&x.field_id)), ("required", Value::Bool(x.required))])
}

pub fn task_type(x: &TaskType) -> Value {
    object(vec![
        ("id", id(&x.id)),
        ("projectId", id(&x.project_id)),
        ("name", text(&x.name)),
        ("description", text(&x.description)),
        ("statusSetId", id(&x.status_set_id)),
        ("fields", Value::Array(x.fields.iter().map(task_type_field).collect())),
        ("version", text(&x.version)),
    ])
}

pub fn task_type_list_item(x: &TaskTypeListItem) -> Value {
    with_preview(x.description_truncated, x.description_length, task_type(&x.task_type))
}

pub fn series(x: &Series) -> Value {
    object(vec![
        ("id", id(&x.id)),
        ("projectId", id(&x.project_id)),
        ("name", text(&x.name)),
        ("prefix", text(&x.prefix)),
        ("version", text(&x.version)),
    ])
}

pub fn field(x: &FieldDefinition) -> Value {
    object(vec![
        ("id", id(&x.id)),
        ("projectId", id(&x.project_id)),
        ("name", text(&x.name)),
        ("type", text(x.field_type.name())),
        ("multiple", Value::Bool(x.multiple)),
        ("enumId", opt_id(x.enum_id.as_ref())),
        ("version", text(&x.version)),
    ])
}

pub fn field_enum(x: &FieldEnum) -> Value {
    object(vec![
        ("id", id(&x.id)),
        ("projectId", id(&x.project_id)),
        ("name", text(&x.name)),
        (
            "values",
            Value::Array(
                x.values
                    .iter()
                    .map(|v| object(vec![("id", id(&v.id)), ("name", text(&v.name))]))
                    .collect(),
            ),
        ),
        ("version", text(&x.version)),
    ])
}

fn board_column(x: &BoardColumn) -> Value {
    let drop: serde_json::Map<String, Value> = x
        .drop_statuses
        .iter()
        .map(|(set, status)| (tasker_core::ids::guid_d(set), id(status)))
        .collect();
    object(vec![
        ("id", id(&x.id)),
        ("name", text(&x.name)),
        ("statusIds", ids(&x.status_ids)),
        (
            "fieldConditions",
            Value::Array(
                x.field_conditions
                    .iter()
                    .map(|c| {
                        object(vec![
                            ("fieldId", id(&c.field_id)),
                            ("operator", text(&c.operator.word())),
                            ("value", opt_str(c.value.as_deref())),
                        ])
                    })
                    .collect(),
            ),
        ),
        ("dropStatuses", Value::Object(drop)),
    ])
}

pub fn board(x: &Board) -> Value {
    object(vec![
        ("id", id(&x.id)),
        ("projectId", id(&x.project_id)),
        ("name", text(&x.name)),
        ("statusSetIds", ids(&x.status_set_ids)),
        ("columns", Value::Array(x.columns.iter().map(board_column).collect())),
        ("version", text(&x.version)),
    ])
}

pub fn link_type(x: &LinkType) -> Value {
    object(vec![
        ("id", id(&x.id)),
        ("projectId", id(&x.project_id)),
        ("name", text(&x.name)),
        ("outwardName", text(&x.outward_name)),
        ("inwardName", text(&x.inward_name)),
        ("version", text(&x.version)),
        ("allowCycles", Value::Bool(x.allow_cycles)),
        ("hierarchical", Value::Bool(x.hierarchical)),
        ("isSymmetric", Value::Bool(x.is_symmetric())),
    ])
}

pub fn user(x: &User) -> Value {
    object(vec![
        ("id", id(&x.id)),
        ("username", text(&x.username)),
        (
            "kind",
            text(match x.kind {
                UserKind::Human => "human",
                UserKind::Agent => "agent",
            }),
        ),
        ("ownerId", opt_id(x.owner_id.as_ref())),
        ("email", opt_str(x.email.as_deref())),
        ("isAdmin", Value::Bool(x.is_admin)),
        ("createdAt", time(&x.created_at)),
        ("version", text(&x.version)),
        ("isAgent", Value::Bool(x.is_agent())),
    ])
}

pub fn series_number(x: &TaskSeriesNumber) -> Value {
    object(vec![("seriesId", id(&x.series_id)), ("number", Value::from(x.number))])
}

pub fn series_numbers(x: &[TaskSeriesNumber]) -> Value {
    Value::Array(x.iter().map(series_number).collect())
}

fn task_link(x: &TaskLink) -> Value {
    object(vec![("typeId", id(&x.type_id)), ("targetId", id(&x.target_id))])
}

fn own_field(x: &OwnField) -> Value {
    object(vec![
        ("name", text(&x.name)),
        ("type", text(x.field_type.name())),
        ("required", Value::Bool(x.required)),
        ("multiple", Value::Bool(x.multiple)),
        ("enumId", opt_id(x.enum_id.as_ref())),
    ])
}

fn task_field(x: &TaskField) -> Value {
    object(vec![
        ("fieldId", id(&x.field_id)),
        ("values", Value::Array(x.values.iter().map(|v| text(v)).collect())),
        ("own", x.own.as_ref().map(own_field).unwrap_or(Value::Null)),
    ])
}

pub fn task(x: &TaskItem) -> Value {
    object(vec![
        ("id", id(&x.id)),
        ("projectId", id(&x.project_id)),
        ("title", text(&x.title)),
        ("description", opt_str(x.description.as_deref())),
        ("typeId", id(&x.type_id)),
        ("statusId", id(&x.status_id)),
        ("seriesNumbers", series_numbers(&x.series_numbers)),
        ("links", Value::Array(x.links.iter().map(task_link).collect())),
        ("fields", Value::Array(x.fields.iter().map(task_field).collect())),
        ("createdAt", time(&x.created_at)),
        ("updatedAt", time(&x.updated_at)),
        ("version", text(&x.version)),
    ])
}

/// `TaskListItem`: усечённое описание, число связей, родители и число дочерних впереди полей задачи.
pub fn task_list_item(x: &TaskListItem) -> Value {
    let mut map = serde_json::Map::new();
    map.insert("descriptionTruncated".into(), Value::Bool(x.description_truncated));
    map.insert("descriptionLength".into(), Value::from(x.description_length));
    map.insert("linksCount".into(), Value::from(x.links_count));
    map.insert("parentIds".into(), ids(&x.parent_ids));
    map.insert("childCount".into(), Value::from(x.child_count));
    if let Value::Object(fields) = task(&x.task) {
        for (key, value) in fields {
            map.insert(key, value);
        }
    }
    Value::Object(map)
}

pub fn field_view(x: &TaskFieldView) -> Value {
    object(vec![
        ("fieldId", id(&x.field_id)),
        ("name", text(&x.name)),
        ("type", text(x.field_type.name())),
        ("multiple", Value::Bool(x.multiple)),
        ("enumId", opt_id(x.enum_id.as_ref())),
        ("required", Value::Bool(x.required)),
        ("source", text(x.source.name())),
        ("values", Value::Array(x.values.iter().map(|v| text(v)).collect())),
        ("texts", Value::Array(x.texts.iter().map(|v| text(v)).collect())),
    ])
}

pub fn link_view(x: &TaskLinkView) -> Value {
    object(vec![
        ("typeId", id(&x.type_id)),
        ("typeName", text(&x.type_name)),
        ("direction", text(x.direction.name())),
        ("name", text(&x.name)),
        (
            "task",
            object(vec![
                ("id", id(&x.task.id)),
                ("title", text(&x.task.title)),
                ("statusId", id(&x.task.status_id)),
                ("seriesNumbers", series_numbers(&x.task.series_numbers)),
            ]),
        ),
    ])
}

/// `LockedEntity` в JSON: camelCase имени (`taskType`).
pub fn locked_entity_json(entity: tasker_core::locks::LockedEntity) -> String {
    let name = entity.name();
    let mut s = String::with_capacity(name.len());
    let mut chars = name.chars();
    if let Some(first) = chars.next() {
        s.push(first.to_ascii_lowercase());
    }
    s.extend(chars);
    s
}

pub fn lock_info(x: &LockInfo) -> Value {
    object(vec![
        ("entity", text(&locked_entity_json(x.entity))),
        ("id", id(&x.id)),
        ("holder", text(&x.holder)),
        ("mine", Value::Bool(x.mine)),
        ("acquiredAt", time(&x.acquired_at)),
        ("expiresAt", time(&x.expires_at)),
    ])
}

/// `CascadeResult<T>`: свойства сущности, затем `affectedTasks`, затем `affectedColumns`, если он не ноль.
pub fn cascade(value: Value, affected_tasks: usize, affected_columns: usize) -> Value {
    let mut map = match value {
        Value::Object(map) => map,
        _ => serde_json::Map::new(),
    };
    map.insert("affectedTasks".into(), Value::from(affected_tasks));
    if affected_columns > 0 {
        map.insert("affectedColumns".into(), Value::from(affected_columns));
    }
    Value::Object(map)
}

/// `{ "deleted": id }`.
pub fn deleted(entity_id: &uuid::Uuid) -> Value {
    object(vec![("deleted", id(entity_id))])
}
