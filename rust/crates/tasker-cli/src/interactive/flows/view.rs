use super::super::app::Kind;
use super::super::step::{Chain, Check, FormValue, FormValues, Item, Section, SectionKind, StepSpec};
use super::super::wizard::{Steps, Wizard, cmd};
use crate::errors::CliError;
use tasker_core::ids::{guid_d, parse_guid};
use tasker_core::model::{Board, FieldDefinition, FieldEnum, FieldType};
use tasker_core::tasks::{Page, TaskFilter};
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

fn select(title: &str, items: Vec<Item>) -> StepSpec {
    StepSpec::Select {
        title: title.to_string(),
        items,
        initial: None,
        filterable: true,
    }
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

fn value_of(w: &Wizard<'_, '_>, project: &uuid::Uuid, field: &FieldDefinition) -> Steps<StepSpec> {
    let title = format!("Value of {}", field.name);
    Ok(match field.field_type {
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
            select(&title, e.values.iter().map(|v| Item::new(&v.name, &v.name)).collect())
        }
        FieldType::Bool => select(&title, vec![Item::new("true", "Yes (true)"), Item::new("false", "No (false)")]),
        _ => {
            let (check, hint) = match field.field_type {
                FieldType::Int => (Check::Int, "A whole number"),
                FieldType::Float => (Check::Float, "A number, e.g. 1.5"),
                FieldType::Date => (Check::Date, "A date as yyyy-MM-dd"),
                _ => (Check::Required, "Text, compared as a whole"),
            };
            StepSpec::Text {
                title,
                initial: String::new(),
                hint: hint.to_string(),
                check,
                allow_empty: false,
            }
        }
    })
}

fn condition_chain(w: &Wizard<'_, '_>, project: &uuid::Uuid, definitions: &[FieldDefinition]) -> Steps<Chain> {
    let enums = crate::commands::enum_names_of(w.ctx.session, project)?;
    let fields = definitions
        .iter()
        .map(|f| Item::new(&f.name, &f.name).detail(crate::commands::field_type_text(f, &enums)))
        .collect();
    let mut chain = Chain::new(select("Field", fields));
    for field in definitions {
        let mut comparison = Chain::new(select(&format!("Comparison for {}", field.name), comparisons(field.field_type)));
        let value = value_of(w, project, field)?;
        for op in comparisons(field.field_type) {
            if !op.key.starts_with(':') {
                comparison = comparison.then(op.key, Chain::new(value.clone()));
            }
        }
        chain = chain.then(&field.name, comparison);
    }
    Ok(chain)
}

fn sort_chain(definitions: &[FieldDefinition]) -> Chain {
    let mut keys: Vec<Item> = BUILT_IN_SORT_KEYS
        .iter()
        .map(|(key, detail)| Item::new(*key, *key).detail(*detail))
        .collect();
    keys.extend(
        definitions
            .iter()
            .map(|f| Item::new(&f.name, &f.name).detail(format!("field, {}", f.field_type.name()))),
    );
    let mut chain = Chain::new(select("Sort key", keys.clone()));
    for key in keys {
        let direction = select(
            &format!("Direction for {}", key.label),
            vec![Item::new("", "Ascending"), Item::new("-", "Descending")],
        );
        chain = chain.then(key.key, Chain::new(direction).prefix());
    }
    chain
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
            Item::new("card", "Card of a task").detail("filters, then one task: fields, links, description"),
            Item::new("links", "Links of a task").detail("filters, then one task"),
        ],
    )?;
    match what.key.as_str() {
        "card" => {
            let task = task_to_show(w, project)?;
            return Ok(cmd!["task", "get", task.key]);
        }
        "links" => {
            let task = task_to_show(w, project)?;
            return Ok(cmd!["task", "links", task.key]);
        }
        _ => {}
    }
    let settings = task_settings(w, project, "List settings", "Show the list", true)?;
    let mut argv = cmd!["task", "list", "--all"];
    let mut sort: Vec<String> = Vec::new();
    for setting in &settings {
        for value in &setting.values {
            match setting.key.as_str() {
                key @ ("type" | "status" | "series" | "field") => argv.extend(cmd![format!("--{key}"), value]),
                "sort" => sort.push(value.clone()),
                "shape" if value == "flat" => argv.push("--flat".into()),
                _ => {}
            }
        }
    }
    if !sort.is_empty() {
        argv.extend(cmd!["--sort", sort.join(",")]);
    }
    Ok(argv)
}

pub fn task_settings(w: &mut Wizard<'_, '_>, project: &uuid::Uuid, title: &str, submit: &str, shape: bool) -> Steps<FormValues> {
    let definitions = field_definitions(w, project)?;
    let mut sections = vec![
        Section::new(
            "type",
            "Task types",
            "any",
            SectionKind::Multi {
                items: super::task_types(w, project)?,
            },
        ),
        Section::new(
            "status",
            "Statuses",
            "any",
            SectionKind::Multi {
                items: super::statuses(w, project)?,
            },
        ),
        Section::new(
            "series",
            "Series",
            "any",
            SectionKind::Multi {
                items: super::series(w, project)?,
            },
        ),
        Section::new(
            "field",
            "Field conditions",
            "none",
            SectionKind::List {
                add: condition_chain(w, project, &definitions)?,
                add_label: "Add a condition".to_string(),
            },
        ),
        Section::new(
            "sort",
            "Sort",
            if shape { "creation order" } else { "newest first" },
            SectionKind::List {
                add: sort_chain(&definitions),
                add_label: "Add a key".to_string(),
            },
        ),
    ];
    if shape {
        sections.push(
            Section::new(
                "shape",
                "Shape",
                "tree",
                SectionKind::Single {
                    items: vec![
                        Item::new("tree", "Tree").detail("children under their parents"),
                        Item::new("flat", "Flat").detail("in the order of the sort"),
                    ],
                },
            )
            .values(vec!["tree".to_string()]),
        );
    }
    w.form(title, sections, submit)
}

pub fn filtered_tasks(w: &Wizard<'_, '_>, project: &uuid::Uuid, settings: &[FormValue]) -> Steps<Vec<Item>> {
    let ids = |key: &str| -> Option<Vec<uuid::Uuid>> {
        let values: Vec<uuid::Uuid> = settings
            .iter()
            .filter(|s| s.key == key)
            .flat_map(|s| s.values.iter().filter_map(|v| parse_guid(v)))
            .collect();
        (!values.is_empty()).then_some(values)
    };
    let strings = |key: &str| -> Vec<String> {
        settings
            .iter()
            .filter(|s| s.key == key)
            .flat_map(|s| s.values.iter().cloned())
            .collect()
    };
    let filter = TaskFilter {
        type_ids: ids("type"),
        status_ids: ids("status"),
        series_ids: ids("series"),
        ..TaskFilter::default()
    };
    let conditions = strings("field");
    let sort = strings("sort");
    let sort = if sort.is_empty() { "-created".to_string() } else { sort.join(",") };
    let ws = w.ctx.session.workspace();
    let names = super::TaskNames::load(w, project)?;
    let mut tasks: Vec<tasker_core::model::TaskItem> = Vec::new();
    loop {
        let page = ws.tasks().list(
            project,
            Some(&filter),
            (!conditions.is_empty()).then_some(conditions.as_slice()),
            Page::new(tasks.len(), crate::kit::MAX_LIMIT),
            0,
            Some(&sort),
        )?;
        let got = page.data.len();
        tasks.extend(page.data.into_iter().map(|x| x.task));
        if got == 0 || tasks.len() >= page.total_count {
            return Ok(names.items(&tasks.iter().collect::<Vec<_>>()));
        }
    }
}

fn task_to_show(w: &mut Wizard<'_, '_>, project: &uuid::Uuid) -> Steps<Item> {
    let settings = task_settings(w, project, "Task filters", "Choose a task", false)?;
    let items = filtered_tasks(w, project, &settings)?;
    w.select("Task", items)
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
