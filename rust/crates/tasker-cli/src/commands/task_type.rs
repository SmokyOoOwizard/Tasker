//! `tasker task-type …` (`TaskTypeCommands` в .NET).
use super::{enum_names, field_type_text, find_status_set, find_task_type};
use crate::context::Context;
use crate::entities;
use crate::errors::{CliError, Result};
use crate::kit;
use crate::session::refs;
use clap::ArgMatches;
use std::collections::HashMap;
use tasker_core::ids::guid_d;
use tasker_core::model::{FieldDefinition, TaskType, TaskTypeField};
use tasker_core::{ConflictCode, TaskerError};
use tasker_services::task_type::{CreateTaskType, RemovedFieldValues, UpdateTaskType};
use uuid::Uuid;

const REQUIRED_MARK: &str = "required";

/// Поле типа из `Имя` или `Имя:required`.
fn parse_field(catalog: &[FieldDefinition], text: &str) -> Result<TaskTypeField> {
    let suffix = format!(":{REQUIRED_MARK}");
    let required = text.len() >= suffix.len() && text[text.len() - suffix.len()..].eq_ignore_ascii_case(&suffix);
    let reference = if required { &text[..text.len() - suffix.len()] } else { text };
    Ok(TaskTypeField {
        field_id: refs::find(catalog, reference, |x| x.id, |x| &x.name, "field")?,
        required,
    })
}

/// Поля типа в виде для вывода: `Priority (enum Priority, required)`.
fn field_line(field: &TaskTypeField, catalog: &[FieldDefinition], enum_names: &HashMap<Uuid, String>) -> String {
    match catalog.iter().find(|x| x.id == field.field_id) {
        Some(definition) => format!(
            "{} ({}{})",
            definition.name,
            field_type_text(definition, enum_names),
            if field.required { ", required" } else { "" }
        ),
        None => format!("{} (unknown field)", guid_d(&field.field_id)),
    }
}

fn choice(drop: bool, keep: bool) -> Result<Option<RemovedFieldValues>> {
    match (drop, keep) {
        (true, true) => Err(CliError::new("Use either --drop-values or --keep-values, not both")),
        (true, _) => Ok(Some(RemovedFieldValues::Clear)),
        (_, true) => Ok(Some(RemovedFieldValues::Keep)),
        _ => Ok(None),
    }
}

/// Новый список полей типа из `--field` (целиком) или `--add-field`/`--remove-field` (по одному); None — поля не меняются.
/// Второе значение — убирается ли хотя бы одно поле.
fn edited_fields(
    ctx: &Context<'_>,
    project_id: &Uuid,
    task_type: &TaskType,
    replace: Option<Vec<String>>,
    add: Option<Vec<String>>,
    remove: Option<Vec<String>>,
) -> Result<(Option<Vec<TaskTypeField>>, bool)> {
    if replace.is_none() && add.is_none() && remove.is_none() {
        return Ok((None, false));
    }
    if replace.is_some() && (add.is_some() || remove.is_some()) {
        return Err(CliError::new("Use either --field or --add-field/--remove-field, not both"));
    }
    let catalog = ctx.session().workspace().fields().get_all(project_id)?;
    let mut list: Vec<TaskTypeField> = match &replace {
        Some(texts) => texts.iter().map(|x| parse_field(&catalog, x)).collect::<Result<_>>()?,
        None => task_type.fields.clone(),
    };
    for text in add.unwrap_or_default() {
        let field = parse_field(&catalog, &text)?;
        match list.iter().position(|x| x.field_id == field.field_id) {
            Some(index) => list[index] = field,
            None => list.push(field),
        }
    }
    for reference in remove.unwrap_or_default() {
        let id = refs::find(&catalog, &reference, |x| x.id, |x| &x.name, "field")?;
        let before = list.len();
        list.retain(|x| x.field_id != id);
        if list.len() == before {
            return Err(CliError::new(format!("Task type '{}' has no field '{reference}'", task_type.name)));
        }
    }
    let removing = task_type.fields.iter().any(|x| list.iter().all(|y| y.field_id != x.field_id));
    Ok((Some(list), removing))
}

pub fn run(ctx: &mut Context<'_>, command: &str, leaf: &ArgMatches) -> Result<()> {
    let project_id = ctx.project_id()?;
    let ws = ctx.session().workspace().clone();
    match command {
        "create" => {
            let sets = ws.status_sets().get_all(&project_id)?;
            let set_id = refs::find(&sets, &kit::required(leaf, "status-set"), |x| x.id, |x| &x.name, "status set")?;
            let texts = kit::values_or_empty(leaf, "field");
            let fields = if texts.is_empty() {
                None
            } else {
                let catalog = ws.fields().get_all(&project_id)?;
                Some(texts.iter().map(|x| parse_field(&catalog, x)).collect::<Result<Vec<_>>>()?)
            };
            let created = ws.task_types().create(
                &project_id,
                &CreateTaskType {
                    name: kit::required(leaf, "name"),
                    status_set_id: set_id,
                    fields,
                    description: kit::text(leaf, "description"),
                },
            )?;
            ctx.print(
                &entities::task_type(&created),
                &format!("Created task type '{}' {}", created.name, guid_d(&created.id)),
            );
        }
        "list" => {
            let length = kit::description_length(leaf);
            let list = kit::Paging::load(leaf, |page| Ok(ws.task_types().list(&project_id, page, length)?))?;
            ctx.print_list(&list, entities::task_type_list_item, |x| {
                kit::row(&x.task_type.id, &[&x.task_type.name])
            });
        }
        "get" => {
            let t = find_task_type(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let sets = ws.status_sets().get_all(&project_id)?;
            let mut text = kit::fields(&[
                ("id", Some(guid_d(&t.id))),
                ("name", Some(t.name.clone())),
                ("status set", Some(kit::named(&sets, &t.status_set_id, |x| x.id, |x| &x.name))),
                ("version", Some(t.version.clone())),
            ]);
            if !t.fields.is_empty() {
                let catalog = ws.fields().get_all(&project_id)?;
                let names = enum_names(ctx, &project_id)?;
                let lines: Vec<String> = t.fields.iter().map(|x| format!("  {}", field_line(x, &catalog, &names))).collect();
                text.push_str(&format!("\nfields:\n{}", lines.join("\n")));
            }
            ctx.print(&entities::task_type(&t), &kit::with_description(text, &t.description));
        }
        "update" => {
            kit::require_change(leaf, &["name", "description", "status-set", "field", "add-field", "remove-field"])?;
            let t = find_task_type(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let set_id = match kit::text(leaf, "status-set") {
                Some(reference) => Some(find_status_set(ctx, &project_id, &reference)?.id),
                None => None,
            };
            let choice = choice(kit::flag(leaf, "drop-values"), kit::flag(leaf, "keep-values"))?;
            let (fields, removing) = edited_fields(
                ctx,
                &project_id,
                &t,
                kit::values(leaf, "field"),
                kit::values(leaf, "add-field"),
                kit::values(leaf, "remove-field"),
            )?;
            let result = ws.task_types().update(
                &project_id,
                &t.id,
                &UpdateTaskType {
                    name: kit::text(leaf, "name"),
                    status_set_id: set_id,
                    version: Some(kit::text(leaf, "expected-version").unwrap_or(t.version.clone())),
                    fields,
                    removed_fields: choice,
                    description: kit::text(leaf, "description"),
                },
            );
            let result = match result {
                Err(e) if e.is_in_use() && removing && choice.is_none() && e.message().contains("have values in the field") => {
                    return Err(TaskerError::conflict(
                        ConflictCode::InUse,
                        format!("{}: repeat with --drop-values or --keep-values", e.message()),
                    )
                    .into());
                }
                other => other?,
            }
            .ok_or_else(|| CliError::new(format!("No task type '{}'", guid_d(&t.id))))?;
            let updated = &result.value;
            let mut text = format!("Updated task type '{}' {}", updated.name, guid_d(&updated.id));
            if removing && let Some(choice) = choice {
                let catalog = ws.fields().get_all(&project_id)?;
                let names: Vec<String> = t
                    .fields
                    .iter()
                    .filter(|x| updated.fields.iter().all(|y| y.field_id != x.field_id))
                    .map(|x| {
                        format!(
                            "'{}'",
                            catalog
                                .iter()
                                .find(|f| f.id == x.field_id)
                                .map(|f| f.name.clone())
                                .unwrap_or_else(|| guid_d(&x.field_id))
                        )
                    })
                    .collect();
                let tasks = kit::tasks(result.affected_tasks);
                text.push('\n');
                text.push_str(&if choice == RemovedFieldValues::Keep {
                    format!("Kept {} as an extra field in {tasks}", names.join(", "))
                } else {
                    format!("Removed {} from {tasks}", names.join(", "))
                });
            }
            ctx.print(
                &entities::cascade(entities::task_type(updated), result.affected_tasks, result.affected_columns),
                &text,
            );
        }
        "delete" => {
            let t = find_task_type(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let version = kit::text(leaf, "expected-version").unwrap_or(t.version.clone());
            ws.task_types().delete(&project_id, &t.id, Some(&version))?;
            ctx.print(&entities::deleted(&t.id), &kit::deleted("task type", &t.name, &t.id));
        }
        _ => return Err(CliError::new("not implemented in this build")),
    }
    Ok(())
}
