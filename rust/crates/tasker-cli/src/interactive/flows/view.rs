use super::super::app::Kind;
use super::super::step::{Check, Item};
use super::super::wizard::{Steps, Wizard, cmd};
use crate::errors::CliError;
use tasker_core::ids::guid_d;
use tasker_core::model::{Board, FieldDefinition, FieldEnum, FieldType};
use tasker_services::Storage;

const BUILT_IN_SORT_KEYS: [(&str, &str); 6] = [
    ("status", "by the order of statuses"),
    ("type", "by the name of the task type"),
    ("title", "alphabetically"),
    ("created", "by the time of creation"),
    ("updated", "by the time of the last change"),
    ("series", "by series and number"),
];

fn field_definitions(w: &Wizard<'_, '_>, project: &uuid::Uuid) -> Steps<Vec<FieldDefinition>> {
    Ok(w.ctx.session.workspace().get_all::<FieldDefinition>(project)?)
}

fn comparisons(field_type: FieldType) -> Vec<Item> {
    let mut items = vec![
        Item::new("=", "equals").detail("Name=value; a multiple field: contains the value"),
        Item::new("!=", "differs").detail("Name!=value; tasks without a value fit too"),
    ];
    if matches!(field_type, FieldType::Int | FieldType::Float | FieldType::Date) {
        items.extend([
            Item::new(">", "greater than"),
            Item::new(">=", "greater or equal"),
            Item::new("<", "less than"),
            Item::new("<=", "less or equal"),
        ]);
    }
    items.extend([
        Item::new(":set", "has a value").detail("Name:set"),
        Item::new(":unset", "has no value").detail("Name:unset"),
        Item::new(":attached", "is connected to the task").detail("Name:attached"),
        Item::new(":detached", "is not connected to the task").detail("Name:detached"),
    ]);
    items
}

fn condition_value(w: &mut Wizard<'_, '_>, project: &uuid::Uuid, field: &FieldDefinition) -> Steps<String> {
    let title = format!("Value of {}", field.name);
    match field.field_type {
        FieldType::Enum => {
            let enum_id = field
                .enum_id
                .ok_or_else(|| CliError::new(format!("Field '{}' has no enum", field.name)))?;
            let e = w
                .ctx
                .session
                .workspace()
                .get_by_id::<FieldEnum>(project, &enum_id)?
                .ok_or_else(|| CliError::new(format!("No enum '{}'", guid_d(&enum_id))))?;
            let items = e.values.iter().map(|v| Item::new(&v.name, &v.name)).collect();
            Ok(w.select(&title, items)?.key)
        }
        FieldType::Bool => Ok(w
            .menu(&title, vec![Item::new("true", "Yes (true)"), Item::new("false", "No (false)")])?
            .key),
        _ => {
            let (check, hint) = match field.field_type {
                FieldType::Int => (Check::Int, "A whole number"),
                FieldType::Float => (Check::Float, "A number, e.g. 1.5"),
                FieldType::Date => (Check::Date, "A date as yyyy-MM-dd"),
                _ => (Check::Required, "Text, compared as a whole"),
            };
            w.text_with(&title, "", check, false, hint)
        }
    }
}

fn conditions(w: &mut Wizard<'_, '_>, project: &uuid::Uuid) -> Steps<Vec<String>> {
    let definitions = field_definitions(w, project)?;
    let enums = crate::commands::enum_names_of(w.ctx.session, project)?;
    let mut conditions: Vec<String> = Vec::new();
    loop {
        let mut items: Vec<Item> = conditions
            .iter()
            .enumerate()
            .map(|(at, c)| Item::new(format!("drop{at}"), c).detail("Enter removes this condition"))
            .collect();
        items.push(Item::new("add", "+ Add a condition").detail("a field, a comparison and a value"));
        items.push(Item::new("done", "Done").detail(if conditions.is_empty() {
            "no conditions"
        } else {
            "all conditions must hold"
        }));
        let what = w.menu("Field conditions", items)?;
        match what.key.as_str() {
            "done" => return Ok(conditions),
            "add" => {
                let choices = definitions
                    .iter()
                    .map(|f| Item::new(guid_d(&f.id), &f.name).detail(crate::commands::field_type_text(f, &enums)))
                    .collect();
                let chosen = w.select("Field", choices)?;
                let field = definitions
                    .iter()
                    .find(|f| guid_d(&f.id) == chosen.key)
                    .ok_or_else(|| CliError::new("No such field"))?;
                let comparison = w.menu(&format!("Comparison for {}", field.name), comparisons(field.field_type))?;
                let condition = if comparison.key.starts_with(':') {
                    format!("{}{}", field.name, comparison.key)
                } else {
                    let value = condition_value(w, project, field)?;
                    format!("{}{}{}", field.name, comparison.key, value)
                };
                conditions.push(condition);
            }
            key => {
                if let Some(at) = key.strip_prefix("drop").and_then(|n| n.parse::<usize>().ok())
                    && at < conditions.len()
                {
                    conditions.remove(at);
                }
            }
        }
    }
}

fn sort_keys(w: &mut Wizard<'_, '_>, project: &uuid::Uuid) -> Steps<Vec<String>> {
    let definitions = field_definitions(w, project)?;
    let mut keys: Vec<String> = Vec::new();
    loop {
        let mut items: Vec<Item> = keys
            .iter()
            .enumerate()
            .map(|(at, k)| {
                let (name, direction) = match k.strip_prefix('-') {
                    Some(name) => (name, "descending"),
                    None => (k.as_str(), "ascending"),
                };
                Item::new(format!("drop{at}"), name).detail(format!("{direction}; Enter removes this key"))
            })
            .collect();
        items.push(Item::new("add", "+ Add a key").detail("a built-in key or a field, ascending or descending"));
        items.push(Item::new("done", "Done").detail(if keys.is_empty() {
            "creation order"
        } else {
            "by the keys in this order"
        }));
        let what = w.menu("Sort", items)?;
        match what.key.as_str() {
            "done" => return Ok(keys),
            "add" => {
                let mut choices: Vec<Item> = BUILT_IN_SORT_KEYS
                    .iter()
                    .map(|(key, detail)| Item::new(*key, *key).detail(*detail))
                    .collect();
                choices.extend(
                    definitions
                        .iter()
                        .map(|f| Item::new(&f.name, &f.name).detail(format!("field, {}", f.field_type.name()))),
                );
                let key = w.select("Sort key", choices)?;
                let direction = w.menu(
                    &format!("Direction for {}", key.label),
                    vec![Item::new("asc", "Ascending"), Item::new("desc", "Descending")],
                )?;
                keys.push(if direction.key == "desc" {
                    format!("-{}", key.key)
                } else {
                    key.key
                });
            }
            key => {
                if let Some(at) = key.strip_prefix("drop").and_then(|n| n.parse::<usize>().ok())
                    && at < keys.len()
                {
                    keys.remove(at);
                }
            }
        }
    }
}

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
    let fields = conditions(w, project)?;
    let sort = sort_keys(w, project)?;
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
    for condition in &fields {
        argv.extend(cmd!["--field", condition]);
    }
    if !sort.is_empty() {
        argv.extend(cmd!["--sort", sort.join(",")]);
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
