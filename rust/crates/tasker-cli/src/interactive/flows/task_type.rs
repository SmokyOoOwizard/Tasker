use super::super::app::Action;
use super::super::step::{Check, Item};
use super::super::wizard::{Steps, Wizard, cmd};
use crate::errors::CliError;
use tasker_core::ids::guid_d;
use tasker_core::model::TaskType;
use tasker_services::Storage;

fn type_of(w: &Wizard<'_, '_>, project: &uuid::Uuid, key: &str) -> Steps<TaskType> {
    let id = tasker_core::ids::parse_guid(key).ok_or_else(|| CliError::new("Bad task type id"))?;
    Ok(w.ctx
        .session
        .workspace()
        .get_by_id::<TaskType>(project, &id)?
        .ok_or_else(|| CliError::new(format!("No task type '{key}'")))?)
}

fn field_argument(w: &mut Wizard<'_, '_>, field: &Item) -> Steps<String> {
    let required = w.yes_no(&format!("Must every task of the type have a value of '{}'", field.label))?;
    Ok(if required {
        format!("{}:required", field.key)
    } else {
        field.key.clone()
    })
}

pub fn run(w: &mut Wizard<'_, '_>, action: Action) -> Steps<Vec<String>> {
    let project = w.project_id()?;
    match action {
        Action::Create => {
            let name = w.text("Task type name", "", Check::Required)?;
            let set = w.select("Status set of the type", super::status_sets(w, &project)?)?;
            let description = w.editor("Description (optional)", "", "What the task type means and when to use it")?;
            let fields = w.multi("Fields of the catalog to attach (optional)", super::fields(w, &project)?, true)?;
            let mut argv = cmd!["task-type", "create", name, "--status-set", set.key];
            if !description.is_empty() {
                argv.extend(cmd!["-d", description]);
            }
            for field in &fields {
                let argument = field_argument(w, field)?;
                argv.extend(cmd!["--field", argument]);
            }
            Ok(argv)
        }
        Action::Change => {
            let chosen = w.select("Task type", super::task_types(w, &project)?)?;
            let task_type = type_of(w, &project, &chosen.key)?;
            let catalog = super::fields(w, &project)?;
            let attached: Vec<Item> = task_type
                .fields
                .iter()
                .filter_map(|f| catalog.iter().find(|c| c.key == guid_d(&f.field_id)).cloned())
                .map(|c| {
                    let required = task_type.fields.iter().any(|f| guid_d(&f.field_id) == c.key && f.required);
                    if required { c.detail("required") } else { c }
                })
                .collect();
            let what = w.menu(
                "What to change",
                vec![
                    Item::new("name", "Name").detail(&task_type.name),
                    Item::new("description", "Description").detail(task_type.description.lines().next().unwrap_or_default()),
                    Item::new("status-set", "Status set"),
                    Item::new("add-field", "Attach a field, or change whether it is required"),
                    Item::new("remove-field", "Detach a field").detail(format!("{} attached", attached.len())),
                ],
            )?;
            let mut argv = cmd!["task-type", "update", chosen.key];
            match what.key.as_str() {
                "name" => {
                    let name = w.text("New name", &task_type.name, Check::Required)?;
                    argv.extend(cmd!["--name", name]);
                }
                "description" => {
                    let description = w.editor("Description", &task_type.description, "An empty text clears the description")?;
                    argv.extend(cmd!["-d", description]);
                }
                "status-set" => {
                    let set = w.select(
                        "New status set (its statuses must cover the tasks of the type)",
                        super::status_sets(w, &project)?,
                    )?;
                    argv.extend(cmd!["--status-set", set.key]);
                }
                "add-field" => {
                    let field = w.select("Field of the catalog", catalog)?;
                    let argument = field_argument(w, &field)?;
                    argv.extend(cmd!["--add-field", argument]);
                }
                _ => {
                    let field = w.select("Field to detach", attached)?;
                    let values = w.menu(
                        "Values of the field in the tasks of the type",
                        vec![
                            Item::new("drop", "Clear them"),
                            Item::new("keep", "Keep them: the field becomes an additional field of those tasks"),
                        ],
                    )?;
                    argv.extend(cmd!["--remove-field", field.key]);
                    argv.push(if values.key == "drop" {
                        "--drop-values".into()
                    } else {
                        "--keep-values".into()
                    });
                }
            }
            argv.extend(w.expected_version(&chosen));
            Ok(argv)
        }
        Action::Delete => {
            let task_type = w.select("Task type", super::task_types(w, &project)?)?;
            super::confirm_delete(w, "task type", &task_type.label)?;
            let mut argv = cmd!["task-type", "delete", task_type.key];
            argv.extend(w.expected_version(&task_type));
            Ok(argv)
        }
    }
}
