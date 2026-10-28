use super::super::app::Action;
use super::super::step::{Check, Item};
use super::super::wizard::{Steps, Wizard, cmd};
use crate::errors::CliError;
use tasker_core::ids::guid_d;
use tasker_core::model::{FieldDefinition, FieldEnum, FieldType};
use tasker_services::Storage;

fn type_items() -> Vec<Item> {
    crate::spec::FIELD_TYPES.iter().map(|t| Item::new(*t, *t)).collect()
}

fn field_of(w: &Wizard<'_, '_>, project: &uuid::Uuid, key: &str) -> Steps<FieldDefinition> {
    let id = tasker_core::ids::parse_guid(key).ok_or_else(|| CliError::new("Bad field id"))?;
    Ok(w.ctx
        .session
        .workspace()
        .get_by_id::<FieldDefinition>(project, &id)?
        .ok_or_else(|| CliError::new(format!("No field '{key}'")))?)
}

fn enum_of(w: &Wizard<'_, '_>, project: &uuid::Uuid, key: &str) -> Steps<FieldEnum> {
    let id = tasker_core::ids::parse_guid(key).ok_or_else(|| CliError::new("Bad enum id"))?;
    Ok(w.ctx
        .session
        .workspace()
        .get_by_id::<FieldEnum>(project, &id)?
        .ok_or_else(|| CliError::new(format!("No enum '{key}'")))?)
}

pub fn run_field(w: &mut Wizard<'_, '_>, action: Action) -> Steps<Vec<String>> {
    let project = w.project_id()?;
    match action {
        Action::Create => {
            let name = w.text("Field name (unique in the project)", "", Check::Required)?;
            let field_type = w.menu("Type of the value", type_items())?;
            let multiple = w.yes_no("Does the field hold several values (a list)")?;
            let mut argv = cmd!["field", "create", name, "--type", field_type.key];
            if multiple {
                argv.push("--multiple".into());
            }
            if field_type.key == "enum" {
                let chosen = w.select("Enum of the values", super::enums(w, &project)?)?;
                argv.extend(cmd!["--enum", chosen.key]);
            }
            Ok(argv)
        }
        Action::Change => {
            let chosen = w.select("Field", super::fields(w, &project)?)?;
            let field = field_of(w, &project, &chosen.key)?;
            let what = w.menu(
                "What to change",
                vec![
                    Item::new("name", "Name").detail(&field.name),
                    Item::new("type", "Type").detail(field.field_type.name()),
                    Item::new("multiple", "One or several values").detail(if field.multiple { "several" } else { "one" }),
                    Item::new("enum", "Enum of the values"),
                ],
            )?;
            let mut argv = cmd!["field", "update", chosen.key];
            match what.key.as_str() {
                "name" => {
                    let name = w.text("New name", &field.name, Check::Required)?;
                    argv.extend(cmd!["--name", name]);
                }
                "type" => {
                    let new_type = w.menu("New type (the values in tasks are converted)", type_items())?;
                    argv.extend(cmd!["--type", new_type.key]);
                    if new_type.key == "enum" {
                        let chosen = w.select("Enum of the values", super::enums(w, &project)?)?;
                        argv.extend(cmd!["--enum", chosen.key]);
                    }
                    if w.yes_no("Clear the values of tasks that do not fit the new type (otherwise such a task stops the change)")? {
                        argv.push("--clear-unconvertible".into());
                    }
                }
                "multiple" => {
                    let several = w.menu(
                        "From now on the field holds",
                        vec![Item::new("several", "Several values (a list)"), Item::new("one", "One value")],
                    )?;
                    if several.key == "several" {
                        argv.push("--multiple".into());
                    } else {
                        argv.push("--single".into());
                        let choice = w.menu(
                            "Tasks with several values",
                            vec![
                                Item::new("keep-first", "Keep the first value"),
                                Item::new("clear", "Clear the values"),
                            ],
                        )?;
                        argv.extend(cmd!["--several", choice.key]);
                    }
                }
                _ => {
                    if field.field_type != FieldType::Enum {
                        return Err(CliError::new(format!("Field '{}' is not an enum field: change its type first", field.name)).into());
                    }
                    let chosen = w.select("New enum (values of tasks are matched by name)", super::enums(w, &project)?)?;
                    argv.extend(cmd!["--enum", chosen.key]);
                    if w.yes_no("Clear the values of tasks that have no match in the new enum")? {
                        argv.push("--clear-unconvertible".into());
                    }
                }
            }
            argv.extend(w.expected_version(&chosen));
            Ok(argv)
        }
        Action::View => Err(CliError::new("Nothing to do").into()),
        Action::Delete => {
            let field = w.select("Field", super::fields(w, &project)?)?;
            super::confirm_delete(w, "field", &field.label)?;
            let mut argv = cmd!["field", "delete", field.key];
            argv.extend(w.expected_version(&field));
            Ok(argv)
        }
    }
}

fn value_items(e: &FieldEnum) -> Vec<Item> {
    e.values.iter().map(|v| Item::new(guid_d(&v.id), &v.name)).collect()
}

pub fn run_enum(w: &mut Wizard<'_, '_>, action: Action) -> Steps<Vec<String>> {
    let project = w.project_id()?;
    match action {
        Action::Create => {
            let name = w.text("Enum name (unique in the project)", "", Check::Required)?;
            let values = w.editor("Values, one per line, in display order", "", "At least one value")?;
            let values = super::lines(&values);
            if values.is_empty() {
                return Err(CliError::new("An enum needs at least one value").into());
            }
            let mut argv = cmd!["enum", "create", name, "--value"];
            argv.extend(values);
            Ok(argv)
        }
        Action::Change => {
            let chosen = w.select("Enum", super::enums(w, &project)?)?;
            let e = enum_of(w, &project, &chosen.key)?;
            let what = w.menu(
                "What to change",
                vec![
                    Item::new("name", "Name").detail(&e.name),
                    Item::new("add", "Add a value at the end"),
                    Item::new("rename", "Rename a value"),
                    Item::new("remove", "Remove a value"),
                    Item::new("values", "Replace the whole list of values"),
                ],
            )?;
            let mut argv = cmd!["enum", "update", chosen.key];
            match what.key.as_str() {
                "name" => {
                    let name = w.text("New name", &e.name, Check::Required)?;
                    argv.extend(cmd!["--name", name]);
                }
                "add" => {
                    let value = w.text("New value", "", Check::Required)?;
                    argv.extend(cmd!["--add-value", value]);
                }
                "rename" => {
                    let value = w.select("Value to rename", value_items(&e))?;
                    let name = w.text("New name of the value", &value.label, Check::Required)?;
                    argv.extend(cmd!["--rename-value", format!("{}={name}", value.label)]);
                }
                "remove" => {
                    let value = w.select("Value to remove", value_items(&e))?;
                    let rest: Vec<Item> = value_items(&e).into_iter().filter(|v| v.key != value.key).collect();
                    let mut choices = vec![Item::new("drop", "Clear it from the tasks that have it selected")];
                    if !rest.is_empty() {
                        choices.push(Item::new("replace", "Give those tasks another value instead"));
                    }
                    let choice = w.menu("Tasks with this value", choices)?;
                    argv.extend(cmd!["--remove-value", value.key]);
                    if choice.key == "drop" {
                        argv.push("--drop".into());
                    } else {
                        let replacement = w.select("Value to give instead", rest)?;
                        argv.extend(cmd!["--replace-with", replacement.key]);
                    }
                }
                _ => {
                    let current = e.values.iter().map(|v| v.name.as_str()).collect::<Vec<_>>().join("\n");
                    let values = w.editor(
                        "Values, one per line, in display order",
                        &current,
                        "A value with the same name stays the same value",
                    )?;
                    let values = super::lines(&values);
                    if values.is_empty() {
                        return Err(CliError::new("An enum needs at least one value").into());
                    }
                    argv.push("--values".into());
                    argv.extend(values);
                }
            }
            argv.extend(w.expected_version(&chosen));
            Ok(argv)
        }
        Action::View => Err(CliError::new("Nothing to do").into()),
        Action::Delete => {
            let e = w.select("Enum", super::enums(w, &project)?)?;
            super::confirm_delete(w, "enum", &e.label)?;
            let mut argv = cmd!["enum", "delete", e.key];
            argv.extend(w.expected_version(&e));
            Ok(argv)
        }
    }
}
