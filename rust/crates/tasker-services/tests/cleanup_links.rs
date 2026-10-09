//! Сценарии `LinkCleanupCoreTests.cs` и `CascadeLockTests.cs`: чистка недействительных связей, нечитаемые файлы, состояние
//! связей; единое правило блокировок массовых правок (удаление серии, чистка).
mod common;

use common::*;
use std::time::Duration;
use tasker_core::ConflictCode;
use tasker_core::ids::default_link_type_id;
use tasker_core::locks::LockedEntity;
use tasker_core::model::{LinkType, TaskItem, TaskLink, TaskSeriesNumber};
use tasker_services::cleanup::{CleanupChange, CleanupChangeKind, CleanupOptions, CleanupReport};
use tasker_services::task::CreateTask;
use uuid::Uuid;

fn link_type(env: &Env, name: &str) -> LinkType {
    env.ws.link_types().find(&env.project, name).unwrap().unwrap()
}

fn create(env: &Env, title: &str) -> TaskItem {
    env.clock.advance(Duration::from_secs(60));
    env.task(title)
}

fn add_raw(env: &Env, task_id: &Uuid, links: &[TaskLink]) {
    let mut task = env.get(task_id);
    task.links.extend_from_slice(links);
    env.ws.update(&task, &task.version).unwrap().unwrap();
}

fn link(type_id: Uuid, target_id: Uuid) -> TaskLink {
    TaskLink { type_id, target_id }
}

fn links(report: &CleanupReport) -> Vec<&CleanupChange> {
    report
        .changes
        .iter()
        .filter(|x| x.kind == CleanupChangeKind::RemovedInvalidLink)
        .collect()
}

fn run(env: &Env, options: CleanupOptions) -> CleanupReport {
    env.ws.cleanup().run(&env.project, options).unwrap()
}

/// Нечитаемый файл задачи (конфликт слияния git).
fn unreadable_task_file(env: &Env) {
    let folder = env.ws.directory().project(&env.project).tasks();
    std::fs::create_dir_all(&folder).unwrap();
    std::fs::write(
        folder.join("broken-0000000b.yaml"),
        b"formatVersion: 9\n<<<<<<< HEAD\nid: x\n=======\nid: y\n>>>>>>> theirs\n",
    )
    .unwrap();
    env.ws.index().sync().unwrap();
}

fn unreadable_link_type_file(env: &Env) {
    let folder = env.ws.directory().project(&env.project).link_types();
    std::fs::create_dir_all(&folder).unwrap();
    std::fs::write(
        folder.join("broken-0000000c.yaml"),
        b"<<<<<<< HEAD\nname: A\n=======\nname: B\n>>>>>>> branch\n",
    )
    .unwrap();
    env.ws.index().sync().unwrap();
}

#[test]
fn a_link_to_a_missing_task_is_removed_and_valid_links_stay() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let a = create(&env, "A");
    let b = create(&env, "B");
    env.ws.links().add(&env.project, &a.id, &blocks.id, &b.id, None).unwrap();
    let gone = Uuid::new_v4();
    add_raw(&env, &a.id, &[link(blocks.id, gone)]);
    let untouched = create(&env, "Untouched");

    let report = run(&env, CleanupOptions::default());
    assert_eq!(env.get(&a.id).links, vec![link(blocks.id, b.id)]);
    let changes = links(&report);
    assert_eq!(changes.len(), 1);
    let change = changes[0];
    assert_eq!(
        (
            change.task_id,
            change.task_title.as_str(),
            change.link_type_id,
            change.link_target_id,
            change.series_id
        ),
        (a.id, "A", Some(blocks.id), Some(gone), None)
    );
    assert_eq!(
        change.description,
        format!("Task 'A': removed link 'Blocks' to a task that does not exist ({gone})")
    );
    assert!(report.links_skip_reason.is_none());
    assert_eq!(env.get(&untouched.id).version, untouched.version);
}

#[test]
fn a_link_of_a_missing_link_type_is_removed() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let a = create(&env, "A");
    let b = create(&env, "B");
    env.ws.links().add(&env.project, &a.id, &blocks.id, &b.id, None).unwrap();
    let gone_type = Uuid::new_v4();
    add_raw(&env, &a.id, &[link(gone_type, b.id)]);
    let report = run(&env, CleanupOptions::default());
    assert_eq!(env.get(&a.id).links, vec![link(blocks.id, b.id)]);
    let changes = links(&report);
    assert_eq!(changes.len(), 1);
    assert_eq!((changes[0].link_type_id, changes[0].link_target_id), (Some(gone_type), Some(b.id)));
    assert_eq!(
        changes[0].description,
        format!(
            "Task 'A': removed a link of a link type that does not exist ({gone_type}) to {}",
            b.id
        )
    );
}

#[test]
fn several_bad_links_in_several_tasks_are_all_removed_in_creation_order() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let relates = link_type(&env, "Relates");
    let a = create(&env, "A");
    let b = create(&env, "B");
    let c = create(&env, "C");
    env.ws.links().add(&env.project, &b.id, &relates.id, &c.id, None).unwrap();
    add_raw(
        &env,
        &c.id,
        &[link(blocks.id, Uuid::new_v4()), link(Uuid::new_v4(), a.id), link(blocks.id, a.id)],
    );
    add_raw(&env, &a.id, &[link(relates.id, Uuid::new_v4())]);
    let report = run(&env, CleanupOptions::default());
    assert_eq!(
        links(&report).iter().map(|x| x.task_title.as_str()).collect::<Vec<_>>(),
        vec!["A", "C", "C"]
    );
    assert!(env.get(&a.id).links.is_empty());
    assert_eq!(env.get(&c.id).links, vec![link(blocks.id, a.id)]);
    assert_eq!(env.get(&b.id).links, vec![link(relates.id, c.id)]);
}

#[test]
fn a_dry_run_reports_the_same_changes_and_writes_nothing_and_the_cleanup_is_idempotent() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let a = create(&env, "A");
    let b = create(&env, "B");
    env.ws.links().add(&env.project, &a.id, &blocks.id, &b.id, None).unwrap();
    let clean = run(&env, CleanupOptions::default());
    assert!(clean.changes.is_empty() && clean.links_skip_reason.is_none());

    add_raw(&env, &a.id, &[link(blocks.id, Uuid::new_v4())]);
    let before = env.get(&a.id);
    let dry = run(
        &env,
        CleanupOptions {
            dry_run: true,
            ..Default::default()
        },
    );
    assert_eq!(links(&dry).len(), 1);
    assert_eq!(env.get(&a.id), before);
    let real = run(&env, CleanupOptions::default());
    assert_eq!(
        links(&dry).iter().map(|x| x.description.clone()).collect::<Vec<_>>(),
        links(&real).iter().map(|x| x.description.clone()).collect::<Vec<_>>()
    );
    assert_eq!(env.get(&a.id).links, vec![link(blocks.id, b.id)]);
    assert!(run(&env, CleanupOptions::default()).changes.is_empty());
}

#[test]
fn links_are_not_cleaned_while_a_task_file_is_unreadable_but_series_still_are() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let series = env.series("TSK");
    let a = env
        .ws
        .tasks()
        .create(
            &env.project,
            &CreateTask {
                series_ids: Some(vec![series.id]),
                ..CreateTask::new("A", env.task_type.id)
            },
        )
        .unwrap();
    let gone = Uuid::new_v4();
    add_raw(&env, &a.id, &[link(blocks.id, gone)]);
    let mut task = env.get(&a.id);
    task.series_numbers.push(TaskSeriesNumber {
        series_id: Uuid::new_v4(),
        number: 7,
    });
    env.ws.update(&task, &task.version).unwrap().unwrap();
    unreadable_task_file(&env);

    let report = run(&env, CleanupOptions::default());
    assert!(!report.skipped);
    assert!(report.changes.iter().any(|x| x.kind == CleanupChangeKind::RemovedInvalidSeries));
    assert!(links(&report).is_empty());
    assert_eq!(
        report.links_skip_reason.as_deref(),
        Some(
            "1 task or link type file(s) cannot be read (merge conflict or broken YAML): a task or a type that cannot be read would look missing and the links to it would be wiped. The links were not checked; fix the files and run cleanup again."
        )
    );
    assert!(env.get(&a.id).links.contains(&link(blocks.id, gone)));
    assert_eq!(env.get(&a.id).series_numbers.len(), 1);
}

#[test]
fn links_are_not_cleaned_while_a_link_type_file_is_unreadable() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let a = create(&env, "A");
    let b = create(&env, "B");
    env.ws.links().add(&env.project, &a.id, &blocks.id, &b.id, None).unwrap();
    add_raw(&env, &a.id, &[link(blocks.id, Uuid::new_v4())]);
    unreadable_link_type_file(&env);
    let report = run(&env, CleanupOptions::default());
    assert!(report.links_skip_reason.is_some());
    assert!(report.changes.is_empty());
    assert_eq!(env.get(&a.id).links.len(), 2);
}

#[test]
fn links_of_the_default_link_types_are_valid_before_the_types_are_saved() {
    let env = Env::new();
    let a = create(&env, "A");
    let b = create(&env, "B");
    let blocks_id = default_link_type_id(&env.project, "blocks");
    add_raw(&env, &a.id, &[link(blocks_id, b.id)]);
    assert!(env.ws.get_all::<LinkType>(&env.project).unwrap().is_empty());
    let report = run(&env, CleanupOptions::default());
    assert!(report.changes.is_empty());
    assert_eq!(env.get(&a.id).links, vec![link(blocks_id, b.id)]);
    assert_eq!(env.ws.link_health().check(&env.project).unwrap().tasks_with_invalid_links, 0);
}

#[test]
fn link_health_counts_tasks_with_bad_links_and_reports_unreadable_files_instead_of_guessing() {
    let env = Env::new();
    let blocks = link_type(&env, "Blocks");
    let a = create(&env, "A");
    let b = create(&env, "B");
    let c = create(&env, "C");
    env.ws.links().add(&env.project, &a.id, &blocks.id, &b.id, None).unwrap();
    let health = env.ws.link_health().check(&env.project).unwrap();
    assert_eq!(
        (health.tasks_with_invalid_links, health.unreadable_files, health.needs_attention()),
        (0, 0, false)
    );

    add_raw(&env, &b.id, &[link(blocks.id, Uuid::new_v4())]);
    add_raw(&env, &c.id, &[link(Uuid::new_v4(), a.id), link(blocks.id, Uuid::new_v4())]);
    let health = env.ws.link_health().check(&env.project).unwrap();
    assert_eq!((health.tasks_with_invalid_links, health.unreadable_files), (2, 0));

    unreadable_task_file(&env);
    unreadable_link_type_file(&env);
    let unclear = env.ws.link_health().check(&env.project).unwrap();
    assert_eq!((unclear.tasks_with_invalid_links, unclear.unreadable_files), (0, 2));
    assert!(unclear.needs_attention());

    // Сводка sync по проектам: только проекты с проблемами.
    let report = tasker_services::health::compute(&env.ws);
    assert_eq!(report.len(), 1);
    assert_eq!(
        (report[0].project_id, report[0].unreadable_link_files, report[0].has_link_problems()),
        (env.project, 2, true)
    );
    assert_eq!(
        tasker_services::health::describe_links(&report[0]),
        vec!["2 task or link type file(s) cannot be read (merge conflict?): fix them, 'tasker cleanup' does not check links until then"]
    );
}

// ---- CascadeLockTests ----

fn anna_locks(env: &Env, task: &TaskItem) {
    env.as_editor("user:anna", "Anna")
        .locks()
        .acquire(LockedEntity::Task, &task.id, "Task", Some(env.project))
        .unwrap();
}

fn anna_releases(env: &Env, task: &TaskItem) {
    env.as_editor("user:anna", "Anna")
        .locks()
        .release(LockedEntity::Task, &task.id)
        .unwrap();
}

fn in_series(env: &Env, title: &str, series: Uuid) -> TaskItem {
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

#[test]
fn deleting_a_series_is_refused_while_a_task_with_its_number_is_locked_and_nothing_is_written() {
    let env = Env::new().with_cascade_timeout(Duration::ZERO);
    let series = env.series("TSK");
    let locked = in_series(&env, "Locked", series.id);
    let free = in_series(&env, "Free", series.id);
    anna_locks(&env, &locked);
    let error = err(env.ws.series().delete(&env.project, &series.id, Some(&series.version)));
    assert_eq!(error.conflict_code(), Some(ConflictCode::Locked));
    assert_eq!(error.message(), "Task 'Locked' is being edited by Anna");
    assert!(env.ws.series().find(&env.project, &series.id.to_string()).unwrap().is_some());
    assert_eq!(
        (env.get(&free.id).series_numbers.len(), env.get(&locked.id).series_numbers.len()),
        (1, 1)
    );
}

#[test]
fn a_locked_task_outside_the_series_does_not_get_in_the_way_and_a_released_lock_lets_the_delete_through() {
    let env = Env::new().with_cascade_timeout(Duration::ZERO);
    let series = env.series("TSK");
    let inside = in_series(&env, "In", series.id);
    let outside = create(&env, "Outside");
    anna_locks(&env, &outside);
    anna_locks(&env, &inside);
    assert!(err(env.ws.series().delete(&env.project, &series.id, Some(&series.version))).is_locked());
    anna_releases(&env, &inside);
    assert!(env.ws.series().delete(&env.project, &series.id, Some(&series.version)).unwrap());
    assert!(env.get(&inside.id).series_numbers.is_empty());
}

#[test]
fn cleanup_is_refused_before_any_write_when_a_task_it_would_change_is_locked_but_a_dry_run_and_unaffected_locks_are_fine() {
    let env = Env::new().with_cascade_timeout(Duration::ZERO);
    let broken = env.seed("Broken", &[(Uuid::new_v4(), 3)]);
    let also_broken = env.seed("AlsoBroken", &[(Uuid::new_v4(), 1)]);
    let clean = env.seed("Clean", &[]);
    anna_locks(&env, &broken);
    anna_locks(&env, &clean);

    let error = err(env.ws.cleanup().run(&env.project, CleanupOptions::default()));
    assert_eq!(error.message(), "Task 'Broken' is being edited by Anna");
    assert_eq!(
        (
            env.get(&also_broken.id).series_numbers.len(),
            env.get(&broken.id).series_numbers.len()
        ),
        (1, 1)
    );

    let dry = run(
        &env,
        CleanupOptions {
            dry_run: true,
            ..Default::default()
        },
    );
    assert_eq!(dry.changes.len(), 2);

    anna_releases(&env, &broken);
    let done = run(&env, CleanupOptions::default());
    assert_eq!(done.changes.len(), 2);
    assert!(env.get(&broken.id).series_numbers.is_empty());
}
