//! Сценарии `TaskFieldsTests.cs`: поля типов задач и значения у задач — каталог, обязательные, дополнительные и собственные поля,
//! каскады при снятии поля с типа и удалении значения перечисления, фильтры по полям, смена определения поля, блокировки.
mod common;

use common::*;
use std::time::Duration;
use tasker_core::ConflictCode;
use tasker_core::ids::guid_d;
use tasker_core::locks::LockedEntity;
use tasker_core::model::{FieldDefinition, FieldEnum, FieldType, OwnField, TaskItem, TaskType, TaskTypeField};
use tasker_core::tasks::Page;
use tasker_services::Workspace;
use tasker_services::field::{CreateField, UpdateField};
use tasker_services::field_conversion::{FieldChangeChoice, FieldValueMapping, SeveralValues};
use tasker_services::field_enum::{CreateFieldEnum, FieldEnumValueInput, RemovedEnumValues, UpdateFieldEnum};
use tasker_services::task::{CreateTask, UpdateTask};
use tasker_services::task_fields::{NewOwnField, TaskFieldChanges, TaskFieldSource, TaskFieldValueInput};
use tasker_services::task_type::{RemovedFieldValues, UpdateTaskType};
use uuid::Uuid;

fn field(env: &Env, name: &str, field_type: FieldType) -> FieldDefinition {
    field_with(env, name, field_type, false, None)
}

fn field_with(env: &Env, name: &str, field_type: FieldType, multiple: bool, enum_id: Option<Uuid>) -> FieldDefinition {
    env.ws
        .fields()
        .create(
            &env.project,
            &CreateField {
                name: name.into(),
                field_type,
                multiple: Some(multiple),
                enum_id,
            },
        )
        .unwrap()
}

fn new_field(env: &Env, name: &str) -> tasker_services::Result<FieldDefinition> {
    env.ws.fields().create(
        &env.project,
        &CreateField {
            name: name.into(),
            field_type: FieldType::String,
            multiple: None,
            enum_id: None,
        },
    )
}

fn enumeration(env: &Env, name: &str, values: &[&str]) -> FieldEnum {
    env.ws
        .enums()
        .create(
            &env.project,
            &CreateFieldEnum {
                name: name.into(),
                values: values.iter().map(|x| x.to_string()).collect(),
            },
        )
        .unwrap()
}

fn priority(env: &Env) -> FieldEnum {
    enumeration(env, "Priority", &["Low", "Medium", "High"])
}

fn tf(field: &FieldDefinition, required: bool) -> TaskTypeField {
    TaskTypeField {
        field_id: field.id,
        required,
    }
}

fn set(values: &[(&FieldDefinition, &[&str])]) -> TaskFieldChanges {
    TaskFieldChanges::values(values.iter().map(|(f, v)| (f.id, v.to_vec())).collect())
}

fn set_id(id: Uuid, values: &[&str]) -> TaskFieldChanges {
    TaskFieldChanges::values(vec![(id, values.to_vec())])
}

fn own(fields: Vec<NewOwnField>) -> TaskFieldChanges {
    TaskFieldChanges {
        new_own_fields: Some(fields),
        ..TaskFieldChanges::default()
    }
}

fn own_field(name: &str, field_type: FieldType, values: &[&str]) -> NewOwnField {
    NewOwnField {
        values: Some(values.iter().map(|x| x.to_string()).collect()),
        ..NewOwnField::new(name, field_type)
    }
}

fn new_task(env: &Env, task_type: &TaskType, title: &str, fields: Option<TaskFieldChanges>) -> tasker_services::Result<TaskItem> {
    // Как настоящие часы: каждая задача создана позже предыдущей, порядок по созданию определён.
    env.clock.advance(Duration::from_secs(1));
    env.ws.tasks().create(
        &env.project,
        &CreateTask {
            fields,
            ..CreateTask::new(title, task_type.id)
        },
    )
}

fn edit(env: &Env, task: &TaskItem, fields: Option<TaskFieldChanges>) -> tasker_services::Result<TaskItem> {
    edit_with(env, task, fields, None, None, None)
}

fn edit_with(
    env: &Env,
    task: &TaskItem,
    fields: Option<TaskFieldChanges>,
    title: Option<&str>,
    type_id: Option<Uuid>,
    status_id: Option<Uuid>,
) -> tasker_services::Result<TaskItem> {
    env.ws
        .tasks()
        .update(
            &env.project,
            &task.id,
            &UpdateTask {
                title: title.map(str::to_string),
                description: None,
                type_id,
                status_id,
                version: Some(task.version.clone()),
                fields,
            },
        )
        .map(|x| x.unwrap())
}

fn view(env: &Env, task: &TaskItem) -> Vec<tasker_services::task_fields::TaskFieldView> {
    env.ws.tasks().get_fields(&env.project, &task.id).unwrap().unwrap()
}

fn values(env: &Env, task: &TaskItem, name: &str) -> Vec<String> {
    view(env, task).into_iter().find(|x| x.name == name).unwrap().values
}

fn names(env: &Env, task: &TaskItem) -> Vec<String> {
    view(env, task).into_iter().map(|x| x.name).collect()
}

fn update_type(
    env: &Env,
    task_type: &TaskType,
    fields: Option<Vec<TaskTypeField>>,
    choice: Option<RemovedFieldValues>,
) -> tasker_services::Result<TaskType> {
    let current = env.ws.task_types().get_by_id(&env.project, &task_type.id).unwrap().unwrap();
    env.ws
        .task_types()
        .update(
            &env.project,
            &task_type.id,
            &UpdateTaskType {
                version: Some(current.version),
                fields,
                removed_fields: choice,
                ..UpdateTaskType::default()
            },
        )
        .map(|x| x.unwrap().value)
}

fn titles_by(env: &Env, filters: &[&str]) -> Vec<String> {
    let filters: Vec<String> = filters.iter().map(|x| x.to_string()).collect();
    let mut titles: Vec<String> = env
        .ws
        .tasks()
        .list(&env.project, None, Some(&filters), Page::first(50), -1, None)
        .unwrap()
        .data
        .into_iter()
        .map(|x| x.task.title)
        .collect();
    titles.sort();
    titles
}

fn list_error(env: &Env, filters: &[&str]) -> String {
    let filters: Vec<String> = filters.iter().map(|x| x.to_string()).collect();
    message(env.ws.tasks().list(&env.project, None, Some(&filters), Page::first(50), -1, None))
}

fn anna(env: &Env) -> Workspace {
    env.as_editor("user:anna", "Anna")
}

fn anna_locks(env: &Env, task: &TaskItem) {
    anna(env)
        .locks()
        .acquire(LockedEntity::Task, &task.id, "Task", Some(env.project))
        .unwrap();
}

fn anna_releases(env: &Env, task: &TaskItem) {
    anna(env).locks().release(LockedEntity::Task, &task.id).unwrap();
}

// ---- поля типа ----

#[test]
fn a_task_type_keeps_its_fields_in_order_with_the_required_flag_and_they_are_read_back() {
    let env = Env::new();
    let estimate = field(&env, "Estimate", FieldType::Int);
    let note = field(&env, "Note", FieldType::String);
    let task_type = env.type_with("Bug", vec![tf(&note, false), tf(&estimate, true)]);
    assert_eq!(
        task_type.fields.iter().map(|x| x.field_id).collect::<Vec<_>>(),
        vec![note.id, estimate.id]
    );
    let read = env.ws.task_types().get_by_id(&env.project, &task_type.id).unwrap().unwrap();
    assert_eq!(read.fields, vec![tf(&note, false), tf(&estimate, true)]);
    assert_eq!(read.version, task_type.version);
    assert_eq!(
        env.ws
            .task_types()
            .get_all(&env.project)
            .unwrap()
            .iter()
            .filter(|x| x.id == task_type.id)
            .count(),
        1
    );

    let updated = update_type(&env, &task_type, Some(vec![tf(&estimate, false), tf(&note, true)]), None).unwrap();
    assert_eq!(updated.fields, vec![tf(&estimate, false), tf(&note, true)]);
    let renamed = env
        .ws
        .task_types()
        .update(
            &env.project,
            &task_type.id,
            &UpdateTaskType {
                name: Some("Bug3".into()),
                version: Some(updated.version.clone()),
                ..UpdateTaskType::default()
            },
        )
        .unwrap()
        .unwrap()
        .value;
    assert_eq!(renamed.fields, updated.fields);
}

#[test]
fn type_fields_must_exist_in_the_catalog_and_not_repeat() {
    let env = Env::new();
    let f = field(&env, "Note", FieldType::String);
    let other = env
        .ws
        .projects()
        .create(&tasker_services::project::CreateProject { name: "Other".into() })
        .unwrap();
    let foreign = env
        .ws
        .fields()
        .create(
            &other.id,
            &CreateField {
                name: "Foreign".into(),
                field_type: FieldType::String,
                multiple: None,
                enum_id: None,
            },
        )
        .unwrap();
    let create = |fields: Vec<TaskTypeField>| {
        env.ws.task_types().create(
            &env.project,
            &tasker_services::task_type::CreateTaskType {
                name: "A".into(),
                status_set_id: env.set.id,
                fields: Some(fields),
                description: None,
            },
        )
    };
    assert!(
        message(create(vec![TaskTypeField {
            field_id: Uuid::new_v4(),
            required: false
        }]))
        .contains("not found in the project")
    );
    assert!(message(create(vec![tf(&foreign, false)])).contains("not found in the project"));
    assert_eq!(message(create(vec![tf(&f, false), tf(&f, true)])), "Fields contains duplicates");
    assert_eq!(env.ws.task_types().get_all(&env.project).unwrap().len(), 1);
}

// ---- значения ----

#[test]
fn values_are_checked_by_type_stored_in_canonical_form_and_shown_with_enum_names() {
    let env = Env::new();
    let prio = priority(&env);
    let number = field(&env, "Number", FieldType::Int);
    let ratio = field(&env, "Ratio", FieldType::Float);
    let flag = field(&env, "Flag", FieldType::Bool);
    let due = field(&env, "Due", FieldType::Date);
    let level = field_with(&env, "Level", FieldType::Enum, false, Some(prio.id));
    let note = field(&env, "Note", FieldType::String);
    let bug = env.type_with(
        "Bug",
        [&number, &ratio, &flag, &due, &level, &note].iter().map(|f| tf(f, false)).collect(),
    );

    let task = new_task(
        &env,
        &bug,
        "Task",
        Some(set(&[
            (&number, &["007"]),
            (&ratio, &[" 2.50 "]),
            (&flag, &["TRUE"]),
            (&due, &["2026-10-02"]),
            (&level, &["high"]),
            (&note, &["  text  "]),
        ])),
    )
    .unwrap();
    let read = env.get(&task.id);
    let v = view(&env, &read);
    assert_eq!(values(&env, &read, "Number"), vec!["7"]);
    assert_eq!(values(&env, &read, "Ratio"), vec!["2.5"]);
    assert_eq!(values(&env, &read, "Flag"), vec!["true"]);
    assert_eq!(values(&env, &read, "Due"), vec!["2026-10-02"]);
    assert_eq!(values(&env, &read, "Level"), vec![guid_d(&prio.values[2].id)]);
    assert_eq!(v.iter().find(|x| x.name == "Level").unwrap().texts, vec!["High"]);
    assert_eq!(values(&env, &read, "Note"), vec!["text"]);
    assert!(v.iter().all(|x| x.source == TaskFieldSource::Type));
    assert_eq!(names(&env, &read), vec!["Number", "Ratio", "Flag", "Due", "Level", "Note"]);

    let by_id = edit(&env, &read, Some(set(&[(&level, &[&guid_d(&prio.values[0].id)])]))).unwrap();
    assert_eq!(view(&env, &by_id).iter().find(|x| x.name == "Level").unwrap().texts, vec!["Low"]);
}

#[test]
fn wrong_values_are_rejected_and_several_values_need_a_multiple_field() {
    let env = Env::new();
    let prio = priority(&env);
    let number = field(&env, "Number", FieldType::Int);
    let ratio = field(&env, "Ratio", FieldType::Float);
    let flag = field(&env, "Flag", FieldType::Bool);
    let due = field(&env, "Due", FieldType::Date);
    let level = field_with(&env, "Level", FieldType::Enum, false, Some(prio.id));
    let tags = field_with(&env, "Tags", FieldType::String, true, None);
    let bug = env.type_with(
        "Bug",
        [&number, &ratio, &flag, &due, &level, &tags].iter().map(|f| tf(f, false)).collect(),
    );
    let invalid = |changes: TaskFieldChanges| message(new_task(&env, &bug, "T", Some(changes)));

    assert_eq!(invalid(set(&[(&number, &["1.5"])])), "Field 'Number': '1.5' is not an integer");
    assert!(invalid(set(&[(&number, &["abc"])])).contains("is not an integer"));
    assert!(invalid(set(&[(&ratio, &["NaN"])])).contains("is not a number"));
    assert!(invalid(set(&[(&ratio, &["1,5"])])).contains("is not a number"));
    assert!(invalid(set(&[(&flag, &["yes"])])).contains("true or false"));
    assert!(invalid(set(&[(&due, &["02.10.2026"])])).contains("yyyy-MM-dd"));
    assert!(invalid(set(&[(&due, &["2026-02-30"])])).contains("yyyy-MM-dd"));
    assert!(invalid(set(&[(&level, &["Urgent"])])).contains("is not a value of enum 'Priority'"));
    assert!(invalid(set(&[(&tags, &[" "])])).contains("must not be empty"));
    assert!(invalid(set(&[(&number, &["1", "2"])])).contains("single value"));
    assert!(invalid(set(&[(&tags, &["a", "A", "a"])])).contains("repeated"));
    assert!(invalid(set_id(Uuid::new_v4(), &["x"])).contains("the task has no field"));
    assert!(invalid(TaskFieldChanges::values(vec![(tags.id, vec!["a"]), (tags.id, vec!["b"])])).contains("duplicates"));
    assert!(
        invalid(TaskFieldChanges {
            remove_fields: Some(vec![tags.id]),
            ..TaskFieldChanges::default()
        })
        .contains("no fields to remove")
    );

    let task = new_task(&env, &bug, "T", Some(set(&[(&tags, &["b", "a", "c"])]))).unwrap();
    assert_eq!(values(&env, &task, "Tags"), vec!["b", "a", "c"]);
}

#[test]
fn clearing_a_value_removes_it_and_an_unset_type_field_is_not_stored_in_the_task() {
    let env = Env::new();
    let note = field(&env, "Note", FieldType::String);
    let bug = env.type_with("Bug", vec![tf(&note, false)]);
    let task = new_task(&env, &bug, "T", None).unwrap();
    assert!(env.get(&task.id).fields.is_empty());
    let with = edit(&env, &task, Some(set(&[(&note, &["x"])]))).unwrap();
    assert_eq!(env.get(&with.id).fields.len(), 1);
    let cleared = edit(&env, &with, Some(set(&[(&note, &[])]))).unwrap();
    assert!(env.get(&cleared.id).fields.is_empty());
    assert!(view(&env, &cleared)[0].values.is_empty());
}

// ---- обязательные поля ----

#[test]
fn a_required_field_without_a_value_fails_creation_listing_the_fields() {
    let env = Env::new();
    let estimate = field(&env, "Estimate", FieldType::Int);
    let owner = field(&env, "Owner", FieldType::String);
    let note = field(&env, "Note", FieldType::String);
    let bug = env.type_with("Bug", vec![tf(&estimate, true), tf(&owner, true), tf(&note, false)]);

    let error = message(new_task(&env, &bug, "T", Some(set(&[(&owner, &["Ann"])]))));
    assert_eq!(error, "Required fields have no value: 'Estimate'");
    assert_eq!(
        message(new_task(&env, &bug, "T", None)),
        "Required fields have no value: 'Estimate', 'Owner'"
    );
    assert!(env.all_tasks().is_empty());
    let task = new_task(&env, &bug, "T", Some(set(&[(&estimate, &["3"]), (&owner, &["Ann"])]))).unwrap();
    assert_eq!(env.get(&task.id).fields.len(), 2);
}

#[test]
fn a_required_field_added_later_leaves_tasks_alone_until_edited_and_a_status_change_never_checks_it() {
    let env = Env::new();
    let estimate = field(&env, "Estimate", FieldType::Int);
    let bug = env.type_with("Bug", vec![]);
    let task = new_task(&env, &bug, "Old", None).unwrap();
    update_type(&env, &bug, Some(vec![tf(&estimate, true)]), None).unwrap();
    assert!(env.get(&task.id).fields.is_empty());

    let moved = edit_with(&env, &task, None, None, None, Some(env.done.id)).unwrap();
    assert_eq!(moved.status_id, env.done.id);
    assert!(message(edit_with(&env, &moved, None, Some("Renamed"), None, None)).contains("'Estimate'"));
    assert_eq!(env.get(&moved.id).title, "Old");

    let fixed = edit_with(&env, &moved, Some(set(&[(&estimate, &["5"])])), Some("Renamed"), None, None).unwrap();
    assert_eq!(fixed.title, "Renamed");
    assert_eq!(values(&env, &fixed, "Estimate"), vec!["5"]);
    assert!(message(edit(&env, &fixed, Some(set(&[(&estimate, &[])])))).contains("'Estimate'"));
}

#[test]
fn creating_a_task_validates_status_and_values_in_one_step_and_a_failed_creation_leaves_nothing() {
    let env = Env::new();
    let estimate = field(&env, "Estimate", FieldType::Int);
    let bug = env.type_with("Bug", vec![tf(&estimate, true)]);
    assert!(new_task(&env, &bug, "T", Some(set(&[(&estimate, &["x"])]))).is_err());
    assert!(
        env.ws
            .tasks()
            .create(
                &env.project,
                &CreateTask {
                    status_id: Some(Uuid::new_v4()),
                    fields: Some(set(&[(&estimate, &["1"])])),
                    ..CreateTask::new("T", bug.id)
                },
            )
            .is_err()
    );
    assert!(env.all_tasks().is_empty());
}

// ---- дополнительные и собственные поля ----

#[test]
fn a_task_gets_extra_catalog_fields_and_own_fields_and_can_remove_them_but_not_type_fields() {
    let env = Env::new();
    let prio = priority(&env);
    let typed = field(&env, "Typed", FieldType::String);
    let extra = field(&env, "Extra", FieldType::Int);
    let valued = field(&env, "Valued", FieldType::String);
    let bug = env.type_with("Bug", vec![tf(&typed, false)]);
    let task = new_task(&env, &bug, "T", None).unwrap();

    let changed = edit(
        &env,
        &task,
        Some(TaskFieldChanges {
            add_fields: Some(vec![extra.id, typed.id]),
            values: Some(vec![TaskFieldValueInput {
                field_id: valued.id,
                values: Some(vec!["v".into()]),
            }]),
            new_own_fields: Some(vec![
                NewOwnField {
                    required: true,
                    multiple: true,
                    ..own_field("Hours", FieldType::Float, &["1.5", "2"])
                },
                NewOwnField {
                    enum_id: Some(prio.id),
                    ..own_field("Mood", FieldType::Enum, &["low"])
                },
            ]),
            remove_fields: None,
        }),
    )
    .unwrap();

    let v = view(&env, &changed);
    assert_eq!(names(&env, &changed), vec!["Typed", "Extra", "Valued", "Hours", "Mood"]);
    assert_eq!(
        v.iter().map(|x| x.source).collect::<Vec<_>>(),
        vec![
            TaskFieldSource::Type,
            TaskFieldSource::Extra,
            TaskFieldSource::Extra,
            TaskFieldSource::Own,
            TaskFieldSource::Own
        ]
    );
    let hours = v.iter().find(|x| x.name == "Hours").unwrap();
    assert_eq!((hours.field_type, hours.required, hours.multiple), (FieldType::Float, true, true));
    assert_eq!(hours.values, vec!["1.5", "2"]);
    let mood = v.iter().find(|x| x.name == "Mood").unwrap();
    assert_eq!((mood.enum_id, mood.texts.clone()), (Some(prio.id), vec!["Low".to_string()]));
    assert!(v.iter().find(|x| x.name == "Extra").unwrap().values.is_empty());

    let read = env.get(&changed.id);
    assert_eq!(
        read.fields
            .iter()
            .find(|x| x.own.as_ref().is_some_and(|o| o.name == "Hours"))
            .unwrap()
            .own,
        Some(OwnField {
            name: "Hours".into(),
            field_type: FieldType::Float,
            required: true,
            multiple: true,
            enum_id: None
        })
    );

    let remove = |ids: Vec<Uuid>| {
        edit(
            &env,
            &changed,
            Some(TaskFieldChanges {
                remove_fields: Some(ids),
                ..TaskFieldChanges::default()
            }),
        )
    };
    assert_eq!(
        message(remove(vec![typed.id])),
        format!(
            "RemoveFields: {} is a field of the task type 'Bug' and cannot be removed from the task",
            typed.id
        )
    );
    assert!(message(remove(vec![Uuid::new_v4()])).contains("no additional or own field"));
    let removed = remove(vec![extra.id, hours.field_id]).unwrap();
    assert_eq!(names(&env, &removed), vec!["Typed", "Valued", "Mood"]);
}

#[test]
fn own_fields_are_validated_names_stay_unique_in_the_task_and_a_required_own_field_needs_a_value() {
    let env = Env::new();
    let prio = priority(&env);
    let note = field(&env, "Note", FieldType::String);
    let bug = env.type_with("Bug", vec![tf(&note, false)]);
    let task = new_task(&env, &bug, "T", None).unwrap();
    let invalid = |fields: Vec<NewOwnField>| message(edit(&env, &task, Some(own(fields))));

    assert!(invalid(vec![NewOwnField::new("note", FieldType::Int)]).contains("already has a field with this name"));
    assert!(invalid(vec![NewOwnField::new(" ", FieldType::Int)]).contains("required"));
    assert!(
        invalid(vec![NewOwnField {
            enum_id: Some(prio.id),
            ..NewOwnField::new("X", FieldType::Int)
        }])
        .contains("only a field of type enum")
    );
    assert!(invalid(vec![NewOwnField::new("X", FieldType::Enum)]).contains("must refer to an enum"));
    assert!(
        invalid(vec![NewOwnField {
            enum_id: Some(Uuid::new_v4()),
            ..NewOwnField::new("X", FieldType::Enum)
        }])
        .contains("enum not found")
    );
    assert!(
        invalid(vec![
            NewOwnField::new("Same", FieldType::Int),
            NewOwnField::new("SAME", FieldType::Bool)
        ])
        .contains("already has a field with this name")
    );
    assert!(invalid(vec![own_field("X", FieldType::Int, &["z"])]).contains("is not an integer"));
    assert!(env.get(&task.id).fields.is_empty());

    assert!(
        invalid(vec![NewOwnField {
            required: true,
            ..NewOwnField::new("Hours", FieldType::Int)
        }])
        .contains("'Hours'")
    );
    let with_value = edit(
        &env,
        &task,
        Some(own(vec![NewOwnField {
            required: true,
            ..own_field("Hours", FieldType::Int, &["4"])
        }])),
    )
    .unwrap();
    let hours = view(&env, &with_value).into_iter().find(|x| x.name == "Hours").unwrap();
    assert!(message(edit(&env, &with_value, Some(set_id(hours.field_id, &[])))).contains("'Hours'"));
    assert!(edit_with(&env, &with_value, None, None, None, Some(env.done.id)).is_ok());
}

#[test]
fn a_catalog_field_that_is_not_the_tasks_becomes_an_extra_field_when_a_value_is_set() {
    let env = Env::new();
    let extra = field(&env, "Extra", FieldType::Int);
    let bug = env.type_with("Bug", vec![]);
    let task = new_task(&env, &bug, "T", None).unwrap();
    let changed = edit(&env, &task, Some(set(&[(&extra, &["4"])]))).unwrap();
    let f = view(&env, &changed).remove(0);
    assert_eq!(
        (f.source, f.required, f.values),
        (TaskFieldSource::Extra, false, vec!["4".to_string()])
    );
    let other = field(&env, "Other", FieldType::String);
    let same = edit(&env, &changed, Some(set(&[(&other, &[])]))).unwrap();
    assert_eq!(env.get(&same.id).fields.len(), 1);
}

// ---- убрали поле из типа ----

#[test]
fn removing_a_type_field_without_values_needs_no_choice_but_with_values_it_needs_one() {
    let env = Env::new();
    let note = field(&env, "Note", FieldType::String);
    let area = field(&env, "Area", FieldType::String);
    let bug = env.type_with("Bug", vec![tf(&note, true), tf(&area, false)]);
    let one = new_task(&env, &bug, "One", Some(set(&[(&note, &["n1"])]))).unwrap();
    let two = new_task(&env, &bug, "Two", Some(set(&[(&note, &["n2"]), (&area, &["a2"])]))).unwrap();

    let error = err(update_type(&env, &bug, Some(vec![tf(&note, true)]), None));
    assert_eq!(error.conflict_code(), Some(ConflictCode::InUse));
    assert_eq!(
        error.message(),
        "1 task(s) of type 'Bug' have values in the field(s) being removed ('Area'): choose to clear those values or to keep them as additional fields of the tasks"
    );
    assert_eq!(
        env.ws.task_types().get_by_id(&env.project, &bug.id).unwrap().unwrap().fields.len(),
        2
    );

    let cleared = edit(&env, &two, Some(set(&[(&area, &[])]))).unwrap();
    let current = env.ws.task_types().get_by_id(&env.project, &bug.id).unwrap().unwrap();
    let result = env
        .ws
        .task_types()
        .update(
            &env.project,
            &bug.id,
            &UpdateTaskType {
                version: Some(current.version),
                fields: Some(vec![tf(&note, true)]),
                ..UpdateTaskType::default()
            },
        )
        .unwrap()
        .unwrap();
    assert_eq!(result.affected_tasks, 0);
    assert_eq!(result.value.fields.iter().map(|x| x.field_id).collect::<Vec<_>>(), vec![note.id]);
    assert_eq!(env.get(&cleared.id).version, cleared.version);
    assert_eq!(env.get(&one.id).fields[0].values, vec!["n1"]);
}

#[test]
fn removing_a_type_field_with_the_choice_to_clear_removes_the_values_of_all_tasks_of_the_type_only() {
    let env = Env::new();
    let note = field(&env, "Note", FieldType::String);
    let bug = env.type_with("Bug", vec![tf(&note, false)]);
    let sibling = env.type_with("Story", vec![tf(&note, false)]);
    let tasks: Vec<TaskItem> = (0..3)
        .map(|i| new_task(&env, &bug, &format!("Bug{i}"), Some(set(&[(&note, &[&format!("v{i}")])]))).unwrap())
        .collect();
    let untouched = new_task(&env, &sibling, "Story", Some(set(&[(&note, &["keep"])]))).unwrap();
    let plain = env.type_with("Plain", vec![]);
    let extra_of_other = new_task(&env, &plain, "Plain", Some(set(&[(&note, &["extra"])]))).unwrap();

    assert!(message(update_type(&env, &bug, Some(vec![]), None)).contains("3 task(s)"));
    for task in &tasks {
        assert_eq!(env.get(&task.id).fields.len(), 1);
    }

    let current = env.ws.task_types().get_by_id(&env.project, &bug.id).unwrap().unwrap();
    let result = env
        .ws
        .task_types()
        .update(
            &env.project,
            &bug.id,
            &UpdateTaskType {
                version: Some(current.version),
                fields: Some(vec![]),
                removed_fields: Some(RemovedFieldValues::Clear),
                ..UpdateTaskType::default()
            },
        )
        .unwrap()
        .unwrap();
    assert!(result.value.fields.is_empty());
    assert_eq!(result.affected_tasks, 3);
    for task in &tasks {
        assert!(env.get(&task.id).fields.is_empty());
        assert_ne!(env.get(&task.id).version, task.version);
    }
    assert_eq!(env.get(&untouched.id), untouched);
    assert_eq!(env.get(&extra_of_other.id).fields[0].values, vec!["extra"]);
}

#[test]
fn removing_a_type_field_with_the_choice_to_keep_turns_it_into_an_optional_additional_field() {
    let env = Env::new();
    let note = field(&env, "Note", FieldType::String);
    let bug = env.type_with("Bug", vec![tf(&note, true)]);
    let with_value = new_task(&env, &bug, "With", Some(set(&[(&note, &["v"])]))).unwrap();
    let mut empty = new_task(&env, &bug, "Empty", Some(set(&[(&note, &["tmp"])]))).unwrap();
    // Пустое обязательное поле через сервис не получить: так задача выглядит после слияния веток.
    empty.fields = vec![];
    empty.version = env.ws.update(&empty, &empty.version).unwrap().unwrap();

    let current = env.ws.task_types().get_by_id(&env.project, &bug.id).unwrap().unwrap();
    let kept = env
        .ws
        .task_types()
        .update(
            &env.project,
            &bug.id,
            &UpdateTaskType {
                version: Some(current.version),
                fields: Some(vec![]),
                removed_fields: Some(RemovedFieldValues::Keep),
                ..UpdateTaskType::default()
            },
        )
        .unwrap()
        .unwrap();
    assert!(kept.value.fields.is_empty());
    assert_eq!(kept.affected_tasks, 1);
    assert_eq!(env.get(&with_value.id).version, with_value.version);
    let f = view(&env, &with_value).remove(0);
    assert_eq!(
        (f.source, f.required, f.values),
        (TaskFieldSource::Extra, false, vec!["v".to_string()])
    );
    assert!(view(&env, &empty).is_empty());

    let edited = edit_with(
        &env,
        &with_value,
        Some(TaskFieldChanges {
            remove_fields: Some(vec![note.id]),
            ..TaskFieldChanges::default()
        }),
        Some("Renamed"),
        None,
        None,
    )
    .unwrap();
    assert!(env.get(&edited.id).fields.is_empty());
    assert!(edit_with(&env, &empty, None, Some("Renamed too"), None, None).is_ok());
}

// ---- смена типа ----

#[test]
fn changing_the_type_keeps_old_fields_as_additional_ones_keeps_shared_values_and_requires_the_new_required_fields() {
    let env = Env::new();
    let shared = field(&env, "Shared", FieldType::String);
    let old_only = field(&env, "OldOnly", FieldType::String);
    let needed = field(&env, "Needed", FieldType::Int);
    let old_type = env.type_with("Old", vec![tf(&shared, false), tf(&old_only, false)]);
    let new_type = env.type_with("New", vec![tf(&shared, true), tf(&needed, true)]);
    let task = new_task(&env, &old_type, "T", Some(set(&[(&shared, &["s"]), (&old_only, &["o"])]))).unwrap();

    let error = message(edit_with(&env, &task, None, None, Some(new_type.id), None));
    assert!(error.contains("'Needed'") && !error.contains("'Shared'"));
    assert_eq!(env.get(&task.id).type_id, old_type.id);

    let changed = edit_with(&env, &task, Some(set(&[(&needed, &["9"])])), None, Some(new_type.id), None).unwrap();
    assert_eq!(changed.type_id, new_type.id);
    let v = view(&env, &changed);
    assert_eq!(names(&env, &changed), vec!["Shared", "Needed", "OldOnly"]);
    assert_eq!(values(&env, &changed, "Shared"), vec!["s"]);
    assert_eq!(
        v.iter().map(|x| x.source).collect::<Vec<_>>(),
        vec![TaskFieldSource::Type, TaskFieldSource::Type, TaskFieldSource::Extra]
    );
    assert_eq!(v.iter().map(|x| x.required).collect::<Vec<_>>(), vec![true, true, false]);
    assert_eq!(values(&env, &changed, "OldOnly"), vec!["o"]);
}

// ---- значения перечисления ----

fn without(prio: &FieldEnum, removed: &[Uuid], choice: Option<RemovedEnumValues>, version: &str) -> UpdateFieldEnum {
    UpdateFieldEnum {
        name: None,
        values: Some(
            prio.values
                .iter()
                .filter(|x| !removed.contains(&x.id))
                .map(|x| FieldEnumValueInput {
                    id: Some(x.id),
                    name: x.name.clone(),
                })
                .collect(),
        ),
        version: Some(version.into()),
        removed: choice,
    }
}

#[test]
fn removing_an_enum_value_that_no_task_uses_needs_no_choice_and_one_that_tasks_use_needs_one() {
    let env = Env::new();
    let prio = priority(&env);
    let (low, medium, high) = (&prio.values[0], &prio.values[1], &prio.values[2]);
    let level = field_with(&env, "Level", FieldType::Enum, false, Some(prio.id));
    let bug = env.type_with("Bug", vec![tf(&level, false)]);
    new_task(&env, &bug, "A", Some(set(&[(&level, &["High"])]))).unwrap();
    new_task(&env, &bug, "B", Some(set(&[(&level, &["High"])]))).unwrap();

    let medium_result = env
        .ws
        .enums()
        .update(&env.project, &prio.id, &without(&prio, &[medium.id], None, &prio.version))
        .unwrap()
        .unwrap();
    assert_eq!(medium_result.affected_tasks, 0);
    let after = medium_result.value;
    assert_eq!(
        after.values.iter().map(|x| x.name.as_str()).collect::<Vec<_>>(),
        vec!["Low", "High"]
    );

    let error = err(env
        .ws
        .enums()
        .update(&env.project, &prio.id, &without(&after, &[high.id], None, &after.version)));
    assert_eq!(error.conflict_code(), Some(ConflictCode::InUse));
    assert_eq!(
        error.message(),
        "Value(s) 'High' of Enum 'Priority' are selected in 2 task(s) and cannot be removed: choose to clear them (from the tasks, and the conditions from the columns) or to reassign them to another value of the enum"
    );
    assert_eq!(
        env.ws.enums().get_by_id(&env.project, &prio.id).unwrap().unwrap().version,
        after.version
    );
    let _ = low;
}

#[test]
fn removing_a_used_enum_value_can_clear_it_from_the_tasks_including_required_fields_and_own_fields() {
    let env = Env::new();
    let prio = priority(&env);
    let (low, high) = (&prio.values[0], &prio.values[2]);
    let level = field_with(&env, "Level", FieldType::Enum, true, Some(prio.id));
    let bug = env.type_with("Bug", vec![tf(&level, true)]);
    let plain = env.type_with("Plain", vec![]);
    let multi = new_task(&env, &bug, "Multi", Some(set(&[(&level, &["Low", "High"])]))).unwrap();
    let only = new_task(&env, &bug, "Only", Some(set(&[(&level, &["High"])]))).unwrap();
    let own_task = new_task(
        &env,
        &plain,
        "Own",
        Some(own(vec![NewOwnField {
            enum_id: Some(prio.id),
            ..own_field("Mood", FieldType::Enum, &["High"])
        }])),
    )
    .unwrap();
    let untouched = new_task(&env, &bug, "Untouched", Some(set(&[(&level, &["Low"])]))).unwrap();

    let result = env
        .ws
        .enums()
        .update(
            &env.project,
            &prio.id,
            &without(&prio, &[high.id], Some(RemovedEnumValues::clear()), &prio.version),
        )
        .unwrap()
        .unwrap();
    assert_eq!(result.affected_tasks, 3);
    assert!(!result.value.values.iter().any(|x| x.id == high.id));
    assert_eq!(env.get(&multi.id).fields[0].values, vec![guid_d(&low.id)]);
    assert!(env.get(&only.id).fields.is_empty());
    assert!(view(&env, &only)[0].values.is_empty());
    let own_entry = env.get(&own_task.id).fields.remove(0);
    assert_eq!(own_entry.own.unwrap().name, "Mood");
    assert!(own_entry.values.is_empty());
    assert_eq!(env.get(&untouched.id), untouched);
    assert!(message(edit_with(&env, &env.get(&only.id), None, Some("Renamed"), None, None)).contains("'Level'"));
}

#[test]
fn removing_a_used_enum_value_can_reassign_it_without_duplicates() {
    let env = Env::new();
    let prio = priority(&env);
    let (low, medium, high) = (&prio.values[0], &prio.values[1], &prio.values[2]);
    let level = field_with(&env, "Level", FieldType::Enum, true, Some(prio.id));
    let bug = env.type_with("Bug", vec![tf(&level, false)]);
    let both = new_task(&env, &bug, "Both", Some(set(&[(&level, &["High", "Low"])]))).unwrap();
    let single = new_task(&env, &bug, "Single", Some(set(&[(&level, &["High"])]))).unwrap();
    let plain = env.type_with("Plain", vec![]);
    let own_only = new_task(
        &env,
        &plain,
        "Own",
        Some(own(vec![NewOwnField {
            multiple: true,
            enum_id: Some(prio.id),
            ..own_field("Mood", FieldType::Enum, &["High", "Medium"])
        }])),
    )
    .unwrap();
    let remove = |choice: RemovedEnumValues| {
        env.ws
            .enums()
            .update(&env.project, &prio.id, &without(&prio, &[high.id], Some(choice), &prio.version))
    };

    assert!(message(remove(RemovedEnumValues::reassign(high.id))).contains("not among the values the enum keeps"));
    assert!(message(remove(RemovedEnumValues::reassign(Uuid::new_v4()))).contains("not among the values the enum keeps"));
    assert!(
        message(remove(RemovedEnumValues {
            clear: true,
            reassign_to: Some(low.id)
        }))
        .contains("exactly one")
    );
    assert!(message(remove(RemovedEnumValues::default())).contains("exactly one"));
    assert_eq!(
        env.ws.enums().get_by_id(&env.project, &prio.id).unwrap().unwrap().version,
        prio.version
    );

    let reassigned = remove(RemovedEnumValues::reassign(low.id)).unwrap().unwrap();
    assert_eq!(
        reassigned.value.values.iter().map(|x| x.name.as_str()).collect::<Vec<_>>(),
        vec!["Low", "Medium"]
    );
    assert_eq!(reassigned.affected_tasks, 3);
    assert_eq!(env.get(&both.id).fields[0].values, vec![guid_d(&low.id)]);
    assert_eq!(env.get(&single.id).fields[0].values, vec![guid_d(&low.id)]);
    assert_eq!(env.get(&own_only.id).fields[0].values, vec![guid_d(&low.id), guid_d(&medium.id)]);
}

#[test]
fn renaming_an_enum_value_keeps_the_tasks_values() {
    let env = Env::new();
    let prio = priority(&env);
    let level = field_with(&env, "Level", FieldType::Enum, false, Some(prio.id));
    let bug = env.type_with("Bug", vec![tf(&level, false)]);
    let task = new_task(&env, &bug, "T", Some(set(&[(&level, &["High"])]))).unwrap();
    let renamed = env
        .ws
        .enums()
        .update(
            &env.project,
            &prio.id,
            &UpdateFieldEnum {
                values: Some(
                    prio.values
                        .iter()
                        .map(|x| FieldEnumValueInput {
                            id: Some(x.id),
                            name: if x.name == "High" { "Critical".into() } else { x.name.clone() },
                        })
                        .collect(),
                ),
                version: Some(prio.version.clone()),
                ..UpdateFieldEnum::default()
            },
        )
        .unwrap()
        .unwrap()
        .value;
    assert_eq!(renamed.values[2].name, "Critical");
    assert_eq!(view(&env, &task)[0].texts, vec!["Critical"]);
}

// ---- «используется» при удалении поля и перечисления ----

#[test]
fn a_field_used_by_a_type_or_a_task_cannot_be_deleted_and_an_enum_used_by_an_own_field_cannot_be_deleted() {
    let env = Env::new();
    let prio = priority(&env);
    let typed = field(&env, "Typed", FieldType::String);
    let added = field(&env, "Added", FieldType::String);
    let free = field(&env, "Free", FieldType::String);
    let bug = env.type_with("Bug", vec![tf(&typed, false)]);
    let task = new_task(
        &env,
        &bug,
        "T",
        Some(TaskFieldChanges {
            add_fields: Some(vec![added.id]),
            new_own_fields: Some(vec![NewOwnField {
                enum_id: Some(prio.id),
                ..NewOwnField::new("Mood", FieldType::Enum)
            }]),
            ..TaskFieldChanges::default()
        }),
    )
    .unwrap();

    let by_type = err(env.ws.fields().delete(&env.project, &typed.id, Some(&typed.version)));
    assert_eq!(by_type.conflict_code(), Some(ConflictCode::InUse));
    assert_eq!(by_type.message(), "Field 'Typed' is used by task type 'Bug' and cannot be deleted");
    assert_eq!(
        message(env.ws.fields().delete(&env.project, &added.id, Some(&added.version))),
        "Field 'Added' is used by 1 task(s) and cannot be deleted"
    );
    assert_eq!(
        message(env.ws.enums().delete(&env.project, &prio.id, Some(&prio.version))),
        "Enum 'Priority' is used by own fields of 1 task(s) and cannot be deleted"
    );
    assert!(env.ws.fields().delete(&env.project, &free.id, Some(&free.version)).unwrap());

    let own_id = view(&env, &task).into_iter().find(|x| x.name == "Mood").unwrap().field_id;
    edit(
        &env,
        &task,
        Some(TaskFieldChanges {
            remove_fields: Some(vec![added.id, own_id]),
            ..TaskFieldChanges::default()
        }),
    )
    .unwrap();
    assert!(env.ws.enums().delete(&env.project, &prio.id, Some(&prio.version)).unwrap());
    assert!(env.ws.fields().delete(&env.project, &added.id, Some(&added.version)).unwrap());
    update_type(&env, &bug, Some(vec![]), None).unwrap();
    assert!(env.ws.fields().delete(&env.project, &typed.id, Some(&typed.version)).unwrap());
}

// ---- блокировки ----

#[test]
fn changing_fields_uses_the_version_and_edit_locks_of_the_task_and_the_type() {
    let env = Env::new();
    let note = field(&env, "Note", FieldType::String);
    let bug = env.type_with("Bug", vec![tf(&note, false)]);
    let task = new_task(&env, &bug, "T", Some(set(&[(&note, &["a"])]))).unwrap();

    let first = edit(&env, &task, Some(set(&[(&note, &["b"])]))).unwrap();
    let stale = err(edit(&env, &task, Some(set(&[(&note, &["c"])]))));
    assert_eq!(stale.conflict_code(), Some(ConflictCode::Modified));
    assert_eq!(env.get(&first.id).fields[0].values, vec!["b"]);

    let stale_type = err(env.ws.task_types().update(
        &env.project,
        &bug.id,
        &UpdateTaskType {
            version: Some("nope".into()),
            fields: Some(vec![]),
            removed_fields: Some(RemovedFieldValues::Clear),
            ..UpdateTaskType::default()
        },
    ));
    assert_eq!(stale_type.conflict_code(), Some(ConflictCode::Modified));
    assert_eq!(env.get(&first.id).fields.len(), 1);

    let anna = anna(&env);
    anna.locks()
        .acquire(LockedEntity::Task, &task.id, "Task", Some(env.project))
        .unwrap();
    anna.locks()
        .acquire(LockedEntity::TaskType, &bug.id, "Type", Some(env.project))
        .unwrap();
    let locked = err(edit(&env, &first, Some(set(&[(&note, &["d"])]))));
    assert_eq!(locked.conflict_code(), Some(ConflictCode::Locked));
    assert_eq!(locked.message(), "Task 'T' is being edited by Anna");
    assert_eq!(
        err(update_type(&env, &bug, Some(vec![]), None)).conflict_code(),
        Some(ConflictCode::Locked)
    );
}

#[test]
fn concurrent_edits_of_one_task_do_not_lose_the_task_and_exactly_one_stale_edit_wins() {
    let env = Env::new();
    let note = field(&env, "Note", FieldType::String);
    let bug = env.type_with("Bug", vec![tf(&note, false)]);
    let task = new_task(&env, &bug, "T", Some(set(&[(&note, &["start"])]))).unwrap();
    let results: Vec<bool> = std::thread::scope(|scope| {
        let handles: Vec<_> = (0..4)
            .map(|i| {
                let ws = env.ws.clone();
                let (project, task, note) = (env.project, task.clone(), note.id);
                scope.spawn(move || {
                    ws.tasks()
                        .update(
                            &project,
                            &task.id,
                            &UpdateTask {
                                version: Some(task.version.clone()),
                                fields: Some(set_id(note, &[&format!("v{i}")])),
                                ..UpdateTask::default()
                            },
                        )
                        .is_ok()
                })
            })
            .collect();
        handles.into_iter().map(|h| h.join().unwrap()).collect()
    });
    assert_eq!(results.iter().filter(|x| **x).count(), 1);
    assert!(env.get(&task.id).fields[0].values[0].starts_with('v'));
}

fn note_tasks(env: &Env, count: usize) -> (FieldDefinition, TaskType, Vec<TaskItem>) {
    let note = field(env, "Note", FieldType::String);
    let bug = env.type_with("Bug", vec![tf(&note, false)]);
    let tasks = (0..count)
        .map(|i| new_task(env, &bug, &format!("Bug{i}"), Some(set(&[(&note, &["v"])]))).unwrap())
        .collect();
    (note, bug, tasks)
}

fn clear_fields(env: &Env, bug: &TaskType) -> tasker_services::Result<TaskType> {
    update_type(env, bug, Some(vec![]), Some(RemovedFieldValues::Clear))
}

#[test]
fn a_clear_waits_out_the_timeout_on_a_task_locked_by_someone_else_then_fails_with_locked_listing_them_and_writes_nothing() {
    let env = Env::new().with_cascade_timeout(Duration::from_millis(300));
    let (_, bug, tasks) = note_tasks(&env, 3);
    anna_locks(&env, &tasks[0]);
    anna_locks(&env, &tasks[2]);

    let error = err(clear_fields(&env, &bug));
    assert_eq!(error.conflict_code(), Some(ConflictCode::Locked));
    assert_eq!(error.message(), "Task 'Bug0', Task 'Bug2' is being edited by Anna");
    for task in &tasks {
        assert_eq!(env.get(&task.id).version, task.version);
    }
    assert_eq!(
        env.ws.task_types().get_by_id(&env.project, &bug.id).unwrap().unwrap().fields.len(),
        1
    );
}

#[test]
fn a_clear_waits_until_the_lock_is_released_and_then_finishes() {
    let env = Env::new();
    let (_, bug, tasks) = note_tasks(&env, 2);
    // Блокировка Анны истекает через 2 минуты: ожидание каскада (до 30 с) само продвигает поддельные часы — и дожидается.
    anna_locks(&env, &tasks[1]);
    env.clock.advance(Duration::from_secs(100));
    let cleared = clear_fields(&env, &bug).unwrap();
    assert!(cleared.fields.is_empty());
    for task in &tasks {
        assert!(env.get(&task.id).fields.is_empty());
    }
}

#[test]
fn a_locked_task_that_the_clear_does_not_touch_does_not_get_in_the_way() {
    let env = Env::new();
    let (note, bug, tasks) = note_tasks(&env, 1);
    let without_value = new_task(&env, &bug, "Empty", None).unwrap();
    let other_type = env.type_with("Other", vec![tf(&note, false)]);
    let other_task = new_task(&env, &other_type, "Other", Some(set(&[(&note, &["keep"])]))).unwrap();
    anna_locks(&env, &without_value);
    anna_locks(&env, &other_task);
    let updated = clear_fields(&env, &bug).unwrap();
    assert!(updated.fields.is_empty());
    assert!(env.get(&tasks[0].id).fields.is_empty());
    assert_eq!(env.get(&other_task.id).fields.len(), 1);
}

#[test]
fn a_reassign_of_an_enum_value_follows_the_same_lock_rule() {
    let env = Env::new().with_cascade_timeout(Duration::from_millis(300));
    let prio = priority(&env);
    let level = field_with(&env, "Level", FieldType::Enum, false, Some(prio.id));
    let bug = env.type_with("Bug", vec![tf(&level, false)]);
    let locked = new_task(&env, &bug, "Locked", Some(set(&[(&level, &["High"])]))).unwrap();
    let free = new_task(&env, &bug, "Free", Some(set(&[(&level, &["High"])]))).unwrap();
    let unrelated = new_task(&env, &bug, "Unrelated", Some(set(&[(&level, &["Low"])]))).unwrap();
    anna_locks(&env, &locked);
    anna_locks(&env, &unrelated);

    let command = without(
        &prio,
        &[prio.values[2].id],
        Some(RemovedEnumValues::reassign(prio.values[0].id)),
        &prio.version,
    );
    let error = err(env.ws.enums().update(&env.project, &prio.id, &command));
    assert_eq!(error.conflict_code(), Some(ConflictCode::Locked));
    assert_eq!(error.message(), "Task 'Locked' is being edited by Anna");
    assert_eq!(env.get(&free.id).version, free.version);
    assert_eq!(
        env.ws.enums().get_by_id(&env.project, &prio.id).unwrap().unwrap().version,
        prio.version
    );

    anna_releases(&env, &locked);
    assert!(env.ws.enums().update(&env.project, &prio.id, &command).unwrap().is_some());
    assert_eq!(env.get(&free.id).fields[0].values, vec![guid_d(&prio.values[0].id)]);
}

#[test]
fn a_clear_with_a_stale_type_version_changes_nothing_and_the_same_call_can_be_repeated() {
    let env = Env::new();
    let (_, bug, tasks) = note_tasks(&env, 3);
    let stale = bug.version.clone();
    env.ws
        .task_types()
        .update(
            &env.project,
            &bug.id,
            &UpdateTaskType {
                name: Some("Renamed".into()),
                version: Some(bug.version.clone()),
                ..UpdateTaskType::default()
            },
        )
        .unwrap();
    assert!(
        err(env.ws.task_types().update(
            &env.project,
            &bug.id,
            &UpdateTaskType {
                version: Some(stale),
                fields: Some(vec![]),
                removed_fields: Some(RemovedFieldValues::Clear),
                ..UpdateTaskType::default()
            },
        ))
        .is_modified()
    );
    for task in &tasks {
        assert_eq!(env.get(&task.id).fields.len(), 1);
    }
    clear_fields(&env, &bug).unwrap();
    for task in &tasks {
        assert!(env.get(&task.id).fields.is_empty());
    }
}

// ---- фильтр по значениям полей ----

#[test]
fn tasks_are_filtered_by_field_values_with_and_semantics_paging_and_total_count() {
    let env = Env::new();
    let levels = enumeration(&env, "Levels", &["Low", "High"]);
    let estimate = field(&env, "Estimate", FieldType::Int);
    let tags = field_with(&env, "Tags", FieldType::String, true, None);
    let level = field_with(&env, "Level", FieldType::Enum, false, Some(levels.id));
    let due = field(&env, "Due", FieldType::Date);
    let ratio = field(&env, "Ratio", FieldType::Float);
    let bug = env.type_with("Bug", vec![tf(&estimate, false), tf(&tags, false), tf(&level, false)]);

    new_task(
        &env,
        &bug,
        "a",
        Some(set(&[
            (&estimate, &["3"]),
            (&tags, &["ui", "api"]),
            (&level, &["High"]),
            (&due, &["2026-10-02"]),
            (&ratio, &["0.5"]),
        ])),
    )
    .unwrap();
    new_task(
        &env,
        &bug,
        "b",
        Some(set(&[(&estimate, &["5"]), (&tags, &["api"]), (&level, &["Low"])])),
    )
    .unwrap();
    new_task(
        &env,
        &bug,
        "c",
        Some(set(&[(&estimate, &["3"]), (&tags, &["db"]), (&level, &["High"])])),
    )
    .unwrap();
    new_task(&env, &bug, "d", None).unwrap();

    assert_eq!(titles_by(&env, &["Estimate=3"]), vec!["a", "c"]);
    assert_eq!(titles_by(&env, &["estimate=003"]), vec!["a", "c"]);
    assert_eq!(titles_by(&env, &["Tags=api"]), vec!["a", "b"]);
    assert_eq!(titles_by(&env, &["Level=high"]), vec!["a", "c"]);
    assert_eq!(titles_by(&env, &["Due=2026-10-02"]), vec!["a"]);
    assert_eq!(titles_by(&env, &["Ratio=0.50"]), vec!["a"]);
    assert_eq!(titles_by(&env, &["Estimate=3", "Level=High", "Tags=ui"]), vec!["a"]);
    assert_eq!(titles_by(&env, &["Tags=ui", "Tags=api"]), vec!["a"]);
    assert!(titles_by(&env, &["Estimate=5", "Level=High"]).is_empty());
    assert!(titles_by(&env, &["Estimate=4"]).is_empty());

    let filter = vec!["Estimate=3".to_string()];
    let first = env
        .ws
        .tasks()
        .list(&env.project, None, Some(&filter), Page::new(0, 1), -1, None)
        .unwrap();
    let second = env
        .ws
        .tasks()
        .list(&env.project, None, Some(&filter), Page::new(1, 1), -1, None)
        .unwrap();
    assert_eq!(
        (first.total_count, first.data.len(), second.total_count, second.data.len()),
        (2, 1, 2, 1)
    );
    assert_eq!(
        first
            .data
            .iter()
            .chain(second.data.iter())
            .map(|x| x.task.title.as_str())
            .collect::<Vec<_>>(),
        vec!["a", "c"]
    );

    let b = env
        .ws
        .tasks()
        .list(&env.project, None, Some(&["Estimate=5".to_string()]), Page::first(50), -1, None)
        .unwrap()
        .data
        .remove(0)
        .task;
    edit(&env, &b, Some(set(&[(&estimate, &["3"])]))).unwrap();
    assert_eq!(titles_by(&env, &["Estimate=3"]), vec!["a", "b", "c"]);
    edit(&env, &env.get(&b.id), Some(set(&[(&estimate, &[])]))).unwrap();
    assert_eq!(titles_by(&env, &["Estimate=3"]), vec!["a", "c"]);
    let doomed = env
        .ws
        .tasks()
        .list(&env.project, None, Some(&filter), Page::first(50), -1, None)
        .unwrap()
        .data
        .remove(0)
        .task;
    assert!(env.ws.tasks().delete(&env.project, &doomed.id, Some(&doomed.version)).unwrap());
    assert_eq!(titles_by(&env, &["Estimate=3"]).len(), 1);
}

#[test]
fn a_field_filter_covers_extra_catalog_fields_and_own_fields_of_the_same_name_and_type_and_bad_filters_are_rejected() {
    let env = Env::new();
    let note = field(&env, "Note", FieldType::String);
    let plain = env.type_with("Plain", vec![]);
    let e = priority(&env);
    field_with(&env, "Level", FieldType::Enum, false, Some(e.id));
    new_task(&env, &plain, "extra", Some(set(&[(&note, &["x=y"])]))).unwrap();
    new_task(&env, &plain, "own", Some(own(vec![own_field("Note", FieldType::String, &["x=y"])]))).unwrap();

    assert_eq!(titles_by(&env, &["Note=x=y"]), vec!["extra", "own"]);
    assert_eq!(titles_by(&env, &["NOTE=x=y"]), vec!["extra", "own"]);
    assert_eq!(
        list_error(&env, &["Note"]),
        "Field filter 'Note': expected 'Name=value' (also !=, >, >=, <, <=) or 'Name:set|unset|attached|detached'"
    );
    assert_eq!(
        list_error(&env, &["Nope=1"]),
        "Field filter 'Nope=1': no field 'Nope' in the project's catalog and no task has an own field with this name"
    );
    assert!(list_error(&env, &["Level=Huge"]).contains("is not a value of enum"));
    assert!(list_error(&env, &["Note="]).contains("must not be empty"));
    field(&env, "Number", FieldType::Int);
    assert!(list_error(&env, &["Number=abc"]).contains("is not an integer"));
}

#[test]
fn comparisons_work_for_int_float_and_date_alone_and_as_a_range_of_two_conditions() {
    let env = Env::new();
    let estimate = field(&env, "Estimate", FieldType::Int);
    let ratio = field(&env, "Ratio", FieldType::Float);
    let due = field(&env, "Due", FieldType::Date);
    let scores = field_with(&env, "Scores", FieldType::Int, true, None);
    let bug = env.type_with("Bug", vec![tf(&estimate, false)]);
    new_task(
        &env,
        &bug,
        "a",
        Some(set(&[
            (&estimate, &["-5"]),
            (&ratio, &["0.1"]),
            (&due, &["2026-01-09"]),
            (&scores, &["1", "20"]),
        ])),
    )
    .unwrap();
    new_task(
        &env,
        &bug,
        "b",
        Some(set(&[
            (&estimate, &["3"]),
            (&ratio, &["0.25"]),
            (&due, &["2026-10-02"]),
            (&scores, &["5"]),
        ])),
    )
    .unwrap();
    new_task(
        &env,
        &bug,
        "c",
        Some(set(&[(&estimate, &["10"]), (&ratio, &["1e2"]), (&due, &["2027-03-01"])])),
    )
    .unwrap();
    new_task(&env, &bug, "d", None).unwrap();

    assert_eq!(titles_by(&env, &["Estimate>3"]), vec!["c"]);
    assert_eq!(titles_by(&env, &["Estimate>=3"]), vec!["b", "c"]);
    assert_eq!(titles_by(&env, &["Estimate<3"]), vec!["a"]);
    assert_eq!(titles_by(&env, &["Estimate<=3"]), vec!["a", "b"]);
    assert_eq!(titles_by(&env, &["Estimate<0"]), vec!["a"]);
    assert_eq!(titles_by(&env, &["Estimate>=003", "Estimate<=0003"]), vec!["b"]);
    assert_eq!(titles_by(&env, &["Estimate>=3", "Estimate<=8"]), vec!["b"]);
    assert!(titles_by(&env, &["Estimate>5", "Estimate<5"]).is_empty());
    assert_eq!(titles_by(&env, &["Ratio>0.2"]), vec!["b", "c"]);
    assert_eq!(titles_by(&env, &["Ratio<1"]), vec!["a", "b"]);
    assert_eq!(titles_by(&env, &["Ratio>=0.250", "Ratio<=0.25"]), vec!["b"]);
    assert_eq!(titles_by(&env, &["Ratio=100"]), vec!["c"]);
    assert_eq!(titles_by(&env, &["Due>2026-01-09"]), vec!["b", "c"]);
    assert_eq!(titles_by(&env, &["Due<2027-01-01"]), vec!["a", "b"]);
    assert_eq!(titles_by(&env, &["Due>=2026-01-09", "Due<=2026-10-02"]), vec!["a", "b"]);
    assert_eq!(titles_by(&env, &["Due<2026-02-01"]), vec!["a"]);
    assert_eq!(titles_by(&env, &["Scores>10"]), vec!["a"]);
    assert_eq!(titles_by(&env, &["Scores>=5"]), vec!["a", "b"]);
    assert_eq!(titles_by(&env, &["Scores<2", "Scores>15"]), vec!["a"]);
    assert_eq!(titles_by(&env, &["Estimate>=3", "Due<2027-01-01", "Scores=5"]), vec!["b"]);
    assert_eq!(titles_by(&env, &[]), vec!["a", "b", "c", "d"]);
}

#[test]
fn comparisons_are_refused_for_string_enum_and_bool_and_bad_operands_are_rejected() {
    let env = Env::new();
    let levels = enumeration(&env, "Levels", &["Low", "High"]);
    field(&env, "Note", FieldType::String);
    field_with(&env, "Level", FieldType::Enum, false, Some(levels.id));
    field(&env, "Flag", FieldType::Bool);
    field(&env, "Estimate", FieldType::Int);
    field(&env, "Due", FieldType::Date);

    assert_eq!(
        list_error(&env, &["Note>a"]),
        "Field filter 'Note>a': '>' does not apply to field 'Note' of type string (only int, float and date can be compared; use = or !=)"
    );
    assert!(list_error(&env, &["Level>=High"]).contains("of type enum"));
    assert!(list_error(&env, &["Flag<true"]).contains("of type bool"));
    assert!(list_error(&env, &["Estimate>2.5"]).contains("is not an integer"));
    assert!(list_error(&env, &["Estimate>=x"]).contains("is not an integer"));
    assert!(list_error(&env, &["Due<tomorrow"]).contains("yyyy-MM-dd"));
    assert!(list_error(&env, &["Estimate>"]).contains("must not be empty"));
    assert!(list_error(&env, &["Level!=Huge"]).contains("is not a value of enum"));
    assert_eq!(
        list_error(&env, &["Estimate:soon"]),
        "Field filter 'Estimate:soon': unknown ':soon' (use :set, :unset, :attached or :detached)"
    );
    assert!(list_error(&env, &[">=3"]).contains("expected 'Name=value'"));
    assert_eq!(
        list_error(&env, &["Estimate!3"]),
        "Field filter 'Estimate!3': expected one of = != > >= < <= after the name 'Estimate'"
    );
    assert!(list_error(&env, &["Nope:set"]).contains("no field 'Nope'"));
    titles_by(&env, &["Note!=a", "Level!=low", "Flag=TRUE", "Estimate:SET", "Due:Unset"]);
}

#[test]
fn not_equal_means_no_value_equals_it_and_includes_tasks_without_a_value_and_complements_equal() {
    let env = Env::new();
    let levels = enumeration(&env, "Levels", &["Low", "High"]);
    let estimate = field(&env, "Estimate", FieldType::Int);
    let tags = field_with(&env, "Tags", FieldType::String, true, None);
    let level = field_with(&env, "Level", FieldType::Enum, false, Some(levels.id));
    let bug = env.type_with("Bug", vec![tf(&estimate, false), tf(&tags, false)]);
    let plain = env.type_with("Plain", vec![]);
    new_task(
        &env,
        &bug,
        "a",
        Some(set(&[(&estimate, &["3"]), (&tags, &["ui", "api"]), (&level, &["High"])])),
    )
    .unwrap();
    new_task(&env, &bug, "b", Some(set(&[(&estimate, &["5"]), (&tags, &["api"])]))).unwrap();
    new_task(&env, &bug, "empty", None).unwrap();
    new_task(&env, &plain, "detached", None).unwrap();

    assert_eq!(titles_by(&env, &["Estimate!=3"]), vec!["b", "detached", "empty"]);
    assert_eq!(titles_by(&env, &["Estimate=3"]), vec!["a"]);
    assert_eq!(titles_by(&env, &["Tags!=api"]), vec!["detached", "empty"]);
    assert_eq!(titles_by(&env, &["Tags!=ui"]), vec!["b", "detached", "empty"]);
    assert_eq!(titles_by(&env, &["Level!=high"]), vec!["b", "detached", "empty"]);
    assert_eq!(titles_by(&env, &["Estimate=3"]).len() + titles_by(&env, &["Estimate!=3"]).len(), 4);
    assert!(titles_by(&env, &["Estimate=3", "Estimate!=3"]).is_empty());
    assert_eq!(titles_by(&env, &["Estimate!=3", "Estimate>=5"]), vec!["b"]);
    assert_eq!(titles_by(&env, &["Tags!=ui", "Tags=api"]), vec!["b"]);
    assert_eq!(titles_by(&env, &["Estimate!=3", "Tags:unset", "Estimate:attached"]), vec!["empty"]);
}

#[test]
fn presence_predicates_tell_values_from_connection_and_follow_task_type_and_field_changes() {
    let env = Env::new();
    let estimate = field(&env, "Estimate", FieldType::Int);
    let note = field(&env, "Note", FieldType::String);
    field_with(&env, "Tags", FieldType::String, true, None);
    let with_estimate = env.type_with("WithEstimate", vec![tf(&estimate, false)]);
    let plain = env.type_with("Plain", vec![]);
    new_task(&env, &with_estimate, "valued", Some(set(&[(&estimate, &["3"])]))).unwrap();
    new_task(&env, &with_estimate, "typed-empty", None).unwrap();
    new_task(&env, &plain, "none", None).unwrap();
    new_task(&env, &plain, "extra-valued", Some(set(&[(&estimate, &["7"])]))).unwrap();
    new_task(
        &env,
        &plain,
        "extra-empty",
        Some(TaskFieldChanges {
            add_fields: Some(vec![estimate.id]),
            ..TaskFieldChanges::default()
        }),
    )
    .unwrap();
    new_task(&env, &plain, "own", Some(own(vec![own_field("Estimate", FieldType::Int, &["1"])]))).unwrap();

    assert_eq!(titles_by(&env, &["Estimate:set"]), vec!["extra-valued", "own", "valued"]);
    assert_eq!(titles_by(&env, &["Estimate:unset"]), vec!["extra-empty", "none", "typed-empty"]);
    assert_eq!(
        titles_by(&env, &["Estimate:attached"]),
        vec!["extra-empty", "extra-valued", "own", "typed-empty", "valued"]
    );
    assert_eq!(titles_by(&env, &["Estimate:detached"]), vec!["none"]);
    assert_eq!(
        titles_by(&env, &["Estimate:attached", "Estimate:unset"]),
        vec!["extra-empty", "typed-empty"]
    );
    assert_eq!(
        titles_by(&env, &["Estimate:set", "Estimate:attached"]),
        vec!["extra-valued", "own", "valued"]
    );
    assert!(titles_by(&env, &["Estimate:set", "Estimate:detached"]).is_empty());
    assert!(titles_by(&env, &["Note:attached"]).is_empty());
    assert_eq!(titles_by(&env, &["Note:detached"]).len(), 6);
    assert_eq!(titles_by(&env, &["Tags:unset"]).len(), 6);

    let by_title = |title: &str| env.all_tasks().into_iter().find(|x| x.title == title).unwrap();
    edit_with(&env, &by_title("typed-empty"), None, None, Some(plain.id), None).unwrap();
    assert_eq!(titles_by(&env, &["Estimate:attached", "Estimate:unset"]), vec!["extra-empty"]);
    edit_with(&env, &by_title("valued"), None, None, Some(plain.id), None).unwrap();
    assert_eq!(
        titles_by(&env, &["Estimate:attached", "Estimate:set"]),
        vec!["extra-valued", "own", "valued"]
    );
    assert!(titles_by(&env, &["Estimate:detached"]).contains(&"typed-empty".to_string()));
    edit_with(&env, &by_title("none"), None, None, Some(with_estimate.id), None).unwrap();
    assert!(titles_by(&env, &["Estimate:attached", "Estimate:unset"]).contains(&"none".to_string()));
    assert!(!titles_by(&env, &["Estimate:detached"]).contains(&"none".to_string()));

    update_type(&env, &plain, Some(vec![tf(&note, false)]), None).unwrap();
    let mut of_plain: Vec<String> = env
        .all_tasks()
        .into_iter()
        .filter(|x| x.type_id == plain.id)
        .map(|x| x.title)
        .collect();
    of_plain.sort();
    assert_eq!(titles_by(&env, &["Note:attached"]), of_plain);
    update_type(&env, &plain, Some(vec![]), Some(RemovedFieldValues::Keep)).unwrap();
    assert!(titles_by(&env, &["Note:attached"]).is_empty());

    edit(
        &env,
        &by_title("extra-valued"),
        Some(TaskFieldChanges {
            remove_fields: Some(vec![estimate.id]),
            ..TaskFieldChanges::default()
        }),
    )
    .unwrap();
    assert!(!titles_by(&env, &["Estimate:attached"]).contains(&"extra-valued".to_string()));
    assert!(titles_by(&env, &["Estimate:detached"]).contains(&"extra-valued".to_string()));
}

#[test]
fn a_filter_by_a_catalog_field_name_also_finds_own_fields_of_the_same_name_and_type_and_skips_other_types() {
    let env = Env::new();
    let estimate = field(&env, "Estimate", FieldType::Int);
    let plain = env.type_with("Plain", vec![]);
    let with_estimate = env.type_with("WithEstimate", vec![tf(&estimate, false)]);
    new_task(&env, &with_estimate, "catalog3", Some(set(&[(&estimate, &["3"])]))).unwrap();
    new_task(&env, &with_estimate, "catalog-empty", None).unwrap();
    new_task(
        &env,
        &plain,
        "own10",
        Some(own(vec![own_field("estimate", FieldType::Int, &["10"])])),
    )
    .unwrap();
    new_task(&env, &plain, "own3", Some(own(vec![own_field("ESTIMATE", FieldType::Int, &["3"])]))).unwrap();
    new_task(
        &env,
        &plain,
        "own-empty",
        Some(own(vec![NewOwnField::new("Estimate", FieldType::Int)])),
    )
    .unwrap();
    new_task(
        &env,
        &plain,
        "own-string",
        Some(own(vec![own_field("Estimate", FieldType::String, &["3"])])),
    )
    .unwrap();
    new_task(&env, &plain, "none", None).unwrap();

    assert_eq!(titles_by(&env, &["Estimate=3"]), vec!["catalog3", "own3"]);
    assert_eq!(
        titles_by(&env, &["Estimate!=3"]),
        vec!["catalog-empty", "none", "own-empty", "own-string", "own10"]
    );
    assert_eq!(titles_by(&env, &["Estimate>3"]), vec!["own10"]);
    assert_eq!(titles_by(&env, &["Estimate>=3"]), vec!["catalog3", "own10", "own3"]);
    assert_eq!(titles_by(&env, &["estimate>=3", "ESTIMATE<=3"]), vec!["catalog3", "own3"]);
    assert_eq!(titles_by(&env, &["Estimate:set"]), vec!["catalog3", "own10", "own3"]);
    assert_eq!(
        titles_by(&env, &["Estimate:unset"]),
        vec!["catalog-empty", "none", "own-empty", "own-string"]
    );
    assert_eq!(
        titles_by(&env, &["Estimate:attached"]),
        vec!["catalog-empty", "catalog3", "own-empty", "own10", "own3"]
    );
    assert_eq!(titles_by(&env, &["Estimate:detached"]), vec!["none", "own-string"]);
    assert_eq!(
        titles_by(&env, &["Estimate:attached", "Estimate:unset"]),
        vec!["catalog-empty", "own-empty"]
    );
    assert!(list_error(&env, &["Estimate=abc"]).contains("is not an integer"));
}

#[test]
fn a_filter_by_an_own_field_name_without_a_catalog_field_reads_the_value_by_its_type_and_refuses_several_types() {
    let env = Env::new();
    let levels = enumeration(&env, "Levels", &["Low", "High"]);
    let plain = env.type_with("Plain", vec![]);
    new_task(
        &env,
        &plain,
        "h1",
        Some(own(vec![
            NewOwnField {
                multiple: true,
                ..own_field("Hours", FieldType::Float, &["1.5", "8"])
            },
            NewOwnField {
                enum_id: Some(levels.id),
                ..own_field("Mood", FieldType::Enum, &["High"])
            },
        ])),
    )
    .unwrap();
    new_task(
        &env,
        &plain,
        "h2",
        Some(own(vec![
            own_field("hours", FieldType::Float, &["2"]),
            own_field("Done", FieldType::Bool, &["true"]),
        ])),
    )
    .unwrap();
    new_task(&env, &plain, "h3", Some(own(vec![NewOwnField::new("Hours", FieldType::Float)]))).unwrap();
    new_task(&env, &plain, "x", Some(own(vec![own_field("Code", FieldType::Int, &["5"])]))).unwrap();
    new_task(&env, &plain, "y", Some(own(vec![own_field("Code", FieldType::String, &["abc"])]))).unwrap();
    new_task(&env, &plain, "z", None).unwrap();

    assert_eq!(titles_by(&env, &["Hours=1.50"]), vec!["h1"]);
    assert_eq!(titles_by(&env, &["HOURS>=2"]), vec!["h1", "h2"]);
    assert_eq!(titles_by(&env, &["Hours>5"]), vec!["h1"]);
    assert_eq!(titles_by(&env, &["Hours<2", "Hours>7"]), vec!["h1"]);
    assert_eq!(titles_by(&env, &["Hours!=1.5"]), vec!["h2", "h3", "x", "y", "z"]);
    assert_eq!(titles_by(&env, &["Hours:set"]), vec!["h1", "h2"]);
    assert_eq!(titles_by(&env, &["Hours:unset"]), vec!["h3", "x", "y", "z"]);
    assert_eq!(titles_by(&env, &["Hours:attached"]), vec!["h1", "h2", "h3"]);
    assert_eq!(titles_by(&env, &["Hours:detached"]), vec!["x", "y", "z"]);
    assert_eq!(titles_by(&env, &["Hours:attached", "Hours:unset"]), vec!["h3"]);
    assert_eq!(titles_by(&env, &["Done=TRUE"]), vec!["h2"]);
    assert_eq!(titles_by(&env, &["Mood=high"]), vec!["h1"]);
    assert!(list_error(&env, &["Hours=abc"]).contains("is not a number"));
    assert!(list_error(&env, &["Done>true"]).contains("does not apply"));
    assert_eq!(
        list_error(&env, &["Code=5"]),
        "Field filter 'Code=5': tasks have own fields 'Code' of several types (int, string) and the catalog has no such field, so the value cannot be read"
    );
    assert!(list_error(&env, &["Nothing=1"]).contains("no field 'Nothing'"));
    field(&env, "Code", FieldType::String);
    assert_eq!(titles_by(&env, &["Code=abc"]), vec!["y"]);
    assert_eq!(titles_by(&env, &["Code:attached"]), vec!["y"]);
}

#[test]
fn own_field_values_follow_the_task_when_it_is_edited_and_deleted_and_own_field_names_are_letters_and_digits() {
    let env = Env::new();
    let plain = env.type_with("Plain", vec![]);
    let task = new_task(&env, &plain, "t", Some(own(vec![own_field("Hours", FieldType::Int, &["4"])]))).unwrap();
    assert_eq!(titles_by(&env, &["Hours=4"]), vec!["t"]);
    let own_id = view(&env, &task)[0].field_id;
    let edited = edit(&env, &task, Some(set_id(own_id, &["9"]))).unwrap();
    assert!(titles_by(&env, &["Hours=4"]).is_empty());
    assert_eq!(titles_by(&env, &["Hours=9"]), vec!["t"]);
    let removed = edit(
        &env,
        &edited,
        Some(TaskFieldChanges {
            remove_fields: Some(vec![own_id]),
            ..TaskFieldChanges::default()
        }),
    )
    .unwrap();
    assert!(list_error(&env, &["Hours=9"]).contains("no field 'Hours'"));
    assert!(
        message(edit(
            &env,
            &removed,
            Some(own(vec![NewOwnField::new("Story points", FieldType::Int)]))
        ))
        .contains("letters and digits")
    );
    assert!(message(edit(&env, &removed, Some(own(vec![NewOwnField::new("A:B", FieldType::Int)])))).contains("letters and digits"));
    let again = edit(&env, &removed, Some(own(vec![own_field("Hours", FieldType::Int, &["1"])]))).unwrap();
    assert_eq!(titles_by(&env, &["Hours=1"]), vec!["t"]);
    assert!(env.ws.tasks().delete(&env.project, &again.id, Some(&again.version)).unwrap());
    assert!(list_error(&env, &["Hours=1"]).contains("no field 'Hours'"));
}

#[test]
fn field_names_consist_of_letters_and_digits_and_a_legacy_name_stays_usable() {
    let env = Env::new();
    for good in ["Estimate", "Оценка", "Story2", "Ёж"] {
        new_field(&env, good).unwrap();
    }
    for bad in [
        "Story points",
        "Story_points",
        "Story-points",
        "A=B",
        "A!",
        "A<B",
        "A>B",
        "A:B",
        "A.B",
        "A,B",
        "Оценка!",
    ] {
        assert!(message(new_field(&env, bad)).contains("letters and digits"), "{bad}");
    }
    assert_eq!(new_field(&env, "  Padded ").unwrap().name, "Padded");

    let f = new_field(&env, "Renamable").unwrap();
    let rename = |name: &str, version: &str| {
        env.ws.fields().update(
            &env.project,
            &f.id,
            &UpdateField {
                name: Some(name.into()),
                version: Some(version.into()),
                ..UpdateField::default()
            },
        )
    };
    assert!(message(rename("Re named", &f.version)).contains("letters and digits"));
    assert_eq!(rename("Новое", &f.version).unwrap().unwrap().name, "Новое");

    let mut legacy = FieldDefinition {
        id: Uuid::new_v4(),
        project_id: env.project,
        name: "Story points".into(),
        field_type: FieldType::Int,
        multiple: false,
        enum_id: None,
        version: String::new(),
    };
    legacy.version = env.ws.add(&legacy).unwrap();
    let old = env.type_with("Old", vec![tf(&legacy, false)]);
    new_task(&env, &old, "five", Some(set(&[(&legacy, &["5"])]))).unwrap();
    new_task(&env, &old, "two", Some(set(&[(&legacy, &["2"])]))).unwrap();
    assert_eq!(titles_by(&env, &["Story points>=3"]), vec!["five"]);
    assert_eq!(titles_by(&env, &["story points!=5", "Story points:set"]), vec!["two"]);

    let same = env
        .ws
        .fields()
        .update(
            &env.project,
            &legacy.id,
            &UpdateField {
                name: Some("Story points".into()),
                version: Some(legacy.version.clone()),
                field_type: Some(FieldType::Float),
                ..UpdateField::default()
            },
        )
        .unwrap()
        .unwrap();
    assert_eq!((same.name.as_str(), same.field_type), ("Story points", FieldType::Float));
    assert!(
        message(env.ws.fields().update(
            &env.project,
            &legacy.id,
            &UpdateField {
                name: Some("Story  points".into()),
                version: Some(same.version.clone()),
                ..UpdateField::default()
            },
        ))
        .contains("letters and digits")
    );
}

// ---- правка определения поля: тип, множественность, перечисление ----

fn change(
    env: &Env,
    f: &FieldDefinition,
    field_type: Option<FieldType>,
    multiple: Option<bool>,
    enum_id: Option<Uuid>,
    choice: Option<FieldChangeChoice>,
) -> tasker_services::Result<FieldDefinition> {
    change_v(env, f, field_type, multiple, enum_id, choice, None)
}

fn change_v(
    env: &Env,
    f: &FieldDefinition,
    field_type: Option<FieldType>,
    multiple: Option<bool>,
    enum_id: Option<Uuid>,
    choice: Option<FieldChangeChoice>,
    version: Option<&str>,
) -> tasker_services::Result<FieldDefinition> {
    env.ws
        .fields()
        .update(
            &env.project,
            &f.id,
            &UpdateField {
                name: None,
                version: Some(version.unwrap_or(&f.version).to_string()),
                field_type,
                multiple,
                enum_id,
                choice,
            },
        )
        .map(|x| x.unwrap())
}

fn current(env: &Env, f: &FieldDefinition) -> FieldDefinition {
    env.ws.fields().get_by_id(&env.project, &f.id).unwrap().unwrap()
}

fn values_of(env: &Env, task: &TaskItem) -> Vec<String> {
    env.get(&task.id).fields.first().map(|f| f.values.clone()).unwrap_or_default()
}

fn mapping(from: &str, to: &str) -> FieldChangeChoice {
    FieldChangeChoice {
        mapping: Some(vec![FieldValueMapping {
            from: from.into(),
            to: to.into(),
        }]),
        ..FieldChangeChoice::default()
    }
}

fn clear_unconvertible() -> FieldChangeChoice {
    FieldChangeChoice {
        clear_unconvertible: true,
        ..FieldChangeChoice::default()
    }
}

fn several(choice: SeveralValues) -> FieldChangeChoice {
    FieldChangeChoice {
        several: Some(choice),
        ..FieldChangeChoice::default()
    }
}

#[test]
fn an_int_field_becomes_float_and_any_field_becomes_string_keeping_the_values_readable() {
    let env = Env::new();
    let count = field_with(&env, "Count", FieldType::Int, true, None);
    let bug = env.type_with("Bug", vec![tf(&count, false)]);
    let task = new_task(&env, &bug, "A", Some(set(&[(&count, &["7", "-2"])]))).unwrap();
    let as_float = change(&env, &count, Some(FieldType::Float), None, None, None).unwrap();
    assert_eq!((as_float.field_type, as_float.multiple), (FieldType::Float, true));
    assert_eq!(values_of(&env, &task), vec!["7", "-2"]);
    let as_string = change(&env, &as_float, Some(FieldType::String), None, None, None).unwrap();
    assert_eq!(current(&env, &count).field_type, FieldType::String);
    assert_eq!(current(&env, &count).version, as_string.version);
    assert_eq!(values_of(&env, &task), vec!["7", "-2"]);
    assert_eq!(view(&env, &task)[0].texts, vec!["7", "-2"]);
}

#[test]
fn a_type_change_that_would_lose_data_is_refused_and_has_to_go_through_string() {
    let env = Env::new();
    let weight = field(&env, "Weight", FieldType::Float);
    let flag = field(&env, "Flag", FieldType::Bool);
    assert_eq!(
        message(change(&env, &weight, Some(FieldType::Int), None, None, None)),
        "Type: cannot change a field from float to int directly (it would lose values): change it to string first, then to the type you need"
    );
    assert!(message(change(&env, &flag, Some(FieldType::Date), None, None, None)).contains("string first"));
    let prio = priority(&env);
    assert!(message(change(&env, &weight, Some(FieldType::Enum), None, Some(prio.id), None)).contains("string first"));
    assert_eq!(current(&env, &weight).version, weight.version);
}

#[test]
fn a_string_field_becomes_int_when_all_values_parse_and_equal_values_merge() {
    let env = Env::new();
    let f = field_with(&env, "Code", FieldType::String, true, None);
    let bug = env.type_with("Bug", vec![tf(&f, false)]);
    let task = new_task(&env, &bug, "A", Some(set(&[(&f, &["007", "7", "12"])]))).unwrap();
    let other = new_task(&env, &bug, "B", Some(set(&[(&f, &["3"])]))).unwrap();
    new_task(&env, &bug, "No value", None).unwrap();
    let changed = change(&env, &f, Some(FieldType::Int), None, None, None).unwrap();
    assert_eq!(changed.field_type, FieldType::Int);
    assert_eq!(values_of(&env, &task), vec!["7", "12"]);
    assert_eq!(values_of(&env, &other), vec!["3"]);
}

#[test]
fn values_that_do_not_parse_need_an_explicit_choice_and_nothing_changes_without_it() {
    let env = Env::new();
    let f = field_with(&env, "Code", FieldType::String, true, None);
    let bug = env.type_with("Bug", vec![tf(&f, true)]);
    let mixed = new_task(&env, &bug, "Mixed", Some(set(&[(&f, &["5", "abc"])]))).unwrap();
    let bad = new_task(&env, &bug, "Bad", Some(set(&[(&f, &["1.5"])]))).unwrap();
    let good = new_task(&env, &bug, "Good", Some(set(&[(&f, &["9"])]))).unwrap();

    let error = err(change(&env, &f, Some(FieldType::Int), None, None, None));
    assert_eq!(error.conflict_code(), Some(ConflictCode::InUse));
    assert_eq!(
        error.message(),
        "Field 'Code' cannot be changed: 2 task(s) have values that do not fit the new definition (e.g. 'abc', '1.5'): choose to clear such values (the others are converted)"
    );
    assert_eq!(current(&env, &f).version, f.version);
    assert_eq!(values_of(&env, &mixed), vec!["5", "abc"]);

    let changed = change(&env, &f, Some(FieldType::Int), None, None, Some(clear_unconvertible())).unwrap();
    assert_eq!(changed.field_type, FieldType::Int);
    assert_eq!(values_of(&env, &mixed), vec!["5"]);
    assert!(env.get(&bad.id).fields.is_empty());
    assert_eq!(values_of(&env, &good), vec!["9"]);
}

#[test]
fn a_choice_is_not_needed_when_no_task_is_affected_and_an_extra_field_stays_when_emptied() {
    let env = Env::new();
    let f = field(&env, "Code", FieldType::String);
    let bug = env.type_with("Bug", vec![]);
    let added = new_task(
        &env,
        &bug,
        "Added",
        Some(TaskFieldChanges {
            add_fields: Some(vec![f.id]),
            ..TaskFieldChanges::default()
        }),
    )
    .unwrap();
    let extra = new_task(&env, &bug, "Extra", Some(set(&[(&f, &["x"])]))).unwrap();
    assert!(message(change(&env, &f, Some(FieldType::Date), None, None, None)).contains("1 task(s)"));
    change(&env, &f, Some(FieldType::Date), None, None, Some(clear_unconvertible())).unwrap();
    assert_eq!(env.get(&added.id).fields[0].field_id, f.id);
    let kept = env.get(&extra.id).fields.remove(0);
    assert_eq!((kept.field_id, kept.values.is_empty()), (f.id, true));
    assert_eq!(current(&env, &f).field_type, FieldType::Date);
    let unused = field(&env, "Unused", FieldType::String);
    assert_eq!(
        change(&env, &unused, Some(FieldType::Bool), None, None, None).unwrap().field_type,
        FieldType::Bool
    );
}

#[test]
fn a_string_field_becomes_enum_matching_values_by_name_or_by_an_explicit_mapping() {
    let env = Env::new();
    let prio = priority(&env);
    let f = field_with(&env, "Level", FieldType::String, true, None);
    let bug = env.type_with("Bug", vec![tf(&f, false)]);
    let task = new_task(&env, &bug, "A", Some(set(&[(&f, &["high", "Urgent"])]))).unwrap();

    let error = message(change(&env, &f, Some(FieldType::Enum), None, Some(prio.id), None));
    assert!(error.contains("1 task(s)") && error.contains("'Urgent'") && error.ends_with("or map them to values of the enum"));
    assert!(
        message(change(
            &env,
            &f,
            Some(FieldType::Enum),
            None,
            Some(prio.id),
            Some(mapping("Urgent", "Nope"))
        ))
        .contains("is not a value")
    );
    assert_eq!(
        message(change(
            &env,
            &f,
            Some(FieldType::String),
            None,
            None,
            Some(mapping("Urgent", "High"))
        )),
        "Choice.Mapping: only when the new type is enum"
    );
    let changed = change(
        &env,
        &f,
        Some(FieldType::Enum),
        None,
        Some(prio.id),
        Some(mapping("Urgent", "Medium")),
    )
    .unwrap();
    assert_eq!((changed.field_type, changed.enum_id), (FieldType::Enum, Some(prio.id)));
    assert_eq!(values_of(&env, &task), vec![guid_d(&prio.values[2].id), guid_d(&prio.values[1].id)]);
}

#[test]
fn an_enum_field_moves_to_another_enum_by_name_by_mapping_or_clearing_and_becomes_string_with_names() {
    let env = Env::new();
    let prio = priority(&env);
    let severity = enumeration(&env, "Severity", &["low", "Critical"]);
    let f = field_with(&env, "Level", FieldType::Enum, true, Some(prio.id));
    let bug = env.type_with("Bug", vec![tf(&f, false)]);
    let task = new_task(&env, &bug, "A", Some(set(&[(&f, &["Low", "High"])]))).unwrap();
    let low_only = new_task(&env, &bug, "B", Some(set(&[(&f, &["Low"])]))).unwrap();
    let (low_new, critical_new) = (guid_d(&severity.values[0].id), guid_d(&severity.values[1].id));

    assert!(message(change(&env, &f, None, None, Some(severity.id), None)).contains("'High'"));
    assert!(
        message(change(&env, &f, None, None, Some(severity.id), Some(mapping("Nope", "Critical"))))
            .contains("is not a value of enum 'Priority'")
    );
    assert_eq!(current(&env, &f).enum_id, Some(prio.id));

    let mapped = change(&env, &f, None, None, Some(severity.id), Some(mapping("High", "Critical"))).unwrap();
    assert_eq!(mapped.enum_id, Some(severity.id));
    assert_eq!(values_of(&env, &task), vec![low_new.clone(), critical_new]);
    assert_eq!(values_of(&env, &low_only), vec![low_new]);

    let back = change(&env, &mapped, None, None, Some(prio.id), Some(clear_unconvertible())).unwrap();
    assert_eq!(values_of(&env, &task), vec![guid_d(&prio.values[0].id)]);
    let text = change(&env, &back, Some(FieldType::String), None, None, None).unwrap();
    assert_eq!((text.field_type, text.enum_id), (FieldType::String, None));
    assert_eq!(values_of(&env, &task), vec!["Low"]);
    assert_eq!(values_of(&env, &low_only), vec!["Low"]);
}

#[test]
fn the_enum_of_own_fields_and_the_enum_itself_are_not_touched_by_a_field_change() {
    let env = Env::new();
    let prio = priority(&env);
    let f = field_with(&env, "Level", FieldType::Enum, false, Some(prio.id));
    let plain = env.type_with("Plain", vec![]);
    let own_task = new_task(
        &env,
        &plain,
        "Own",
        Some(own(vec![NewOwnField {
            enum_id: Some(prio.id),
            ..own_field("Mood", FieldType::Enum, &["High"])
        }])),
    )
    .unwrap();
    change(&env, &f, Some(FieldType::String), None, None, None).unwrap();
    let entry = env.get(&own_task.id).fields.remove(0);
    assert_eq!(entry.values, vec![guid_d(&prio.values[2].id)]);
    assert_eq!(entry.own.unwrap().field_type, FieldType::Enum);
    assert_eq!(
        env.ws.enums().get_by_id(&env.project, &prio.id).unwrap().unwrap().version,
        prio.version
    );
}

#[test]
fn multiplicity_one_to_several_keeps_values_and_several_to_one_needs_a_choice_for_tasks_with_several() {
    let env = Env::new();
    let f = field(&env, "Tags", FieldType::String);
    let bug = env.type_with("Bug", vec![tf(&f, false)]);
    let one = new_task(&env, &bug, "One", Some(set(&[(&f, &["a"])]))).unwrap();
    let multi = change(&env, &f, None, Some(true), None, None).unwrap();
    assert!(multi.multiple);
    assert_eq!(values_of(&env, &one), vec!["a"]);

    let two = new_task(&env, &bug, "Two", Some(set(&[(&f, &["b", "c"])]))).unwrap();
    let error = err(change(&env, &multi, None, Some(false), None, None));
    assert_eq!(error.conflict_code(), Some(ConflictCode::InUse));
    assert_eq!(
        error.message(),
        "Field 'Tags' cannot be changed: 1 task(s) have several values, but the field will hold one: choose to keep the first value or to clear them"
    );
    assert!(current(&env, &f).multiple);
    assert_eq!(values_of(&env, &two), vec!["b", "c"]);

    let first = change(&env, &multi, None, Some(false), None, Some(several(SeveralValues::KeepFirst))).unwrap();
    assert!(!first.multiple);
    assert_eq!(values_of(&env, &two), vec!["b"]);
    assert_eq!(values_of(&env, &one), vec!["a"]);

    let again = change(&env, &first, None, Some(true), None, None).unwrap();
    let three = new_task(&env, &bug, "Three", Some(set(&[(&f, &["x", "y"])]))).unwrap();
    change(&env, &again, None, Some(false), None, Some(several(SeveralValues::Clear))).unwrap();
    assert!(values_of(&env, &three).is_empty());
    assert_eq!(values_of(&env, &two), vec!["b"]);
}

#[test]
fn type_and_multiplicity_can_change_together_and_the_converted_values_decide_whether_several_remain() {
    let env = Env::new();
    let f = field_with(&env, "Code", FieldType::String, true, None);
    let bug = env.type_with("Bug", vec![tf(&f, false)]);
    let merged = new_task(&env, &bug, "Merged", Some(set(&[(&f, &["07", "7"])]))).unwrap();
    let split = new_task(&env, &bug, "Split", Some(set(&[(&f, &["1", "2"])]))).unwrap();
    assert!(message(change(&env, &f, Some(FieldType::Int), Some(false), None, None)).contains("1 task(s)"));
    change(
        &env,
        &f,
        Some(FieldType::Int),
        Some(false),
        None,
        Some(several(SeveralValues::KeepFirst)),
    )
    .unwrap();
    assert_eq!(values_of(&env, &merged), vec!["7"]);
    assert_eq!(values_of(&env, &split), vec!["1"]);
}

#[test]
fn a_field_change_is_validated_against_the_enum_and_the_version() {
    let env = Env::new();
    let prio = priority(&env);
    let f = field(&env, "Level", FieldType::String);
    let enum_field = field_with(&env, "Prio", FieldType::Enum, false, Some(prio.id));
    assert_eq!(
        message(change(&env, &f, Some(FieldType::Enum), None, None, None)),
        "EnumId: a field of type enum must refer to an enum of the project"
    );
    assert_eq!(
        message(change(&env, &f, None, None, Some(prio.id), None)),
        "EnumId: only a field of type enum has an enum"
    );
    assert!(message(change(&env, &enum_field, Some(FieldType::String), None, Some(prio.id), None)).contains("only a field of type enum"));
    assert!(message(change(&env, &f, Some(FieldType::Enum), None, Some(Uuid::new_v4()), None)).contains("enum not found"));
    assert!(
        message(env.ws.fields().update(
            &env.project,
            &f.id,
            &UpdateField {
                field_type: Some(FieldType::Int),
                ..UpdateField::default()
            },
        ))
        .contains("Version is required")
    );
    assert_eq!(
        err(change_v(&env, &f, Some(FieldType::Int), None, None, None, Some("stale"))).conflict_code(),
        Some(ConflictCode::Modified)
    );
    assert!(
        env.ws
            .fields()
            .update(
                &env.project,
                &Uuid::new_v4(),
                &UpdateField {
                    version: Some("v".into()),
                    field_type: Some(FieldType::Int),
                    ..UpdateField::default()
                },
            )
            .unwrap()
            .is_none()
    );
    assert_eq!(
        change(&env, &enum_field, None, Some(true), None, None).unwrap().enum_id,
        Some(prio.id)
    );
    assert_eq!(current(&env, &f).field_type, FieldType::String);
}

#[test]
fn a_field_change_that_hits_a_locked_task_changes_nothing_and_a_stale_version_leaves_the_tasks_alone() {
    let env = Env::new().with_cascade_timeout(Duration::from_millis(300));
    let f = field(&env, "Code", FieldType::String);
    let bug = env.type_with("Bug", vec![tf(&f, false)]);
    let locked = new_task(&env, &bug, "Locked", Some(set(&[(&f, &["001"])]))).unwrap();
    let free = new_task(&env, &bug, "Free", Some(set(&[(&f, &["002"])]))).unwrap();
    let unrelated = new_task(&env, &bug, "Unrelated", None).unwrap();
    anna_locks(&env, &locked);
    anna_locks(&env, &unrelated);

    let error = err(change(&env, &f, Some(FieldType::Int), None, None, None));
    assert_eq!(error.conflict_code(), Some(ConflictCode::Locked));
    assert_eq!(error.message(), "Task 'Locked' is being edited by Anna");
    assert_eq!(current(&env, &f).field_type, FieldType::String);
    assert_eq!(env.get(&free.id).version, free.version);
    assert!(err(change_v(&env, &f, Some(FieldType::Int), None, None, None, Some("stale"))).is_modified());
    assert_eq!(env.get(&free.id).version, free.version);

    anna_releases(&env, &locked);
    assert_eq!(
        change(&env, &f, Some(FieldType::Int), None, None, None).unwrap().field_type,
        FieldType::Int
    );
    assert_eq!(values_of(&env, &locked), vec!["1"]);
    assert_eq!(values_of(&env, &free), vec!["2"]);
}

#[test]
fn fields_of_tasks_and_types_survive_a_fresh_workspace_and_the_index_finds_them_by_field_and_enum() {
    let env = Env::new();
    let (field_id, own_id, enum_id) = (Uuid::new_v4(), Uuid::new_v4(), Uuid::new_v4());
    let mut task = env.seed("Fields", &[]);
    task.fields = vec![
        tasker_core::model::TaskField {
            field_id,
            values: ["null", "true", "007", "~", "a: b", "- x", "multi\nline"]
                .iter()
                .map(|x| x.to_string())
                .collect(),
            own: None,
        },
        tasker_core::model::TaskField {
            field_id: own_id,
            values: vec!["3".into(), "8".into()],
            own: Some(OwnField {
                name: "Оценка".into(),
                field_type: FieldType::Int,
                required: true,
                multiple: true,
                enum_id: None,
            }),
        },
        tasker_core::model::TaskField {
            field_id: Uuid::new_v4(),
            values: vec![guid_d(&Uuid::new_v4())],
            own: Some(OwnField {
                name: "Mood".into(),
                field_type: FieldType::Enum,
                required: false,
                multiple: false,
                enum_id: Some(enum_id),
            }),
        },
    ];
    task.version = env.ws.update(&task, &task.version).unwrap().unwrap();

    // Новый экземпляр области знает файлы только с диска.
    let fresh = Workspace::open(&env.dir).unwrap();
    let read = fresh.tasks().get_by_id(&env.project, &task.id).unwrap().unwrap();
    assert_eq!(read.fields, task.fields);
    let by_field = |ids: Vec<Uuid>| {
        fresh
            .tasks()
            .get_all(
                &env.project,
                Some(&tasker_core::tasks::TaskFilter {
                    field_ids: Some(ids),
                    ..Default::default()
                }),
            )
            .unwrap()
            .len()
    };
    assert_eq!(by_field(vec![field_id]), 1);
    assert_eq!(by_field(vec![Uuid::new_v4(), own_id]), 1);
    assert_eq!(by_field(vec![Uuid::new_v4()]), 0);
    assert_eq!(by_field(vec![]), 0);
    let by_enum = |id: Uuid| {
        fresh
            .tasks()
            .count(
                &env.project,
                &tasker_core::tasks::TaskFilter {
                    enum_ids: Some(vec![id]),
                    ..Default::default()
                },
            )
            .unwrap()
    };
    assert_eq!((by_enum(enum_id), by_enum(Uuid::new_v4())), (1, 0));
}
