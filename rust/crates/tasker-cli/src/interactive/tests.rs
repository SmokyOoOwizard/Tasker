use super::app::{App, Effect};
use super::step::{Check, Key, StepState};
use crate::session::Session;
use std::path::{Path, PathBuf};
use std::time::Duration;

fn copy_dir(from: &Path, to: &Path) {
    std::fs::create_dir_all(to).unwrap();
    for entry in std::fs::read_dir(from).unwrap() {
        let entry = entry.unwrap();
        let target = to.join(entry.file_name());
        if entry.file_type().unwrap().is_dir() {
            copy_dir(&entry.path(), &target);
        } else {
            std::fs::copy(entry.path(), target).unwrap();
        }
    }
}

fn golden_copy() -> PathBuf {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../target/tmp")
        .join(uuid::Uuid::new_v4().simple().to_string());
    let workspace = root.join("golden");
    copy_dir(
        &Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../tests/golden/workspace"),
        &workspace,
    );
    workspace.canonicalize().unwrap()
}

fn open(workspace: &Path) -> App {
    let session = Session::open(Some(workspace.to_str().unwrap()), None, false).unwrap();
    let project = session.find_project("Golden").unwrap();
    App::new(session, Some(project), Duration::from_secs(10))
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct Snapshot {
    crumb: String,
    items: Vec<String>,
    value: String,
}

fn snapshot(app: &App) -> Snapshot {
    let crumb = app.breadcrumb();
    let (items, value) = match app.state() {
        Some(StepState::List(list)) => (
            list.items.iter().map(|i| i.key.clone()).collect(),
            if list.multi {
                list.picked.join(",")
            } else {
                list.current().map(|i| i.key.clone()).unwrap_or_default()
            },
        ),
        Some(StepState::Text(text)) => (Vec::new(), text.value.clone()),
        Some(StepState::Editor(editor)) => (Vec::new(), editor.text.clone()),
        Some(StepState::Confirm(_)) | None => (Vec::new(), String::new()),
    };
    Snapshot { crumb, items, value }
}

fn valid_text(check: Check) -> &'static str {
    match check {
        Check::Int => "7",
        Check::Float => "1.5",
        Check::Date => "2026-01-02",
        Check::Color => "#123456",
        Check::Prefix => "WLK",
        Check::Any | Check::Required => "walk",
    }
}

fn is_branching(list: &super::step::ListStep) -> bool {
    !list.multi && list.items.len() <= 12 && list.items.iter().all(|i| i.version.is_none())
}

fn prepare(app: &mut App, choice: usize) {
    match app.state_mut().expect("a step") {
        StepState::List(list) => {
            if list.multi {
                if list.picked.is_empty() && !list.allow_empty {
                    app.handle(Key::Char(' '));
                }
            } else {
                list.cursor = choice;
            }
        }
        StepState::Text(text) => {
            if text.value.is_empty() && !text.allow_empty {
                for c in valid_text(text.check).chars() {
                    app.handle(Key::Char(c));
                }
            }
        }
        StepState::Editor(_) | StepState::Confirm(_) => {}
    }
}

struct Walk {
    trail: Vec<Snapshot>,
    branches: Vec<(String, usize)>,
    done: bool,
}

fn walk(app: &mut App, path: &[usize]) -> Walk {
    let mut trail = Vec::new();
    let mut branches = Vec::new();
    for _ in 0..60 {
        if app.result.is_some() {
            return Walk {
                trail,
                branches,
                done: false,
            };
        }
        if !trail.is_empty() && app.breadcrumb() == "What to do" {
            return Walk {
                trail,
                branches,
                done: true,
            };
        }
        let mut choice = 0;
        if let Some(StepState::List(list)) = app.state()
            && is_branching(list)
        {
            choice = path.get(branches.len()).copied().unwrap_or(list.items.len() - 1);
            branches.push((app.breadcrumb(), list.items.len()));
        }
        prepare(app, choice);
        trail.push(snapshot(app));
        match app.handle(Key::Enter) {
            Effect::Run(_) | Effect::Quit => {
                return Walk {
                    trail,
                    branches,
                    done: false,
                };
            }
            Effect::None | Effect::OpenEditor(_) => {}
        }
    }
    panic!("the walk {path:?} did not end in 60 steps: {}", app.breadcrumb());
}

fn back_to_menu(app: &mut App, forward: &[Snapshot], path: &[usize]) {
    let mut expected: Vec<Snapshot> = forward.to_vec();
    while let Some(step) = expected.pop() {
        app.handle(Key::Esc);
        assert!(
            app.result.is_none(),
            "{path:?}: a result appeared while going back from {}",
            step.crumb
        );
        let now = snapshot(app);
        assert_eq!(now.crumb, step.crumb, "{path:?}: Esc skipped or repeated a step");
        assert_eq!(
            now.items, step.items,
            "{path:?}: the list at {} shows other items after Esc",
            step.crumb
        );
        assert_eq!(now.value, step.value, "{path:?}: the value at {} was lost after Esc", step.crumb);
    }
    assert_eq!(
        app.breadcrumb(),
        "What to do",
        "{path:?}: Esc from the first step does not reach the menu"
    );
    assert!(app.in_menu());
}

fn menu_entries(app: &App) -> Vec<String> {
    match app.state() {
        Some(StepState::List(list)) => list.items.iter().map(|i| i.key.clone()).collect(),
        _ => Vec::new(),
    }
}

#[test]
fn every_flow_walks_back_step_by_step_with_the_same_steps_items_and_values() {
    let workspace = golden_copy();
    let mut app = open(&workspace);
    let entries = menu_entries(&app);
    let actions: Vec<usize> = entries.iter().enumerate().filter(|(_, k)| *k != "quit").map(|(i, _)| i).collect();
    let mut pending: Vec<Vec<usize>> = actions.iter().map(|&a| vec![a]).collect();
    let mut walked = 0;
    let mut explored: std::collections::HashSet<String> = std::collections::HashSet::new();
    while let Some(path) = pending.pop() {
        assert_eq!(app.breadcrumb(), "What to do");
        let walk = walk(&mut app, &path);
        walked += 1;
        assert!(walked < 2000, "too many flows");
        if walk.done {
            app.handle(Key::Esc);
            assert_eq!(app.breadcrumb(), "What to do", "{path:?}: Esc in the menu must stay in the menu");
        } else {
            app.handle(Key::Esc);
            let mut forward = walk.trail.clone();
            let landed = snapshot(&app);
            let last = forward.pop().expect("at least the menu step");
            assert_eq!(
                landed.crumb, last.crumb,
                "{path:?}: after the end Esc must show the last step again"
            );
            assert_eq!(landed.value, last.value, "{path:?}: the last step lost its value");
            back_to_menu(&mut app, &forward, &path);
        }
        for (at, (crumb, size)) in walk.branches.iter().enumerate().skip(path.len()) {
            if !explored.insert(crumb.clone()) {
                continue;
            }
            for choice in 0..size - 1 {
                let mut next = path.clone();
                next.extend(walk.branches[path.len()..at].iter().map(|(_, s)| s - 1));
                next.push(choice);
                pending.push(next);
            }
        }
        for (at, (_, size)) in walk.branches.iter().enumerate().take(path.len()) {
            assert!(path[at] < *size, "{path:?}: the branch {at} has only {size} items");
        }
    }
    eprintln!("walked {walked} flows");
    assert!(walked > 100, "only {walked} flows were walked");
    std::fs::remove_dir_all(workspace.parent().unwrap()).unwrap();
}

fn execute(app: &mut App, effect: Effect) {
    let Effect::Run(argv) = effect else {
        panic!("expected a command to run")
    };
    let (mut out, mut err) = (Vec::new(), Vec::new());
    let code = crate::app::run(&argv, &mut out, &mut err);
    app.set_result(&String::from_utf8_lossy(&out), &String::from_utf8_lossy(&err), code);
}

fn choose(app: &mut App, key: &str) -> Effect {
    let Some(StepState::List(list)) = app.state_mut() else {
        panic!("expected a list at {}", app.breadcrumb())
    };
    let at = list
        .items
        .iter()
        .position(|i| i.key == key)
        .unwrap_or_else(|| panic!("no item {key} in {:?}", list.items.iter().map(|i| &i.key).collect::<Vec<_>>()));
    list.cursor = at;
    app.handle(Key::Enter)
}

fn type_in(app: &mut App, text: &str) {
    for c in text.chars() {
        app.handle(Key::Char(c));
    }
}

fn crumbs_back_to_menu(app: &mut App) -> Vec<String> {
    let mut seen = Vec::new();
    for _ in 0..20 {
        app.handle(Key::Esc);
        seen.push(app.breadcrumb());
        if app.in_menu() && app.breadcrumb() == "What to do" {
            return seen;
        }
    }
    panic!("Esc never reached the menu: {seen:?}");
}

#[test]
fn after_a_failed_command_esc_walks_back_through_each_step_once() {
    let workspace = golden_copy();
    let mut app = open(&workspace);
    choose(&mut app, "create");
    choose(&mut app, "series");
    assert_eq!(app.breadcrumb(), "Create › Series › Series name");
    type_in(&mut app, "Second golden");
    app.handle(Key::Enter);
    assert_eq!(app.breadcrumb(), "Create › Series › Prefix (Latin letters and digits, e.g. TSK)");
    type_in(&mut app, "GLD");
    let effect = app.handle(Key::Enter);
    execute(&mut app, effect);
    assert_eq!(app.result.as_ref().map(|r| r.kind), Some(super::app::ResultKind::Failed));
    app.handle(Key::Esc);
    assert_eq!(app.breadcrumb(), "Create › Series › Prefix (Latin letters and digits, e.g. TSK)");
    assert_eq!(snapshot(&app).value, "GLD");
    assert_eq!(
        crumbs_back_to_menu(&mut app),
        ["Create › Series › Series name", "Create › Create what", "What to do"]
    );
    assert_eq!(snapshot(&app).value, "create");
    std::fs::remove_dir_all(workspace.parent().unwrap()).unwrap();
}

#[test]
fn a_flow_that_cannot_start_returns_to_the_kind_list_once_not_twice() {
    let workspace = golden_copy();
    let code = crate::app::run(
        &[
            "-w".to_string(),
            workspace.to_string_lossy().into_owned(),
            "project".into(),
            "create".into(),
            "Empty".into(),
        ],
        &mut Vec::new(),
        &mut Vec::new(),
    );
    assert_eq!(code, 0);
    let session = Session::open(Some(workspace.to_str().unwrap()), None, false).unwrap();
    let project = session.find_project("Empty").unwrap();
    let mut app = App::new(session, Some(project), Duration::from_secs(10));
    choose(&mut app, "change");
    choose(&mut app, "status");
    assert_eq!(app.result.as_ref().map(|r| r.kind), Some(super::app::ResultKind::Failed));
    assert!(app.result.as_ref().unwrap().text.contains("Nothing to choose from"));
    app.handle(Key::Esc);
    assert_eq!(app.breadcrumb(), "Change › Change what");
    assert_eq!(snapshot(&app).value, "status");
    assert_eq!(crumbs_back_to_menu(&mut app), ["What to do"]);
    std::fs::remove_dir_all(workspace.parent().unwrap()).unwrap();
}

#[test]
fn esc_from_a_view_shows_every_earlier_step_again_with_its_value_and_f_reruns_with_one_change() {
    let workspace = golden_copy();
    let mut app = open(&workspace);
    choose(&mut app, "browse");
    choose(&mut app, "task");
    choose(&mut app, "list");
    assert_eq!(app.breadcrumb(), "Browse › Task › Only these task types (optional)");
    app.handle(Key::Enter);
    app.handle(Key::Enter);
    app.handle(Key::Enter);
    assert_eq!(app.breadcrumb(), "Browse › Task › Field conditions");
    choose(&mut app, "done");
    assert_eq!(app.breadcrumb(), "Browse › Task › Sort");
    choose(&mut app, "add");
    assert_eq!(app.breadcrumb(), "Browse › Task › Sort key");
    choose(&mut app, "created");
    assert_eq!(app.breadcrumb(), "Browse › Task › Direction for created");
    choose(&mut app, "desc");
    assert_eq!(app.breadcrumb(), "Browse › Task › Sort");
    assert_eq!(snapshot(&app).items, ["drop0", "add", "done"]);
    choose(&mut app, "done");
    let effect = choose(&mut app, "flat");
    let Effect::Run(argv) = &effect else {
        panic!("expected the list to run")
    };
    assert!(argv.windows(2).any(|w| w[0] == "--sort" && w[1] == "-created"), "{argv:?}");
    execute(&mut app, effect);
    assert_eq!(app.result.as_ref().map(|r| r.kind), Some(super::app::ResultKind::View));

    app.handle(Key::Esc);
    assert_eq!(app.breadcrumb(), "Browse › Task › Shape of the list");
    assert_eq!(snapshot(&app).value, "flat");
    let expected_back = [
        ("Browse › Task › Sort", "done"),
        ("Browse › Task › Direction for created", "desc"),
        ("Browse › Task › Sort key", "created"),
        ("Browse › Task › Sort", "add"),
        ("Browse › Task › Field conditions", "done"),
    ];
    for (crumb, value) in expected_back {
        app.handle(Key::Esc);
        assert_eq!(app.breadcrumb(), crumb);
        assert_eq!(snapshot(&app).value, value);
    }
    choose(&mut app, "add");
    assert_eq!(app.breadcrumb(), "Browse › Task › Field");
    let Some(StepState::List(list)) = app.state() else { panic!() };
    let priority = list
        .items
        .iter()
        .find(|i| i.label == "Priority")
        .expect("the Priority field")
        .key
        .clone();
    assert!(list.items.iter().any(|i| i.detail.starts_with("enum")), "{:?}", list.items);
    choose(&mut app, &priority);
    assert_eq!(app.breadcrumb(), "Browse › Task › Comparison for Priority");
    assert_eq!(snapshot(&app).items, ["=", "!=", ":set", ":unset", ":attached", ":detached"]);
    choose(&mut app, "=");
    assert_eq!(app.breadcrumb(), "Browse › Task › Value of Priority");
    choose(&mut app, "High");
    assert_eq!(app.breadcrumb(), "Browse › Task › Field conditions");
    assert_eq!(snapshot(&app).items, ["drop0", "add", "done"]);
    choose(&mut app, "done");
    let expected_forward = [
        ("Browse › Task › Sort", "add"),
        ("Browse › Task › Sort key", "created"),
        ("Browse › Task › Direction for created", "desc"),
        ("Browse › Task › Sort", "done"),
        ("Browse › Task › Shape of the list", "flat"),
    ];
    for (crumb, value) in expected_forward {
        assert_eq!(app.breadcrumb(), crumb);
        assert_eq!(snapshot(&app).value, value);
        if crumb.ends_with("Shape of the list") {
            break;
        }
        assert!(matches!(app.handle(Key::Enter), Effect::None));
    }
    let effect = app.handle(Key::Enter);
    let Effect::Run(argv) = &effect else {
        panic!("expected the list to run")
    };
    assert!(argv.windows(2).any(|w| w[0] == "--field" && w[1] == "Priority=High") && argv.contains(&"--flat".to_string()));
    execute(&mut app, effect);

    app.handle(Key::Char('f'));
    assert_eq!(app.breadcrumb(), "Browse › Task › Which setting to change");
    let Some(StepState::List(list)) = app.state() else { panic!() };
    let titles: Vec<String> = list.items.iter().map(|i| format!("{}: {}", i.label, i.detail)).collect();
    assert_eq!(
        titles,
        [
            "Tasks: what to show: List with filters",
            "Only these task types (optional): (none)",
            "Only these statuses (optional): (none)",
            "Only these series (optional): (none)",
            "Field conditions: + Add a condition",
            "Field: Priority",
            "Comparison for Priority: equals",
            "Value of Priority: High",
            "Field conditions: Done",
            "Sort: + Add a key",
            "Sort key: created",
            "Direction for created: Descending",
            "Sort: Done",
            "Shape of the list: Flat"
        ]
    );
    choose(&mut app, "13");
    assert_eq!(app.breadcrumb(), "Browse › Task › Shape of the list");
    let effect = choose(&mut app, "tree");
    let Effect::Run(argv) = &effect else {
        panic!("expected the list to run again")
    };
    assert!(!argv.contains(&"--flat".to_string()) && argv.contains(&"Priority=High".to_string()));
    execute(&mut app, effect);

    app.handle(Key::Char('f'));
    choose(&mut app, "2");
    assert_eq!(app.breadcrumb(), "Browse › Task › Only these statuses (optional)");
    app.handle(Key::Char(' '));
    let effect = app.handle(Key::Enter);
    let Effect::Run(argv) = &effect else {
        panic!("the later steps must be replayed after f")
    };
    assert!(argv.contains(&"--status".to_string()) && argv.contains(&"-created".to_string()));
    execute(&mut app, effect);

    app.handle(Key::Char('q'));
    assert_eq!(app.breadcrumb(), "What to do");
    std::fs::remove_dir_all(workspace.parent().unwrap()).unwrap();
}
