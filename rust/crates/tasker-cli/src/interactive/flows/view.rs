use super::super::app::Kind;
use super::super::step::{Check, Item};
use super::super::wizard::{Steps, Wizard, cmd};
use crate::errors::CliError;
use tasker_core::ids::guid_d;
use tasker_core::model::Board;
use tasker_services::Storage;

const FIELD_HINT: &str =
    "Name=value, Name!=value, Name>=3 (> < <=), Name:set, Name:unset; several: separate with a semicolon; empty: no condition";
const SORT_HINT: &str = "Keys separated by commas, '-' before a key for descending order: status, type, title, created, updated, series or a field name; empty: creation order";

fn list_or_card(w: &mut Wizard<'_, '_>, kind: Kind, items: Vec<Item>, group: &str) -> Steps<Vec<String>> {
    let what = w.menu(
        &format!("{}: what to show", kind.label()),
        vec![Item::new("list", "List").detail("all of them"), Item::new("card", "Card of one")],
    )?;
    if what.key == "list" {
        return Ok(cmd![group, "list", "--all"]);
    }
    let chosen = w.select(kind.label(), items)?;
    Ok(cmd![group, "get", chosen.key])
}

fn tasks(w: &mut Wizard<'_, '_>, project: &uuid::Uuid) -> Steps<Vec<String>> {
    let what = w.menu(
        "Tasks: what to show",
        vec![
            Item::new("list", "List with filters").detail("tree by parents, or flat"),
            Item::new("card", "Card of a task").detail("fields, links, description"),
            Item::new("links", "Links of a task"),
        ],
    )?;
    match what.key.as_str() {
        "card" => {
            let task = w.select("Task", super::tasks(w, project)?)?;
            return Ok(cmd!["task", "get", task.key]);
        }
        "links" => {
            let task = w.select("Task", super::tasks(w, project)?)?;
            return Ok(cmd!["task", "links", task.key]);
        }
        _ => {}
    }
    let types = w.multi("Only these task types (optional)", super::task_types(w, project)?, true)?;
    let statuses = w.multi("Only these statuses (optional)", super::statuses(w, project)?, true)?;
    let series = w.multi("Only these series (optional)", super::series(w, project)?, true)?;
    let fields = w.text_with("Field conditions (optional)", "", Check::Any, true, FIELD_HINT)?;
    let sort = w.text_with("Sort (optional)", "", Check::Any, true, SORT_HINT)?;
    let shape = w.menu(
        "Shape of the list",
        vec![
            Item::new("tree", "Tree").detail("children under their parents"),
            Item::new("flat", "Flat").detail("in the order of the sort"),
        ],
    )?;
    let mut argv = cmd!["task", "list", "--all"];
    for t in &types {
        argv.extend(cmd!["--type", t.key]);
    }
    for s in &statuses {
        argv.extend(cmd!["--status", s.key]);
    }
    for s in &series {
        argv.extend(cmd!["--series", s.key]);
    }
    for condition in fields.split(';').map(str::trim).filter(|c| !c.is_empty()) {
        argv.extend(cmd!["--field", condition]);
    }
    if !sort.is_empty() {
        argv.extend(cmd!["--sort", sort]);
    }
    if shape.key == "flat" {
        argv.push("--flat".into());
    }
    Ok(argv)
}

fn boards(w: &mut Wizard<'_, '_>, project: &uuid::Uuid) -> Steps<Vec<String>> {
    let what = w.menu(
        "Boards: what to show",
        vec![
            Item::new("show", "Whole board").detail("every column with its tasks"),
            Item::new("column", "One column of a board"),
            Item::new("card", "Board card").detail("columns and their statuses"),
            Item::new("list", "List of boards"),
        ],
    )?;
    if what.key == "list" {
        return Ok(cmd!["board", "list", "--all"]);
    }
    let board = w.select("Board", super::boards(w, project)?)?;
    match what.key.as_str() {
        "show" => Ok(cmd!["board", "show", board.key, "--all"]),
        "card" => Ok(cmd!["board", "get", board.key]),
        _ => {
            let id = tasker_core::ids::parse_guid(&board.key).ok_or_else(|| CliError::new("Bad board id"))?;
            let found = w
                .ctx
                .session
                .workspace()
                .get_by_id::<Board>(project, &id)?
                .ok_or_else(|| CliError::new(format!("No board '{}'", board.key)))?;
            let columns = found
                .columns
                .iter()
                .map(|c| Item::new(guid_d(&c.id), &c.name).detail(format!("{} statuses", c.status_ids.len())))
                .collect();
            let column = w.select("Column", columns)?;
            Ok(cmd!["board", "tasks", board.key, column.key, "--all"])
        }
    }
}

pub fn run(w: &mut Wizard<'_, '_>, kind: Kind) -> Steps<Vec<String>> {
    match kind {
        Kind::Project => Ok(cmd!["project", "list", "--all"]),
        Kind::User => Ok(cmd!["user", "list", "--all"]),
        Kind::Agent => Ok(cmd!["agent", "list", "--all"]),
        _ => {
            let project = w.project_id()?;
            match kind {
                Kind::Task => tasks(w, &project),
                Kind::Board => boards(w, &project),
                Kind::Status => list_or_card(w, kind, super::statuses(w, &project)?, "status"),
                Kind::StatusSet => list_or_card(w, kind, super::status_sets(w, &project)?, "status-set"),
                Kind::TaskType => list_or_card(w, kind, super::task_types(w, &project)?, "task-type"),
                Kind::Series => list_or_card(w, kind, super::series(w, &project)?, "series"),
                Kind::Field => list_or_card(w, kind, super::fields(w, &project)?, "field"),
                Kind::Enum => list_or_card(w, kind, super::enums(w, &project)?, "enum"),
                Kind::LinkType => list_or_card(w, kind, super::link_type_items(w, &project)?, "link-type"),
                _ => Err(CliError::new("Nothing to show").into()),
            }
        }
    }
}
