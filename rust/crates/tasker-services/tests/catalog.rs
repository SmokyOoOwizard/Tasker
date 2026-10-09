//! Сценарии `FieldCatalogTests.cs`: каталог полей и перечислений проекта — создание, поиск, уникальность имён, правка значений
//! перечислений, версии, «используется», блокировки, гонка имён, файлы каталога.
mod common;

use common::*;
use tasker_core::ConflictCode;
use tasker_core::locks::LockedEntity;
use tasker_core::model::{FieldDefinition, FieldEnum, FieldType};
use tasker_core::tasks::Page;
use tasker_services::Workspace;
use tasker_services::field::{CreateField, UpdateField};
use tasker_services::field_enum::{CreateFieldEnum, FieldEnumValueInput, UpdateFieldEnum};
use tasker_services::locks::EntityLockService;
use uuid::Uuid;

fn new_enum(env: &Env, project: &Uuid, name: &str, values: &[&str]) -> tasker_services::Result<FieldEnum> {
    let values = if values.is_empty() { vec!["Low", "High"] } else { values.to_vec() };
    env.ws.enums().create(
        project,
        &CreateFieldEnum {
            name: name.into(),
            values: values.iter().map(|x| x.to_string()).collect(),
        },
    )
}

fn new_field(
    env: &Env,
    project: &Uuid,
    name: &str,
    field_type: FieldType,
    multiple: Option<bool>,
    enum_id: Option<Uuid>,
) -> tasker_services::Result<FieldDefinition> {
    env.ws.fields().create(
        project,
        &CreateField {
            name: name.into(),
            field_type,
            multiple,
            enum_id,
        },
    )
}

fn new_project(env: &Env) -> Uuid {
    env.ws
        .projects()
        .create(&tasker_services::project::CreateProject { name: "Other".into() })
        .unwrap()
        .id
}

fn rename_enum(ws: &Workspace, project: &Uuid, id: &Uuid, name: &str, version: Option<&str>) -> tasker_services::Result<Option<FieldEnum>> {
    ws.enums()
        .update(
            project,
            id,
            &UpdateFieldEnum {
                name: Some(name.into()),
                version: version.map(str::to_string),
                ..UpdateFieldEnum::default()
            },
        )
        .map(|x| x.map(|r| r.value))
}

fn rename_field(
    ws: &Workspace,
    project: &Uuid,
    id: &Uuid,
    name: &str,
    version: Option<&str>,
) -> tasker_services::Result<Option<FieldDefinition>> {
    ws.fields().update(
        project,
        id,
        &UpdateField {
            name: Some(name.into()),
            version: version.map(str::to_string),
            ..UpdateField::default()
        },
    )
}

// ---- перечисления ----

#[test]
fn an_enum_is_created_with_values_that_have_ids_listed_by_name_and_read_back_by_id() {
    let env = Env::new();
    let p = &env.project;
    let priority = new_enum(&env, p, "Priority", &["Low", "Medium", "High"]).unwrap();
    new_enum(&env, p, "Area", &["UI", "API"]).unwrap();
    assert_eq!(
        priority.values.iter().map(|x| x.name.as_str()).collect::<Vec<_>>(),
        vec!["Low", "Medium", "High"]
    );
    let mut ids: Vec<Uuid> = priority.values.iter().map(|x| x.id).collect();
    ids.sort();
    ids.dedup();
    assert_eq!(ids.len(), 3);
    assert!(!ids.contains(&Uuid::nil()));
    assert!(!priority.version.is_empty());

    let all = env.ws.enums().get_all(p).unwrap();
    assert_eq!(all.iter().map(|x| x.name.as_str()).collect::<Vec<_>>(), vec!["Area", "Priority"]);
    let page = env.ws.enums().get_range(p, Page::new(1, 1)).unwrap();
    assert_eq!((page.total_count, page.data.len(), page.data[0].name.as_str()), (2, 1, "Priority"));
    let read = env.ws.enums().get_by_id(p, &priority.id).unwrap().unwrap();
    assert_eq!((read.values, read.version), (priority.values.clone(), priority.version.clone()));
    assert!(env.ws.enums().get_by_id(p, &Uuid::new_v4()).unwrap().is_none());

    let other = new_project(&env);
    assert!(env.ws.enums().get_all(&other).unwrap().is_empty());
    assert!(env.ws.enums().get_by_id(&other, &priority.id).unwrap().is_none());

    // Поиск по id или имени без учёта регистра.
    assert_eq!(env.ws.enums().find(p, "priority").unwrap().unwrap().id, priority.id);
    assert_eq!(env.ws.enums().find(p, &priority.id.to_string()).unwrap().unwrap().id, priority.id);
    assert!(env.ws.enums().find(p, "nope").unwrap().is_none());
}

#[test]
fn enum_names_are_unique_in_the_project_ignoring_case_but_not_across_projects() {
    let env = Env::new();
    let p = &env.project;
    new_enum(&env, p, "Priority", &[]).unwrap();
    let error = err(new_enum(&env, p, "  priority ", &[]));
    assert_eq!(error.message(), "Enum 'priority' already exists in the project");
    assert!(error.is_in_use());
    new_enum(&env, &new_project(&env), "Priority", &[]).unwrap();

    let area = new_enum(&env, p, "Area", &[]).unwrap();
    assert!(err(rename_enum(&env.ws, p, &area.id, "PRIORITY", Some(&area.version))).is_in_use());
    assert_eq!(
        rename_enum(&env.ws, p, &area.id, "AREA", Some(&area.version))
            .unwrap()
            .unwrap()
            .name,
        "AREA"
    );
}

#[test]
fn an_enum_without_values_or_with_repeated_or_empty_values_is_rejected() {
    let env = Env::new();
    let p = &env.project;
    assert_eq!(
        message(env.ws.enums().create(
            p,
            &CreateFieldEnum {
                name: "E".into(),
                values: vec![]
            }
        )),
        "Enum must contain at least one value"
    );
    assert_eq!(
        message(new_enum(&env, p, "E", &["Low", "low"])),
        "Enum value 'low' is repeated: values must be unique"
    );
    assert_eq!(message(new_enum(&env, p, "E", &["Low", " "])), "Enum value is required");
    assert_eq!(message(new_enum(&env, p, " ", &["Low"])), "Enum name is required");
    assert!(env.ws.enums().get_all(p).unwrap().is_empty());
}

#[test]
fn updating_an_enum_renames_values_keeping_their_ids_adds_removes_and_reorders() {
    let env = Env::new();
    let p = &env.project;
    let e = new_enum(&env, p, "Priority", &["Low", "Medium", "High"]).unwrap();
    let (low, medium, high) = (&e.values[0], &e.values[1], &e.values[2]);
    let updated = env
        .ws
        .enums()
        .update(
            p,
            &e.id,
            &UpdateFieldEnum {
                name: Some("Prio".into()),
                values: Some(vec![
                    FieldEnumValueInput {
                        id: Some(high.id),
                        name: "Critical".into(),
                    },
                    FieldEnumValueInput {
                        id: Some(low.id),
                        name: "Low".into(),
                    },
                    FieldEnumValueInput {
                        id: None,
                        name: "Urgent".into(),
                    },
                ]),
                version: Some(e.version.clone()),
                removed: None,
            },
        )
        .unwrap()
        .unwrap()
        .value;
    assert_eq!(updated.name, "Prio");
    assert_eq!(
        updated.values.iter().map(|x| x.name.as_str()).collect::<Vec<_>>(),
        vec!["Critical", "Low", "Urgent"]
    );
    assert_eq!((updated.values[0].id, updated.values[1].id), (high.id, low.id));
    assert!(!updated.values.iter().any(|x| x.id == medium.id));
    assert!(![low.id, medium.id, high.id].contains(&updated.values[2].id));
    assert_ne!(e.version, updated.version);
    let read = env.ws.enums().get_by_id(p, &e.id).unwrap().unwrap();
    assert_eq!(
        (read.values.clone(), read.version.clone()),
        (updated.values.clone(), updated.version.clone())
    );
    let renamed_only = rename_enum(&env.ws, p, &e.id, "P2", Some(&updated.version)).unwrap().unwrap();
    assert_eq!(renamed_only.values, updated.values);
}

#[test]
fn updating_an_enum_validates_value_ids_names_and_that_something_remains() {
    let env = Env::new();
    let p = &env.project;
    let e = new_enum(&env, p, "Priority", &["Low", "High"]).unwrap();
    let update = |values: Vec<FieldEnumValueInput>| {
        env.ws.enums().update(
            p,
            &e.id,
            &UpdateFieldEnum {
                values: Some(values),
                version: Some(e.version.clone()),
                ..UpdateFieldEnum::default()
            },
        )
    };
    let v = |id: Option<Uuid>, name: &str| FieldEnumValueInput { id, name: name.into() };
    assert!(message(update(vec![v(Some(Uuid::new_v4()), "Ghost")])).contains("not found in the enum"));
    assert!(message(update(vec![v(Some(e.values[0].id), "A"), v(Some(e.values[0].id), "B")])).contains("repeated"));
    assert!(message(update(vec![v(Some(e.values[0].id), "X"), v(None, "x")])).contains("must be unique"));
    assert!(message(update(vec![])).contains("at least one"));
    assert_eq!(env.ws.enums().get_by_id(p, &e.id).unwrap().unwrap().values, e.values);
    assert!(rename_enum(&env.ws, p, &Uuid::new_v4(), "X", Some("v")).unwrap().is_none());
}

#[test]
fn changing_or_deleting_an_enum_requires_the_current_version() {
    let env = Env::new();
    let p = &env.project;
    let e = new_enum(&env, p, "Priority", &[]).unwrap();
    assert!(message(rename_enum(&env.ws, p, &e.id, "X", None)).contains("Version is required"));
    let first = rename_enum(&env.ws, p, &e.id, "First", Some(&e.version)).unwrap().unwrap();
    assert_eq!(
        err(rename_enum(&env.ws, p, &e.id, "Second", Some(&e.version))).conflict_code(),
        Some(ConflictCode::Modified)
    );
    assert_eq!(
        err(env.ws.enums().delete(p, &e.id, Some(&e.version))).conflict_code(),
        Some(ConflictCode::Modified)
    );
    assert_eq!(env.ws.enums().get_by_id(p, &e.id).unwrap().unwrap().name, "First");
    assert!(env.ws.enums().delete(p, &e.id, Some(&first.version)).unwrap());
    assert!(env.ws.enums().get_by_id(p, &e.id).unwrap().is_none());
    assert!(!env.ws.enums().delete(p, &e.id, Some(&first.version)).unwrap());
}

// ---- поля ----

#[test]
fn a_field_is_created_for_each_type_listed_by_name_and_read_back() {
    let env = Env::new();
    let p = &env.project;
    let priority = new_enum(&env, p, "Priority", &[]).unwrap();
    let created = vec![
        new_field(&env, p, "Note", FieldType::String, None, None).unwrap(),
        new_field(&env, p, "Estimate", FieldType::Int, None, None).unwrap(),
        new_field(&env, p, "Weight", FieldType::Float, None, None).unwrap(),
        new_field(&env, p, "Billable", FieldType::Bool, None, None).unwrap(),
        new_field(&env, p, "Due", FieldType::Date, None, None).unwrap(),
        new_field(&env, p, "Tags", FieldType::String, Some(true), None).unwrap(),
        new_field(&env, p, "Level", FieldType::Enum, None, Some(priority.id)).unwrap(),
        new_field(&env, p, "Levels", FieldType::Enum, Some(true), Some(priority.id)).unwrap(),
    ];
    let all = env.ws.fields().get_all(p).unwrap();
    let mut expected: Vec<&str> = created.iter().map(|x| x.name.as_str()).collect();
    expected.sort();
    assert_eq!(all.iter().map(|x| x.name.as_str()).collect::<Vec<_>>(), expected);
    for f in &created {
        assert_eq!(&env.ws.fields().get_by_id(p, &f.id).unwrap().unwrap(), f);
        assert_eq!(all.iter().find(|x| x.id == f.id).unwrap(), f);
    }
    assert_eq!(
        (created[1].field_type, created[1].multiple, created[1].enum_id),
        (FieldType::Int, false, None)
    );
    assert_eq!((created[5].field_type, created[5].multiple), (FieldType::String, true));
    assert_eq!(
        (created[6].field_type, created[6].multiple, created[6].enum_id),
        (FieldType::Enum, false, Some(priority.id))
    );
    let page = env.ws.fields().get_range(p, Page::new(2, 3)).unwrap();
    assert_eq!((page.total_count, page.data.len()), (8, 3));
    let other = new_project(&env);
    assert!(env.ws.fields().get_all(&other).unwrap().is_empty());
    assert!(env.ws.fields().get_by_id(p, &Uuid::new_v4()).unwrap().is_none());
    assert_eq!(env.ws.fields().find(p, "level").unwrap().unwrap().id, created[6].id);
    assert_eq!(
        env.ws.fields().find(p, &created[6].id.to_string()).unwrap().unwrap().id,
        created[6].id
    );
    assert!(env.ws.fields().find(p, "nope").unwrap().is_none());
}

#[test]
fn an_enum_field_needs_an_existing_enum_of_the_same_project_and_other_fields_must_not_have_one() {
    let env = Env::new();
    let p = &env.project;
    let priority = new_enum(&env, p, "Priority", &[]).unwrap();
    let other = new_project(&env);
    let foreign = new_enum(&env, &other, "Foreign", &[]).unwrap();
    assert_eq!(
        message(new_field(&env, p, "A", FieldType::Enum, None, None)),
        "EnumId: a field of type enum must refer to an enum of the project"
    );
    assert!(message(new_field(&env, p, "B", FieldType::Enum, None, Some(Uuid::new_v4()))).contains("enum not found"));
    assert!(message(new_field(&env, p, "C", FieldType::Enum, None, Some(foreign.id))).contains("enum not found"));
    assert_eq!(
        message(new_field(&env, p, "D", FieldType::Int, None, Some(priority.id))),
        "EnumId: only a field of type enum has an enum"
    );
    assert_eq!(
        message(new_field(&env, p, " ", FieldType::String, None, None)),
        "Field name is required"
    );
    assert!(env.ws.fields().get_all(p).unwrap().is_empty());
}

#[test]
fn field_names_are_unique_in_the_project_ignoring_case_but_not_across_projects() {
    let env = Env::new();
    let p = &env.project;
    let estimate = new_field(&env, p, "Estimate", FieldType::Int, None, None).unwrap();
    assert_eq!(
        message(new_field(&env, p, " estimate ", FieldType::Float, None, None)),
        "Field 'estimate' already exists in the project"
    );
    new_field(&env, &new_project(&env), "Estimate", FieldType::Int, None, None).unwrap();
    let weight = new_field(&env, p, "Weight", FieldType::Float, None, None).unwrap();
    assert!(err(rename_field(&env.ws, p, &weight.id, "ESTIMATE", Some(&weight.version))).is_in_use());
    assert_eq!(
        rename_field(&env.ws, p, &estimate.id, "ESTIMATE", Some(&estimate.version))
            .unwrap()
            .unwrap()
            .name,
        "ESTIMATE"
    );
}

#[test]
fn updating_a_field_renames_it_and_keeps_type_multiplicity_and_enum_and_requires_the_version() {
    let env = Env::new();
    let p = &env.project;
    let priority = new_enum(&env, p, "Priority", &[]).unwrap();
    let f = new_field(&env, p, "Level", FieldType::Enum, Some(true), Some(priority.id)).unwrap();
    let renamed = rename_field(&env.ws, p, &f.id, "Severity", Some(&f.version)).unwrap().unwrap();
    assert_eq!(
        renamed,
        FieldDefinition {
            name: "Severity".into(),
            version: renamed.version.clone(),
            ..f.clone()
        }
    );
    assert_ne!(f.version, renamed.version);
    assert_eq!(env.ws.fields().get_by_id(p, &f.id).unwrap().unwrap(), renamed);
    assert!(rename_field(&env.ws, p, &Uuid::new_v4(), "X", Some("v")).unwrap().is_none());

    let note = new_field(&env, p, "Note", FieldType::String, None, None).unwrap();
    assert!(message(rename_field(&env.ws, p, &note.id, "X", None)).contains("Version is required"));
    let first = rename_field(&env.ws, p, &note.id, "First", Some(&note.version)).unwrap().unwrap();
    assert!(err(rename_field(&env.ws, p, &note.id, "Second", Some(&note.version))).is_modified());
    assert!(err(env.ws.fields().delete(p, &note.id, Some(&note.version))).is_modified());
    assert_eq!(env.ws.fields().get_by_id(p, &note.id).unwrap().unwrap().name, "First");
    assert!(env.ws.fields().delete(p, &note.id, Some(&first.version)).unwrap());
    assert!(env.ws.fields().get_by_id(p, &note.id).unwrap().is_none());
    assert!(!env.ws.fields().delete(p, &note.id, Some(&first.version)).unwrap());
}

// ---- «используется» ----

#[test]
fn an_enum_used_by_a_field_cannot_be_deleted_until_the_field_is_gone() {
    let env = Env::new();
    let p = &env.project;
    let priority = new_enum(&env, p, "Priority", &[]).unwrap();
    let spare = new_enum(&env, p, "Spare", &[]).unwrap();
    let level = new_field(&env, p, "Level", FieldType::Enum, None, Some(priority.id)).unwrap();
    new_field(&env, p, "Level2", FieldType::Enum, Some(true), Some(priority.id)).unwrap();
    let error = err(env.ws.enums().delete(p, &priority.id, Some(&priority.version)));
    assert_eq!(error.conflict_code(), Some(ConflictCode::InUse));
    assert_eq!(
        error.message(),
        "Enum 'Priority' is used by field 'Level'; field 'Level2' and cannot be deleted"
    );
    assert!(env.ws.enums().get_by_id(p, &priority.id).unwrap().is_some());

    let renamed = rename_enum(&env.ws, p, &priority.id, "Prio", Some(&priority.version))
        .unwrap()
        .unwrap();
    assert_eq!(env.ws.fields().get_by_id(p, &level.id).unwrap().unwrap().enum_id, Some(priority.id));
    assert!(env.ws.enums().delete(p, &spare.id, Some(&spare.version)).unwrap());
    for f in env.ws.fields().get_all(p).unwrap() {
        assert!(env.ws.fields().delete(p, &f.id, Some(&f.version)).unwrap());
    }
    assert!(env.ws.enums().delete(p, &priority.id, Some(&renamed.version)).unwrap());
}

// ---- блокировки правки ----

#[test]
fn a_field_or_enum_locked_by_someone_else_cannot_be_changed_or_deleted() {
    let env = Env::new();
    let p = &env.project;
    let priority = new_enum(&env, p, "Priority", &[]).unwrap();
    let f = new_field(&env, p, "Note", FieldType::String, None, None).unwrap();
    let ivan = env.as_editor("user:ivan", "Ivan");
    let anna = env.as_editor("user:anna", "Anna");
    ivan.locks().acquire(LockedEntity::Field, &f.id, "Field 'Note'", Some(*p)).unwrap();
    ivan.locks()
        .acquire(LockedEntity::Enum, &priority.id, "Enum 'Priority'", Some(*p))
        .unwrap();

    let field409 = err(rename_field(&anna, p, &f.id, "X", Some(&f.version)));
    assert_eq!(
        (field409.conflict_code(), field409.message().as_str()),
        (Some(ConflictCode::Locked), "Field 'Note' is being edited by Ivan")
    );
    assert!(err(anna.fields().delete(p, &f.id, Some(&f.version))).is_locked());
    let enum409 = err(rename_enum(&anna, p, &priority.id, "X", Some(&priority.version)));
    assert_eq!(enum409.message(), "Enum 'Priority' is being edited by Ivan");
    assert!(err(anna.enums().delete(p, &priority.id, Some(&priority.version))).is_locked());
    assert_eq!(env.ws.fields().get_by_id(p, &f.id).unwrap().unwrap().name, "Note");

    let renamed = rename_field(&ivan, p, &f.id, "Memo", Some(&f.version)).unwrap().unwrap();
    assert!(ivan.fields().delete(p, &f.id, Some(&renamed.version)).unwrap());
    assert!(ivan.locks().get(LockedEntity::Field, &f.id).is_none());
}

#[test]
fn entity_locks_name_fields_and_enums_and_do_not_find_missing_ones() {
    let env = Env::new();
    let p = &env.project;
    let priority = new_enum(&env, p, "Priority", &[]).unwrap();
    let f = new_field(&env, p, "Note", FieldType::String, None, None).unwrap();
    let locks = env.ws.entity_locks();
    let field_lock = locks.acquire(Some(*p), LockedEntity::Field, &f.id).unwrap().unwrap();
    let enum_lock = locks.acquire(Some(*p), LockedEntity::Enum, &priority.id).unwrap().unwrap();
    assert_eq!(
        (field_lock.entity, field_lock.id, field_lock.mine),
        (LockedEntity::Field, f.id, true)
    );
    assert_eq!(
        (enum_lock.entity, enum_lock.id, enum_lock.mine),
        (LockedEntity::Enum, priority.id, true)
    );
    assert!(locks.get(LockedEntity::Field, &f.id).is_some());
    assert_eq!(locks.get_by_project(p).len(), 2);
    assert!(locks.acquire(Some(*p), LockedEntity::Field, &Uuid::new_v4()).unwrap().is_none());
    assert!(locks.acquire(Some(*p), LockedEntity::Enum, &f.id).unwrap().is_none());
    assert!(locks.release(LockedEntity::Field, &f.id).unwrap());
    assert!(locks.get(LockedEntity::Field, &f.id).is_none());
    assert_eq!(EntityLockService::parse_entity(Some("field")).unwrap(), LockedEntity::Field);
    assert_eq!(EntityLockService::parse_entity(Some("Enum")).unwrap(), LockedEntity::Enum);
    assert!(EntityLockService::is_project_scoped(LockedEntity::Field));
}

// ---- гонка имён ----

#[test]
fn concurrent_creation_of_the_same_name_leaves_exactly_one() {
    let env = Env::new();
    let results: Vec<bool> = std::thread::scope(|scope| {
        let handles: Vec<_> = (0..4)
            .map(|_| {
                let ws = env.ws.clone();
                let project = env.project;
                scope.spawn(move || {
                    ws.fields()
                        .create(
                            &project,
                            &CreateField {
                                name: "Same".into(),
                                field_type: FieldType::String,
                                multiple: None,
                                enum_id: None,
                            },
                        )
                        .is_ok()
                })
            })
            .collect();
        handles.into_iter().map(|h| h.join().unwrap()).collect()
    });
    assert_eq!(results.iter().filter(|x| **x).count(), 1);
    assert_eq!(env.ws.fields().get_all(&env.project).unwrap().len(), 1);
}

// ---- файлы ----

#[test]
fn fields_and_enums_are_files_in_their_own_folders_and_unreadable_files_are_counted() {
    let env = Env::new();
    let p = &env.project;
    let priority = new_enum(&env, p, "Приоритет", &["Низкий", "Высокий"]).unwrap();
    let level = new_field(&env, p, "Level", FieldType::Enum, Some(true), Some(priority.id)).unwrap();
    let plain = new_field(&env, p, "Note", FieldType::String, None, None).unwrap();
    let project = env.ws.directory().project(p);
    let read = |folder: std::path::PathBuf, id: &Uuid| {
        let prefix = tasker_files::names::id_prefix(id);
        let file = std::fs::read_dir(folder)
            .unwrap()
            .flatten()
            .find(|e| e.file_name().to_string_lossy().ends_with(&format!("-{prefix}.yaml")))
            .unwrap();
        std::fs::read_to_string(file.path()).unwrap()
    };
    let enum_text = read(project.enums(), &priority.id);
    assert!(enum_text.starts_with("formatVersion: 9\n"));
    assert!(
        enum_text.contains("name: Приоритет")
            && enum_text.contains(&format!("id: {}", priority.values[0].id))
            && enum_text.contains("name: Низкий")
    );
    let field_text = read(project.fields(), &level.id);
    assert!(
        field_text.contains("type: enum")
            && field_text.contains(&format!("enum: {}", priority.id))
            && field_text.contains("multiple: true")
    );
    let plain_text = read(project.fields(), &plain.id);
    assert!(!plain_text.contains("multiple:") && !plain_text.contains("enum:"));

    let fresh = Workspace::open(&env.dir).unwrap();
    let fields = fresh.fields().get_all(p).unwrap();
    assert_eq!(fields.iter().map(|x| x.name.as_str()).collect::<Vec<_>>(), vec!["Level", "Note"]);
    assert_eq!(fields[0].enum_id, Some(priority.id));
    assert_eq!(fresh.enums().get_all(p).unwrap()[0].values, priority.values);

    // Рукописный файл без formatVersion читается; конфликт слияния, неизвестный тип и формат новее — проблемы области.
    let good = Uuid::new_v4();
    std::fs::write(
        project.fields().join(format!("{good}.yaml")),
        format!("id: {good}\nname: Hand\ntype: INT\n"),
    )
    .unwrap();
    std::fs::write(project.fields().join(format!("{}.yaml", Uuid::new_v4())), "<<<<<<< HEAD\n").unwrap();
    std::fs::write(
        project.fields().join(format!("{}.yaml", Uuid::new_v4())),
        "id: 1\nname: x\ntype: banana\n",
    )
    .unwrap();
    std::fs::write(project.enums().join(format!("{}.yaml", Uuid::new_v4())), "<<<<<<< HEAD\n").unwrap();
    std::fs::write(
        project.enums().join(format!("{}.yaml", Uuid::new_v4())),
        "formatVersion: 99\nid: x\n",
    )
    .unwrap();
    env.ws.index().sync().unwrap();
    let names: Vec<String> = env.ws.fields().get_all(p).unwrap().into_iter().map(|x| x.name).collect();
    assert_eq!(names, vec!["Hand", "Level", "Note"]);
    assert_eq!(env.ws.fields().get_by_id(p, &good).unwrap().unwrap().field_type, FieldType::Int);
    assert_eq!(env.ws.fields().count_unreadable(p).unwrap(), 2);
    assert_eq!(env.ws.enums().count_unreadable(p).unwrap(), 2);
    assert_eq!(env.ws.enums().get_all(p).unwrap().len(), 1);
    assert_eq!(env.ws.fields().count_unreadable(&Uuid::new_v4()).unwrap(), 0);
}
