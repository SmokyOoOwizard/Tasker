//! Сценарии `SeriesCoreTests.cs`: серии, номера задач в сериях, состояние серий и чистка — на временной области.
mod common;

use common::*;
use std::time::Duration;
use tasker_core::ConflictCode;
use tasker_core::model::TaskItem;
use tasker_services::cleanup::{CleanupChangeKind, CleanupOptions};
use tasker_services::series::{CreateSeries, UpdateSeries};
use tasker_services::task::{CreateTask, UpdateTask};
use uuid::Uuid;

fn sn(t: &TaskItem) -> Vec<(Uuid, i32)> {
    t.series_numbers.iter().map(|x| (x.series_id, x.number)).collect()
}

fn create_in(env: &Env, title: &str, series: &[Uuid]) -> tasker_services::Result<TaskItem> {
    env.ws.tasks().create(
        &env.project,
        &CreateTask {
            series_ids: Some(series.to_vec()),
            ..CreateTask::new(title, env.task_type.id)
        },
    )
}

// ---- SeriesServiceTests ----

#[test]
fn create_trims_name_and_stores_series() {
    let env = Env::new();
    let created = env.ws.series().create(&env.project, &CreateSeries::new("  Tasks  ", "TSK")).unwrap();
    assert_eq!(created.name, "Tasks");
    assert_eq!(created.prefix, "TSK");
    assert_eq!(created.project_id, env.project);
    assert_ne!(created.version, "");
    assert_eq!(env.ws.series().get_by_id(&env.project, &created.id).unwrap().unwrap(), created);
}

#[test]
fn create_allows_prefixes_that_differ_only_in_case() {
    let env = Env::new();
    env.ws.series().create(&env.project, &CreateSeries::new("Upper", "TSK")).unwrap();
    env.ws.series().create(&env.project, &CreateSeries::new("Lower", "tsk")).unwrap();
    let prefixes: Vec<String> = env.ws.series().get_all(&env.project).unwrap().into_iter().map(|x| x.prefix).collect();
    assert_eq!(prefixes, vec!["TSK", "tsk"]);
}

#[test]
fn create_with_taken_prefix_conflicts_and_writes_nothing() {
    let env = Env::new();
    env.ws.series().create(&env.project, &CreateSeries::new("First", "TSK")).unwrap();
    let e = err(env.ws.series().create(&env.project, &CreateSeries::new("Second", "TSK")));
    assert!(e.is_in_use());
    assert_eq!(e.message(), "Series prefix 'TSK' is already used in the project");
    assert_eq!(env.ws.series().get_all(&env.project).unwrap().len(), 1);
}

#[test]
fn create_same_prefix_in_another_project_is_fine() {
    let env = Env::new();
    env.ws.series().create(&env.project, &CreateSeries::new("First", "TSK")).unwrap();
    let other = env.ws.projects().create(&tasker_services::project::CreateProject { name: "Other".into() }).unwrap();
    env.ws.series().create(&other.id, &CreateSeries::new("Other project", "TSK")).unwrap();
}

#[test]
fn create_validates_input() {
    let env = Env::new();
    for (name, prefix) in [("", "TSK"), ("  ", "TSK"), ("Name", ""), ("Name", "A-B"), ("Name", "1234567890123456789012")] {
        assert!(err(env.ws.series().create(&env.project, &CreateSeries::new(name, prefix))).is_validation(), "{name:?} {prefix:?}");
    }
    assert!(env.ws.series().get_all(&env.project).unwrap().is_empty());
}

#[test]
fn concurrent_creates_of_one_prefix_produce_one_series() {
    let env = Env::new();
    let results: Vec<bool> = std::thread::scope(|scope| {
        let handles: Vec<_> = (0..10)
            .map(|_| {
                let ws = env.ws.clone();
                let project = env.project;
                scope.spawn(move || ws.series().create(&project, &CreateSeries::new("S", "TSK")).is_ok())
            })
            .collect();
        handles.into_iter().map(|h| h.join().unwrap()).collect()
    });
    assert_eq!(results.iter().filter(|x| **x).count(), 1);
    assert_eq!(env.ws.series().get_all(&env.project).unwrap().len(), 1);
}

#[test]
fn update_renames_prefix_and_name_and_keeps_null_fields() {
    let env = Env::new();
    let series = env.ws.series().create(&env.project, &CreateSeries::new("Old", "OLD")).unwrap();
    let updated = env
        .ws
        .series()
        .update(
            &env.project,
            &series.id,
            &UpdateSeries {
                name: Some(" New ".into()),
                prefix: Some("NEW".into()),
                version: Some(series.version.clone()),
            },
        )
        .unwrap()
        .unwrap();
    assert_eq!((updated.name.as_str(), updated.prefix.as_str()), ("New", "NEW"));
    assert_ne!(series.version, updated.version);
    assert_eq!(env.ws.series().get_by_id(&env.project, &series.id).unwrap().unwrap(), updated);

    let renamed = env
        .ws
        .series()
        .update(
            &env.project,
            &series.id,
            &UpdateSeries {
                name: Some("Other".into()),
                prefix: None,
                version: Some(updated.version.clone()),
            },
        )
        .unwrap()
        .unwrap();
    assert_eq!((renamed.name.as_str(), renamed.prefix.as_str()), ("Other", "NEW"));
    let reprefixed = env
        .ws
        .series()
        .update(
            &env.project,
            &series.id,
            &UpdateSeries {
                name: None,
                prefix: Some("ABC".into()),
                version: Some(renamed.version.clone()),
            },
        )
        .unwrap()
        .unwrap();
    assert_eq!((reprefixed.name.as_str(), reprefixed.prefix.as_str()), ("Other", "ABC"));
}

#[test]
fn update_prefix_taken_by_another_series_conflicts_but_own_and_case_variants_are_fine() {
    let env = Env::new();
    let a = env.ws.series().create(&env.project, &CreateSeries::new("A", "AAA")).unwrap();
    let b = env.ws.series().create(&env.project, &CreateSeries::new("B", "BBB")).unwrap();
    let update = |prefix: &str, name: Option<&str>, version: &str| {
        env.ws.series().update(
            &env.project,
            &b.id,
            &UpdateSeries {
                name: name.map(str::to_string),
                prefix: Some(prefix.into()),
                version: Some(version.into()),
            },
        )
    };
    assert!(err(update("AAA", None, &b.version)).is_in_use());
    let same = update("BBB", Some("B2"), &b.version).unwrap().unwrap();
    assert_eq!(same.prefix, "BBB");
    let lower = update("aaa", None, &same.version).unwrap().unwrap();
    assert_eq!(lower.prefix, "aaa");
    assert_eq!(env.ws.series().get_by_id(&env.project, &a.id).unwrap().unwrap().prefix, "AAA");
}

#[test]
fn update_checks_version_and_existence() {
    let env = Env::new();
    let series = env.ws.series().create(&env.project, &CreateSeries::new("A", "AAA")).unwrap();
    let update = |id: &Uuid, name: Option<&str>, prefix: Option<&str>, version: Option<&str>| {
        env.ws.series().update(
            &env.project,
            id,
            &UpdateSeries {
                name: name.map(str::to_string),
                prefix: prefix.map(str::to_string),
                version: version.map(str::to_string),
            },
        )
    };
    assert!(update(&Uuid::new_v4(), Some("x"), None, Some("any")).unwrap().is_none());
    assert_eq!(
        message(update(&series.id, Some("x"), None, None)),
        "Version is required: pass the version of the entity you are changing"
    );
    let stale = err(update(&series.id, Some("x"), None, Some("stale")));
    assert_eq!(stale.conflict_code(), Some(ConflictCode::Modified));
    assert_eq!(stale.message(), "Series 'A' was changed by someone else; reload it and try again");
    assert!(err(update(&series.id, Some(" "), None, Some(&series.version))).is_validation());
    assert!(err(update(&series.id, None, Some("A-B"), Some(&series.version))).is_validation());
    assert_eq!(env.ws.series().get_by_id(&env.project, &series.id).unwrap().unwrap().name, "A");
}

#[test]
fn find_by_guid_or_exact_prefix() {
    let env = Env::new();
    let upper = env.series("TSK");
    let lower = env.series("tsk");
    let find = |text: &str| env.ws.series().find(&env.project, text).unwrap().map(|x| x.id);
    assert_eq!(find(&upper.id.to_string()), Some(upper.id));
    assert_eq!(find(" TSK "), Some(upper.id));
    assert_eq!(find("tsk"), Some(lower.id));
    assert_eq!(find("Tsk"), None);
    assert_eq!(find(&Uuid::new_v4().to_string()), None);
    assert_eq!(find("TSK-5"), None);
    assert_eq!(find(""), None);
    assert_eq!(env.ws.series().find(&Uuid::new_v4(), &upper.id.to_string()).unwrap(), None);
}

#[test]
fn delete_missing_series_returns_false_and_checks_version() {
    let env = Env::new();
    let series = env.ws.series().create(&env.project, &CreateSeries::new("A", "AAA")).unwrap();
    assert!(!env.ws.series().delete(&env.project, &Uuid::new_v4(), Some("x")).unwrap());
    assert!(err(env.ws.series().delete(&env.project, &series.id, None)).is_validation());
    assert_eq!(err(env.ws.series().delete(&env.project, &series.id, Some("stale"))).conflict_code(), Some(ConflictCode::Modified));
    assert!(env.ws.series().get_by_id(&env.project, &series.id).unwrap().is_some());
    assert!(env.ws.series().delete(&env.project, &series.id, Some(&series.version)).unwrap());
    assert!(env.ws.series().get_by_id(&env.project, &series.id).unwrap().is_none());
}

#[test]
fn delete_removes_references_from_tasks_and_keeps_other_numbers_in_order() {
    let env = Env::new();
    let a = env.series("AAA");
    let b = env.series("BBB");
    let c = env.series("CCC");
    let gone = Uuid::new_v4();
    let only_a = env.seed("only A", &[(a.id, 1)]);
    let both = env.seed("both", &[(b.id, 4), (a.id, 2), (c.id, 9)]);
    let only_b = env.seed("only B", &[(b.id, 1)]);
    let dangling = env.seed("dangling", &[(gone, 3), (a.id, 3)]);
    let none = env.seed("none", &[]);
    env.clock.advance(Duration::from_secs(86_400));

    assert!(env.ws.series().delete(&env.project, &a.id, Some(&a.version)).unwrap());

    assert!(env.get(&only_a.id).series_numbers.is_empty());
    assert_eq!(sn(&env.get(&both.id)), vec![(b.id, 4), (c.id, 9)]);
    assert_eq!(sn(&env.get(&dangling.id)), vec![(gone, 3)]);
    for changed in [&only_a, &both, &dangling] {
        assert_eq!(env.get(&changed.id).updated_at, env.now());
    }
    for untouched in [&only_b, &none] {
        assert_eq!(&env.get(&untouched.id), untouched);
    }
}

#[test]
fn delete_series_removes_references_from_more_than_one_page_of_tasks() {
    let env = Env::new();
    let a = env.series("AAA");
    let b = env.series("BBB");
    for i in 1..=230 {
        env.seed(&format!("t{i}"), &[(a.id, i), (b.id, i)]);
    }
    assert!(env.ws.series().delete(&env.project, &a.id, Some(&a.version)).unwrap());
    let all = env.all_tasks();
    assert_eq!(all.len(), 230);
    assert!(all.iter().all(|t| t.series_numbers.iter().map(|x| x.series_id).collect::<Vec<_>>() == vec![b.id]));
}

#[test]
fn delete_series_removes_only_that_series_from_a_task_in_two_series() {
    let env = Env::new();
    let a = env.series("AAA");
    let b = env.series("BBB");
    let task = env.seed("t", &[(a.id, 1), (b.id, 7)]);
    assert!(env.ws.series().delete(&env.project, &a.id, Some(&a.version)).unwrap());
    assert_eq!(sn(&env.get(&task.id)), vec![(b.id, 7)]);
}

// ---- TaskServiceSeriesTests ----

#[test]
fn create_without_series_has_no_numbers() {
    let env = Env::new();
    let plain = env.task("Plain");
    let with_empty = create_in(&env, "Empty", &[]).unwrap();
    assert!(plain.series_numbers.is_empty() && with_empty.series_numbers.is_empty());
}

#[test]
fn create_with_one_series_numbers_from_one() {
    let env = Env::new();
    let s = env.series("TSK");
    let first = create_in(&env, "a", &[s.id]).unwrap();
    let second = create_in(&env, "b", &[s.id]).unwrap();
    assert_eq!(sn(&first), vec![(s.id, 1)]);
    assert_eq!(sn(&second), vec![(s.id, 2)]);
    assert_eq!(sn(&env.get(&second.id)), sn(&second));
    assert_eq!(env.get(&second.id).version, second.version);
}

#[test]
fn create_with_many_series_gets_a_number_in_each() {
    let env = Env::new();
    let a = env.series("AAA");
    let b = env.series("BBB");
    let c = env.series("CCC");
    env.seed("old", &[(b.id, 7)]);
    let task = create_in(&env, "t", &[a.id, b.id, c.id]).unwrap();
    assert_eq!(sn(&task), vec![(a.id, 1), (b.id, 8), (c.id, 1)]);
}

#[test]
fn create_takes_max_plus_one_with_gaps_and_ignores_other_series_and_dangling_refs() {
    let env = Env::new();
    let s = env.series("TSK");
    let other = env.series("OTH");
    env.seed("one", &[(s.id, 1)]);
    env.seed("five", &[(s.id, 5)]);
    env.seed("dangling", &[(Uuid::new_v4(), 99)]);
    env.seed("other", &[(other.id, 50)]);
    assert_eq!(sn(&create_in(&env, "t", &[s.id]).unwrap()), vec![(s.id, 6)]);
}

#[test]
fn create_reuses_the_number_of_a_deleted_top_task() {
    let env = Env::new();
    let s = env.series("TSK");
    create_in(&env, "a", &[s.id]).unwrap();
    let top = create_in(&env, "b", &[s.id]).unwrap();
    env.ws.tasks().delete(&env.project, &top.id, Some(&top.version)).unwrap();
    assert_eq!(sn(&create_in(&env, "c", &[s.id]).unwrap()), vec![(s.id, 2)]);
}

#[test]
fn create_with_missing_duplicate_or_foreign_series_writes_nothing() {
    let env = Env::new();
    let good = env.series("TSK");
    let missing = Uuid::new_v4();
    let e = err(create_in(&env, "t", &[good.id, missing]));
    assert_eq!(e.message(), format!("SeriesIds: not found in the project: {missing}"));
    assert_eq!(err(create_in(&env, "t", &[good.id, good.id])).message(), "SeriesIds contains duplicates");
    let other = env.ws.projects().create(&tasker_services::project::CreateProject { name: "Other".into() }).unwrap();
    let foreign = env.ws.series().create(&other.id, &CreateSeries::new("For", "FOR")).unwrap();
    assert!(err(create_in(&env, "t", &[foreign.id])).is_validation());
    assert!(env.all_tasks().is_empty());
}

#[test]
fn create_with_invalid_title_type_or_status_and_series_writes_nothing() {
    let env = Env::new();
    let s = env.series("TSK");
    let create = |title: &str, type_id: Uuid, status: Option<Uuid>| {
        env.ws.tasks().create(
            &env.project,
            &CreateTask {
                title: title.into(),
                type_id,
                status_id: status,
                series_ids: Some(vec![s.id]),
                ..CreateTask::default()
            },
        )
    };
    assert_eq!(message(create(" ", env.task_type.id, None)), "Title is required");
    let unknown = Uuid::new_v4();
    assert_eq!(message(create("t", unknown, None)), format!("TypeId: not found in the project: {unknown}"));
    assert_eq!(
        message(create("t", env.task_type.id, Some(unknown))),
        format!("StatusId: status {unknown} is not in the status set of task type 'Task'")
    );
    assert!(env.all_tasks().is_empty());
}

#[test]
fn update_and_delete_keep_working_and_update_keeps_series_numbers() {
    let env = Env::new();
    let s = env.series("TSK");
    let task = create_in(&env, "t", &[s.id]).unwrap();
    let updated = env
        .ws
        .tasks()
        .update(
            &env.project,
            &task.id,
            &UpdateTask {
                title: Some("New title".into()),
                description: Some("d".into()),
                version: Some(task.version.clone()),
                ..UpdateTask::default()
            },
        )
        .unwrap()
        .unwrap();
    assert_eq!(sn(&updated), vec![(s.id, 1)]);
    assert_eq!(sn(&env.get(&task.id)), vec![(s.id, 1)]);
    assert!(env.ws.tasks().delete(&env.project, &task.id, Some(&updated.version)).unwrap());
}

#[test]
fn parallel_creates_and_add_to_series_get_unique_consecutive_numbers() {
    let env = Env::new();
    let a = env.series("AAA");
    let b = env.series("BBB");
    std::thread::scope(|scope| {
        for i in 0..20 {
            let ws = env.ws.clone();
            let (project, type_id, a, b) = (env.project, env.task_type.id, a.id, b.id);
            scope.spawn(move || {
                ws.tasks()
                    .create(
                        &project,
                        &CreateTask {
                            series_ids: Some(vec![a, b]),
                            ..CreateTask::new(&format!("t{i}"), type_id)
                        },
                    )
                    .unwrap()
            });
        }
    });
    let mut numbers_a: Vec<i32> = env.all_tasks().iter().map(|t| t.series_numbers.iter().find(|x| x.series_id == a.id).unwrap().number).collect();
    numbers_a.sort();
    assert_eq!(numbers_a, (1..=20).collect::<Vec<_>>());
    assert!(env.ws.index().number_conflicts(&env.project).unwrap().is_empty());

    let s = env.series("TSK");
    let tasks: Vec<TaskItem> = (0..15).map(|i| env.seed(&format!("x{i}"), &[])).collect();
    std::thread::scope(|scope| {
        for t in &tasks {
            let ws = env.ws.clone();
            let (project, s) = (env.project, s.id);
            scope.spawn(move || ws.tasks().add_to_series(&project, &t.id, &s, Some(&t.version)).unwrap());
        }
    });
    let mut numbers: Vec<i32> = tasks
        .iter()
        .map(|t| env.get(&t.id).series_numbers.iter().find(|x| x.series_id == s.id).unwrap().number)
        .collect();
    numbers.sort();
    assert_eq!(numbers, (1..=15).collect::<Vec<_>>());
}

#[test]
fn add_to_series_appends_next_number_and_keeps_existing_order() {
    let env = Env::new();
    let a = env.series("AAA");
    let b = env.series("BBB");
    env.seed("x", &[(b.id, 10)]);
    let task = env.seed("t", &[(b.id, 3)]);
    env.clock.advance(Duration::from_secs(5 * 3600));
    let result = env.ws.tasks().add_to_series(&env.project, &task.id, &a.id, Some(&task.version)).unwrap().unwrap();
    assert_eq!(sn(&result), vec![(b.id, 3), (a.id, 1)]);
    assert_eq!(result.updated_at, env.now());
    assert_eq!(result, env.get(&task.id));
    assert_ne!(task.version, result.version);
}

#[test]
fn add_to_series_when_already_member_returns_task_unchanged_without_writing() {
    let env = Env::new();
    let s = env.series("TSK");
    let task = env.seed("t", &[(s.id, 4)]);
    let result = env.ws.tasks().add_to_series(&env.project, &task.id, &s.id, Some(&task.version)).unwrap().unwrap();
    assert_eq!(result, task);
    assert_eq!(env.get(&task.id).version, task.version);
}

#[test]
fn add_to_series_missing_task_or_series_gives_none_and_bad_version_is_rejected() {
    let env = Env::new();
    let s = env.series("TSK");
    let task = env.seed("t", &[]);
    assert!(env.ws.tasks().add_to_series(&env.project, &Uuid::new_v4(), &s.id, Some("x")).unwrap().is_none());
    assert!(env.ws.tasks().add_to_series(&env.project, &task.id, &Uuid::new_v4(), Some(&task.version)).unwrap().is_none());
    assert!(err(env.ws.tasks().add_to_series(&env.project, &task.id, &s.id, None)).is_validation());
    let e = err(env.ws.tasks().add_to_series(&env.project, &task.id, &s.id, Some("stale")));
    assert_eq!(e.conflict_code(), Some(ConflictCode::Modified));
    assert_eq!(env.get(&task.id), task);
}

#[test]
fn add_to_series_reports_modified_when_task_changed_on_disk() {
    let env = Env::new();
    let s = env.series("TSK");
    let task = env.seed("t", &[]);
    // Задачу изменили снаружи: версия, которую видел клиент, устарела.
    env.ws.tasks().update(&env.project, &task.id, &UpdateTask { title: Some("t2".into()), version: Some(task.version.clone()), ..UpdateTask::default() }).unwrap();
    let e = err(env.ws.tasks().add_to_series(&env.project, &task.id, &s.id, Some(&task.version)));
    assert_eq!(e.conflict_code(), Some(ConflictCode::Modified));
}

#[test]
fn remove_from_series_removes_only_that_series_and_frees_the_number() {
    let env = Env::new();
    let a = env.series("AAA");
    let b = env.series("BBB");
    let c = env.series("CCC");
    let task = env.seed("t", &[(a.id, 1), (b.id, 2), (c.id, 3)]);
    env.clock.advance(Duration::from_secs(3600));
    let result = env.ws.tasks().remove_from_series(&env.project, &task.id, &b.id, Some(&task.version)).unwrap().unwrap();
    assert_eq!(sn(&result), vec![(a.id, 1), (c.id, 3)]);
    assert_eq!(result.updated_at, env.now());
    assert_eq!(result, env.get(&task.id));
    assert_eq!(env.ws.index().max_number(&env.project, &b.id).unwrap(), 0);
}

#[test]
fn remove_from_series_not_a_member_returns_task_as_is_missing_task_is_none_and_version_is_checked() {
    let env = Env::new();
    let s = env.series("TSK");
    let task = env.seed("t", &[]);
    assert_eq!(env.ws.tasks().remove_from_series(&env.project, &task.id, &s.id, Some(&task.version)).unwrap().unwrap(), task);
    assert!(env.ws.tasks().remove_from_series(&env.project, &Uuid::new_v4(), &s.id, Some("x")).unwrap().is_none());
    assert!(err(env.ws.tasks().remove_from_series(&env.project, &task.id, &s.id, Some(""))).is_validation());
    assert!(err(env.ws.tasks().remove_from_series(&env.project, &task.id, &s.id, Some("stale"))).is_modified());
}

#[test]
fn remove_from_series_also_removes_a_dangling_reference() {
    let env = Env::new();
    let gone = Uuid::new_v4();
    let task = env.seed("t", &[(gone, 2)]);
    let result = env.ws.tasks().remove_from_series(&env.project, &task.id, &gone, Some(&task.version)).unwrap().unwrap();
    assert!(result.series_numbers.is_empty());
}

#[test]
fn remove_from_series_by_number() {
    let env = Env::new();
    let a = env.series("AAA");
    let b = env.series("BBB");
    let t1 = env.seed("one", &[(a.id, 1), (b.id, 1)]);
    env.seed("two", &[(a.id, 2)]);
    let result = env.ws.tasks().remove_from_series_by_number(&env.project, &a.id, 1).unwrap().unwrap();
    assert_eq!(result.id, t1.id);
    assert_eq!(sn(&result), vec![(b.id, 1)]);
    assert_eq!(result, env.get(&t1.id));
    assert!(env.ws.tasks().remove_from_series_by_number(&env.project, &a.id, 7).unwrap().is_none());

    let d1 = env.seed("d1", &[(a.id, 9)]);
    let d2 = env.seed("d2", &[(a.id, 9)]);
    let e = message(env.ws.tasks().remove_from_series_by_number(&env.project, &a.id, 9));
    assert!(e.starts_with("Several tasks have number 9 in the series: "));
    assert!(e.contains(&d1.id.to_string()) && e.contains(&d2.id.to_string()) && e.ends_with("; remove by task id instead"));
}

#[test]
fn renumber_to_free_number_keeps_position_of_other_series() {
    let env = Env::new();
    let a = env.series("AAA");
    let b = env.series("BBB");
    let task = env.seed("t", &[(a.id, 1), (b.id, 2)]);
    env.clock.advance(Duration::from_secs(7200));
    let result = env.ws.tasks().renumber(&env.project, &task.id, &a.id, Some(40), Some(&task.version)).unwrap().unwrap();
    assert_eq!(sn(&result), vec![(a.id, 40), (b.id, 2)]);
    assert_eq!(result.updated_at, env.now());
    assert_eq!(result, env.get(&task.id));
}

#[test]
fn renumber_to_taken_number_conflicts_including_a_duplicate_of_own_number() {
    let env = Env::new();
    let a = env.series("AAA");
    let t1 = env.seed("one", &[(a.id, 1)]);
    let t2 = env.seed("two", &[(a.id, 2)]);
    let t3 = env.seed("dup of two", &[(a.id, 2)]);
    let e = err(env.ws.tasks().renumber(&env.project, &t2.id, &a.id, Some(1), Some(&t2.version)));
    assert!(e.is_in_use());
    assert_eq!(e.message(), "Number 1 is already taken in the series");
    assert!(err(env.ws.tasks().renumber(&env.project, &t3.id, &a.id, Some(2), Some(&t3.version))).is_in_use());
    assert_eq!(env.get(&t1.id), t1);
    for to in [0, -3] {
        let e = err(env.ws.tasks().renumber(&env.project, &t1.id, &a.id, Some(to), Some(&t1.version)));
        assert_eq!(e.message(), format!("Series number must be 1 or greater, got {to}"));
    }
}

#[test]
fn renumber_to_own_number_is_a_noop_and_without_target_takes_max_plus_one() {
    let env = Env::new();
    let a = env.series("AAA");
    let task = env.seed("t", &[(a.id, 3)]);
    assert_eq!(env.ws.tasks().renumber(&env.project, &task.id, &a.id, Some(3), Some(&task.version)).unwrap().unwrap(), task);
    env.seed("other", &[(a.id, 8)]);
    let result = env.ws.tasks().renumber(&env.project, &task.id, &a.id, None, Some(&task.version)).unwrap().unwrap();
    assert_eq!(sn(&result), vec![(a.id, 9)]);
}

#[test]
fn renumber_returns_none_for_missing_task_series_or_membership_and_checks_version() {
    let env = Env::new();
    let a = env.series("AAA");
    let b = env.series("BBB");
    let task = env.seed("t", &[(a.id, 1)]);
    let renumber = |task_id: &Uuid, series: &Uuid, version: Option<&str>| env.ws.tasks().renumber(&env.project, task_id, series, Some(5), version);
    assert!(renumber(&Uuid::new_v4(), &a.id, Some("x")).unwrap().is_none());
    assert!(renumber(&task.id, &Uuid::new_v4(), Some(&task.version)).unwrap().is_none());
    assert!(renumber(&task.id, &b.id, Some(&task.version)).unwrap().is_none());
    assert!(err(renumber(&task.id, &a.id, None)).is_validation());
    assert!(err(renumber(&task.id, &a.id, Some("stale"))).is_modified());
    assert_eq!(env.get(&task.id), task);
}

#[test]
fn resolve_by_guid_prefix_number_and_duplicates() {
    let env = Env::new();
    let task = env.seed("t", &[]);
    assert_eq!(env.ws.tasks().resolve(&env.project, &task.id.to_string(), false).unwrap(), vec![task.clone()]);
    assert!(env.ws.tasks().resolve(&env.project, &Uuid::new_v4().to_string(), false).unwrap().is_empty());
    assert!(env.ws.tasks().resolve(&Uuid::new_v4(), &task.id.to_string(), false).unwrap().is_empty());

    let upper = env.series("TSK");
    let lower = env.series("tsk");
    let in_upper = env.seed("upper", &[(upper.id, 5)]);
    let in_lower = env.seed("lower", &[(lower.id, 5)]);
    assert_eq!(env.ws.tasks().resolve(&env.project, "TSK-5", false).unwrap(), vec![in_upper]);
    assert_eq!(env.ws.tasks().resolve(&env.project, " tsk-5 ", false).unwrap(), vec![in_lower]);
    assert!(env.ws.tasks().resolve(&env.project, "Tsk-5", false).unwrap().is_empty());
    assert!(env.ws.tasks().resolve(&env.project, "TSK-6", false).unwrap().is_empty());
    assert!(env.ws.tasks().resolve(&env.project, "NOPE-5", false).unwrap().is_empty());

    let late = env.seed_at("late", &[(upper.id, 2)], Env::at("2026-01-03T00:00:00+00:00"), Uuid::new_v4());
    let early = env.seed_at("early", &[(upper.id, 2)], Env::at("2026-01-02T00:00:00+00:00"), Uuid::new_v4());
    let ids: Vec<Uuid> = env.ws.tasks().resolve(&env.project, "TSK-2", false).unwrap().iter().map(|x| x.id).collect();
    assert_eq!(ids, vec![early.id, late.id]);

    assert_eq!(message(env.ws.tasks().resolve(&env.project, "hello", false)), "'hello' is neither a task id nor a reference like TSK-5");
    // Префикс id — только когда разрешён.
    let key = &task.id.simple().to_string()[..8];
    assert!(env.ws.tasks().resolve(&env.project, key, false).is_err());
    assert_eq!(env.ws.tasks().resolve(&env.project, key, true).unwrap(), vec![task.clone()]);
    assert_eq!(env.ws.tasks().resolve_id(&env.project, key).unwrap(), Some(task.id));
    assert_eq!(env.ws.tasks().resolve_id(&env.project, "00000000").unwrap(), None);
    assert_eq!(
        message(env.ws.tasks().resolve_id(&env.project, "TSK-1")),
        "'TSK-1' is not a task id: give the full id or its first 8+ hex characters"
    );
}

// ---- SeriesHealthTests ----

#[test]
fn clean_project_needs_no_attention() {
    let env = Env::new();
    let s = env.series("TSK");
    env.seed("t", &[(s.id, 1)]);
    let health = env.ws.series_health().check(&env.project).unwrap();
    assert!(!health.needs_attention());
    assert!(health.number_conflicts.is_empty() && health.prefix_conflicts.is_empty());
    assert_eq!((health.tasks_with_invalid_series, health.unreadable_series_files), (0, 0));
}

#[test]
fn all_four_counters_are_reported() {
    let env = Env::new();
    let s = env.series("TSK");
    let d1 = env.ws.series().create(&env.project, &CreateSeries::new("D1", "DUP")).unwrap();
    // Второй DUP пишем напрямую: сервис его не пустит (как после слияния веток).
    let d2 = tasker_core::model::Series {
        id: Uuid::new_v4(),
        project_id: env.project,
        name: "D2".into(),
        prefix: "DUP".into(),
        version: String::new(),
    };
    env.ws.add(&d2).unwrap();
    let t1 = env.seed_at("a", &[(s.id, 1)], env.now(), Uuid::new_v4());
    let t2 = env.seed_at("b", &[(s.id, 1)], Env::at("2026-01-02T00:00:00+00:00"), Uuid::new_v4());
    env.seed("c", &[(Uuid::new_v4(), 4)]);
    env.seed("d", &[(s.id, 2), (Uuid::new_v4(), 1)]);

    let health = env.ws.series_health().check(&env.project).unwrap();
    assert!(health.needs_attention());
    assert_eq!(health.number_conflicts.len(), 1);
    assert_eq!((health.number_conflicts[0].series_id, health.number_conflicts[0].number), (s.id, 1));
    assert_eq!(health.number_conflicts[0].task_ids, vec![t1.id, t2.id]);
    let mut ids = vec![d1.id, d2.id];
    ids.sort();
    assert_eq!(health.prefix_conflicts.len(), 1);
    assert_eq!((health.prefix_conflicts[0].prefix.as_str(), &health.prefix_conflicts[0].series_ids), ("DUP", &ids));
    assert_eq!((health.tasks_with_invalid_series, health.unreadable_series_files), (2, 0));

    env.unreadable_series_file("broken");
    let health = env.ws.series_health().check(&env.project).unwrap();
    assert_eq!((health.unreadable_series_files, health.tasks_with_invalid_series), (1, 0));
    assert!(health.needs_attention());
}

// ---- SeriesCleanupTests ----

fn g(n: u8) -> Uuid {
    Uuid::from_bytes([n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])
}

#[test]
fn cleanup_nothing_to_do_changes_nothing() {
    let env = Env::new();
    let s = env.series("TSK");
    let t = env.seed("t", &[(s.id, 1)]);
    let report = env.ws.cleanup().run(&env.project, CleanupOptions { resolve_conflicts: true, dry_run: false }).unwrap();
    assert!(report.changes.is_empty() && report.remaining_number_conflicts.is_empty() && report.prefix_conflicts.is_empty());
    assert!(!report.skipped && report.skip_reason.is_none() && report.links_skip_reason.is_none());
    assert_eq!(env.get(&t.id), t);
}

#[test]
fn cleanup_removes_invalid_references_keeps_valid_ones_and_describes_them() {
    let env = Env::new();
    let s = env.series("TSK");
    let gone = Uuid::new_v4();
    let task = env.seed("Fix login", &[(s.id, 1), (gone, 5)]);
    env.clock.advance(Duration::from_secs(3 * 86_400));
    let report = env.ws.cleanup().run(&env.project, CleanupOptions::default()).unwrap();
    assert_eq!(report.changes.len(), 1);
    let change = &report.changes[0];
    assert_eq!((change.task_id, change.task_title.as_str(), change.kind), (task.id, "Fix login", CleanupChangeKind::RemovedInvalidSeries));
    assert_eq!((change.series_id, change.old_number, change.new_number), (Some(gone), Some(5), None));
    assert_eq!(change.description, "Task 'Fix login': removed invalid series reference (was #5)");
    let stored = env.get(&task.id);
    assert_eq!(sn(&stored), vec![(s.id, 1)]);
    assert_eq!(stored.updated_at, env.now());
}

#[test]
fn cleanup_several_invalid_references_on_one_task_give_one_change_each() {
    let env = Env::new();
    let (g1, g2) = (Uuid::new_v4(), Uuid::new_v4());
    let task = env.seed("t", &[(g1, 1), (g2, 2)]);
    let report = env.ws.cleanup().run(&env.project, CleanupOptions::default()).unwrap();
    let described: Vec<(Option<Uuid>, Option<i32>)> = report.changes.iter().map(|x| (x.series_id, x.old_number)).collect();
    assert_eq!(described, vec![(Some(g1), Some(1)), (Some(g2), Some(2))]);
    assert!(env.get(&task.id).series_numbers.is_empty());
}

#[test]
fn cleanup_unreadable_series_skip_everything_but_still_report_conflicts() {
    let env = Env::new();
    let s = env.series("TSK");
    let t1 = env.seed_at("a", &[(s.id, 1), (Uuid::new_v4(), 3)], Env::at("2026-01-02T00:00:00+00:00"), Uuid::new_v4());
    let t2 = env.seed_at("b", &[(s.id, 1)], Env::at("2026-01-03T00:00:00+00:00"), Uuid::new_v4());
    env.unreadable_series_file("broken");
    let report = env.ws.cleanup().run(&env.project, CleanupOptions { resolve_conflicts: true, dry_run: false }).unwrap();
    assert!(report.skipped);
    assert_eq!(
        report.skip_reason.as_deref(),
        Some("Some series files cannot be read (merge conflict or broken YAML): a series that cannot be read would look missing and references to it would be wiped. Nothing was changed; fix the series files and run cleanup again.")
    );
    assert!(report.changes.is_empty());
    assert_eq!(report.remaining_number_conflicts.len(), 1);
    assert_eq!(report.remaining_number_conflicts[0].task_ids, vec![t1.id, t2.id]);
    assert_eq!(env.get(&t1.id), t1);
}

#[test]
fn cleanup_prefix_conflicts_are_always_reported_and_conflicts_left_alone_without_resolve() {
    let env = Env::new();
    let a = env.series("DUP");
    let b = tasker_core::model::Series {
        id: Uuid::new_v4(),
        project_id: env.project,
        name: "B".into(),
        prefix: "DUP".into(),
        version: String::new(),
    };
    env.ws.add(&b).unwrap();
    let mut ids = vec![a.id, b.id];
    ids.sort();
    for options in [CleanupOptions::default(), CleanupOptions { resolve_conflicts: true, dry_run: true }] {
        let report = env.ws.cleanup().run(&env.project, options).unwrap();
        assert_eq!(report.prefix_conflicts.len(), 1);
        assert_eq!(report.prefix_conflicts[0].series_ids, ids);
    }
    let t1 = env.seed("a", &[(a.id, 1)]);
    let t2 = env.seed_at("b", &[(a.id, 1)], Env::at("2026-01-02T00:00:00+00:00"), Uuid::new_v4());
    let report = env.ws.cleanup().run(&env.project, CleanupOptions::default()).unwrap();
    assert!(report.changes.is_empty());
    assert_eq!(report.remaining_number_conflicts[0].task_ids, vec![t1.id, t2.id]);
    env.unreadable_series_file("broken");
    assert_eq!(env.ws.cleanup().run(&env.project, CleanupOptions::default()).unwrap().prefix_conflicts.len(), 1);
}

#[test]
fn cleanup_two_conflicting_tasks_the_earliest_keeps_the_number() {
    let env = Env::new();
    let s = env.series("TSK");
    let later = env.seed_at("later", &[(s.id, 3)], Env::at("2026-01-03T00:00:00+00:00"), Uuid::new_v4());
    let earlier = env.seed_at("earlier", &[(s.id, 3)], Env::at("2026-01-02T00:00:00+00:00"), Uuid::new_v4());
    env.seed("top", &[(s.id, 7)]);
    let report = env.ws.cleanup().run(&env.project, CleanupOptions { resolve_conflicts: true, dry_run: false }).unwrap();
    assert_eq!(report.changes.len(), 1);
    let change = &report.changes[0];
    assert_eq!((change.task_id, change.kind, change.series_id, change.old_number, change.new_number), (later.id, CleanupChangeKind::Renumbered, Some(s.id), Some(3), Some(8)));
    assert_eq!(change.description, "Task 'later': duplicate number TSK-3 changed to TSK-8");
    assert_eq!(sn(&env.get(&earlier.id)), vec![(s.id, 3)]);
    assert_eq!(sn(&env.get(&later.id)), vec![(s.id, 8)]);
    assert!(report.remaining_number_conflicts.is_empty());
    assert!(env.ws.index().number_conflicts(&env.project).unwrap().is_empty());
}

#[test]
fn cleanup_three_tasks_with_equal_created_at_are_ordered_by_guid() {
    let env = Env::new();
    let s = env.series("TSK");
    let when = Env::at("2026-01-02T00:00:00+00:00");
    let third = env.seed_at("third", &[(s.id, 2)], when, g(3));
    let first = env.seed_at("first", &[(s.id, 2)], when, g(1));
    let second = env.seed_at("second", &[(s.id, 2)], when, g(2));
    env.seed("top", &[(s.id, 10)]);
    let report = env.ws.cleanup().run(&env.project, CleanupOptions { resolve_conflicts: true, dry_run: false }).unwrap();
    let changes: Vec<(Uuid, i32, i32)> = report.changes.iter().map(|x| (x.task_id, x.old_number.unwrap(), x.new_number.unwrap())).collect();
    assert_eq!(changes, vec![(second.id, 2, 11), (third.id, 2, 12)]);
    assert_eq!(sn(&env.get(&first.id)), vec![(s.id, 2)]);
    assert_eq!(sn(&env.get(&second.id)), vec![(s.id, 11)]);
    assert_eq!(sn(&env.get(&third.id)), vec![(s.id, 12)]);
}

#[test]
fn cleanup_several_series_conflicts_invalid_references_and_a_task_in_two_groups_at_once() {
    let env = Env::new();
    let a = env.series("AAA");
    let b = env.series("BBB");
    let gone = Uuid::new_v4();
    let t1 = Env::at("2026-01-02T00:00:00+00:00");
    let t2 = Env::at("2026-01-03T00:00:00+00:00");
    let t3 = Env::at("2026-01-04T00:00:00+00:00");
    let a1 = env.seed_at("a1", &[(a.id, 1), (b.id, 1)], t1, Uuid::new_v4());
    let a2 = env.seed_at("a2", &[(a.id, 1), (b.id, 1), (gone, 9)], t2, Uuid::new_v4());
    let a3 = env.seed_at("a3", &[(a.id, 1)], t3, Uuid::new_v4());
    let b2 = env.seed("b2", &[(b.id, 5)]);
    let report = env.ws.cleanup().run(&env.project, CleanupOptions { resolve_conflicts: true, dry_run: false }).unwrap();
    assert_eq!(report.changes.iter().filter(|x| x.kind == CleanupChangeKind::RemovedInvalidSeries).count(), 1);
    assert_eq!(report.changes.iter().filter(|x| x.kind == CleanupChangeKind::Renumbered).count(), 3);
    assert_eq!(sn(&env.get(&a1.id)), vec![(a.id, 1), (b.id, 1)]);
    assert_eq!(sn(&env.get(&a2.id)), vec![(a.id, 2), (b.id, 6)]);
    assert_eq!(sn(&env.get(&a3.id)), vec![(a.id, 3)]);
    assert_eq!(sn(&env.get(&b2.id)), vec![(b.id, 5)]);
    assert!(env.ws.index().number_conflicts(&env.project).unwrap().is_empty());
    assert_eq!(env.ws.series_health().check(&env.project).unwrap().tasks_with_invalid_series, 0);
}

#[test]
fn cleanup_duplicates_inside_a_nonexistent_series_are_not_renumbered() {
    let env = Env::new();
    let gone = Uuid::new_v4();
    env.seed("a", &[(gone, 1)]);
    env.seed("b", &[(gone, 1)]);
    for dry in [true, false] {
        let report = env.ws.cleanup().run(&env.project, CleanupOptions { resolve_conflicts: true, dry_run: dry }).unwrap();
        assert!(report.changes.iter().all(|c| c.kind == CleanupChangeKind::RemovedInvalidSeries));
        assert_eq!(report.changes.len(), 2);
        assert!(report.remaining_number_conflicts.is_empty());
    }
}

#[test]
fn cleanup_dry_run_writes_nothing_and_matches_the_real_run_and_second_run_changes_nothing() {
    let env = Env::new();
    let a = env.series("AAA");
    let b = env.series("BBB");
    let gone = Uuid::new_v4();
    env.seed_at("x", &[(a.id, 1), (gone, 2)], Env::at("2026-01-02T00:00:00+00:00"), Uuid::new_v4());
    env.seed_at("y", &[(a.id, 1), (b.id, 4)], Env::at("2026-01-03T00:00:00+00:00"), Uuid::new_v4());
    env.seed_at("z", &[(a.id, 1), (b.id, 4)], Env::at("2026-01-04T00:00:00+00:00"), Uuid::new_v4());
    env.seed_at("w", &[(b.id, 4)], Env::at("2026-01-05T00:00:00+00:00"), Uuid::new_v4());
    let before = env.all_tasks();

    let dry = env.ws.cleanup().run(&env.project, CleanupOptions { resolve_conflicts: true, dry_run: true }).unwrap();
    assert_eq!(env.all_tasks(), before);
    let real = env.ws.cleanup().run(&env.project, CleanupOptions { resolve_conflicts: true, dry_run: false }).unwrap();
    assert_eq!(dry.changes, real.changes);
    assert_eq!(dry.remaining_number_conflicts, real.remaining_number_conflicts);
    assert!(!real.changes.is_empty());
    assert_ne!(env.all_tasks(), before);
    assert!(!env.ws.series_health().check(&env.project).unwrap().needs_attention());

    let snapshot = env.all_tasks();
    let second = env.ws.cleanup().run(&env.project, CleanupOptions { resolve_conflicts: true, dry_run: false }).unwrap();
    assert!(second.changes.is_empty());
    assert_eq!(env.all_tasks(), snapshot);
}

#[test]
fn cleanup_dry_run_without_resolve_reports_conflicts_of_valid_series_only() {
    let env = Env::new();
    let s = env.series("TSK");
    let gone = Uuid::new_v4();
    let t1 = env.seed_at("a", &[(s.id, 1), (gone, 1)], Env::at("2026-01-02T00:00:00+00:00"), Uuid::new_v4());
    let t2 = env.seed_at("b", &[(s.id, 1), (gone, 1)], Env::at("2026-01-03T00:00:00+00:00"), Uuid::new_v4());
    let dry = env.ws.cleanup().run(&env.project, CleanupOptions { resolve_conflicts: false, dry_run: true }).unwrap();
    let real = env.ws.cleanup().run(&env.project, CleanupOptions::default()).unwrap();
    assert_eq!(dry.remaining_number_conflicts.len(), 1);
    assert_eq!(dry.remaining_number_conflicts[0].task_ids, vec![t1.id, t2.id]);
    assert_eq!(dry.remaining_number_conflicts, real.remaining_number_conflicts);
    assert_eq!(dry.changes, real.changes);
}
