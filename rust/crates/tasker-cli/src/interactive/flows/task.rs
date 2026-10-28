use super::super::app::Action;
use super::super::step::{Check, Item};
use super::super::wizard::{Steps, Wizard, cmd};
use crate::errors::CliError;
use tasker_core::ids::guid_d;
use tasker_core::model::{FieldDefinition, FieldEnum, FieldType, LinkType, Status, StatusSet, TaskItem, TaskType};
use tasker_services::Storage;
use tasker_services::links::LinkDirection;
use uuid::Uuid;

const NO_VALUE: &str = "";

fn task_of(w: &Wizard<'_, '_>, project: &Uuid, key: &str) -> Steps<TaskItem> {
    let id = tasker_core::ids::parse_guid(key).ok_or_else(|| CliError::new("Bad task id"))?;
    Ok(w.ctx
        .session
        .workspace()
        .get_by_id::<TaskItem>(project, &id)?
        .ok_or_else(|| CliError::new(format!("No task '{key}'")))?)
}

fn type_of(w: &Wizard<'_, '_>, project: &Uuid, id: &Uuid) -> Steps<TaskType> {
    Ok(w.ctx
        .session
        .workspace()
        .get_by_id::<TaskType>(project, id)?
        .ok_or_else(|| CliError::new(format!("No task type '{}'", guid_d(id))))?)
}

fn set_statuses(w: &Wizard<'_, '_>, project: &Uuid, set_id: &Uuid) -> Steps<Vec<Item>> {
    let ws = w.ctx.session.workspace();
    let set = ws
        .get_by_id::<StatusSet>(project, set_id)?
        .ok_or_else(|| CliError::new(format!("No status set '{}'", guid_d(set_id))))?;
    let statuses: Vec<Status> = ws.get_all::<Status>(project)?;
    Ok(set
        .status_ids
        .iter()
        .filter_map(|id| statuses.iter().find(|s| s.id == *id))
        .map(|s| Item::new(guid_d(&s.id), &s.name).detail(&s.color))
        .collect())
}

fn enum_values(w: &Wizard<'_, '_>, project: &Uuid, enum_id: &Uuid) -> Steps<Vec<Item>> {
    let e = w
        .ctx
        .session
        .workspace()
        .get_by_id::<FieldEnum>(project, enum_id)?
        .ok_or_else(|| CliError::new(format!("No enum '{}'", guid_d(enum_id))))?;
    Ok(e.values.iter().map(|v| Item::new(guid_d(&v.id), &v.name)).collect())
}

struct FieldInput<'a> {
    id: Uuid,
    name: &'a str,
    field_type: FieldType,
    multiple: bool,
    required: bool,
    enum_id: Option<Uuid>,
    current: Vec<String>,
}

fn field_values(w: &mut Wizard<'_, '_>, project: &Uuid, field: &FieldInput<'_>) -> Steps<Vec<String>> {
    let title = format!(
        "{}{}",
        field.name,
        if field.required {
            " (required)"
        } else {
            " (optional: no value is allowed)"
        }
    );
    match field.field_type {
        FieldType::Enum => {
            let enum_id = field
                .enum_id
                .ok_or_else(|| CliError::new(format!("Field '{}' has no enum", field.name)))?;
            let mut items = enum_values(w, project, &enum_id)?;
            if field.multiple {
                let chosen = w.multi(&title, items, !field.required)?;
                return Ok(chosen.into_iter().map(|i| i.key).collect());
            }
            if !field.required {
                items.insert(0, Item::new(NO_VALUE, "(no value)"));
            }
            let chosen = w.select(&title, items)?;
            Ok(if chosen.key == NO_VALUE { Vec::new() } else { vec![chosen.key] })
        }
        FieldType::Bool => {
            let mut items = vec![Item::new("true", "Yes (true)"), Item::new("false", "No (false)")];
            if !field.required {
                items.insert(0, Item::new(NO_VALUE, "(no value)"));
            }
            let chosen = w.menu(&title, items)?;
            Ok(if chosen.key == NO_VALUE { Vec::new() } else { vec![chosen.key] })
        }
        _ => {
            let check = match field.field_type {
                FieldType::Int => Check::Int,
                FieldType::Float => Check::Float,
                FieldType::Date => Check::Date,
                _ => Check::Required,
            };
            let hint = match (field.multiple, field.required) {
                (true, true) => "Several values: separate them with a semicolon",
                (true, false) => "Several values: separate them with a semicolon; empty clears",
                (false, false) => "Empty clears the value",
                (false, true) => "",
            };
            let initial = field.current.join("; ");
            let text = w.text_with(&title, &initial, check, !field.required, hint)?;
            if field.multiple {
                let values: Vec<String> = text
                    .split(';')
                    .map(str::trim)
                    .filter(|v| !v.is_empty())
                    .map(str::to_string)
                    .collect();
                for value in &values {
                    if let Some(error) = check.error(value, false) {
                        return Err(CliError::new(error).into());
                    }
                }
                Ok(values)
            } else if text.is_empty() {
                Ok(Vec::new())
            } else {
                Ok(vec![text])
            }
        }
    }
}

fn field_arguments(field_id: &Uuid, values: &[String]) -> Vec<String> {
    let id = guid_d(field_id);
    if values.is_empty() {
        return cmd!["--field", format!("{id}=")];
    }
    values.iter().flat_map(|v| cmd!["--field", format!("{id}={v}")]).collect()
}

fn hierarchical_types(w: &Wizard<'_, '_>, project: &Uuid) -> Steps<Vec<LinkType>> {
    Ok(super::link_types(w, project)?.into_iter().filter(|t| t.hierarchical).collect())
}

fn parent_type_arguments(w: &mut Wizard<'_, '_>, project: &Uuid) -> Steps<Vec<String>> {
    let types = hierarchical_types(w, project)?;
    if types.len() <= 1 {
        return Ok(Vec::new());
    }
    let items = types
        .iter()
        .map(|t| Item::new(guid_d(&t.id), &t.name).detail(&t.outward_name))
        .collect();
    let chosen = w.select("Hierarchical link type of the parent", items)?;
    Ok(cmd!["--parent-type", chosen.key])
}

fn others(w: &Wizard<'_, '_>, project: &Uuid, except: &str) -> Steps<Vec<Item>> {
    Ok(super::tasks(w, project)?.into_iter().filter(|t| t.key != except).collect())
}

pub fn run(w: &mut Wizard<'_, '_>, action: Action) -> Steps<Vec<String>> {
    let project = w.project_id()?;
    match action {
        Action::Create => create(w, &project),
        Action::Change => change(w, &project),
        Action::Delete => {
            let task = w.select("Task", super::tasks(w, &project)?)?;
            super::confirm_delete(w, "task", &task.label)?;
            let mut argv = cmd!["task", "delete", task.key];
            argv.extend(w.expected_version(&task));
            Ok(argv)
        }
    }
}

fn create(w: &mut Wizard<'_, '_>, project: &Uuid) -> Steps<Vec<String>> {
    let title = w.text("Task title", "", Check::Required)?;
    let chosen_type = w.select("Task type", super::task_types(w, project)?)?;
    let type_id = tasker_core::ids::parse_guid(&chosen_type.key).ok_or_else(|| CliError::new("Bad task type id"))?;
    let task_type = type_of(w, project, &type_id)?;
    let mut statuses = set_statuses(w, project, &task_type.status_set_id)?;
    statuses.insert(0, Item::new("default", "(default: the first status of the type's set)"));
    let status = w.menu("Status", statuses)?;
    let description = w.editor("Description (optional)", "", "Press e to write it in your editor")?;
    let series = w.multi("Series to put the task into (optional)", super::series(w, project)?, true)?;
    let parents = w.multi("Parent tasks (optional)", super::tasks(w, project)?, true)?;
    let mut argv = cmd!["task", "create", title, "--type", chosen_type.key];
    if status.key != "default" {
        argv.extend(cmd!["--status", status.key]);
    }
    if !description.is_empty() {
        argv.extend(cmd!["-d", description]);
    }
    for s in &series {
        argv.extend(cmd!["--series", s.key]);
    }
    if !parents.is_empty() {
        argv.push("--parent".into());
        argv.extend(parents.iter().map(|p| p.key.clone()));
        argv.extend(parent_type_arguments(w, project)?);
    }
    let catalog: Vec<FieldDefinition> = w.ctx.session.workspace().get_all::<FieldDefinition>(project)?;
    for type_field in &task_type.fields {
        let Some(definition) = catalog.iter().find(|f| f.id == type_field.field_id) else {
            continue;
        };
        let input = FieldInput {
            id: definition.id,
            name: &definition.name,
            field_type: definition.field_type,
            multiple: definition.multiple,
            required: type_field.required,
            enum_id: definition.enum_id,
            current: Vec::new(),
        };
        let values = field_values(w, project, &input)?;
        if !values.is_empty() {
            argv.extend(field_arguments(&input.id, &values));
        }
    }
    Ok(argv)
}

fn change(w: &mut Wizard<'_, '_>, project: &Uuid) -> Steps<Vec<String>> {
    let chosen = w.select("Task", super::tasks(w, project)?)?;
    let task = task_of(w, project, &chosen.key)?;
    let what = w.menu(
        "What to change",
        vec![
            Item::new("title", "Title").detail(&task.title),
            Item::new("description", "Description").detail(task.description.as_deref().unwrap_or("").lines().next().unwrap_or_default()),
            Item::new("type", "Task type"),
            Item::new("status", "Status"),
            Item::new("series", "Series: add to one or remove from one"),
            Item::new("parents", "Parents: add or remove"),
            Item::new("fields", "Value of a field"),
            Item::new("links", "Links: link to a task or unlink"),
        ],
    )?;
    let ws = w.ctx.session.workspace().clone();
    let mut argv = cmd!["task", "update", chosen.key];
    match what.key.as_str() {
        "title" => {
            let title = w.text("New title", &task.title, Check::Required)?;
            argv.extend(cmd!["--title", title]);
        }
        "description" => {
            let description = w.editor(
                "Description",
                task.description.as_deref().unwrap_or(""),
                "An empty text clears the description",
            )?;
            argv.extend(cmd!["-d", description]);
        }
        "type" => {
            let new_type = w.select("New task type", super::task_types(w, project)?)?;
            let type_id = tasker_core::ids::parse_guid(&new_type.key).ok_or_else(|| CliError::new("Bad task type id"))?;
            let task_type = type_of(w, project, &type_id)?;
            let mut statuses = set_statuses(w, project, &task_type.status_set_id)?;
            statuses.insert(0, Item::new("keep", "(keep the current status)"));
            let status = w.menu("Status in the new type", statuses)?;
            argv.extend(cmd!["--type", new_type.key]);
            if status.key != "keep" {
                argv.extend(cmd!["--status", status.key]);
            }
        }
        "status" => {
            let task_type = type_of(w, project, &task.type_id)?;
            let status = w.select("New status", set_statuses(w, project, &task_type.status_set_id)?)?;
            argv.extend(cmd!["--status", status.key]);
        }
        "series" => {
            let all = super::series(w, project)?;
            let (inside, outside): (Vec<Item>, Vec<Item>) = all
                .into_iter()
                .partition(|s| task.series_numbers.iter().any(|n| guid_d(&n.series_id) == s.key));
            let mut choices = Vec::new();
            if !outside.is_empty() {
                choices.push(Item::new("add", "Add to a series").detail("the task gets the next number"));
            }
            if !inside.is_empty() {
                choices.push(Item::new("remove", "Remove from a series").detail("the number becomes free"));
            }
            if choices.is_empty() {
                return Err(CliError::new("The project has no series").into());
            }
            let choice = w.menu("Series", choices)?;
            if choice.key == "add" {
                let series = w.select("Series to add the task to", outside)?;
                return Ok(cmd!["series", "add-task", series.key, chosen.key]);
            }
            let series = w.select("Series to remove the task from", inside)?;
            let number = task
                .series_numbers
                .iter()
                .find(|n| guid_d(&n.series_id) == series.key)
                .map(|n| n.number)
                .ok_or_else(|| CliError::new("The task is not in that series"))?;
            return Ok(cmd!["series", "remove-task", series.key, number]);
        }
        "parents" => {
            let links = ws.links().get_links(project, &task.id)?.unwrap_or_default();
            let hierarchical = hierarchical_types(w, project)?;
            let prefixes = ws.series().prefixes(project)?;
            let parents: Vec<Item> = links
                .iter()
                .filter(|l| l.direction == LinkDirection::Inward && hierarchical.iter().any(|t| t.id == l.type_id))
                .map(|l| {
                    let refs = crate::commands::series::references(&l.task.series_numbers, &prefixes);
                    let handle = if refs.is_empty() {
                        tasker_core::ShortId::of(&l.task.id)
                    } else {
                        refs.join(",")
                    };
                    Item::new(
                        format!("{}|{}", guid_d(&l.type_id), guid_d(&l.task.id)),
                        format!("{handle}  {}", l.task.title),
                    )
                    .detail(&l.type_name)
                })
                .collect();
            let mut choices = vec![Item::new("add", "Add a parent").detail("the task becomes a child of it")];
            if !parents.is_empty() {
                choices.push(Item::new("remove", "Remove a parent").detail(format!("{} now", parents.len())));
            }
            let choice = w.menu("Parents", choices)?;
            if choice.key == "add" {
                let parent = w.select("Parent task", others(w, project, &chosen.key)?)?;
                argv.extend(cmd!["--add-parent", parent.key]);
                argv.extend(parent_type_arguments(w, project)?);
            } else {
                let parent = w.select("Parent to remove", parents)?;
                let (type_id, parent_id) = parent.key.split_once('|').ok_or_else(|| CliError::new("Bad parent key"))?;
                argv.extend(cmd!["--remove-parent", parent_id, "--parent-type", type_id]);
            }
            return Ok(argv);
        }
        "fields" => {
            let views = ws.tasks().fields_of(project, &task)?;
            if views.is_empty() {
                return Err(CliError::new(format!("Task '{}' has no fields", task.title)).into());
            }
            let items = views
                .iter()
                .map(|v| {
                    Item::new(guid_d(&v.field_id), &v.name).detail(format!(
                        "{}: {}",
                        crate::commands::type_text(v.field_type, v.multiple, None),
                        if v.texts.is_empty() { "-".to_string() } else { v.texts.join(", ") }
                    ))
                })
                .collect();
            let field = w.select("Field", items)?;
            let view = views
                .iter()
                .find(|v| guid_d(&v.field_id) == field.key)
                .ok_or_else(|| CliError::new("No such field"))?;
            let input = FieldInput {
                id: view.field_id,
                name: &view.name,
                field_type: view.field_type,
                multiple: view.multiple,
                required: view.required,
                enum_id: view.enum_id,
                current: if view.field_type == FieldType::Enum {
                    view.values.clone()
                } else {
                    view.texts.clone()
                },
            };
            let values = field_values(w, project, &input)?;
            argv.extend(field_arguments(&input.id, &values));
        }
        _ => {
            let links = ws.links().get_links(project, &task.id)?.unwrap_or_default();
            let mut choices = vec![Item::new("link", "Link to another task")];
            if !links.is_empty() {
                choices.push(Item::new("unlink", "Remove a link").detail(format!("{} now", links.len())));
            }
            let choice = w.menu("Links", choices)?;
            if choice.key == "link" {
                let mut phrases: Vec<Item> = Vec::new();
                for t in super::link_types(w, project)? {
                    phrases.push(Item::new(format!("{}|out", guid_d(&t.id)), &t.outward_name).detail(&t.name));
                    if t.inward_name != t.outward_name {
                        phrases.push(Item::new(format!("{}|in", guid_d(&t.id)), &t.inward_name).detail(&t.name));
                    }
                }
                let phrase = w.select("How this task relates to the other one", phrases)?;
                let other = w.select("The other task", others(w, project, &chosen.key)?)?;
                return Ok(cmd!["task", "link", chosen.key, phrase.label, other.key]);
            }
            let prefixes = ws.series().prefixes(project)?;
            let items = links
                .iter()
                .enumerate()
                .map(|(index, l)| {
                    let refs = crate::commands::series::references(&l.task.series_numbers, &prefixes);
                    let handle = if refs.is_empty() {
                        tasker_core::ShortId::of(&l.task.id)
                    } else {
                        refs.join(",")
                    };
                    Item::new(index.to_string(), format!("{} {handle}  {}", l.name, l.task.title)).detail(&l.type_name)
                })
                .collect();
            let link = w.select("Link to remove", items)?;
            let view = links
                .get(link.key.parse::<usize>().unwrap_or(usize::MAX))
                .ok_or_else(|| CliError::new("No such link"))?;
            return Ok(cmd!["task", "unlink", chosen.key, view.name, guid_d(&view.task.id)]);
        }
    }
    argv.extend(w.expected_version(&chosen));
    Ok(argv)
}
