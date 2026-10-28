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
        Some(StepState::Form(form)) => (
            form.sections.iter().map(|s| s.key.clone()).collect(),
            form.sections
                .iter()
                .map(|s| format!("{}={}", s.key, s.values.join("|")))
                .collect::<Vec<_>>()
                .join(";"),
        ),
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
        StepState::Form(form) => form.cursor = form.sections.len(),
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

fn form(app: &mut App) -> &mut super::step::FormStep {
    match app.state_mut() {
        Some(StepState::Form(form)) => form,
        _ => panic!("expected the settings form"),
    }
}

fn open_section(app: &mut App, key: &str) {
    let form = form(app);
    form.cursor = form.sections.iter().position(|s| s.key == key).unwrap();
    app.handle(Key::Enter);
}

fn popup(app: &mut App) -> (String, Vec<String>) {
    let form = form(app);
    let layer = form.popup.as_ref().expect("an open popup").layer();
    let items = match &layer.inner {
        super::step::Inner::List(list) => list.items.iter().map(|i| i.key.clone()).collect(),
        super::step::Inner::Text(text) => vec![text.value.clone()],
    };
    (layer.title.clone(), items)
}

fn pick_in_popup(app: &mut App, key: &str) {
    let form = form(app);
    let layer = form.popup.as_mut().expect("an open popup").layer_mut();
    let super::step::Inner::List(list) = &mut layer.inner else {
        panic!("expected a list in the popup")
    };
    list.cursor = list
        .items
        .iter()
        .position(|i| i.key == key)
        .unwrap_or_else(|| panic!("no item {key} in {:?}", list.items.iter().map(|i| &i.key).collect::<Vec<_>>()));
    app.handle(Key::Enter);
}

fn section_values(app: &mut App, key: &str) -> Vec<String> {
    form(app).sections.iter().find(|s| s.key == key).unwrap().values.clone()
}

#[test]
fn the_list_settings_form_edits_each_section_in_a_popup_and_esc_in_a_popup_only_closes_it() {
    let workspace = golden_copy();
    let mut app = open(&workspace);
    choose(&mut app, "browse");
    choose(&mut app, "task");
    choose(&mut app, "list");
    assert_eq!(app.breadcrumb(), "Browse › Task › List settings");
    assert_eq!(snapshot(&app).items, ["type", "status", "series", "field", "sort", "shape"]);
    assert_eq!(snapshot(&app).value, "type=;status=;series=;field=;sort=;shape=tree");

    open_section(&mut app, "status");
    let (title, items) = popup(&mut app);
    assert_eq!(title, "Statuses");
    assert!(items.len() >= 4, "{items:?}");
    app.handle(Key::Char(' '));
    app.handle(Key::Esc);
    assert!(form(&mut app).popup.is_none());
    assert_eq!(section_values(&mut app, "status"), Vec::<String>::new());
    open_section(&mut app, "status");
    app.handle(Key::Char(' '));
    app.handle(Key::Enter);
    assert_eq!(section_values(&mut app, "status").len(), 1);

    open_section(&mut app, "field");
    assert_eq!(
        popup(&mut app),
        ("Field conditions".to_string(), vec!["add".to_string(), "done".to_string()])
    );
    pick_in_popup(&mut app, "add");
    let (title, items) = popup(&mut app);
    assert_eq!(title, "Field");
    assert!(
        items.contains(&"Priority".to_string()) && items.contains(&"Estimate".to_string()),
        "{items:?}"
    );
    pick_in_popup(&mut app, "Priority");
    assert_eq!(
        popup(&mut app),
        (
            "Comparison for Priority".to_string(),
            ["=", "!=", ":set", ":unset", ":attached", ":detached"].map(String::from).to_vec()
        )
    );
    pick_in_popup(&mut app, "=");
    let (title, items) = popup(&mut app);
    assert_eq!(title, "Value of Priority");
    assert!(items.contains(&"High".to_string()), "{items:?}");
    app.handle(Key::Esc);
    assert_eq!(popup(&mut app).0, "Comparison for Priority");
    pick_in_popup(&mut app, "!=");
    pick_in_popup(&mut app, "High");
    assert_eq!(popup(&mut app).1, ["drop0", "add", "done"]);
    assert_eq!(section_values(&mut app, "field"), ["Priority!=High"]);
    pick_in_popup(&mut app, "add");
    pick_in_popup(&mut app, "Estimate");
    let (_, items) = popup(&mut app);
    assert!(items.contains(&">".to_string()), "{items:?}");
    pick_in_popup(&mut app, ">");
    assert_eq!(popup(&mut app), ("Value of Estimate".to_string(), vec![String::new()]));
    type_in(&mut app, "x");
    app.handle(Key::Enter);
    assert_eq!(popup(&mut app).0, "Value of Estimate");
    app.handle(Key::Backspace);
    type_in(&mut app, "3");
    app.handle(Key::Enter);
    assert_eq!(section_values(&mut app, "field"), ["Priority!=High", "Estimate>3"]);
    pick_in_popup(&mut app, "drop0");
    assert_eq!(section_values(&mut app, "field"), ["Estimate>3"]);
    pick_in_popup(&mut app, "done");
    assert!(form(&mut app).popup.is_none());

    open_section(&mut app, "sort");
    pick_in_popup(&mut app, "add");
    pick_in_popup(&mut app, "created");
    assert_eq!(
        popup(&mut app),
        ("Direction for created".to_string(), vec![String::new(), "-".to_string()])
    );
    pick_in_popup(&mut app, "-");
    pick_in_popup(&mut app, "add");
    pick_in_popup(&mut app, "title");
    pick_in_popup(&mut app, "");
    assert_eq!(section_values(&mut app, "sort"), ["-created", "title"]);
    app.handle(Key::Esc);
    assert!(form(&mut app).popup.is_none());

    open_section(&mut app, "shape");
    pick_in_popup(&mut app, "flat");
    assert_eq!(section_values(&mut app, "shape"), ["flat"]);
    form(&mut app).cursor = 5;
    app.handle(Key::Backspace);
    assert_eq!(section_values(&mut app, "shape"), Vec::<String>::new());
    open_section(&mut app, "shape");
    pick_in_popup(&mut app, "flat");

    app.handle(Key::End);
    let effect = app.handle(Key::Enter);
    let Effect::Run(argv) = &effect else {
        panic!("expected the list to run")
    };
    let tail: Vec<&str> = argv.iter().map(String::as_str).skip_while(|a| *a != "task").collect();
    assert_eq!(tail[..5], ["task", "list", "--all", "--status", tail[4]],);
    assert_eq!(tail[5..], ["--field", "Estimate>3", "--flat", "--sort", "-created,title"]);
    execute(&mut app, effect);
    assert_eq!(app.result.as_ref().map(|r| r.kind), Some(super::app::ResultKind::View));
    std::fs::remove_dir_all(workspace.parent().unwrap()).unwrap();
}

#[test]
fn the_card_of_a_task_is_chosen_from_a_list_narrowed_by_the_same_filters() {
    let workspace = golden_copy();
    let mut app = open(&workspace);
    choose(&mut app, "browse");
    choose(&mut app, "task");
    choose(&mut app, "card");
    assert_eq!(app.breadcrumb(), "Browse › Task › Task filters");
    assert_eq!(snapshot(&app).items, ["type", "status", "series", "field", "sort"]);
    open_section(&mut app, "status");
    {
        let layer = form(&mut app).popup.as_mut().unwrap().layer_mut();
        let super::step::Inner::List(list) = &mut layer.inner else {
            panic!()
        };
        list.cursor = list
            .items
            .iter()
            .position(|i| i.label == "In Progress")
            .expect("the In Progress status");
    }
    app.handle(Key::Char(' '));
    app.handle(Key::Enter);
    open_section(&mut app, "field");
    pick_in_popup(&mut app, "add");
    pick_in_popup(&mut app, "Priority");
    pick_in_popup(&mut app, "=");
    pick_in_popup(&mut app, "High");
    pick_in_popup(&mut app, "done");
    app.handle(Key::End);
    app.handle(Key::Enter);
    assert_eq!(
        app.breadcrumb(),
        "Browse › Task › Task",
        "{:?}",
        app.result.as_ref().map(|r| &r.text)
    );
    let Some(StepState::List(list)) = app.state() else { panic!() };
    let labels: Vec<String> = list.items.iter().map(|i| format!("{}  {}", i.label, i.detail)).collect();
    assert_eq!(labels, ["BUG-1  Bug with all fields (renamed)  In Progress"]);
    let first = list.items[0].key.clone();
    let effect = choose(&mut app, &first);
    let Effect::Run(argv) = &effect else {
        panic!("expected the card to open")
    };
    assert!(argv.contains(&"get".to_string()) && argv.contains(&first), "{argv:?}");
    execute(&mut app, effect);
    assert_eq!(app.result.as_ref().map(|r| r.kind), Some(super::app::ResultKind::View));
    app.handle(Key::Esc);
    assert_eq!(app.breadcrumb(), "Browse › Task › Task");
    app.handle(Key::Esc);
    assert_eq!(app.breadcrumb(), "Browse › Task › Task filters");
    assert!(snapshot(&app).value.contains("field=Priority=High"));
    std::fs::remove_dir_all(workspace.parent().unwrap()).unwrap();
}

#[test]
fn esc_from_a_view_shows_the_settings_again_with_their_values_and_f_reruns_with_one_change() {
    let workspace = golden_copy();
    let mut app = open(&workspace);
    choose(&mut app, "browse");
    choose(&mut app, "task");
    choose(&mut app, "list");
    assert_eq!(app.breadcrumb(), "Browse › Task › List settings");
    open_section(&mut app, "sort");
    pick_in_popup(&mut app, "add");
    pick_in_popup(&mut app, "created");
    pick_in_popup(&mut app, "-");
    pick_in_popup(&mut app, "done");
    open_section(&mut app, "shape");
    pick_in_popup(&mut app, "flat");
    app.handle(Key::End);
    let effect = app.handle(Key::Enter);
    let Effect::Run(argv) = &effect else {
        panic!("expected the list to run")
    };
    assert!(argv.windows(2).any(|w| w[0] == "--sort" && w[1] == "-created"), "{argv:?}");
    execute(&mut app, effect);
    assert_eq!(app.result.as_ref().map(|r| r.kind), Some(super::app::ResultKind::View));

    app.handle(Key::Esc);
    assert_eq!(app.breadcrumb(), "Browse › Task › List settings");
    assert_eq!(snapshot(&app).value, "type=;status=;series=;field=;sort=-created;shape=flat");
    assert!(form(&mut app).popup.is_none());
    open_section(&mut app, "field");
    pick_in_popup(&mut app, "add");
    pick_in_popup(&mut app, "Priority");
    pick_in_popup(&mut app, "=");
    pick_in_popup(&mut app, "High");
    pick_in_popup(&mut app, "done");
    app.handle(Key::End);
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
            "List settings: Field conditions: Priority=High; Sort: -created; Shape: Flat",
        ]
    );
    choose(&mut app, "1");
    assert_eq!(app.breadcrumb(), "Browse › Task › List settings");
    assert_eq!(
        snapshot(&app).value,
        "type=;status=;series=;field=Priority=High;sort=-created;shape=flat"
    );
    open_section(&mut app, "shape");
    pick_in_popup(&mut app, "tree");
    app.handle(Key::End);
    let effect = app.handle(Key::Enter);
    let Effect::Run(argv) = &effect else {
        panic!("expected the list to run again")
    };
    assert!(!argv.contains(&"--flat".to_string()) && argv.contains(&"Priority=High".to_string()));
    execute(&mut app, effect);

    app.handle(Key::Esc);
    assert_eq!(app.breadcrumb(), "Browse › Task › List settings");
    assert_eq!(
        crumbs_back_to_menu(&mut app),
        ["Browse › Task › Tasks: what to show", "Browse › Browse what", "What to do"]
    );
    std::fs::remove_dir_all(workspace.parent().unwrap()).unwrap();
}
