//! JSON результатов инструментов — формы записей .NET (`TaskerJson`: camelCase, enum строками, свойства в порядке объявления, null не
//! пропускаются; даты как `System.Text.Json` — `format_json`). Собирается руками: `serde` у моделей пишет даты в форме «O»
//! и не знает вычисляемых свойств (`isAgent`, `isSymmetric`, `needsAttention`).
use serde_json::{Map, Value};
use tasker_core::Timestamp;
use tasker_core::ids::guid_d;
use tasker_core::locks::LockedEntity;
use tasker_core::model::{
    Board, BoardColumn, ColumnFieldFilter, FieldDefinition, FieldEnum, LinkType, OwnField, Project, Series, Status, StatusSet, TaskField,
    TaskItem, TaskSeriesNumber, TaskType, User,
};
use tasker_core::tasks::ListPage;
use tasker_files::index::{NumberConflict, WorkspaceProblem};
use tasker_services::CascadeResult;
use tasker_services::health::{PrefixConflict, SeriesHealth};
use tasker_services::links::{LinkedTask, TaskLinkView};
use tasker_services::locks::LockInfo;
use tasker_services::preview::{StatusListItem, TaskListItem, TaskTypeListItem};
use tasker_services::task::TaskDetails;
use tasker_services::task_fields::TaskFieldView;
use uuid::Uuid;

/// Объект с порядком ключей как в записи.
pub fn object(pairs: Vec<(&str, Value)>) -> Value {
    Value::Object(pairs.into_iter().map(|(k, v)| (k.to_string(), v)).collect::<Map<_, _>>())
}

pub fn guid(id: &Uuid) -> Value {
    Value::String(guid_d(id))
}

pub fn guid_opt(id: &Option<Uuid>) -> Value {
    id.as_ref().map(guid).unwrap_or(Value::Null)
}

pub fn guids(ids: &[Uuid]) -> Value {
    Value::Array(ids.iter().map(guid).collect())
}

pub fn strings(items: &[String]) -> Value {
    Value::Array(items.iter().map(|s| Value::String(s.clone())).collect())
}

pub fn string_opt(text: &Option<String>) -> Value {
    text.as_ref().map(|s| Value::String(s.clone())).unwrap_or(Value::Null)
}

pub fn timestamp(at: &Timestamp) -> Value {
    Value::String(at.format_json())
}

/// `ListDto<T>`: `totalCount`, `offset`, `limit`, `data`.
pub fn page<T>(page: &ListPage<T>, item: impl Fn(&T) -> Value) -> Value {
    object(vec![
        ("totalCount", Value::from(page.total_count)),
        ("offset", Value::from(page.offset)),
        ("limit", Value::from(page.limit)),
        ("data", Value::Array(page.data.iter().map(item).collect())),
    ])
}

pub fn project(p: &Project) -> Value {
    object(vec![
        ("id", guid(&p.id)),
        ("name", Value::String(p.name.clone())),
        ("createdAt", timestamp(&p.created_at)),
        ("version", Value::String(p.version.clone())),
    ])
}

pub fn user(u: &User) -> Value {
    object(vec![
        ("id", guid(&u.id)),
        ("username", Value::String(u.username.clone())),
        ("kind", Value::String(if u.is_agent() { "agent" } else { "human" }.to_string())),
        ("ownerId", guid_opt(&u.owner_id)),
        ("email", string_opt(&u.email)),
        ("isAdmin", Value::Bool(u.is_admin)),
        ("createdAt", timestamp(&u.created_at)),
        ("version", Value::String(u.version.clone())),
        ("isAgent", Value::Bool(u.is_agent())),
    ])
}

fn status_fields(s: &Status) -> Vec<(&'static str, Value)> {
    vec![
        ("id", guid(&s.id)),
        ("projectId", guid(&s.project_id)),
        ("name", Value::String(s.name.clone())),
        ("color", Value::String(s.color.clone())),
        ("description", Value::String(s.description.clone())),
        ("version", Value::String(s.version.clone())),
    ]
}

pub fn status(s: &Status) -> Value {
    object(status_fields(s))
}

pub fn status_list_item(item: &StatusListItem) -> Value {
    let mut pairs = vec![
        ("descriptionTruncated", Value::Bool(item.description_truncated)),
        ("descriptionLength", Value::from(item.description_length)),
    ];
    pairs.extend(status_fields(&item.status));
    object(pairs)
}

pub fn status_set(s: &StatusSet) -> Value {
    object(vec![
        ("id", guid(&s.id)),
        ("projectId", guid(&s.project_id)),
        ("name", Value::String(s.name.clone())),
        ("statusIds", guids(&s.status_ids)),
        ("version", Value::String(s.version.clone())),
    ])
}

fn task_type_fields(t: &TaskType) -> Vec<(&'static str, Value)> {
    vec![
        ("id", guid(&t.id)),
        ("projectId", guid(&t.project_id)),
        ("name", Value::String(t.name.clone())),
        ("description", Value::String(t.description.clone())),
        ("statusSetId", guid(&t.status_set_id)),
        (
            "fields",
            Value::Array(
                t.fields
                    .iter()
                    .map(|f| object(vec![("fieldId", guid(&f.field_id)), ("required", Value::Bool(f.required))]))
                    .collect(),
            ),
        ),
        ("version", Value::String(t.version.clone())),
    ]
}

pub fn task_type(t: &TaskType) -> Value {
    object(task_type_fields(t))
}

pub fn task_type_list_item(item: &TaskTypeListItem) -> Value {
    let mut pairs = vec![
        ("descriptionTruncated", Value::Bool(item.description_truncated)),
        ("descriptionLength", Value::from(item.description_length)),
    ];
    pairs.extend(task_type_fields(&item.task_type));
    object(pairs)
}

pub fn field(f: &FieldDefinition) -> Value {
    object(vec![
        ("id", guid(&f.id)),
        ("projectId", guid(&f.project_id)),
        ("name", Value::String(f.name.clone())),
        ("type", Value::String(f.field_type.name().to_string())),
        ("multiple", Value::Bool(f.multiple)),
        ("enumId", guid_opt(&f.enum_id)),
        ("version", Value::String(f.version.clone())),
    ])
}

pub fn field_enum(e: &FieldEnum) -> Value {
    object(vec![
        ("id", guid(&e.id)),
        ("projectId", guid(&e.project_id)),
        ("name", Value::String(e.name.clone())),
        (
            "values",
            Value::Array(
                e.values
                    .iter()
                    .map(|v| object(vec![("id", guid(&v.id)), ("name", Value::String(v.name.clone()))]))
                    .collect(),
            ),
        ),
        ("version", Value::String(e.version.clone())),
    ])
}

fn column_filter(f: &ColumnFieldFilter) -> Value {
    object(vec![
        ("fieldId", guid(&f.field_id)),
        ("operator", Value::String(f.operator.word())),
        ("value", string_opt(&f.value)),
    ])
}

pub fn column(c: &BoardColumn) -> Value {
    object(vec![
        ("id", guid(&c.id)),
        ("name", Value::String(c.name.clone())),
        ("statusIds", guids(&c.status_ids)),
        (
            "fieldConditions",
            Value::Array(c.field_conditions.iter().map(column_filter).collect()),
        ),
        (
            "dropStatuses",
            Value::Object(
                c.drop_statuses
                    .iter()
                    .map(|(set, status)| (guid_d(set), guid(status)))
                    .collect::<Map<_, _>>(),
            ),
        ),
    ])
}

pub fn board(b: &Board) -> Value {
    object(vec![
        ("id", guid(&b.id)),
        ("projectId", guid(&b.project_id)),
        ("name", Value::String(b.name.clone())),
        ("statusSetIds", guids(&b.status_set_ids)),
        ("columns", Value::Array(b.columns.iter().map(column).collect())),
        ("version", Value::String(b.version.clone())),
    ])
}

pub fn series(s: &Series) -> Value {
    object(vec![
        ("id", guid(&s.id)),
        ("projectId", guid(&s.project_id)),
        ("name", Value::String(s.name.clone())),
        ("prefix", Value::String(s.prefix.clone())),
        ("version", Value::String(s.version.clone())),
    ])
}

pub fn link_type(t: &LinkType) -> Value {
    object(vec![
        ("id", guid(&t.id)),
        ("projectId", guid(&t.project_id)),
        ("name", Value::String(t.name.clone())),
        ("outwardName", Value::String(t.outward_name.clone())),
        ("inwardName", Value::String(t.inward_name.clone())),
        ("version", Value::String(t.version.clone())),
        ("allowCycles", Value::Bool(t.allow_cycles)),
        ("hierarchical", Value::Bool(t.hierarchical)),
        ("isSymmetric", Value::Bool(t.is_symmetric())),
    ])
}

fn series_number(n: &TaskSeriesNumber) -> Value {
    object(vec![("seriesId", guid(&n.series_id)), ("number", Value::from(n.number))])
}

pub fn series_numbers(numbers: &[TaskSeriesNumber]) -> Value {
    Value::Array(numbers.iter().map(series_number).collect())
}

fn own_field(o: &OwnField) -> Value {
    object(vec![
        ("name", Value::String(o.name.clone())),
        ("type", Value::String(o.field_type.name().to_string())),
        ("required", Value::Bool(o.required)),
        ("multiple", Value::Bool(o.multiple)),
        ("enumId", guid_opt(&o.enum_id)),
    ])
}

fn task_field(f: &TaskField) -> Value {
    object(vec![
        ("fieldId", guid(&f.field_id)),
        ("values", strings(&f.values)),
        ("own", f.own.as_ref().map(own_field).unwrap_or(Value::Null)),
    ])
}

fn task_fields(t: &TaskItem) -> Vec<(&'static str, Value)> {
    vec![
        ("id", guid(&t.id)),
        ("projectId", guid(&t.project_id)),
        ("title", Value::String(t.title.clone())),
        ("description", string_opt(&t.description)),
        ("typeId", guid(&t.type_id)),
        ("statusId", guid(&t.status_id)),
        ("seriesNumbers", series_numbers(&t.series_numbers)),
        (
            "links",
            Value::Array(
                t.links
                    .iter()
                    .map(|l| object(vec![("typeId", guid(&l.type_id)), ("targetId", guid(&l.target_id))]))
                    .collect(),
            ),
        ),
        ("fields", Value::Array(t.fields.iter().map(task_field).collect())),
        ("createdAt", timestamp(&t.created_at)),
        ("updatedAt", timestamp(&t.updated_at)),
        ("version", Value::String(t.version.clone())),
    ]
}

pub fn task(t: &TaskItem) -> Value {
    object(task_fields(t))
}

/// `TaskListItem`: усечённое описание, число связей, родители и дочерние, затем сама задача.
pub fn task_list_item(item: &TaskListItem) -> Value {
    let mut pairs = vec![
        ("descriptionTruncated", Value::Bool(item.description_truncated)),
        ("descriptionLength", Value::from(item.description_length)),
        ("linksCount", Value::from(item.links_count)),
        ("parentIds", guids(&item.parent_ids)),
        ("childCount", Value::from(item.child_count)),
    ];
    pairs.extend(task_fields(&item.task));
    object(pairs)
}

pub fn task_field_view(v: &TaskFieldView) -> Value {
    object(vec![
        ("fieldId", guid(&v.field_id)),
        ("name", Value::String(v.name.clone())),
        ("type", Value::String(v.field_type.name().to_string())),
        ("multiple", Value::Bool(v.multiple)),
        ("enumId", guid_opt(&v.enum_id)),
        ("required", Value::Bool(v.required)),
        ("source", Value::String(v.source.name().to_string())),
        ("values", strings(&v.values)),
        ("texts", strings(&v.texts)),
    ])
}

fn linked_task(t: &LinkedTask) -> Value {
    object(vec![
        ("id", guid(&t.id)),
        ("title", Value::String(t.title.clone())),
        ("statusId", guid(&t.status_id)),
        ("seriesNumbers", series_numbers(&t.series_numbers)),
    ])
}

pub fn link_view(v: &TaskLinkView) -> Value {
    object(vec![
        ("typeId", guid(&v.type_id)),
        ("typeName", Value::String(v.type_name.clone())),
        ("direction", Value::String(v.direction.name().to_string())),
        ("name", Value::String(v.name.clone())),
        ("task", linked_task(&v.task)),
    ])
}

pub fn link_views(views: &[TaskLinkView]) -> Value {
    Value::Array(views.iter().map(link_view).collect())
}

/// `TaskDetails`: виды полей, связи с обеих сторон, их число, затем задача.
pub fn task_details(d: &TaskDetails) -> Value {
    let mut pairs = vec![
        ("fieldViews", Value::Array(d.field_views.iter().map(task_field_view).collect())),
        ("linkViews", link_views(&d.link_views)),
        ("linkCount", Value::from(d.link_count)),
    ];
    pairs.extend(task_fields(&d.task));
    object(pairs)
}

/// `LockedEntity` как `JsonStringEnumConverter(CamelCase)`: `taskType`, `statusSet`.
pub fn locked_entity(entity: LockedEntity) -> Value {
    let name = entity.name();
    let mut s = String::with_capacity(name.len());
    s.push(name.chars().next().unwrap_or('x').to_ascii_lowercase());
    s.push_str(&name[1..]);
    Value::String(s)
}

pub fn lock_info(l: &LockInfo) -> Value {
    object(vec![
        ("entity", locked_entity(l.entity)),
        ("id", guid(&l.id)),
        ("holder", Value::String(l.holder.clone())),
        ("mine", Value::Bool(l.mine)),
        ("acquiredAt", timestamp(&l.acquired_at)),
        ("expiresAt", timestamp(&l.expires_at)),
    ])
}

fn number_conflict(c: &NumberConflict) -> Value {
    object(vec![
        ("seriesId", guid(&c.series_id)),
        ("number", Value::from(c.number)),
        ("taskIds", guids(&c.task_ids)),
    ])
}

fn prefix_conflict(c: &PrefixConflict) -> Value {
    object(vec![
        ("prefix", Value::String(c.prefix.clone())),
        ("seriesIds", guids(&c.series_ids)),
    ])
}

pub fn series_health(h: &SeriesHealth) -> Value {
    object(vec![
        (
            "numberConflicts",
            Value::Array(h.number_conflicts.iter().map(number_conflict).collect()),
        ),
        (
            "prefixConflicts",
            Value::Array(h.prefix_conflicts.iter().map(prefix_conflict).collect()),
        ),
        ("tasksWithInvalidSeries", Value::from(h.tasks_with_invalid_series)),
        ("unreadableSeriesFiles", Value::from(h.unreadable_series_files)),
        ("needsAttention", Value::Bool(h.needs_attention())),
    ])
}

pub fn workspace_problem(p: &WorkspaceProblem) -> Value {
    object(vec![
        ("path", Value::String(p.path.clone())),
        ("error", Value::String(p.error.clone())),
    ])
}

/// `CascadeResult<T>`: свойства сущности, затем `affectedTasks` и (при ненулевом) `affectedColumns`.
pub fn cascade<T>(result: &CascadeResult<T>, value: impl Fn(&T) -> Value) -> Value {
    let mut map = match value(&result.value) {
        Value::Object(map) => map,
        _ => Map::new(),
    };
    map.insert("affectedTasks".into(), Value::from(result.affected_tasks));
    if result.affected_columns != 0 {
        map.insert("affectedColumns".into(), Value::from(result.affected_columns));
    }
    Value::Object(map)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn locked_entity_names_are_camel_case() {
        assert_eq!(locked_entity(LockedEntity::TaskType), Value::String("taskType".into()));
        assert_eq!(locked_entity(LockedEntity::Task), Value::String("task".into()));
    }

    #[test]
    fn page_shape() {
        let list = ListPage {
            total_count: 3,
            offset: 1,
            limit: 2,
            data: vec![1, 2],
        };
        assert_eq!(
            tasker_core::json::to_string(&page(&list, |n| Value::from(*n))),
            "{\"totalCount\":3,\"offset\":1,\"limit\":2,\"data\":[1,2]}"
        );
    }
}
