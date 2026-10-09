//! Сценарии `BoardFieldFilterTests.cs` (уровень сервисов): условия колонок по полям хранятся в колонке по id, задачи попадают в
//! колонку сами, проверки против каталога и исключающие условия у общего статуса, каскады поля и перечисления, перенос задачи.
mod common;

use common::*;
use std::time::Duration;
use tasker_core::ConflictCode;
use tasker_core::ids::guid_d;
use tasker_core::model::{Board, FieldDefinition, FieldEnum, FieldOperator, FieldType, TaskItem, TaskType, TaskTypeField};
use tasker_core::tasks::Page;
use tasker_services::board::{BoardColumnInput, CreateBoard, MoveTask, UpdateBoard};
use tasker_services::field::{CreateField, UpdateField};
use tasker_services::field_enum::{CreateFieldEnum, FieldEnumValueInput, RemovedEnumValues, UpdateFieldEnum};
use tasker_services::task::{CreateTask, UpdateTask};
use tasker_services::task_fields::TaskFieldChanges;
use uuid::Uuid;

/// Проект: набор Flow (Open, Closed — в Env это Backlog и Done), тип Bug с полями Level (enum Low/High) и Estimate (int).
struct P {
    env: Env,
    levels: FieldEnum,
    level: FieldDefinition,
    estimate: FieldDefinition,
    bug: TaskType,
}

impl P {
    fn new() -> P {
        let env = Env::new();
        let levels = env
            .ws
            .enums()
            .create(
                &env.project,
                &CreateFieldEnum {
                    name: "LevelValues".into(),
                    values: vec!["Low".into(), "High".into()],
                },
            )
            .unwrap();
        let level = env
            .ws
            .fields()
            .create(
                &env.project,
                &CreateField {
                    name: "Level".into(),
                    field_type: FieldType::Enum,
                    multiple: None,
                    enum_id: Some(levels.id),
                },
            )
            .unwrap();
        let estimate = env
            .ws
            .fields()
            .create(
                &env.project,
                &CreateField {
                    name: "Estimate".into(),
                    field_type: FieldType::Int,
                    multiple: None,
                    enum_id: None,
                },
            )
            .unwrap();
        let bug = env.type_with(
            "Bug",
            vec![
                TaskTypeField {
                    field_id: level.id,
                    required: false,
                },
                TaskTypeField {
                    field_id: estimate.id,
                    required: false,
                },
            ],
        );
        P {
            env,
            levels,
            level,
            estimate,
            bug,
        }
    }

    fn open(&self) -> Uuid {
        self.env.backlog.id
    }

    fn closed(&self) -> Uuid {
        self.env.done.id
    }

    fn low(&self) -> String {
        guid_d(&self.levels.values[0].id)
    }

    fn high(&self) -> String {
        guid_d(&self.levels.values[1].id)
    }

    fn task(&self, title: &str, level: Option<&str>, estimate: Option<i32>, status: Option<Uuid>) -> TaskItem {
        self.env.clock.advance(Duration::from_secs(1));
        let mut values: Vec<(Uuid, Vec<&str>)> = Vec::new();
        if let Some(level) = level {
            values.push((self.level.id, vec![level]));
        }
        let estimate_text = estimate.map(|e| e.to_string());
        if let Some(e) = &estimate_text {
            values.push((self.estimate.id, vec![e.as_str()]));
        }
        self.env
            .ws
            .tasks()
            .create(
                &self.env.project,
                &CreateTask {
                    status_id: status,
                    fields: Some(TaskFieldChanges::values(values)),
                    ..CreateTask::new(title, self.bug.id)
                },
            )
            .unwrap()
    }

    fn column(&self, name: &str, statuses: Vec<Uuid>, filters: &[&str]) -> BoardColumnInput {
        BoardColumnInput {
            id: None,
            name: name.into(),
            status_ids: statuses.clone(),
            drop_statuses: Some(vec![(self.env.set.id, statuses[0])]),
            field_filters: Some(filters.iter().map(|x| x.to_string()).collect()),
        }
    }

    fn board(&self, name: &str, columns: Vec<BoardColumnInput>) -> tasker_services::Result<Board> {
        self.env.ws.boards().create(
            &self.env.project,
            &CreateBoard {
                name: name.into(),
                status_set_ids: vec![self.env.set.id],
                columns,
            },
        )
    }

    fn titles(&self, board: &Board, column: usize, field_filters: &[&str]) -> Vec<String> {
        let filters: Vec<String> = field_filters.iter().map(|x| x.to_string()).collect();
        let mut titles: Vec<String> = self
            .env
            .ws
            .boards()
            .column_tasks(
                &self.env.project,
                &board.id,
                &board.columns[column].id,
                Page::first(50),
                if filters.is_empty() { None } else { Some(&filters) },
                -1,
                None,
            )
            .unwrap()
            .unwrap()
            .data
            .into_iter()
            .map(|x| x.task.title)
            .collect();
        titles.sort();
        titles
    }

    fn reload(&self, board: &Board) -> Board {
        self.env.ws.boards().get_by_id(&self.env.project, &board.id).unwrap().unwrap()
    }

    fn keep_columns(&self, board: &Board, filters: impl Fn(&str) -> Option<Vec<String>>) -> Vec<BoardColumnInput> {
        board
            .columns
            .iter()
            .map(|c| BoardColumnInput {
                id: Some(c.id),
                name: c.name.clone(),
                status_ids: c.status_ids.clone(),
                drop_statuses: Some(vec![(self.env.set.id, c.status_ids[0])]),
                field_filters: filters(&c.name),
            })
            .collect()
    }
}

#[test]
fn a_column_stores_conditions_by_id_and_collects_the_matching_tasks_by_itself() {
    let p = P::new();
    let env = &p.env;
    let board = p
        .board(
            "Main",
            vec![
                p.column("Critical", vec![p.open()], &["Level=High"]),
                p.column("Rest", vec![p.open()], &["Level!=High"]),
                p.column("Closed", vec![p.closed()], &[]),
            ],
        )
        .unwrap();
    let stored = &board.columns[0].field_conditions[0];
    assert_eq!((stored.field_id, stored.operator, stored.value.clone()), (p.level.id, FieldOperator::Equal, Some(p.high())));
    assert_eq!(board.columns[1].field_conditions[0].operator, FieldOperator::NotEqual);
    assert!(board.columns[2].field_conditions.is_empty());

    p.task("a", Some("High"), Some(5), None);
    let b = p.task("b", Some("Low"), Some(2), None);
    p.task("c", None, None, None);
    p.task("d", Some("High"), Some(1), Some(p.closed()));
    assert_eq!(p.titles(&board, 0, &[]), vec!["a"]);
    assert_eq!(p.titles(&board, 1, &[]), vec!["b", "c"]);
    assert_eq!(p.titles(&board, 2, &[]), vec!["d"]);

    env.ws
        .tasks()
        .update(
            &env.project,
            &b.id,
            &UpdateTask {
                version: Some(b.version.clone()),
                fields: Some(TaskFieldChanges::values(vec![(p.level.id, vec!["High"])])),
                ..UpdateTask::default()
            },
        )
        .unwrap();
    assert_eq!(p.titles(&board, 0, &[]), vec!["a", "b"]);
    assert_eq!(p.titles(&board, 1, &[]), vec!["c"]);
    assert_eq!(p.titles(&board, 0, &["Estimate>=5"]), vec!["a"]);
    assert_eq!(p.titles(&board, 0, &["Estimate<5"]), vec!["b"]);
    assert!(p.titles(&board, 1, &["Level=High"]).is_empty());

    // Переименование поля и значения enum колонку не ломает: условия хранятся по id.
    let level = env.ws.fields().get_by_id(&env.project, &p.level.id).unwrap().unwrap();
    env.ws
        .fields()
        .update(
            &env.project,
            &p.level.id,
            &UpdateField {
                name: Some("Severity".into()),
                version: Some(level.version),
                ..UpdateField::default()
            },
        )
        .unwrap();
    let levels = env.ws.enums().get_by_id(&env.project, &p.levels.id).unwrap().unwrap();
    env.ws
        .enums()
        .update(
            &env.project,
            &p.levels.id,
            &UpdateFieldEnum {
                values: Some(vec![
                    FieldEnumValueInput {
                        id: Some(p.levels.values[0].id),
                        name: "Minor".into(),
                    },
                    FieldEnumValueInput {
                        id: Some(p.levels.values[1].id),
                        name: "Critical".into(),
                    },
                ]),
                version: Some(levels.version),
                ..UpdateFieldEnum::default()
            },
        )
        .unwrap();
    assert_eq!(p.titles(&board, 0, &[]), vec!["a", "b"]);
    assert_eq!(p.titles(&board, 1, &[]), vec!["c"]);
    assert_eq!(p.titles(&board, 0, &["Severity=Critical", "Estimate>=5"]), vec!["a"]);
    assert_eq!(env.ws.boards().describe_filters(&env.project, &board.columns[0]).unwrap(), vec!["Severity=Critical"]);

    // Условия живут вместе с доской: правка без колонок и колонки без fieldFilters их сохраняют.
    let read = p.reload(&board);
    assert_eq!(read.columns[0].field_conditions[0].value, Some(p.high()));
    let renamed = env
        .ws
        .boards()
        .update(
            &env.project,
            &board.id,
            &UpdateBoard {
                name: Some("Main 2".into()),
                version: Some(read.version.clone()),
                ..UpdateBoard::default()
            },
        )
        .unwrap()
        .unwrap();
    assert_eq!(renamed.columns[1].field_conditions[0].field_id, p.level.id);
    let kept = env
        .ws
        .boards()
        .update(
            &env.project,
            &board.id,
            &UpdateBoard {
                columns: Some(p.keep_columns(&renamed, |_| None)),
                version: Some(renamed.version.clone()),
                ..UpdateBoard::default()
            },
        )
        .unwrap()
        .unwrap();
    assert_eq!((kept.columns[0].field_conditions.len(), kept.columns[1].field_conditions.len()), (1, 1));

    // [] — условий нет (колонки с общим статусом пересекаются: отказ), другой текст — заменяет.
    let cleared = err(env.ws.boards().update(
        &env.project,
        &board.id,
        &UpdateBoard {
            columns: Some(p.keep_columns(&kept, |_| Some(vec![]))),
            version: Some(kept.version.clone()),
            ..UpdateBoard::default()
        },
    ));
    assert!(cleared.message().contains("already used by column"));
    let changed = env
        .ws
        .boards()
        .update(
            &env.project,
            &board.id,
            &UpdateBoard {
                columns: Some(p.keep_columns(&kept, |name| match name {
                    "Critical" => Some(vec!["Estimate>=5".into()]),
                    "Rest" => Some(vec!["Estimate<5".into()]),
                    _ => Some(vec![]),
                })),
                version: Some(kept.version.clone()),
                ..UpdateBoard::default()
            },
        )
        .unwrap()
        .unwrap();
    let c = &changed.columns[0].field_conditions[0];
    assert_eq!((c.operator, c.field_id, c.value.as_deref()), (FieldOperator::GreaterOrEqual, p.estimate.id, Some("5")));
    assert_eq!(p.titles(&changed, 0, &[]), vec!["a"]);
    assert_eq!(p.titles(&changed, 1, &[]), vec!["b"]);
}

#[test]
fn conditions_are_validated_against_the_catalog_and_a_shared_status_needs_exclusive_conditions() {
    let p = P::new();
    let bad = |filters: &[&str]| message(p.board("X", vec![p.column("C", vec![p.open()], filters)]));
    assert!(bad(&["Nope=1"]).contains("no field 'Nope'"));
    assert!(bad(&["Level>=High"]).contains("does not apply to field 'Level'"));
    assert!(bad(&["Estimate=abc"]).contains("is not an integer"));
    assert!(bad(&["Level=Huge"]).contains("is not a value of enum"));
    assert!(bad(&["Level"]).contains("expected"));
    assert!(bad(&["Estimate:soon"]).contains("unknown ':soon'"));
    assert!(bad(&["Nope=1"]).starts_with("Columns[0].FieldFilters: "));

    let shared = |first: &[&str], second: &[&str]| {
        message(p.board(
            "Shared",
            vec![p.column("A", vec![p.open()], first), p.column("B", vec![p.open()], second)],
        ))
    };
    for (first, second) in [
        (&[][..], &[][..]),
        (&["Level=High"][..], &[][..]),
        (&["Level=High"][..], &["Level=High"][..]),
        (&["Level=High"][..], &["Estimate=1"][..]),
        (&["Estimate>=3"][..], &["Estimate>=2"][..]),
        (&["Level!=High"][..], &["Level!=Low"][..]),
    ] {
        let m = shared(first, second);
        assert!(m.contains("already used by column 'A'"), "{first:?} {second:?}: {m}");
        assert!(m.starts_with("Columns[1].StatusIds: status "));
    }
    let exclusive = |first: &[&str], second: &[&str]| {
        p.board(
            &format!("E{}", Uuid::new_v4().simple()),
            vec![p.column("A", vec![p.open()], first), p.column("B", vec![p.open()], second)],
        )
        .unwrap_or_else(|e| panic!("{first:?} {second:?}: {}", e.message()));
    };
    exclusive(&["Level=High"], &["Level=Low"]);
    exclusive(&["Level=High"], &["Level!=High"]);
    exclusive(&["Level:set"], &["Level:unset"]);
    exclusive(&["Level:attached"], &["Level:detached"]);
    exclusive(&["Estimate>=3"], &["Estimate<3"]);
    exclusive(&["Estimate>3"], &["Estimate<=3"]);
    exclusive(&["Estimate>5"], &["Estimate<3", "Level=High"]);
    exclusive(&["Estimate=3"], &["Estimate=4"]);
    exclusive(&["Estimate:unset"], &["Estimate>=0"]);
    exclusive(&["Level=High", "Estimate>=3"], &["Level=High", "Estimate<3"]);

    let board = p.board("Dup", vec![p.column("A", vec![p.open()], &["Level=High", "level=high"])]).unwrap();
    assert_eq!(board.columns[0].field_conditions.len(), 1);

    // Прочие проверки доски.
    let env = &p.env;
    let create = |name: &str, sets: Vec<Uuid>, columns: Vec<BoardColumnInput>| {
        env.ws.boards().create(
            &env.project,
            &CreateBoard {
                name: name.into(),
                status_set_ids: sets,
                columns,
            },
        )
    };
    assert_eq!(message(create("X", vec![], vec![])), "Board must include at least one status set");
    assert_eq!(message(create("X", vec![env.set.id], vec![])), "Board must have at least one column");
    assert_eq!(message(create(" ", vec![env.set.id], vec![p.column("A", vec![p.open()], &[])])), "Board name is required");
    let unknown = Uuid::new_v4();
    assert_eq!(
        message(create("X", vec![env.set.id], vec![p.column("A", vec![unknown], &[])])),
        format!("Columns[0].StatusIds (statuses of the board's status sets): not found in the project: {unknown}")
    );
    let bad_drop = BoardColumnInput {
        drop_statuses: Some(vec![(env.set.id, p.closed())]),
        ..p.column("A", vec![p.open()], &[])
    };
    assert_eq!(
        message(create("X", vec![env.set.id], vec![bad_drop])),
        format!("Columns[0].DropStatuses: status {} is not one of the column's statuses", p.closed())
    );
}

#[test]
fn a_field_used_by_a_column_is_not_deleted_or_retyped_but_may_be_renamed() {
    let p = P::new();
    let env = &p.env;
    let spare = env
        .ws
        .fields()
        .create(
            &env.project,
            &CreateField {
                name: "Spare".into(),
                field_type: FieldType::Int,
                multiple: None,
                enum_id: None,
            },
        )
        .unwrap();
    let board = p.board("Main", vec![p.column("Big", vec![p.open()], &["Spare>=3"])]).unwrap();

    let deleted = err(env.ws.fields().delete(&env.project, &spare.id, Some(&spare.version)));
    assert_eq!(deleted.conflict_code(), Some(ConflictCode::InUse));
    assert_eq!(deleted.message(), "Field 'Spare' is used by board 'Main' (column 'Big') and cannot be deleted");
    let change = |update: UpdateField| env.ws.fields().update(&env.project, &spare.id, &update);
    let retyped = err(change(UpdateField {
        version: Some(spare.version.clone()),
        field_type: Some(FieldType::String),
        ..UpdateField::default()
    }));
    assert_eq!(
        retyped.message(),
        "Field 'Spare' is used by board 'Main' (column 'Big') and cannot be changed to another type, enum or multiplicity (remove its conditions from the columns first)"
    );
    assert!(
        err(change(UpdateField {
            version: Some(spare.version.clone()),
            multiple: Some(true),
            ..UpdateField::default()
        }))
        .is_in_use()
    );
    let renamed = change(UpdateField {
        version: Some(spare.version.clone()),
        name: Some("Spare2".into()),
        ..UpdateField::default()
    })
    .unwrap()
    .unwrap();
    assert_eq!(p.reload(&board).columns[0].field_conditions[0].field_id, spare.id);

    let current = p.reload(&board);
    env.ws
        .boards()
        .update(
            &env.project,
            &board.id,
            &UpdateBoard {
                columns: Some(vec![BoardColumnInput {
                    id: Some(current.columns[0].id),
                    name: "Big".into(),
                    status_ids: vec![p.open()],
                    drop_statuses: None,
                    field_filters: Some(vec![]),
                }]),
                version: Some(current.version.clone()),
                ..UpdateBoard::default()
            },
        )
        .unwrap();
    assert!(env.ws.fields().delete(&env.project, &spare.id, Some(&renamed.version)).unwrap());
}

#[test]
fn a_removed_enum_value_used_by_a_column_needs_a_choice_clear_drops_the_condition_reassign_replaces_the_value() {
    let p = P::new();
    let env = &p.env;
    let board = p
        .board(
            "Main",
            vec![
                p.column("Critical", vec![p.open()], &["Level=High"]),
                p.column("Closed", vec![p.closed()], &["Level=High", "Estimate>=1"]),
            ],
        )
        .unwrap();
    let only_low = |removed: Option<RemovedEnumValues>, version: &str| UpdateFieldEnum {
        name: None,
        values: Some(vec![FieldEnumValueInput {
            id: Some(p.levels.values[0].id),
            name: "Low".into(),
        }]),
        version: Some(version.into()),
        removed,
    };
    let levels = env.ws.enums().get_by_id(&env.project, &p.levels.id).unwrap().unwrap();
    let refused = err(env.ws.enums().update(&env.project, &p.levels.id, &only_low(None, &levels.version)));
    assert_eq!(refused.conflict_code(), Some(ConflictCode::InUse));
    assert_eq!(
        refused.message(),
        "Value(s) 'High' of Enum 'LevelValues' are used in the field conditions of board 'Main' (column 'Critical'), board 'Main' (column 'Closed') and cannot be removed: choose to clear them (from the tasks, and the conditions from the columns) or to reassign them to another value of the enum"
    );
    assert_eq!(env.ws.enums().get_by_id(&env.project, &p.levels.id).unwrap().unwrap().values.len(), 2);

    let reassigned = env
        .ws
        .enums()
        .update(
            &env.project,
            &p.levels.id,
            &only_low(Some(RemovedEnumValues::reassign(p.levels.values[0].id)), &levels.version),
        )
        .unwrap()
        .unwrap();
    assert_eq!((reassigned.affected_columns, reassigned.affected_tasks), (2, 0));
    let after = p.reload(&board);
    assert_eq!(after.columns[0].field_conditions[0].value, Some(p.low()));
    assert_eq!(after.columns[1].field_conditions[0].value, Some(p.low()));
    assert_eq!(after.columns[1].field_conditions[1].operator, FieldOperator::GreaterOrEqual);
    let json = reassigned.to_json();
    assert_eq!((json["affectedTasks"].as_u64(), json["affectedColumns"].as_u64()), (Some(0), Some(2)));

    // «Убрать»: условие на значение исчезает из колонки, остальные условия остаются.
    let more = env
        .ws
        .enums()
        .update(
            &env.project,
            &p.levels.id,
            &UpdateFieldEnum {
                values: Some(vec![
                    FieldEnumValueInput {
                        id: Some(p.levels.values[0].id),
                        name: "Low".into(),
                    },
                    FieldEnumValueInput {
                        id: None,
                        name: "Extra".into(),
                    },
                ]),
                version: Some(reassigned.value.version.clone()),
                ..UpdateFieldEnum::default()
            },
        )
        .unwrap()
        .unwrap()
        .value;
    let board_now = p.reload(&board);
    env.ws
        .boards()
        .update(
            &env.project,
            &board.id,
            &UpdateBoard {
                columns: Some(p.keep_columns(&board_now, |name| {
                    Some(if name == "Closed" { vec!["Level=Extra".into(), "Estimate>=1".into()] } else { vec!["Level=Extra".into()] })
                })),
                version: Some(board_now.version.clone()),
                ..UpdateBoard::default()
            },
        )
        .unwrap();
    let cleared = env
        .ws
        .enums()
        .update(&env.project, &p.levels.id, &only_low(Some(RemovedEnumValues::clear()), &more.version))
        .unwrap()
        .unwrap();
    assert_eq!(cleared.affected_columns, 2);
    let after_clear = p.reload(&board);
    assert!(after_clear.columns[0].field_conditions.is_empty());
    assert_eq!(after_clear.columns[1].field_conditions.len(), 1);
    assert_eq!(after_clear.columns[1].field_conditions[0].field_id, p.estimate.id);
}

#[test]
fn a_removed_enum_value_is_refused_when_the_change_would_make_two_columns_show_the_same_tasks() {
    let p = P::new();
    let env = &p.env;
    let board = p
        .board(
            "Main",
            vec![p.column("Critical", vec![p.open()], &["Level=High"]), p.column("Rest", vec![p.open()], &["Level!=High"])],
        )
        .unwrap();
    let levels = env.ws.enums().get_by_id(&env.project, &p.levels.id).unwrap().unwrap();
    let only_low = |removed: RemovedEnumValues| UpdateFieldEnum {
        name: None,
        values: Some(vec![FieldEnumValueInput {
            id: Some(p.levels.values[0].id),
            name: "Low".into(),
        }]),
        version: Some(levels.version.clone()),
        removed: Some(removed),
    };
    let refused = err(env.ws.enums().update(&env.project, &p.levels.id, &only_low(RemovedEnumValues::clear())));
    assert_eq!(
        refused.message(),
        "Board 'Main': after the change columns 'Critical' and 'Rest' would show the same tasks (the same status and conditions that no longer exclude each other): change their conditions on the board first"
    );
    assert_eq!(env.ws.enums().get_by_id(&env.project, &p.levels.id).unwrap().unwrap().values.len(), 2);
    assert_eq!(p.reload(&board).columns[0].field_conditions[0].value, Some(p.high()));
    let reassigned = env
        .ws
        .enums()
        .update(&env.project, &p.levels.id, &only_low(RemovedEnumValues::reassign(p.levels.values[0].id)))
        .unwrap()
        .unwrap();
    assert_eq!(reassigned.affected_columns, 2);
}

#[test]
fn a_task_that_does_not_match_the_conditions_of_the_column_is_not_moved_into_it() {
    let p = P::new();
    let env = &p.env;
    let board = p
        .board(
            "Main",
            vec![p.column("Critical", vec![p.open()], &["Level=High", "Estimate>=3"]), p.column("Done", vec![p.closed()], &[])],
        )
        .unwrap();
    let (critical, done) = (board.columns[0].id, board.columns[1].id);
    let high = p.task("high", Some("High"), Some(5), Some(p.closed()));
    let low = p.task("low", Some("Low"), Some(5), Some(p.closed()));
    let small = p.task("small", Some("High"), Some(1), Some(p.closed()));
    let mv = |column: &Uuid, task: &TaskItem| {
        env.ws.boards().move_task(
            &env.project,
            &board.id,
            column,
            &MoveTask {
                task_id: task.id,
                version: Some(task.version.clone()),
            },
        )
    };

    let moved = mv(&critical, &high).unwrap().unwrap();
    assert_eq!(moved.status_id, p.open());
    assert_eq!(p.titles(&board, 0, &[]), vec!["high"]);

    let refused = message(mv(&critical, &low));
    assert_eq!(
        refused,
        "Task 'low' does not match the field conditions of column 'Critical' (Level=High, Estimate>=3): change the task's fields first, a move does not change them"
    );
    assert_eq!(env.get(&low.id).status_id, p.closed());
    assert!(message(mv(&critical, &small)).contains("does not match"));

    assert_eq!(mv(&done, &moved).unwrap().unwrap().status_id, p.closed());
    let fresh_low = env.get(&low.id);
    let fixed_low = env
        .ws
        .tasks()
        .update(
            &env.project,
            &low.id,
            &UpdateTask {
                version: Some(fresh_low.version),
                fields: Some(TaskFieldChanges::values(vec![(p.level.id, vec!["High"])])),
                ..UpdateTask::default()
            },
        )
        .unwrap()
        .unwrap();
    assert_eq!(mv(&critical, &fixed_low).unwrap().unwrap().status_id, p.open());
    assert_eq!(p.titles(&board, 0, &[]), vec!["low"]);

    // Нет доски, колонки или задачи — None; нет правила переноса — ошибка.
    assert!(mv(&Uuid::new_v4(), &moved).unwrap().is_none());
    assert!(env.ws.boards().move_task(&env.project, &Uuid::new_v4(), &critical, &MoveTask { task_id: moved.id, version: None }).unwrap().is_none());
    let no_rule = p
        .board(
            "NoRule",
            vec![BoardColumnInput {
                drop_statuses: Some(vec![]),
                ..p.column("Only", vec![p.open()], &[])
            }],
        )
        .unwrap();
    assert_eq!(
        message(env.ws.boards().move_task(&env.project, &no_rule.id, &no_rule.columns[0].id, &MoveTask { task_id: moved.id, version: None })),
        "Column 'Only' has no drop rule for tasks of type 'Bug'"
    );
}

#[test]
fn a_board_file_keeps_conditions_by_id_and_is_read_back_by_a_fresh_workspace() {
    let p = P::new();
    let env = &p.env;
    let board = p
        .board("Main", vec![p.column("Big", vec![p.open()], &["Estimate>=3", "Level:unset"]), p.column("Rest", vec![p.closed()], &[])])
        .unwrap();
    let folder = env.ws.directory().project(&env.project).boards();
    let file = std::fs::read_dir(&folder).unwrap().flatten().next().unwrap().path();
    let text = std::fs::read_to_string(&file).unwrap();
    assert!(text.starts_with("formatVersion: 9\n"));
    assert!(text.contains(&format!(
        "  fieldFilters:\n  - field: {}\n    op: greaterOrEqual\n    value: \"3\"\n  - field: {}\n    op: unset\n",
        p.estimate.id, p.level.id
    )));
    let read = tasker_services::Workspace::open(&env.dir).unwrap().boards().get_by_id(&env.project, &board.id).unwrap().unwrap();
    assert_eq!(read.columns[0].field_conditions, board.columns[0].field_conditions);
    assert!(read.columns[1].field_conditions.is_empty());

    // Файл с неизвестным оператором (записан более новым Tasker) не читается молча.
    std::fs::write(&file, text + &format!("  fieldFilters:\n  - field: {}\n    op: matches\n    value: x\n", Uuid::new_v4())).unwrap();
    let error = err(env.ws.boards().get_by_id(&env.project, &board.id));
    assert_eq!(error.conflict_code(), Some(ConflictCode::UnsupportedFormat));
    assert!(error.message().contains("unknown field filter operator 'matches': update Tasker"));
}
