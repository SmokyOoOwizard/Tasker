//! Хранилище SQLite напрямую (TSK-159): перенос in-process сценариев `SeriesDbStorageTests` и `EditLockStorageContract` (.NET) —
//! уникальный индекс номеров и откат, запросы по номерам, `exclusive` (повтор после нарушения уникальности, предел попыток,
//! вложенность, параллельные писатели в отдельных хранилищах — как отдельные процессы), каскад удаления проекта, блокировки правки
//! в `edit_locks`, версии-счётчики.
mod common;

use common::*;
use rusqlite::Connection;
use std::time::Duration;
use tasker_core::locks::{EditHolder, LockedEntity};
use tasker_core::model::TaskItem;
use tasker_core::tasks::{Page, TaskFilter};
use tasker_services::db::db_error_of;
use tasker_services::{Error, Workspace};
use uuid::Uuid;

fn is_unique(e: &Error) -> bool {
    db_error_of(e).is_some_and(|db| db.is_unique_violation())
}

fn file(env: &Env) -> std::path::PathBuf {
    env.dir.join("tasker.db")
}

fn sql_count(env: &Env, sql: &str) -> i64 {
    Connection::open(file(env)).unwrap().query_row(sql, [], |row| row.get(0)).unwrap()
}

fn new_task(env: &Env, numbers: &[(Uuid, i32)]) -> TaskItem {
    let mut task = env.seed("probe", &[]);
    // Только модель: запись — в самом сценарии.
    task.id = Uuid::new_v4();
    task.series_numbers = numbers
        .iter()
        .map(|(s, n)| tasker_core::model::TaskSeriesNumber { series_id: *s, number: *n })
        .collect();
    task
}

#[test]
fn versions_are_counters_and_values_are_stored_like_ef_core() {
    assert!(sqlite());
    let env = Env::new();
    assert_eq!(env.backlog.version, "1");
    let task = env.seed("Counter", &[]);
    assert_eq!(task.version, "1");
    let renamed = TaskItem {
        title: "Renamed".into(),
        ..task.clone()
    };
    assert_eq!(env.ws.update(&renamed, "1").unwrap().as_deref(), Some("2"));
    assert_eq!(env.ws.update(&renamed, "1").unwrap(), None); // устаревшая версия
    assert_eq!(env.ws.update(&renamed, "a1b2c3d4e5f60718").unwrap(), None); // версия файла в БД не бывает
    let connection = Connection::open(file(&env)).unwrap();
    let (id, created, version): (String, String, i64) = connection
        .query_row("SELECT Id, CreatedAt, Version FROM tasks WHERE Title = 'Renamed'", [], |r| {
            Ok((r.get(0)?, r.get(1)?, r.get(2)?))
        })
        .unwrap();
    assert_eq!(id, task.id.to_string().to_uppercase());
    assert_eq!(created, "2026-01-01 00:00:00");
    assert_eq!(version, 2);
}

#[test]
fn duplicate_number_violates_the_unique_index_and_a_failed_update_changes_nothing() {
    let env = Env::new();
    let series = Uuid::new_v4();
    env.seed("one", &[(series, 1)]);
    let duplicate = new_task(&env, &[(series, 1)]);
    assert!(is_unique(&env.ws.add(&duplicate).unwrap_err()));

    let other = env.seed("two", &[(series, 2)]);
    let changed = TaskItem {
        title: "Changed".into(),
        series_numbers: vec![tasker_core::model::TaskSeriesNumber {
            series_id: series,
            number: 1,
        }],
        ..other.clone()
    };
    assert!(is_unique(&env.ws.update(&changed, &other.version).unwrap_err()));
    let read = env.get(&other.id);
    assert_eq!(read.title, "two");
    assert_eq!(read.series_numbers[0].number, 2);
    assert_eq!(read.version, other.version);
    // Тот же номер в другой серии — можно.
    env.seed("three", &[(Uuid::new_v4(), 1)]);
}

#[test]
fn numbering_queries_and_deletes() {
    let env = Env::new();
    let (s1, s2, gone) = (Uuid::new_v4(), Uuid::new_v4(), Uuid::new_v4());
    assert_eq!(env.ws.max_number(&env.project, &s1).unwrap(), 0);
    let t0 = Env::at("2025-12-31T00:00:00+00:00");
    let minute = |m: i64| tasker_core::Timestamp::from_unix_ticks(t0.unix_ticks() + m * 600_000_000);
    let a = env.seed_at("a", &[(s1, 1)], minute(0), Uuid::new_v4());
    let b = env.seed_at("b", &[(s1, 5), (s2, 2)], minute(1), Uuid::new_v4());
    let c = env.seed_at("c", &[(gone, 9)], minute(2), Uuid::new_v4());
    env.seed_at("d", &[], minute(3), Uuid::new_v4());
    assert_eq!(env.ws.max_number(&env.project, &s1).unwrap(), 5);
    assert_eq!(env.ws.max_number(&env.project, &gone).unwrap(), 9);
    assert_eq!(env.ws.max_number(&Uuid::new_v4(), &s1).unwrap(), 0);
    let found = env.ws.tasks_by_number(&env.project, &s1, 5).unwrap();
    assert_eq!(found.iter().map(|x| x.id).collect::<Vec<_>>(), vec![b.id]);
    assert_eq!(found[0].series_numbers.len(), 2);
    assert!(env.ws.number_conflicts(&env.project).unwrap().is_empty());
    let ids = |tasks: Vec<TaskItem>| tasks.into_iter().map(|x| x.id).collect::<Vec<_>>();
    assert_eq!(
        ids(env.ws.tasks_with_series_not_in(&env.project, &[]).unwrap()),
        vec![a.id, b.id, c.id]
    );
    assert_eq!(ids(env.ws.tasks_with_series_not_in(&env.project, &[s1, s2]).unwrap()), vec![c.id]);

    let filter = TaskFilter {
        series_ids: Some(vec![s1]),
        ..TaskFilter::default()
    };
    let page = env.ws.task_range(&env.project, Some(&filter), Page::new(1, 1)).unwrap();
    assert_eq!((page.total_count, page.data[0].id), (2, b.id));
    assert_eq!(
        env.ws
            .count_tasks(
                &env.project,
                &TaskFilter {
                    series_ids: Some(vec![]),
                    ..TaskFilter::default()
                }
            )
            .unwrap(),
        0
    );

    // Удаление задачи убирает её номера (каскад), номер свободен снова.
    assert!(env.ws.delete::<TaskItem>(&env.project, &c.id, &c.version).unwrap());
    assert_eq!(env.ws.max_number(&env.project, &gone).unwrap(), 0);
    env.seed("again", &[(gone, 9)]);
}

#[test]
fn number_conflicts_are_found_in_a_database_without_the_unique_index() {
    let env = Env::new();
    Connection::open(file(&env))
        .unwrap()
        .execute_batch("DROP INDEX IX_task_series_numbers_ProjectId_SeriesId_Number")
        .unwrap();
    let (s1, s2) = (Uuid::new_v4(), Uuid::new_v4());
    let t0 = Env::at("2025-12-31T00:00:00+00:00");
    let at = |m: i64| tasker_core::Timestamp::from_unix_ticks(t0.unix_ticks() + m * 600_000_000);
    let late = env.seed_at("late", &[(s1, 4)], at(5), Uuid::new_v4());
    let early = env.seed_at("early", &[(s1, 4)], at(0), Uuid::new_v4());
    let third = env.seed_at("third", &[(s1, 4), (s2, 1)], at(9), Uuid::new_v4());
    let other = env.seed_at("other", &[(s2, 1), (s1, 3)], at(0), Uuid::new_v4());
    let conflicts = env.ws.number_conflicts(&env.project).unwrap();
    let mut order = [s1, s2];
    order.sort();
    assert_eq!(conflicts.iter().map(|x| x.series_id).collect::<Vec<_>>(), order.to_vec());
    let of = |s: Uuid| conflicts.iter().find(|x| x.series_id == s).unwrap().task_ids.clone();
    assert_eq!(of(s1), vec![early.id, late.id, third.id]);
    assert_eq!(of(s2), vec![other.id, third.id]);
}

#[test]
fn exclusive_retries_after_a_unique_violation_and_rolls_back_the_failed_attempt() {
    let env = Env::new();
    let series = Uuid::new_v4();
    let survivor = new_task(&env, &[(series, 1)]);
    let clash = new_task(&env, &[(series, 1)]);
    let mut attempts = 0;
    let result = env
        .ws
        .exclusive(&env.project, || {
            attempts += 1;
            env.ws.add(&survivor)?;
            if attempts == 1 {
                env.ws.add(&clash)?;
            }
            Ok(attempts)
        })
        .unwrap();
    assert_eq!(result, 2);
    assert_eq!(env.ws.tasks_by_number(&env.project, &series, 1).unwrap()[0].id, survivor.id);
    assert!(env.ws.get_by_id::<TaskItem>(&env.project, &clash.id).unwrap().is_none());
}

#[test]
fn exclusive_gives_up_after_five_attempts_and_does_not_retry_other_errors() {
    let env = Env::new();
    let series = Uuid::new_v4();
    env.seed("taken", &[(series, 1)]);
    let mut attempts = 0;
    let error = env
        .ws
        .exclusive(&env.project, || {
            attempts += 1;
            env.ws.add(&new_task(&env, &[(series, 1)]))
        })
        .unwrap_err();
    assert!(is_unique(&error));
    assert_eq!(attempts, 5);

    attempts = 0;
    let error = env
        .ws
        .exclusive(&env.project, || -> tasker_services::Result<()> {
            attempts += 1;
            Err(Error::validation("boom"))
        })
        .unwrap_err();
    assert_eq!((error.message(), attempts), ("boom".to_string(), 1));
    assert_eq!(env.ws.exclusive(&env.project, || Ok(7)).unwrap(), 7);
}

#[test]
fn nested_exclusive_runs_in_the_outer_transaction_and_rolls_back_with_it() {
    let env = Env::new();
    let series = Uuid::new_v4();
    let mut outer = 0;
    let error = env
        .ws
        .exclusive(&env.project, || -> tasker_services::Result<()> {
            outer += 1;
            env.ws.exclusive(&env.project, || {
                env.ws.add(&new_task(&env, &[(series, 1)]))?;
                Err(Error::validation("boom"))
            })
        })
        .unwrap_err();
    assert_eq!((error.message(), outer), ("boom".to_string(), 1));
    assert_eq!(env.ws.max_number(&env.project, &series).unwrap(), 0);
    let number = env
        .ws
        .exclusive(&env.project, || {
            env.ws.exclusive(&env.project, || {
                let next = env.ws.max_number(&env.project, &series)? + 1;
                env.ws.add(&new_task(&env, &[(series, next)]))?;
                Ok(next)
            })
        })
        .unwrap();
    assert_eq!(number, 1);
}

#[test]
fn parallel_writers_with_their_own_connections_get_unique_consecutive_numbers() {
    let env = Env::new();
    let series = Uuid::new_v4();
    // Отдельное хранилище на поток — свой пул соединений, как у отдельного процесса.
    std::thread::scope(|scope| {
        for _ in 0..20 {
            let ws = Workspace::open_sqlite(file(&env)).unwrap();
            let template = new_task(&env, &[]);
            let project = env.project;
            scope.spawn(move || {
                ws.exclusive(&project, || {
                    let number = ws.max_number(&project, &series)? + 1;
                    let task = TaskItem {
                        id: Uuid::new_v4(),
                        series_numbers: vec![tasker_core::model::TaskSeriesNumber { series_id: series, number }],
                        ..template.clone()
                    };
                    ws.add(&task)?;
                    Ok(number)
                })
                .unwrap()
            });
        }
    });
    assert_eq!(env.ws.max_number(&env.project, &series).unwrap(), 20);
    assert_eq!(sql_count(&env, "SELECT COUNT(DISTINCT Number) FROM task_series_numbers"), 20);
}

#[test]
fn deleting_the_project_cascades_to_everything_in_it() {
    let env = Env::new();
    env.seed("t", &[(Uuid::new_v4(), 1)]);
    let project = env.ws.projects().get_by_id(&env.project).unwrap().unwrap();
    assert!(env.ws.projects().delete(&env.project, Some(&project.version)).unwrap());
    for table in [
        "tasks",
        "task_series_numbers",
        "statuses",
        "status_sets",
        "status_set_statuses",
        "task_types",
    ] {
        assert_eq!(sql_count(&env, &format!("SELECT COUNT(*) FROM {table}")), 0, "{table}");
    }
}

#[test]
fn edit_locks_live_in_the_table_and_follow_the_ef_contract() {
    let env = Env::new();
    let id = Uuid::new_v4();
    let anna = EditHolder::new("user:anna", "Anna");
    let ivan = EditHolder::new("user:ivan", "Ivan");
    let t0 = env.now();
    let later = |s: u64| tasker_services::clock::add(t0, Duration::from_secs(s));

    let first = env
        .ws
        .lock_acquire(LockedEntity::Task, &id, &anna, &t0, &later(120), Some(env.project))
        .unwrap();
    assert_eq!(first.holder, anna);
    // Своя — продлевается, момент взятия сохраняется.
    let renewed = env
        .ws
        .lock_acquire(LockedEntity::Task, &id, &anna, &later(60), &later(180), Some(env.project))
        .unwrap();
    assert_eq!((renewed.acquired_at, renewed.expires_at), (t0, later(180)));
    // Чужая действующая — возвращается как есть.
    let refused = env
        .ws
        .lock_acquire(LockedEntity::Task, &id, &ivan, &later(61), &later(181), Some(env.project))
        .unwrap();
    assert_eq!(refused.holder, anna);
    assert_eq!(env.ws.locks_by_project(&env.project, &later(62)).unwrap().len(), 1);
    // Снять может только держатель; истёкшую берёт другой с новым моментом взятия.
    assert!(!env.ws.lock_release(LockedEntity::Task, &id, &ivan.key).unwrap());
    let taken = env
        .ws
        .lock_acquire(LockedEntity::Task, &id, &ivan, &later(200), &later(320), None)
        .unwrap();
    assert_eq!((taken.holder.clone(), taken.acquired_at), (ivan.clone(), later(200)));
    assert!(env.ws.lock_get(LockedEntity::Task, &id, &later(400)).unwrap().is_none());
    // Тот же id другого вида — независимая блокировка; remove снимает чью угодно.
    env.ws
        .lock_acquire(LockedEntity::Status, &id, &anna, &t0, &later(120), None)
        .unwrap();
    env.ws.lock_remove(LockedEntity::Task, &id).unwrap();
    assert!(env.ws.lock_get(LockedEntity::Status, &id, &t0).unwrap().is_some());
    let rows: Vec<(String, String, String)> = {
        let connection = Connection::open(file(&env)).unwrap();
        let mut statement = connection.prepare("SELECT Entity, Id, AcquiredAt FROM edit_locks").unwrap();
        statement
            .query_map([], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))
            .unwrap()
            .map(|r| r.unwrap())
            .collect()
    };
    assert_eq!(
        rows,
        vec![(
            "Status".to_string(),
            id.to_string().to_uppercase(),
            "2026-01-01 00:00:00".to_string()
        )]
    );
}
