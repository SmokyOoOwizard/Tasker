//! Сценарии `EditLockTests.cs` (уровень сервисов): кто может писать, пока запись занята; блокировки по виду сущности
//! (`EntityLockService`). Хранилище блокировок проверено в `tasker-files` (`edit_locks`).
mod common;

use common::*;
use std::time::Duration;
use tasker_core::ConflictCode;
use tasker_core::locks::{EditHolder, LockedEntity};
use tasker_services::clock::add;
use tasker_services::locks::{DURATION, EntityLockService};
use tasker_services::series::UpdateSeries;
use tasker_services::task::{CreateTask, UpdateTask};
use uuid::Uuid;

fn ivan(env: &Env) -> tasker_services::Workspace {
    env.as_editor("user:ivan", "Ivan")
}

fn anna(env: &Env) -> tasker_services::Workspace {
    env.as_editor("user:anna", "Anna")
}

#[test]
fn holder_can_acquire_and_renew_their_own_lock() {
    let env = Env::new();
    let ivan = ivan(&env);
    let id = Uuid::new_v4();
    let first = ivan.locks().acquire(LockedEntity::Task, &id, "Task 'A'", None).unwrap();
    env.clock.advance(Duration::from_secs(60));
    let renewed = ivan.locks().acquire(LockedEntity::Task, &id, "Task 'A'", None).unwrap();
    assert_eq!(first.holder, EditHolder::new("user:ivan", "Ivan"));
    assert_eq!(first.acquired_at, renewed.acquired_at);
    assert_eq!(renewed.expires_at, add(env.now(), DURATION));
}

#[test]
fn another_holder_is_rejected_and_told_who_holds_the_lock() {
    let env = Env::new();
    let id = Uuid::new_v4();
    ivan(&env).locks().acquire(LockedEntity::Task, &id, "Task 'A'", None).unwrap();
    let error = err(anna(&env).locks().acquire(LockedEntity::Task, &id, "Task 'A'", None));
    assert_eq!(error.conflict_code(), Some(ConflictCode::Locked));
    assert_eq!(error.message(), "Task 'A' is being edited by Ivan");
}

#[test]
fn lock_expires_and_can_then_be_taken_by_another() {
    let env = Env::new();
    let id = Uuid::new_v4();
    ivan(&env).locks().acquire(LockedEntity::Task, &id, "Task 'A'", None).unwrap();
    env.clock.advance(DURATION + Duration::from_secs(1));
    let anna = anna(&env);
    assert!(anna.locks().get(LockedEntity::Task, &id).is_none());
    assert_eq!(
        anna.locks().acquire(LockedEntity::Task, &id, "Task 'A'", None).unwrap().holder.name,
        "Anna"
    );
}

#[test]
fn only_the_holder_can_release_and_different_entities_do_not_affect_each_other() {
    let env = Env::new();
    let id = Uuid::new_v4();
    let (ivan, anna) = (ivan(&env), anna(&env));
    ivan.locks().acquire(LockedEntity::Task, &id, "Task 'A'", None).unwrap();
    assert!(!anna.locks().release(LockedEntity::Task, &id).unwrap());
    assert!(anna.locks().get(LockedEntity::Task, &id).is_some());
    assert!(ivan.locks().release(LockedEntity::Task, &id).unwrap());
    assert!(ivan.locks().get(LockedEntity::Task, &id).is_none());

    ivan.locks().acquire(LockedEntity::Task, &id, "Task", None).unwrap();
    anna.locks().acquire(LockedEntity::Status, &id, "Status", None).unwrap();
    anna.locks().ensure_writable(LockedEntity::Board, &id, "Board").unwrap();
}

#[test]
fn task_update_is_rejected_while_someone_else_edits_it_and_passes_otherwise() {
    let env = Env::new();
    let (ivan, anna) = (ivan(&env), anna(&env));
    let task = env.task("Fix login");
    ivan.locks()
        .acquire(LockedEntity::Task, &task.id, "Task", Some(env.project))
        .unwrap();
    let update = |ws: &tasker_services::Workspace, title: &str| {
        ws.tasks().update(
            &env.project,
            &task.id,
            &UpdateTask {
                title: Some(title.into()),
                version: Some(task.version.clone()),
                ..UpdateTask::default()
            },
        )
    };
    let error = err(update(&anna, "Other"));
    assert!(error.is_locked() && error.message().contains("Ivan"));
    assert_eq!(env.get(&task.id).title, "Fix login");

    let updated = update(&ivan, "Fixed").unwrap().unwrap();
    assert_eq!(updated.title, "Fixed");
    assert_eq!(ivan.locks().get(LockedEntity::Task, &task.id).unwrap().holder.name, "Ivan");

    // Свободная сущность пишется без блокировки; истёкшая не мешает.
    let free = env.task("A");
    env.ws
        .tasks()
        .update(
            &env.project,
            &free.id,
            &UpdateTask {
                title: Some("B".into()),
                version: Some(free.version.clone()),
                ..UpdateTask::default()
            },
        )
        .unwrap();
    assert!(env.ws.locks().get(LockedEntity::Task, &free.id).is_none());
    env.clock.advance(DURATION + Duration::from_secs(1));
    assert_eq!(update(&anna, "Later").unwrap_err().conflict_code(), Some(ConflictCode::Modified)); // версия устарела после правки Ивана
    let current = env.get(&task.id);
    let later = anna
        .tasks()
        .update(
            &env.project,
            &task.id,
            &UpdateTask {
                title: Some("Later".into()),
                version: Some(current.version),
                ..UpdateTask::default()
            },
        )
        .unwrap()
        .unwrap();
    assert_eq!(later.title, "Later");
}

#[test]
fn task_delete_is_rejected_for_others_and_removes_the_lock_for_the_holder() {
    let env = Env::new();
    let (ivan, anna) = (ivan(&env), anna(&env));
    let task = env.task("A");
    ivan.locks()
        .acquire(LockedEntity::Task, &task.id, "Task", Some(env.project))
        .unwrap();
    assert!(err(anna.tasks().delete(&env.project, &task.id, Some(&task.version))).is_locked());
    assert!(env.ws.tasks().get_by_id(&env.project, &task.id).unwrap().is_some());
    assert!(ivan.tasks().delete(&env.project, &task.id, Some(&task.version)).unwrap());
    assert!(ivan.locks().get(LockedEntity::Task, &task.id).is_none());
}

#[test]
fn series_operations_on_a_locked_task_and_a_locked_series_are_rejected() {
    let env = Env::new();
    let (ivan, anna) = (ivan(&env), anna(&env));
    let series = env.series("TSK");
    let task = env
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
    ivan.locks()
        .acquire(LockedEntity::Task, &task.id, "Task", Some(env.project))
        .unwrap();
    assert!(
        err(anna
            .tasks()
            .remove_from_series(&env.project, &task.id, &series.id, Some(&task.version)))
        .is_locked()
    );
    assert!(err(anna.tasks().remove_from_series_by_number(&env.project, &series.id, 1)).is_locked());
    assert!(
        err(anna
            .tasks()
            .renumber(&env.project, &task.id, &series.id, Some(5), Some(&task.version)))
        .is_locked()
    );
    assert_eq!(env.get(&task.id).series_numbers.len(), 1);

    ivan.locks()
        .acquire(LockedEntity::Series, &series.id, "Series", Some(env.project))
        .unwrap();
    assert!(
        err(anna.series().update(
            &env.project,
            &series.id,
            &UpdateSeries {
                name: Some("New".into()),
                version: Some(series.version.clone()),
                ..UpdateSeries::default()
            },
        ))
        .is_locked()
    );
    assert!(err(anna.series().delete(&env.project, &series.id, Some(&series.version))).is_locked());
}

#[test]
fn entity_locks_check_the_entity_exists_name_it_and_list_the_project_locks() {
    let env = Env::new();
    let task = env.task("Fix login");
    let ivan = ivan(&env);
    let info = ivan
        .entity_locks()
        .acquire(Some(env.project), LockedEntity::Task, &task.id)
        .unwrap()
        .unwrap();
    assert_eq!((info.holder.as_str(), info.mine, info.entity), ("Ivan", true, LockedEntity::Task));
    assert!(
        ivan.entity_locks()
            .acquire(Some(env.project), LockedEntity::Task, &Uuid::new_v4())
            .unwrap()
            .is_none()
    );
    assert_eq!(
        message(ivan.entity_locks().acquire(None, LockedEntity::Status, &env.backlog.id)),
        "ProjectId is required to lock a Status"
    );
    let error = err(anna(&env).entity_locks().acquire(Some(env.project), LockedEntity::Task, &task.id));
    assert_eq!(error.message(), "Task 'Fix login' is being edited by Ivan");
    let anna_view = anna(&env).entity_locks().get(LockedEntity::Task, &task.id).unwrap();
    assert!(!anna_view.mine);

    // Проект блокируется сам по себе и попадает в свои же блокировки. Часы сдвигаются: список упорядочен по времени
    // взятия, а при равном времени — по id, и случайные id делали бы порядок случайным.
    env.clock.advance(Duration::from_secs(1));
    ivan.entity_locks()
        .acquire(None, LockedEntity::Project, &env.project)
        .unwrap()
        .unwrap();
    let listed = ivan.entity_locks().get_by_project(&env.project);
    assert_eq!(
        listed.iter().map(|x| x.entity).collect::<Vec<_>>(),
        vec![LockedEntity::Task, LockedEntity::Project]
    );

    assert_eq!(
        EntityLockService::parse_entity(Some("status-set")).unwrap(),
        LockedEntity::StatusSet
    );
    assert_eq!(EntityLockService::parse_entity(Some("taskType")).unwrap(), LockedEntity::TaskType);
    assert_eq!(
        message(EntityLockService::parse_entity(Some("note"))),
        "Entity: unknown kind 'note'; expected one of project, task, taskType, status, statusSet, board, series, user, linkType, field, enum"
    );
}
