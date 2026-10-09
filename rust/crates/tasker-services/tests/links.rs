//! Сценарии `LinkCoreTests.cs` и `LinkCycleTests.cs`: типы связей по умолчанию, связи с обеих сторон, симметричные типы, проверки,
//! версии, удаление, блокировки, разбор фраз, циклы (запрет при создании и обнаружение после слияния git).
mod common;

use common::*;
use std::collections::HashMap;
use std::time::Duration;
use tasker_core::ConflictCode;
use tasker_core::ids::{DEFAULT_LINK_TYPES, default_link_type_id};
use tasker_core::locks::LockedEntity;
use tasker_core::model::{LinkType, TaskItem, TaskLink};
use tasker_core::tasks::{Page, TaskFilter};
use tasker_files::index::LinkEdge;
use tasker_services::cleanup::CleanupOptions;
use tasker_services::link_cycles;
use tasker_services::link_type::{CreateLinkType, DEFAULT_VERSION, LinkDirection, UpdateLinkType};
use tasker_services::links::MAX_VIEWED;
use tasker_services::task::CreateTask;
use uuid::Uuid;

fn link_type(env: &Env, name: &str) -> LinkType {
    env.ws.link_types().find(&env.project, name).unwrap().unwrap()
}

fn create(env: &Env, title: &str) -> TaskItem {
    env.clock.advance(Duration::from_secs(1));
    env.task(title)
}

fn create_in(env: &Env, title: &str, series: Uuid) -> TaskItem {
    env.clock.advance(Duration::from_secs(1));
    env.ws
        .tasks()
        .create(
            &env.project,
            &CreateTask {
                series_ids: Some(vec![series]),
                ..CreateTask::new(title, env.task_type.id)
            },
        )
        .unwrap()
}

fn add(env: &Env, source: &TaskItem, type_id: &Uuid, target: &TaskItem) -> tasker_services::Result<Option<TaskItem>> {
    env.ws.links().add(&env.project, &source.id, type_id, &target.id, None)
}

fn remove(env: &Env, source: &TaskItem, type_id: &Uuid, target: &TaskItem) -> tasker_services::Result<Option<TaskItem>> {
    env.ws.links().remove(&env.project, &source.id, type_id, &target.id, None)
}

fn links_of(env: &Env, task: &TaskItem) -> Vec<tasker_services::links::TaskLinkView> {
    env.ws.links().get_links(&env.project, &task.id).unwrap().unwrap()
}

/// Как после слияния веток: связь появилась в файле задачи мимо сервиса.
fn add_raw(env: &Env, source: &Uuid, type_id: &Uuid, target: &Uuid) {
    let mut task = env.get(source);
    task.links.push(TaskLink {
        type_id: *type_id,
        target_id: *target,
    });
    env.ws.update(&task, &task.version).unwrap().unwrap();
}

// ---- типы по умолчанию ----

#[test]
fn a_project_shows_the_jira_link_types_without_writing_and_saves_them_on_the_first_write() {
    let env = Env::new();
    let types = env.ws.link_types().get_all(&env.project).unwrap();
    let again = env.ws.link_types().get_all(&env.project).unwrap();
    let page = env.ws.link_types().get_range(&env.project, Page::first(50)).unwrap();
    let one = env.ws.link_types().get_by_id(&env.project, &types[0].id).unwrap().unwrap();

    assert_eq!(
        types.iter().map(|x| x.name.as_str()).collect::<Vec<_>>(),
        vec!["Blocks", "Cloners", "Duplicate", "Parent/Child", "Problem/Incident", "Relates"]
    );
    assert!(env.ws.get_all::<LinkType>(&env.project).unwrap().is_empty()); // чтение ничего не записало
    assert_eq!(
        types.iter().map(|x| x.id).collect::<Vec<_>>(),
        again.iter().map(|x| x.id).collect::<Vec<_>>()
    );
    assert_eq!(page.total_count, 6);
    assert_eq!(one.id, types[0].id);
    assert!(types.iter().all(|x| x.version == DEFAULT_VERSION));

    env.ws
        .link_types()
        .create(&env.project, &CreateLinkType::new("Mine", "x", Some("y")))
        .unwrap();
    assert_eq!(env.ws.get_all::<LinkType>(&env.project).unwrap().len(), 7);
    env.ws
        .link_types()
        .create(&env.project, &CreateLinkType::new("Mine2", "x", Some("y")))
        .unwrap();
    assert_eq!(env.ws.get_all::<LinkType>(&env.project).unwrap().len(), 8);
    let saved: Vec<Uuid> = env
        .ws
        .link_types()
        .get_all(&env.project)
        .unwrap()
        .into_iter()
        .filter(|x| x.name != "Mine" && x.name != "Mine2")
        .map(|x| x.id)
        .collect();
    assert_eq!(saved, types.iter().map(|x| x.id).collect::<Vec<_>>());

    let blocks = types.iter().find(|x| x.name == "Blocks").unwrap();
    assert_eq!(
        (blocks.outward_name.as_str(), blocks.inward_name.as_str()),
        ("blocks", "is blocked by")
    );
    assert!(!blocks.is_symmetric());
    assert!(types.iter().find(|x| x.name == "Relates").unwrap().is_symmetric());
    let names = |n: &str| {
        let t = types.iter().find(|x| x.name == n).unwrap();
        (t.outward_name.clone(), t.inward_name.clone())
    };
    assert_eq!(names("Problem/Incident"), ("causes".into(), "is caused by".into()));
    assert_eq!(names("Cloners"), ("clones".into(), "is cloned by".into()));
    assert_eq!(names("Duplicate"), ("duplicates".into(), "is duplicated by".into()));
    // Детерминированные id — те же, что считает ядро.
    for definition in &DEFAULT_LINK_TYPES {
        assert!(
            saved.contains(&default_link_type_id(&env.project, definition.key)),
            "{}",
            definition.key
        );
    }
}

#[test]
fn default_type_ids_are_stable_per_project_and_differ_between_projects() {
    let project = Uuid::new_v4();
    assert_eq!(default_link_type_id(&project, "blocks"), default_link_type_id(&project, "blocks"));
    assert_ne!(default_link_type_id(&project, "blocks"), default_link_type_id(&project, "relates"));
    assert_ne!(
        default_link_type_id(&project, "blocks"),
        default_link_type_id(&Uuid::new_v4(), "blocks")
    );
    let mut ids: Vec<Uuid> = DEFAULT_LINK_TYPES.iter().map(|x| default_link_type_id(&project, x.key)).collect();
    ids.sort();
    ids.dedup();
    assert_eq!(ids.len(), 6);
    // Эталон .NET для золотого проекта: Guid(byte[]) читает первые группы little-endian.
    let golden = Uuid::parse_str("11111111-1111-4111-8111-111111111111").unwrap();
    let expected = tasker_core::ids::guid_d(&default_link_type_id(&golden, "blocks"));
    assert_eq!(expected.len(), 36);
}

#[test]
fn a_default_type_read_before_it_was_saved_has_a_stale_version_and_the_client_must_reread() {
    let env = Env::new();
    let shown = link_type(&env, "Blocks");
    let stale = err(env.ws.link_types().update(
        &env.project,
        &shown.id,
        &UpdateLinkType {
            name: Some("Blockers".into()),
            version: Some(shown.version.clone()),
            ..UpdateLinkType::default()
        },
    ));
    assert_eq!(stale.conflict_code(), Some(ConflictCode::Modified));
    let saved = link_type(&env, "Blocks");
    assert_eq!(saved.id, shown.id);
    assert_ne!(saved.version, DEFAULT_VERSION);
    let renamed = env
        .ws
        .link_types()
        .update(
            &env.project,
            &saved.id,
            &UpdateLinkType {
                name: Some("Blockers".into()),
                version: Some(saved.version.clone()),
                ..UpdateLinkType::default()
            },
        )
        .unwrap()
        .unwrap();
    assert_eq!(renamed.name, "Blockers");
}

#[test]
fn defaults_are_not_recreated_while_the_project_has_its_own_types() {
    let env = Env::new();
    env.ws.link_types().ensure_defaults(&env.project).unwrap();
    for t in env
        .ws
        .link_types()
        .get_all(&env.project)
        .unwrap()
        .iter()
        .filter(|x| x.name != "Blocks")
    {
        assert!(env.ws.link_types().delete(&env.project, &t.id, Some(&t.version)).unwrap());
    }
    let left = env.ws.link_types().get_all(&env.project).unwrap();
    assert_eq!(
        left.iter().map(|x| x.name.as_str()).collect::<Vec<_>>(),
        vec!["Blocks", "Parent/Child"]
    );

    env.ws
        .link_types()
        .create(
            &env.project,
            &CreateLinkType {
                hierarchical: Some(true),
                ..CreateLinkType::new("Epic link", "has story", Some("belongs to epic"))
            },
        )
        .unwrap();
    let parent = left.iter().find(|x| x.name == "Parent/Child").unwrap();
    let saved = link_type(&env, "Parent/Child");
    assert!(env.ws.link_types().delete(&env.project, &saved.id, Some(&saved.version)).unwrap());
    assert_eq!(
        env.ws
            .link_types()
            .get_all(&env.project)
            .unwrap()
            .iter()
            .map(|x| x.name.as_str())
            .collect::<Vec<_>>(),
        vec!["Blocks", "Epic link"]
    );
    assert!(parent.hierarchical);
}

// ---- связи: обе стороны ----

#[test]
fn a_link_is_stored_on_the_source_and_each_side_sees_its_own_name() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let a = create(&env, "Fix login");
    let b = create(&env, "Release");
    let updated = add(&env, &a, &blocks.id, &b).unwrap().unwrap();
    assert_eq!(
        updated.links,
        vec![TaskLink {
            type_id: blocks.id,
            target_id: b.id
        }]
    );
    assert_ne!(a.version, updated.version);
    assert_eq!(env.get(&b.id).version, b.version);
    assert!(env.get(&b.id).links.is_empty());

    let from_a = links_of(&env, &a);
    assert_eq!(from_a.len(), 1);
    assert_eq!(
        (from_a[0].direction, from_a[0].name.as_str(), from_a[0].type_name.as_str()),
        (LinkDirection::Outward, "blocks", "Blocks")
    );
    assert_eq!((from_a[0].task.id, from_a[0].task.title.as_str()), (b.id, "Release"));
    let from_b = links_of(&env, &b);
    assert_eq!(from_b.len(), 1);
    assert_eq!(
        (from_b[0].direction, from_b[0].name.as_str(), from_b[0].task.id),
        (LinkDirection::Inward, "is blocked by", a.id)
    );
}

#[test]
fn adding_the_same_link_twice_changes_nothing_the_second_time() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let a = create(&env, "A");
    let b = create(&env, "B");
    let first = add(&env, &a, &blocks.id, &b).unwrap().unwrap();
    let second = add(&env, &a, &blocks.id, &b).unwrap().unwrap();
    assert_eq!(second.links.len(), 1);
    assert_eq!(first.version, second.version);
}

#[test]
fn one_pair_can_have_links_of_different_types_and_both_directions_and_they_are_shown_in_order() {
    let env = Env::new();
    let dup = link_type(&env, "Duplicate");
    let relates = link_type(&env, "Relates");
    let a = create(&env, "A");
    let b = create(&env, "B");
    add(&env, &a, &dup.id, &b).unwrap();
    add(&env, &b, &dup.id, &a).unwrap();
    add(&env, &a, &relates.id, &b).unwrap();
    assert_eq!(
        links_of(&env, &a).iter().map(|x| x.name.as_str()).collect::<Vec<_>>(),
        vec!["duplicates", "is duplicated by", "relates to"]
    );

    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let dup = link_type(&env, "Duplicate");
    let a = create(&env, "A");
    let z = create(&env, "Zeta");
    let b = create(&env, "Beta");
    let c = create(&env, "Gamma");
    add(&env, &a, &dup.id, &z).unwrap();
    add(&env, &a, &blocks.id, &z).unwrap();
    add(&env, &a, &blocks.id, &b).unwrap();
    add(&env, &c, &blocks.id, &a).unwrap();
    assert_eq!(
        links_of(&env, &a)
            .iter()
            .map(|x| format!("{}/{}/{}", x.type_name, x.name, x.task.title))
            .collect::<Vec<_>>(),
        vec![
            "Blocks/blocks/Beta",
            "Blocks/blocks/Zeta",
            "Blocks/is blocked by/Gamma",
            "Duplicate/duplicates/Zeta"
        ]
    );
}

// ---- симметричные типы ----

#[test]
fn a_symmetric_link_looks_the_same_from_both_sides_is_not_added_twice_and_is_removed_from_either_side() {
    let env = Env::new();
    let relates = link_type(&env, "Relates");
    let a = create(&env, "A");
    let b = create(&env, "B");
    add(&env, &a, &relates.id, &b).unwrap();
    let reverse = add(&env, &b, &relates.id, &a).unwrap().unwrap();
    assert!(reverse.links.is_empty());
    assert_eq!(
        links_of(&env, &a).iter().map(|x| x.name.as_str()).collect::<Vec<_>>(),
        vec!["relates to"]
    );
    assert_eq!(
        links_of(&env, &b).iter().map(|x| x.name.as_str()).collect::<Vec<_>>(),
        vec!["relates to"]
    );

    // Убираем со стороны B, хотя хранится связь в A.
    remove(&env, &b, &relates.id, &a).unwrap();
    assert!(links_of(&env, &a).is_empty() && links_of(&env, &b).is_empty());
}

// ---- проверки ----

#[test]
fn invalid_links_are_rejected_with_a_reason() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let a = create(&env, "A");
    assert_eq!(
        message(add(&env, &a, &blocks.id, &a)),
        "TargetId: a task cannot be linked to itself"
    );
    let missing = Uuid::new_v4();
    assert_eq!(
        message(env.ws.links().add(&env.project, &a.id, &blocks.id, &missing, None)),
        format!("TargetId: task not found in the project: {missing}")
    );
    let b = create(&env, "B");
    assert_eq!(
        message(env.ws.links().add(&env.project, &a.id, &missing, &b.id, None)),
        format!("TypeId: link type not found in the project: {missing}")
    );
    assert!(
        env.ws
            .links()
            .add(&env.project, &Uuid::new_v4(), &blocks.id, &b.id, None)
            .unwrap()
            .is_none()
    );
    assert!(env.ws.links().get_links(&env.project, &Uuid::new_v4()).unwrap().is_none());
    assert!(env.get(&a.id).links.is_empty());

    // Задача другого проекта не может быть целью.
    let other = env
        .ws
        .projects()
        .create(&tasker_services::project::CreateProject { name: "Other".into() })
        .unwrap();
    let mut foreign = env.seed("Foreign", &[]);
    env.ws.tasks().delete(&env.project, &foreign.id, Some(&foreign.version)).unwrap();
    foreign.project_id = other.id;
    env.ws.add(&foreign).unwrap();
    assert!(err(add(&env, &a, &blocks.id, &foreign)).is_validation());
}

// ---- версии ----

#[test]
fn with_a_version_a_stale_one_is_rejected_and_with_the_current_one_the_task_gets_a_new_version() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let a = create(&env, "A");
    let b = create(&env, "B");
    let renamed = env
        .ws
        .tasks()
        .update(
            &env.project,
            &a.id,
            &tasker_services::task::UpdateTask {
                title: Some("A renamed".into()),
                version: Some(a.version.clone()),
                ..Default::default()
            },
        )
        .unwrap()
        .unwrap();
    let stale = err(env.ws.links().add(&env.project, &a.id, &blocks.id, &b.id, Some(&a.version)));
    assert_eq!(stale.conflict_code(), Some(ConflictCode::Modified));
    assert!(env.get(&a.id).links.is_empty());

    env.clock.advance(Duration::from_secs(60));
    let updated = env
        .ws
        .links()
        .add(&env.project, &a.id, &blocks.id, &b.id, Some(&renamed.version))
        .unwrap()
        .unwrap();
    assert_ne!(renamed.version, updated.version);
    assert_eq!(updated.version, env.get(&a.id).version);
    assert_eq!(updated.updated_at, env.now());
    assert_eq!(updated.title, "A renamed");
}

#[test]
fn concurrent_links_from_one_task_without_a_version_are_all_kept() {
    let env = Env::new();
    let dup = link_type(&env, "Duplicate");
    let a = create(&env, "A");
    let targets: Vec<TaskItem> = (0..6).map(|i| create(&env, &format!("T{i}"))).collect();
    std::thread::scope(|scope| {
        for t in &targets {
            let ws = env.ws.clone();
            let (project, a, dup) = (env.project, a.id, dup.id);
            scope.spawn(move || ws.links().add(&project, &a, &dup, &t.id, None).unwrap());
        }
    });
    assert_eq!(env.get(&a.id).links.len(), 6);
}

// ---- удаление ----

#[test]
fn removing_a_link_is_idempotent_and_deleting_a_task_removes_links_on_both_sides() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let relates = link_type(&env, "Relates");
    let a = create(&env, "A");
    let b = create(&env, "B");
    add(&env, &a, &blocks.id, &b).unwrap();
    let removed = remove(&env, &a, &blocks.id, &b).unwrap().unwrap();
    let again = remove(&env, &a, &blocks.id, &b).unwrap().unwrap();
    assert!(removed.links.is_empty() && again.links.is_empty());
    assert!(
        env.ws
            .links()
            .remove(&env.project, &Uuid::new_v4(), &blocks.id, &b.id, None)
            .unwrap()
            .is_none()
    );

    let c = create(&env, "C");
    add(&env, &a, &blocks.id, &b).unwrap();
    add(&env, &c, &relates.id, &b).unwrap();
    add(&env, &a, &blocks.id, &c).unwrap();
    assert!(env.ws.tasks().delete(&env.project, &b.id, Some(&env.get(&b.id).version)).unwrap());
    assert_eq!(
        env.get(&a.id).links,
        vec![TaskLink {
            type_id: blocks.id,
            target_id: c.id
        }]
    );
    assert!(env.get(&c.id).links.is_empty());
    assert_eq!(links_of(&env, &c).len(), 1);

    env.ws.tasks().delete(&env.project, &a.id, Some(&env.get(&a.id).version)).unwrap();
    assert!(links_of(&env, &c).is_empty());
}

#[test]
fn links_to_a_missing_task_or_type_are_skipped_when_shown_and_task_details_show_both_sides() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let a = create(&env, "A");
    let b = create(&env, "B");
    let c = create(&env, "C");
    add(&env, &a, &blocks.id, &b).unwrap();
    add(&env, &b, &blocks.id, &c).unwrap();
    add_raw(&env, &a.id, &blocks.id, &Uuid::new_v4());
    add_raw(&env, &a.id, &Uuid::new_v4(), &b.id);

    let views = links_of(&env, &a);
    assert_eq!(views.len(), 1);
    assert_eq!(views[0].task.id, b.id);

    let details = env.ws.tasks().describe_by_id(&env.project, &b.id).unwrap().unwrap();
    assert_eq!(details.link_count, 2);
    assert_eq!(
        details
            .link_views
            .iter()
            .map(|x| (x.direction, x.name.as_str(), x.task.id))
            .collect::<Vec<_>>(),
        vec![
            (LinkDirection::Outward, "blocks", c.id),
            (LinkDirection::Inward, "is blocked by", a.id)
        ]
    );
    assert_eq!(
        details.task.links,
        vec![TaskLink {
            type_id: blocks.id,
            target_id: c.id
        }]
    );
    let d = create(&env, "D");
    let alone = env.ws.tasks().describe_by_id(&env.project, &d.id).unwrap().unwrap();
    assert!(alone.link_views.is_empty() && alone.link_count == 0);
}

#[test]
fn task_details_cap_long_link_lists_but_report_the_total() {
    let env = Env::new();
    let relates = link_type(&env, "Relates");
    let hub = create(&env, "Hub");
    let total = MAX_VIEWED + 5;
    for i in 0..total {
        let other = create(&env, &format!("Other {i:03}"));
        add(&env, &other, &relates.id, &hub).unwrap();
    }
    let details = env.ws.tasks().describe_by_id(&env.project, &hub.id).unwrap().unwrap();
    assert_eq!(details.link_count, total);
    assert_eq!(details.link_views.len(), MAX_VIEWED);
    assert_eq!(links_of(&env, &hub).len(), total);
}

// ---- блокировка на время правки ----

#[test]
fn a_task_someone_else_edits_cannot_get_a_link_but_can_be_a_target() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let a = create(&env, "A");
    let b = create(&env, "B");
    env.ws
        .locks()
        .acquire(LockedEntity::Task, &a.id, "Task", Some(env.project))
        .unwrap();

    let anna = env.as_editor("user:anna", "Anna");
    assert_eq!(
        err(anna.links().add(&env.project, &a.id, &blocks.id, &b.id, None)).conflict_code(),
        Some(ConflictCode::Locked)
    );
    assert_eq!(
        err(anna.links().remove(&env.project, &a.id, &blocks.id, &b.id, None)).conflict_code(),
        Some(ConflictCode::Locked)
    );
    let linked = anna.links().add(&env.project, &b.id, &blocks.id, &a.id, None).unwrap().unwrap();
    assert_eq!(linked.links.len(), 1);
}

// ---- типы связей ----

#[test]
fn link_types_can_be_created_renamed_and_deleted_when_unused() {
    let env = Env::new();
    let depends = env
        .ws
        .link_types()
        .create(
            &env.project,
            &CreateLinkType::new("Depends", "depends on", Some("is a dependency of")),
        )
        .unwrap();
    assert_eq!(
        (depends.outward_name.as_str(), depends.inward_name.as_str()),
        ("depends on", "is a dependency of")
    );
    let renamed = env
        .ws
        .link_types()
        .update(
            &env.project,
            &depends.id,
            &UpdateLinkType {
                name: Some("Dependency".into()),
                inward_name: Some("is needed by".into()),
                version: Some(depends.version.clone()),
                ..UpdateLinkType::default()
            },
        )
        .unwrap()
        .unwrap();
    assert_eq!(
        (renamed.name.as_str(), renamed.outward_name.as_str(), renamed.inward_name.as_str()),
        ("Dependency", "depends on", "is needed by")
    );
    assert!(
        env.ws
            .link_types()
            .delete(&env.project, &depends.id, Some(&renamed.version))
            .unwrap()
    );
    assert!(env.ws.link_types().find(&env.project, "Dependency").unwrap().is_none());
}

#[test]
fn a_type_without_inward_name_is_symmetric_and_names_must_be_unique_and_valid() {
    let env = Env::new();
    let same = env
        .ws
        .link_types()
        .create(&env.project, &CreateLinkType::new("Pair", "goes with", None))
        .unwrap();
    assert!(same.is_symmetric());
    assert_eq!(same.inward_name, "goes with");
    let create = |name: &str, outward: &str, inward: &str| {
        env.ws
            .link_types()
            .create(&env.project, &CreateLinkType::new(name, outward, Some(inward)))
    };
    assert_eq!(message(create("pair", "x", "y")), "Link type 'pair' already exists in the project");
    assert!(err(create("blocks", "x", "y")).is_in_use());
    assert_eq!(message(create(" ", "x", "y")), "Link type name is required");
    assert_eq!(message(create("N", "", "y")), "Outward name is required");
    assert_eq!(
        message(create("N", "x", &"y".repeat(101))),
        "Inward name must be at most 100 characters"
    );

    let blocks = link_type(&env, "Blocks");
    let update = |id: &Uuid, version: Option<&str>| {
        env.ws.link_types().update(
            &env.project,
            id,
            &UpdateLinkType {
                name: Some("B2".into()),
                version: version.map(str::to_string),
                ..UpdateLinkType::default()
            },
        )
    };
    assert!(err(update(&blocks.id, None)).is_validation());
    assert!(err(update(&blocks.id, Some("999"))).is_modified());
    assert!(update(&Uuid::new_v4(), Some("1")).unwrap().is_none());
}

#[test]
fn a_type_with_links_cannot_be_deleted_until_they_are_removed_and_a_locked_type_is_untouchable() {
    let env = Env::new();
    let mut blocks = link_type(&env, "Blocks");
    let a = create(&env, "A");
    let b = create(&env, "B");
    add(&env, &a, &blocks.id, &b).unwrap();
    blocks = link_type(&env, "Blocks");
    let error = err(env.ws.link_types().delete(&env.project, &blocks.id, Some(&blocks.version)));
    assert_eq!(error.conflict_code(), Some(ConflictCode::InUse));
    assert_eq!(
        error.message(),
        "Link type 'Blocks' is used by links of 1 task(s) and cannot be deleted"
    );
    remove(&env, &a, &blocks.id, &b).unwrap();
    assert!(env.ws.link_types().delete(&env.project, &blocks.id, Some(&blocks.version)).unwrap());
    assert!(!env.ws.link_types().delete(&env.project, &blocks.id, Some(&blocks.version)).unwrap());

    let dup = link_type(&env, "Duplicate");
    env.ws
        .locks()
        .acquire(LockedEntity::LinkType, &dup.id, "Link type", Some(env.project))
        .unwrap();
    let anna = env.as_editor("user:anna", "Anna");
    assert!(
        err(anna.link_types().update(
            &env.project,
            &dup.id,
            &UpdateLinkType {
                name: Some("D2".into()),
                version: Some(dup.version.clone()),
                ..UpdateLinkType::default()
            },
        ))
        .is_locked()
    );
    assert!(err(anna.link_types().delete(&env.project, &dup.id, Some(&dup.version))).is_locked());
}

// ---- разбор того, что написал человек ----

#[test]
fn a_phrase_finds_the_type_and_the_side() {
    let env = Env::new();
    for (phrase, expected, direction) in [
        ("Blocks", "Blocks", LinkDirection::Outward),
        ("blocks", "Blocks", LinkDirection::Outward),
        ("  BLOCKS ", "Blocks", LinkDirection::Outward),
        ("is blocked by", "Blocks", LinkDirection::Inward),
        ("Is Duplicated By", "Duplicate", LinkDirection::Inward),
        ("duplicates", "Duplicate", LinkDirection::Outward),
        ("relates to", "Relates", LinkDirection::Outward),
        ("Problem/Incident", "Problem/Incident", LinkDirection::Outward),
        ("is caused by", "Problem/Incident", LinkDirection::Inward),
    ] {
        let (t, d) = env.ws.link_types().resolve(&env.project, phrase).unwrap();
        assert_eq!((t.name.as_str(), d), (expected, direction), "{phrase}");
    }
    let blocks = link_type(&env, "Blocks");
    let (t, d) = env.ws.link_types().resolve(&env.project, &blocks.id.to_string()).unwrap();
    assert_eq!((t.id, d), (blocks.id, LinkDirection::Outward));
    assert_eq!(
        message(env.ws.link_types().resolve(&env.project, "frobnicates")),
        "No link type or link name 'frobnicates'; available: blocks, is blocked by, clones, is cloned by, duplicates, is duplicated by, includes, is part of, causes, is caused by, relates to"
    );
    assert_eq!(message(env.ws.link_types().resolve(&env.project, " ")), "Link type is required");
    env.ws
        .link_types()
        .create(&env.project, &CreateLinkType::new("Mine", "blocks", Some("is stopped by")))
        .unwrap();
    assert!(message(env.ws.link_types().resolve(&env.project, "blocks")).contains("several link types"));
}

#[test]
fn the_task_filter_matches_tasks_with_a_link_of_the_given_types() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let relates = link_type(&env, "Relates");
    let a = create(&env, "A");
    let b = create(&env, "B");
    add(&env, &a, &blocks.id, &b).unwrap();
    let count = |ids: Option<Vec<Uuid>>| {
        env.ws
            .tasks()
            .count(
                &env.project,
                &TaskFilter {
                    link_type_ids: ids,
                    ..TaskFilter::default()
                },
            )
            .unwrap()
    };
    assert_eq!(count(Some(vec![blocks.id])), 1);
    assert_eq!(count(Some(vec![relates.id])), 0);
    assert_eq!(count(Some(vec![relates.id, blocks.id])), 1);
    assert_eq!(count(Some(vec![])), 0);
    assert_eq!(count(None), 2);
}

// ---- циклы ----

#[test]
fn default_types_forbid_cycles_only_for_blocks_and_parent_child() {
    let env = Env::new();
    assert!(!link_type(&env, "Blocks").allow_cycles);
    assert!(!link_type(&env, "Parent/Child").allow_cycles);
    for name in ["Duplicate", "Cloners", "Relates", "Problem/Incident"] {
        assert!(link_type(&env, name).allow_cycles, "{name}");
    }
}

#[test]
fn a_reverse_link_closing_a_cycle_is_rejected_with_the_shortest_path_and_nothing_is_written() {
    let env = Env::new();
    let tsk = env.series("TSK");
    let blocks = link_type(&env, "Blocks");
    let a = create_in(&env, "A", tsk.id);
    let b = create_in(&env, "B", tsk.id);
    let c = create_in(&env, "C", tsk.id);
    add(&env, &a, &blocks.id, &b).unwrap();
    let before = env.get(&b.id);
    let e = message(add(&env, &b, &blocks.id, &a));
    assert!(e.starts_with("Cycle: TSK-2 → TSK-1 → TSK-2") && e.contains("'Blocks'"));
    assert_eq!(env.get(&b.id).version, before.version);
    assert!(env.get(&b.id).links.is_empty());

    add(&env, &b, &blocks.id, &c).unwrap();
    add(&env, &a, &blocks.id, &c).unwrap(); // ромб, а не круг
    assert!(message(add(&env, &c, &blocks.id, &a)).starts_with("Cycle: TSK-3 → TSK-1 → TSK-3"));
    remove(&env, &a, &blocks.id, &c).unwrap();
    assert!(message(add(&env, &c, &blocks.id, &a)).starts_with("Cycle: TSK-3 → TSK-1 → TSK-2 → TSK-3"));

    // «A is blocked by B» хранится как «B blocks A» — это тот же цикл; другие типы цикл Blocks не замыкают.
    let (t, direction) = env.ws.link_types().resolve(&env.project, "is blocked by").unwrap();
    assert_eq!(direction, LinkDirection::Inward);
    assert!(add(&env, &b, &t.id, &a).is_err());
    let duplicate = link_type(&env, "Duplicate");
    let x = create(&env, "X");
    let y = create(&env, "Y");
    add(&env, &x, &duplicate.id, &y).unwrap();
    add(&env, &y, &blocks.id, &x).unwrap();
    add(&env, &y, &duplicate.id, &x).unwrap();
    assert_eq!(env.get(&y.id).links.len(), 2);
}

#[test]
fn a_symmetric_type_never_forms_a_cycle_and_a_custom_type_chooses_whether_it_allows_cycles() {
    let env = Env::new();
    let relates = link_type(&env, "Relates");
    let a = create(&env, "A");
    let b = create(&env, "B");
    let strict_pair = env
        .ws
        .link_types()
        .create(
            &env.project,
            &CreateLinkType {
                allow_cycles: Some(false),
                ..CreateLinkType::new("Twins", "twins with", None)
            },
        )
        .unwrap();
    add(&env, &a, &relates.id, &b).unwrap();
    add(&env, &b, &relates.id, &a).unwrap();
    add(&env, &a, &strict_pair.id, &b).unwrap();
    add(&env, &b, &strict_pair.id, &a).unwrap();

    let free = env
        .ws
        .link_types()
        .create(&env.project, &CreateLinkType::new("Feeds", "feeds", Some("is fed by")))
        .unwrap();
    let strict = env
        .ws
        .link_types()
        .create(
            &env.project,
            &CreateLinkType {
                allow_cycles: Some(false),
                ..CreateLinkType::new("Parent", "contains", Some("is part of"))
            },
        )
        .unwrap();
    assert!(free.allow_cycles && !strict.allow_cycles);
    add(&env, &a, &free.id, &b).unwrap();
    add(&env, &b, &free.id, &a).unwrap();
    add(&env, &a, &strict.id, &b).unwrap();
    assert!(err(add(&env, &b, &strict.id, &a)).is_validation());

    let allowed = env
        .ws
        .link_types()
        .update(
            &env.project,
            &strict.id,
            &UpdateLinkType {
                allow_cycles: Some(true),
                version: Some(strict.version.clone()),
                ..UpdateLinkType::default()
            },
        )
        .unwrap()
        .unwrap();
    assert!(allowed.allow_cycles);
    add(&env, &b, &strict.id, &a).unwrap();
    let forbidden = env
        .ws
        .link_types()
        .update(
            &env.project,
            &strict.id,
            &UpdateLinkType {
                allow_cycles: Some(false),
                version: Some(allowed.version.clone()),
                ..UpdateLinkType::default()
            },
        )
        .unwrap()
        .unwrap();
    assert!(!forbidden.allow_cycles);
    let count = |t: &TaskItem| env.get(&t.id).links.iter().filter(|x| x.type_id == strict.id).count();
    assert_eq!(count(&a) + count(&b), 2);
    let renamed = env
        .ws
        .link_types()
        .update(
            &env.project,
            &strict.id,
            &UpdateLinkType {
                name: Some("Parent2".into()),
                version: Some(forbidden.version.clone()),
                ..UpdateLinkType::default()
            },
        )
        .unwrap()
        .unwrap();
    assert!(!renamed.allow_cycles);
}

#[test]
fn parallel_opposite_links_let_exactly_one_through() {
    for _ in 0..5 {
        let env = Env::new();
        let blocks = link_type(&env, "Blocks");
        let a = create(&env, "A");
        let b = create(&env, "B");
        let results: Vec<Option<tasker_services::Error>> = std::thread::scope(|scope| {
            let forward = {
                let ws = env.ws.clone();
                let (project, a, b, t) = (env.project, a.id, b.id, blocks.id);
                scope.spawn(move || ws.links().add(&project, &a, &t, &b, None).err())
            };
            let backward = {
                let ws = env.ws.clone();
                let (project, a, b, t) = (env.project, a.id, b.id, blocks.id);
                scope.spawn(move || ws.links().add(&project, &b, &t, &a, None).err())
            };
            vec![forward.join().unwrap(), backward.join().unwrap()]
        });
        assert_eq!(results.iter().filter(|x| x.is_none()).count(), 1);
        assert!(results.iter().flatten().all(|e| e.is_validation()));
        assert_eq!(env.get(&a.id).links.len() + env.get(&b.id).links.len(), 1);
    }
}

#[test]
fn an_existing_cycle_is_reported_by_the_health_check_and_cleanup_does_not_remove_it() {
    let env = Env::new();
    let tsk = env.series("TSK");
    let blocks = link_type(&env, "Blocks");
    let a = create_in(&env, "A", tsk.id);
    let b = create_in(&env, "B", tsk.id);
    let c = create_in(&env, "C", tsk.id);
    add(&env, &a, &blocks.id, &b).unwrap();
    add(&env, &b, &blocks.id, &c).unwrap();
    assert!(env.ws.link_health().check(&env.project).unwrap().cycles.is_empty());

    add_raw(&env, &c.id, &blocks.id, &a.id);
    let versions: Vec<String> = [&a, &b, &c].iter().map(|x| env.get(&x.id).version).collect();
    let health = env.ws.link_health().check(&env.project).unwrap();
    assert!(health.needs_attention());
    assert_eq!(health.cycles.len(), 1);
    assert_eq!(health.cycles[0].type_name, "Blocks");
    assert_eq!(
        health.cycles[0].format(&HashMap::from([(tsk.id, "TSK".to_string())])),
        "TSK-1 → TSK-2 → TSK-3 → TSK-1"
    );

    let report = env.ws.cleanup().run(&env.project, CleanupOptions::default()).unwrap();
    assert!(report.changes.is_empty());
    assert_eq!(report.link_cycles.len(), 1);
    assert_eq!([&a, &b, &c].iter().map(|x| env.get(&x.id).version).collect::<Vec<_>>(), versions);

    assert_eq!(
        links_of(&env, &a).iter().map(|x| x.name.as_str()).collect::<Vec<_>>(),
        vec!["blocks", "is blocked by"]
    );
    assert_eq!(
        env.ws
            .tasks()
            .describe_by_id(&env.project, &a.id)
            .unwrap()
            .unwrap()
            .link_views
            .len(),
        2
    );
}

#[test]
fn cycles_of_types_that_allow_them_and_two_separate_cycles_are_counted_correctly() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let duplicate = link_type(&env, "Duplicate");
    let t: Vec<TaskItem> = (1..=4).map(|i| create(&env, &i.to_string())).collect();
    add_raw(&env, &t[0].id, &blocks.id, &t[1].id);
    add_raw(&env, &t[1].id, &blocks.id, &t[0].id);
    add_raw(&env, &t[2].id, &blocks.id, &t[3].id);
    add_raw(&env, &t[3].id, &blocks.id, &t[2].id);
    add_raw(&env, &t[0].id, &duplicate.id, &t[1].id);
    add_raw(&env, &t[1].id, &duplicate.id, &t[0].id);
    let cycles = env.ws.link_health().check(&env.project).unwrap().cycles;
    assert_eq!(cycles.len(), 2);
    assert!(cycles.iter().all(|x| x.path.len() == 3 && x.path[0].id == x.path[2].id));
}

#[test]
fn cycle_finding_handles_loops_branches_and_dangling_links_deterministically() {
    let n: Vec<Uuid> = (0..6).map(|_| Uuid::new_v4()).collect();
    let e = |from: usize, to: usize| LinkEdge {
        source_id: n[from],
        target_id: n[to],
    };
    let cycles = link_cycles::cycles_of(&[e(0, 1), e(1, 2), e(2, 0), e(2, 3), e(4, 0), e(5, 5)]);
    assert_eq!(cycles.len(), 2);
    assert!(cycles.iter().any(|x| x.len() == 2 && x[0] == n[5] && x[1] == n[5]));
    let circle = cycles.iter().find(|x| x.len() == 4).unwrap();
    assert_eq!(circle[0], circle[3]);
    let mut members: Vec<Uuid> = circle[..3].to_vec();
    members.sort();
    let mut expected = vec![n[0], n[1], n[2]];
    expected.sort();
    assert_eq!(members, expected);
    assert!(link_cycles::cycles_of(&[e(0, 1), e(1, 2)]).is_empty());
}
