//! Папки проектов `projects/<slug>-<id8>/` (файловая область): новый проект — в папке по названию, переименование переносит папку
//! одной операцией под блокировкой записи (параллельный читатель видит проект и его задачи всё время), прежнее имя по Guid читается и
//! переносится при записи `project.yaml`, две папки одного проекта читаются вместе и удаляются вместе.
mod common;

use common::*;
use tasker_files::project_folders;
use tasker_services::Workspace;
use tasker_services::project::UpdateProject;
use tasker_services::task::CreateTask;

fn folders(env: &Env) -> Vec<String> {
    let mut names: Vec<String> = std::fs::read_dir(env.ws.directory().projects())
        .unwrap()
        .flatten()
        .map(|x| x.file_name().to_string_lossy().into_owned())
        .collect();
    names.sort();
    names
}

fn id8(env: &Env) -> String {
    tasker_files::names::id_prefix(&env.project)
}

fn rename(env: &Env, name: &str) {
    let project = env.ws.projects().get_by_id(&env.project).unwrap().unwrap();
    env.ws
        .projects()
        .update(
            &env.project,
            &UpdateProject {
                name: Some(name.into()),
                version: Some(project.version),
            },
        )
        .unwrap()
        .unwrap();
}

#[test]
fn a_new_project_lives_in_a_folder_named_after_it_and_renaming_moves_the_folder() {
    let env = Env::new();
    let task = env.task("One");
    assert_eq!(folders(&env), vec![format!("test-{}", id8(&env))]);

    rename(&env, "Мой проект: v2");
    assert_eq!(folders(&env), vec![format!("мой-проект-v2-{}", id8(&env))]);
    let project = env.ws.projects().get_by_id(&env.project).unwrap().unwrap();
    assert_eq!(project.name, "Мой проект: v2");
    assert_eq!(env.ws.tasks().get_by_id(&env.project, &task.id).unwrap().unwrap().title, "One");
    assert_eq!(
        env.ws
            .projects()
            .get_range(tasker_core::tasks::Page::first(10))
            .unwrap()
            .total_count,
        1
    );
    // Новая задача — в новой папке, старой папки больше нет.
    env.task("Two");
    assert_eq!(folders(&env).len(), 1);
    assert!(
        env.ws
            .index()
            .problems(tasker_core::tasks::Page::first(10))
            .unwrap()
            .data
            .is_empty()
    );
}

#[test]
fn a_legacy_guid_folder_is_read_and_moved_on_the_next_write_of_the_project() {
    let env = Env::new();
    let task = env.task("One");
    let projects = env.ws.directory().projects();
    let legacy = tasker_core::ids::guid_d(&env.project);
    std::fs::rename(projects.join(format!("test-{}", id8(&env))), projects.join(&legacy)).unwrap();
    env.ws.index().sync().unwrap();

    assert_eq!(env.ws.projects().get_by_id(&env.project).unwrap().unwrap().name, "Test");
    assert_eq!(env.ws.tasks().get_by_id(&env.project, &task.id).unwrap().unwrap().title, "One");
    rename(&env, "Test");
    assert_eq!(folders(&env), vec![format!("test-{}", id8(&env))]);
    assert_eq!(env.ws.tasks().get_all(&env.project, None).unwrap().len(), 1);
}

#[test]
fn two_folders_of_one_project_are_read_together_reported_merged_and_deleted_together() {
    let env = Env::new();
    let first = env.task("One");
    let projects = env.ws.directory().projects();
    let main = format!("test-{}", id8(&env));
    // Вторая папка после слияния git: прежнее имя проекта, только задача.
    let second = format!("old-name-{}", id8(&env));
    let tasks = projects.join(&main).join("tasks");
    let file = std::fs::read_dir(&tasks).unwrap().flatten().next().unwrap().path();
    std::fs::create_dir_all(projects.join(&second).join("tasks")).unwrap();
    std::fs::rename(&file, projects.join(&second).join("tasks").join(file.file_name().unwrap())).unwrap();
    env.ws.index().sync().unwrap();

    assert_eq!(env.ws.tasks().get_by_id(&env.project, &first.id).unwrap().unwrap().title, "One");
    assert_eq!(env.ws.tasks().get_all(&env.project, None).unwrap().len(), 1);
    let problems = env.ws.index().problems(tasker_core::tasks::Page::first(10)).unwrap();
    assert_eq!(problems.total_count, 1);
    assert_eq!(problems.data[0].path, format!("projects/{second}"));
    assert_eq!(problems.data[0].error, project_folders::second_folder_reason(&main));
    // Новая задача — в основную папку.
    env.task("Two");
    assert_eq!(std::fs::read_dir(&tasks).unwrap().count(), 1);

    let project = env.ws.projects().get_by_id(&env.project).unwrap().unwrap();
    assert!(env.ws.projects().delete(&env.project, Some(&project.version)).unwrap());
    assert!(folders(&env).is_empty());
    assert!(
        env.ws
            .index()
            .problems(tasker_core::tasks::Page::first(10))
            .unwrap()
            .data
            .is_empty()
    );
}

#[test]
fn a_reader_in_another_process_sees_the_project_while_it_is_renamed_again_and_again() {
    let env = Env::new();
    let task = env.task("One");
    let writer = Workspace::open(&env.dir).unwrap();
    let project = env.project;
    let stop = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
    let reader = {
        let ws = Workspace::open(&env.dir).unwrap();
        let stop = stop.clone();
        std::thread::spawn(move || {
            let mut reads = 0;
            while !stop.load(std::sync::atomic::Ordering::SeqCst) {
                assert!(ws.projects().get_by_id(&project).unwrap().is_some(), "the project vanished");
                assert!(ws.tasks().get_by_id(&project, &task.id).unwrap().is_some(), "the task vanished");
                reads += 1;
            }
            reads
        })
    };
    for i in 0..30 {
        let current = writer.projects().get_by_id(&project).unwrap().unwrap();
        writer
            .projects()
            .update(
                &project,
                &UpdateProject {
                    name: Some(format!("Name {i}")),
                    version: Some(current.version),
                },
            )
            .unwrap()
            .unwrap();
        // Запись задачи в проект, который как раз переезжает, — в его папку, а не в прежнюю.
        writer
            .tasks()
            .create(&project, &CreateTask::new(&format!("Task {i}"), env.task_type.id))
            .unwrap();
    }
    stop.store(true, std::sync::atomic::Ordering::SeqCst);
    assert!(reader.join().unwrap() > 0);
    let names = folders(&env);
    assert_eq!(names, vec![format!("name-29-{}", tasker_files::names::id_prefix(&project))]);
    assert_eq!(env.ws.tasks().get_all(&project, None).unwrap().len(), 31);
}
