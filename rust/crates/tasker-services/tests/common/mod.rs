//! Общее для интеграционных тестов: временная область в `target/tmp` с проектом, статусами, набором и типом — как `SeriesEnv` и
//! соседние окружения в тестах .NET.
#![allow(dead_code)]
use std::path::PathBuf;
use std::sync::Arc;
use tasker_core::model::{Status, StatusSet, TaskType};
use tasker_services::clock::FakeClock;
use tasker_services::project::CreateProject;
use tasker_services::status::CreateStatus;
use tasker_services::status_set::CreateStatusSet;
use tasker_services::task::CreateTask;
use tasker_services::task_type::CreateTaskType;
use tasker_services::{Error, Workspace};
use uuid::Uuid;

pub fn temp_dir() -> PathBuf {
    let dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../target/tmp")
        .join(Uuid::new_v4().simple().to_string());
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

pub struct Env {
    pub dir: PathBuf,
    pub ws: Workspace,
    pub clock: Arc<FakeClock>,
    pub project: Uuid,
    pub backlog: Status,
    pub done: Status,
    pub set: StatusSet,
    pub task_type: TaskType,
}

impl Env {
    pub fn new() -> Env {
        let dir = temp_dir();
        let clock = Arc::new(FakeClock::at("2026-01-01T00:00:00+00:00"));
        let ws = Workspace::open(&dir).unwrap().with_clock(clock.clone());
        let project = ws.projects().create(&CreateProject { name: "Test".into() }).unwrap().id;
        let backlog = ws.statuses().create(&project, &status("Backlog")).unwrap();
        let done = ws.statuses().create(&project, &status("Done")).unwrap();
        let set = ws
            .status_sets()
            .create(
                &project,
                &CreateStatusSet {
                    name: "Main".into(),
                    status_ids: vec![backlog.id, done.id],
                },
            )
            .unwrap();
        let task_type = ws
            .task_types()
            .create(
                &project,
                &CreateTaskType {
                    name: "Task".into(),
                    status_set_id: set.id,
                    fields: None,
                    description: None,
                },
            )
            .unwrap();
        Env {
            dir,
            ws,
            clock,
            project,
            backlog,
            done,
            set,
            task_type,
        }
    }

    /// Та же область от имени другого держателя блокировок.
    pub fn as_editor(&self, key: &str, name: &str) -> Workspace {
        self.ws.clone().with_editor(tasker_core::locks::EditHolder::new(key, name))
    }

    pub fn task(&self, title: &str) -> tasker_core::model::TaskItem {
        self.ws
            .tasks()
            .create(&self.project, &CreateTask::new(title, self.task_type.id))
            .unwrap()
    }

    pub fn type_with(&self, name: &str, fields: Vec<tasker_core::model::TaskTypeField>) -> TaskType {
        self.ws
            .task_types()
            .create(
                &self.project,
                &CreateTaskType {
                    name: name.into(),
                    status_set_id: self.set.id,
                    fields: Some(fields),
                    description: None,
                },
            )
            .unwrap()
    }
}

impl Drop for Env {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.dir);
    }
}

pub fn status(name: &str) -> CreateStatus {
    CreateStatus {
        name: name.into(),
        color: "#1E90FF".into(),
        description: None,
    }
}

/// Текст ошибки.
pub fn message<T: std::fmt::Debug>(result: Result<T, Error>) -> String {
    match result {
        Ok(v) => panic!("expected an error, got {v:?}"),
        Err(e) => e.message(),
    }
}

pub fn err<T: std::fmt::Debug>(result: Result<T, Error>) -> Error {
    match result {
        Ok(v) => panic!("expected an error, got {v:?}"),
        Err(e) => e,
    }
}
