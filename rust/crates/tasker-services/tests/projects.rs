//! Сценарии `ProjectDeleteTests.cs` и `VanishedEntityTests.cs` (уровень сервисов): статистика и удаление проекта, метка удалённого
//! проекта, понятная ошибка «не найдено», когда тип или набор исчезли посреди операции; статусы, наборы и типы.
mod common;

use common::*;
use tasker_core::ConflictCode;
use tasker_core::tasks::Page;
use tasker_services::board::{BoardColumnInput, CreateBoard};
use tasker_services::project::{CreateProject, UpdateProject};
use tasker_services::series::CreateSeries;
use tasker_services::status::UpdateStatus;
use tasker_services::status_set::{CreateStatusSet, UpdateStatusSet};
use tasker_services::task::{CreateTask, UpdateTask};
use tasker_services::task_type::{CreateTaskType, UpdateTaskType};
use uuid::Uuid;

#[test]
fn stats_count_entities_and_delete_removes_the_project_with_everything_in_it() {
    let env = Env::new();
    let p = &env.project;
    env.ws
        .boards()
        .create(
            p,
            &CreateBoard {
                name: "Main".into(),
                status_set_ids: vec![env.set.id],
                columns: vec![
                    BoardColumnInput::new("Todo", vec![env.backlog.id]),
                    BoardColumnInput::new("Finished", vec![env.done.id]),
                ],
            },
        )
        .unwrap();
    env.task("One");
    env.task("Two");
    env.ws.series().create(p, &CreateSeries::new("Main", "DM")).unwrap();

    let stats = env.ws.projects().get_stats(p).unwrap().unwrap();
    assert_eq!(
        (
            stats.tasks,
            stats.boards,
            stats.statuses,
            stats.status_sets,
            stats.task_types,
            stats.series,
            stats.link_types
        ),
        (2, 1, 2, 1, 1, 1, 0)
    );
    assert!(env.ws.projects().get_stats(&Uuid::new_v4()).unwrap().is_none());

    let project = env.ws.projects().get_by_id(p).unwrap().unwrap();
    assert_eq!(project.name, "Test");
    assert!(err(env.ws.projects().delete(p, None)).is_validation());
    assert_eq!(
        err(env.ws.projects().delete(p, Some("stale"))).conflict_code(),
        Some(ConflictCode::Modified)
    );
    assert!(!env.ws.projects().delete(&Uuid::new_v4(), Some("x")).unwrap());

    assert!(env.ws.projects().delete(p, Some(&project.version)).unwrap());
    assert!(env.ws.projects().get_by_id(p).unwrap().is_none());
    assert_eq!(env.ws.projects().get_range(Page::first(50)).unwrap().total_count, 0);
    assert!(env.ws.task_types().get_all(p).unwrap().is_empty());
    assert!(env.ws.tasks().get_all(p, None).unwrap().is_empty());
    assert!(!env.ws.directory().project(p).root().exists());

    // «Поздний» писатель не воскрешает папку удалённого проекта.
    let late = err(env.ws.tasks().create(p, &CreateTask::new("Late", env.task_type.id)));
    assert!(late.is_validation());
    let late_series = message(env.ws.series().create(p, &CreateSeries::new("Late", "LT")));
    assert_eq!(late_series, format!("ProjectId: project not found: {p}"));
    assert!(!env.ws.directory().project(p).root().exists());
}

#[test]
fn projects_are_created_listed_and_renamed_with_a_version() {
    let env = Env::new();
    let other = env.ws.projects().create(&CreateProject { name: "  Alpha  ".into() }).unwrap();
    assert_eq!(other.name, "Alpha");
    assert_eq!(
        message(env.ws.projects().create(&CreateProject { name: " ".into() })),
        "Project name is required"
    );
    let names: Vec<String> = env.ws.projects().get_all().unwrap().into_iter().map(|x| x.name).collect();
    assert_eq!(names, vec!["Alpha", "Test"]);
    let renamed = env
        .ws
        .projects()
        .update(
            &other.id,
            &UpdateProject {
                name: Some("Beta".into()),
                version: Some(other.version.clone()),
            },
        )
        .unwrap()
        .unwrap();
    assert_eq!(renamed.name, "Beta");
    assert_ne!(renamed.version, other.version);
    assert!(
        err(env.ws.projects().update(
            &other.id,
            &UpdateProject {
                name: Some("C".into()),
                version: Some(other.version.clone())
            }
        ))
        .is_modified()
    );
    assert!(
        env.ws
            .projects()
            .update(
                &Uuid::new_v4(),
                &UpdateProject {
                    name: Some("C".into()),
                    version: Some("v".into())
                }
            )
            .unwrap()
            .is_none()
    );
}

#[test]
fn create_after_the_status_set_vanished_is_not_found_and_describe_after_the_project_vanished_is_not_found() {
    let env = Env::new();
    let p = &env.project;
    let task = env.task("One");
    let set = env.ws.status_sets().get_by_id(p, &env.set.id).unwrap().unwrap();
    // Набор из-под типа: сервис его не отдаст (тип использует), удаляем файлом — как в другой ветке.
    assert!(env.ws.delete::<tasker_core::model::StatusSet>(p, &set.id, &set.version).unwrap());
    let late = err(env.ws.tasks().create(p, &CreateTask::new("late", env.task_type.id)));
    assert!(late.is_not_found());
    assert_eq!(
        late.message(),
        format!(
            "Status set {} of task type {} not found (the project may have been deleted)",
            set.id, env.task_type.id
        )
    );

    let project = env.ws.projects().get_by_id(p).unwrap().unwrap();
    env.ws.projects().delete(p, Some(&project.version)).unwrap();
    let described = err(env.ws.tasks().describe(p, &task));
    assert!(described.is_not_found());
    assert_eq!(
        described.message(),
        format!(
            "Task type {} of task {} not found (the project may have been deleted)",
            env.task_type.id, task.id
        )
    );
    assert!(err(env.ws.tasks().fields_of(p, &task)).is_not_found());
}

#[test]
fn statuses_status_sets_and_task_types_check_usages_and_cascades() {
    let env = Env::new();
    let p = &env.project;
    let statuses = env.ws.statuses();
    // Статус: правка и список с усечённым описанием.
    let long = "α".repeat(10);
    let updated = statuses
        .update(
            p,
            &env.backlog.id,
            &UpdateStatus {
                color: Some("#abcdef".into()),
                description: Some(long.clone()),
                version: Some(env.backlog.version.clone()),
                ..UpdateStatus::default()
            },
        )
        .unwrap()
        .unwrap();
    assert_eq!((updated.color.as_str(), updated.description.as_str()), ("#ABCDEF", long.as_str()));
    let listed = statuses.list(p, Page::first(50), 3).unwrap();
    let backlog = listed.data.iter().find(|x| x.status.id == env.backlog.id).unwrap();
    assert_eq!(
        (
            backlog.status.description.as_str(),
            backlog.description_truncated,
            backlog.description_length
        ),
        ("ααα", true, 10)
    );
    assert_eq!(
        message(statuses.update(
            p,
            &env.done.id,
            &UpdateStatus {
                color: Some("red".into()),
                version: Some(env.done.version.clone()),
                ..Default::default()
            }
        )),
        "Color must be in #RRGGBB format"
    );

    // Удалить статус, который в наборе и у задач, нельзя; текст перечисляет все места.
    let task = env.task("T");
    let current = statuses.get_by_id(p, &env.backlog.id).unwrap().unwrap();
    assert_eq!(
        message(statuses.delete(p, &env.backlog.id, Some(&current.version))),
        "Status 'Backlog' is used by 1 task(s); status set 'Main' and cannot be deleted"
    );
    let spare = env.ws.statuses().create(p, &status("Spare")).unwrap();
    assert!(statuses.delete(p, &spare.id, Some(&spare.version)).unwrap());

    // Набор: убрать статус, которым пользуются задачи типа, нельзя; убрать последний — нельзя; набор типа не удаляется.
    let sets = env.ws.status_sets();
    let set = sets.get_by_id(p, &env.set.id).unwrap().unwrap();
    let shrink = |ids: Vec<Uuid>, version: &str| {
        sets.update(
            p,
            &env.set.id,
            &UpdateStatusSet {
                status_ids: Some(ids),
                version: Some(version.into()),
                ..UpdateStatusSet::default()
            },
        )
    };
    assert_eq!(
        message(shrink(vec![env.done.id], &set.version)),
        "Status 'Backlog' is used by 1 task(s) and cannot be removed from status set 'Main'"
    );
    assert_eq!(message(shrink(vec![], &set.version)), "Status set must contain at least one status");
    let unknown = Uuid::new_v4();
    assert_eq!(
        message(shrink(vec![unknown], &set.version)),
        format!("StatusIds: not found in the project: {unknown}")
    );
    assert_eq!(
        message(sets.delete(p, &env.set.id, Some(&set.version))),
        "Status set 'Main' is used by task type 'Task' and cannot be deleted"
    );
    let reordered = shrink(vec![env.done.id, env.backlog.id], &set.version).unwrap().unwrap();
    assert_eq!(reordered.status_ids, vec![env.done.id, env.backlog.id]);
    assert_eq!(
        message(sets.create(
            p,
            &CreateStatusSet {
                name: "Dup".into(),
                status_ids: vec![env.done.id, env.done.id]
            }
        )),
        "StatusIds contains duplicates"
    );

    // Тип: сменить набор можно, только если статусы задач есть в новом; тип с задачами не удаляется.
    let other_set = sets
        .create(
            p,
            &CreateStatusSet {
                name: "Other".into(),
                status_ids: vec![env.done.id],
            },
        )
        .unwrap();
    let types = env.ws.task_types();
    let task_type = types.get_by_id(p, &env.task_type.id).unwrap().unwrap();
    let error = err(types.update(
        p,
        &task_type.id,
        &UpdateTaskType {
            status_set_id: Some(other_set.id),
            version: Some(task_type.version.clone()),
            ..Default::default()
        },
    ));
    assert_eq!(error.conflict_code(), Some(ConflictCode::InUse));
    assert_eq!(
        error.message(),
        "1 task(s) of type 'Task' have statuses that are not in status set 'Other'; move them to other statuses first"
    );
    assert_eq!(
        message(types.delete(p, &task_type.id, Some(&task_type.version))),
        "Task type 'Task' is used by 1 task(s) and cannot be deleted"
    );
    env.ws
        .tasks()
        .update(
            p,
            &task.id,
            &UpdateTask {
                status_id: Some(env.done.id),
                version: Some(task.version.clone()),
                ..Default::default()
            },
        )
        .unwrap();
    let moved = types
        .update(
            p,
            &task_type.id,
            &UpdateTaskType {
                status_set_id: Some(other_set.id),
                version: Some(task_type.version.clone()),
                ..Default::default()
            },
        )
        .unwrap()
        .unwrap()
        .value;
    assert_eq!(moved.status_set_id, other_set.id);
    assert_eq!(
        message(types.create(
            p,
            &CreateTaskType {
                name: "X".into(),
                status_set_id: unknown,
                fields: None,
                description: None
            }
        )),
        format!("StatusSetId: not found in the project: {unknown}")
    );
    let with_description = types
        .create(
            p,
            &CreateTaskType {
                name: "Described".into(),
                status_set_id: env.set.id,
                fields: Some(vec![]),
                description: Some("  Some text  ".into()),
            },
        )
        .unwrap();
    assert_eq!(with_description.description, "  Some text  ");
    let listed = types.list(p, Page::first(50), 4).unwrap();
    let described = listed.data.iter().find(|x| x.task_type.id == with_description.id).unwrap();
    assert_eq!(
        (
            described.task_type.description.as_str(),
            described.description_truncated,
            described.description_length
        ),
        ("  So", true, 13)
    );
}
