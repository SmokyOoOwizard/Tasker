//! `tasker field …` и `tasker enum …` (`FieldCommands` в .NET): каталог полей проекта и перечисления.
use super::{enum_names, field_type_text, find_enum, find_field};
use crate::context::Context;
use crate::entities;
use crate::errors::{CliError, Result};
use crate::kit;
use crate::session::refs;
use clap::ArgMatches;
use tasker_core::ids::guid_d;
use tasker_core::model::{FieldEnum, FieldEnumValue, FieldType};
use tasker_core::{ConflictCode, TaskerError};
use tasker_services::field::{CreateField, UpdateField};
use tasker_services::field_conversion::{FieldChangeChoice, FieldValueMapping, SeveralValues};
use tasker_services::field_enum::{CreateFieldEnum, FieldEnumValueInput, RemovedEnumValues, UpdateFieldEnum};

pub const TYPE_NAMES: &str = "string, int, float, bool, date, enum";

/// Тип значения по названию (без учёта регистра); число — не тип.
pub fn parse_type(text: &str) -> Result<FieldType> {
    if text.trim().parse::<i64>().is_ok() {
        return Err(CliError::new(format!("Unknown field type '{text}': use one of {TYPE_NAMES}")));
    }
    FieldType::parse(text.trim()).ok_or_else(|| CliError::new(format!("Unknown field type '{text}': use one of {TYPE_NAMES}")))
}

fn parse_several(text: Option<&str>) -> Result<Option<SeveralValues>> {
    match text.map(|t| t.to_lowercase()) {
        None => Ok(None),
        Some(t) if t == "keep-first" || t == "keepfirst" => Ok(Some(SeveralValues::KeepFirst)),
        Some(t) if t == "clear" => Ok(Some(SeveralValues::Clear)),
        Some(_) => Err(CliError::new(format!(
            "Unknown --several '{}': use keep-first or clear",
            text.unwrap_or_default()
        ))),
    }
}

pub fn run_field(ctx: &mut Context<'_>, command: &str, leaf: &ArgMatches) -> Result<()> {
    let project_id = ctx.project_id()?;
    let ws = ctx.session().workspace().clone();
    match command {
        "create" => {
            let kind = parse_type(&kit::required(leaf, "type"))?;
            let enum_id = match kit::text(leaf, "enum") {
                Some(reference) => Some(find_enum(ctx, &project_id, &reference)?.id),
                None => None,
            };
            let field = ws.fields().create(
                &project_id,
                &CreateField {
                    name: kit::required(leaf, "name"),
                    field_type: kind,
                    multiple: if kit::flag(leaf, "multiple") { Some(true) } else { None },
                    enum_id,
                },
            )?;
            let names = enum_names(ctx, &project_id)?;
            ctx.print(
                &entities::field(&field),
                &format!(
                    "Created field '{}' ({}) {}",
                    field.name,
                    field_type_text(&field, &names),
                    guid_d(&field.id)
                ),
            );
        }
        "list" => {
            let names = enum_names(ctx, &project_id)?;
            let list = kit::Paging::load(leaf, |page| Ok(ws.fields().get_range(&project_id, page)?))?;
            ctx.print_list(&list, entities::field, |x| kit::row(&x.id, &[&x.name, &field_type_text(x, &names)]));
        }
        "get" => {
            let field = find_field(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let enums = ws.enums().get_all(&project_id)?;
            let text = kit::fields(&[
                ("id", Some(guid_d(&field.id))),
                ("name", Some(field.name.clone())),
                ("type", Some(field.field_type.name().to_string())),
                ("multiple", field.multiple.then(|| "yes".to_string())),
                ("enum", field.enum_id.map(|id| kit::named(&enums, &id, |x| x.id, |x| &x.name))),
                ("version", Some(field.version.clone())),
            ]);
            ctx.print(&entities::field(&field), &text);
        }
        "update" => {
            kit::require_change(leaf, &["name", "type", "multiple", "single", "enum"])?;
            if kit::flag(leaf, "multiple") && kit::flag(leaf, "single") {
                return Err(CliError::new("Use either --multiple or --single, not both"));
            }
            let field = find_field(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let kind = match kit::text(leaf, "type") {
                Some(text) => Some(parse_type(&text)?),
                None => None,
            };
            let enum_id = match kit::text(leaf, "enum") {
                Some(reference) => Some(find_enum(ctx, &project_id, &reference)?.id),
                None => None,
            };
            let multiple = if kit::flag(leaf, "multiple") {
                Some(true)
            } else if kit::flag(leaf, "single") {
                Some(false)
            } else {
                None
            };
            let mapping = match kit::values(leaf, "map") {
                Some(pairs) => Some(
                    pairs
                        .iter()
                        .map(|x| match x.split_once('=') {
                            Some((from, to)) if !from.is_empty() => Ok(FieldValueMapping {
                                from: from.to_string(),
                                to: to.to_string(),
                            }),
                            _ => Err(CliError::new(format!("--map expects From=To, not '{x}'"))),
                        })
                        .collect::<Result<Vec<_>>>()?,
                ),
                None => None,
            };
            let several = kit::text(leaf, "several");
            let clear = kit::flag(leaf, "clear-unconvertible");
            let chosen = clear || several.is_some() || mapping.is_some();
            let choice = if chosen {
                Some(FieldChangeChoice {
                    clear_unconvertible: clear,
                    several: parse_several(several.as_deref())?,
                    mapping,
                })
            } else {
                None
            };
            let result = ws.fields().update(
                &project_id,
                &field.id,
                &UpdateField {
                    name: kit::text(leaf, "name"),
                    version: Some(kit::text(leaf, "expected-version").unwrap_or(field.version.clone())),
                    field_type: kind,
                    multiple,
                    enum_id,
                    choice: choice.clone(),
                },
            );
            let updated = match result {
                Err(e) if e.is_in_use() && choice.is_none() => {
                    return Err(TaskerError::conflict(
                        ConflictCode::InUse,
                        format!(
                            "{}: repeat with --clear-unconvertible, --several keep-first|clear or --map From=To as needed",
                            e.message()
                        ),
                    )
                    .into());
                }
                other => other?,
            }
            .ok_or_else(|| CliError::new(format!("No field '{}'", guid_d(&field.id))))?;
            let names = enum_names(ctx, &project_id)?;
            ctx.print(
                &entities::field(&updated),
                &format!(
                    "Updated field '{}' ({}) {}",
                    updated.name,
                    field_type_text(&updated, &names),
                    guid_d(&updated.id)
                ),
            );
        }
        "delete" => {
            let field = find_field(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let version = kit::text(leaf, "expected-version").unwrap_or(field.version.clone());
            ws.fields().delete(&project_id, &field.id, Some(&version))?;
            ctx.print(&entities::deleted(&field.id), &kit::deleted("field", &field.name, &field.id));
        }
        _ => return Err(CliError::new("not implemented in this build")),
    }
    Ok(())
}

fn values_text(value: &FieldEnum) -> String {
    value.values.iter().map(|x| x.name.as_str()).collect::<Vec<_>>().join(", ")
}

/// Значение перечисления по id или названию (без учёта регистра).
fn find_value<'a>(value: &'a FieldEnum, reference: &str) -> Result<&'a FieldEnumValue> {
    refs::find_item(
        &value.values,
        reference,
        |x| x.id,
        |x| &x.name,
        &format!("value of enum '{}'", value.name),
        false,
    )
}

/// Правка значений: `--values` (список целиком) или `--add-value`/`--remove-value`/`--rename-value` (по одному); None — не меняются.
fn edited_values(
    current: &FieldEnum,
    replace: Option<Vec<String>>,
    add: Option<Vec<String>>,
    remove: Option<Vec<String>>,
    rename: Option<Vec<String>>,
) -> Result<Option<Vec<FieldEnumValueInput>>> {
    if let Some(replace) = replace {
        if add.is_some() || remove.is_some() || rename.is_some() {
            return Err(CliError::new(
                "Use either --values or --add-value/--remove-value/--rename-value, not both",
            ));
        }
        // Значение с тем же названием остаётся тем же значением (его id выбран у задач); остальные — новые.
        return Ok(Some(
            replace
                .iter()
                .map(|x| FieldEnumValueInput {
                    id: current
                        .values
                        .iter()
                        .find(|v| tasker_core::validate::eq_ignore_case(&v.name, x))
                        .map(|v| v.id),
                    name: x.clone(),
                })
                .collect(),
        ));
    }
    if add.is_none() && remove.is_none() && rename.is_none() {
        return Ok(None);
    }
    let mut list: Vec<FieldEnumValueInput> = current
        .values
        .iter()
        .map(|x| FieldEnumValueInput {
            id: Some(x.id),
            name: x.name.clone(),
        })
        .collect();
    for pair in rename.unwrap_or_default() {
        let (old, new) = match pair.split_once('=') {
            Some((old, new)) if !old.is_empty() => (old, new),
            _ => return Err(CliError::new(format!("--rename-value expects Old=New, not '{pair}'"))),
        };
        let id = find_value(current, old)?.id;
        if let Some(index) = list.iter().position(|x| x.id == Some(id)) {
            list[index] = FieldEnumValueInput {
                id: Some(id),
                name: new.to_string(),
            };
        }
    }
    for reference in remove.unwrap_or_default() {
        let id = find_value(current, &reference)?.id;
        list.retain(|x| x.id != Some(id));
    }
    for added in add.unwrap_or_default() {
        list.push(FieldEnumValueInput { id: None, name: added });
    }
    Ok(Some(list))
}

pub fn run_enum(ctx: &mut Context<'_>, command: &str, leaf: &ArgMatches) -> Result<()> {
    let project_id = ctx.project_id()?;
    let ws = ctx.session().workspace().clone();
    match command {
        "create" => {
            let created = ws.enums().create(
                &project_id,
                &CreateFieldEnum {
                    name: kit::required(leaf, "name"),
                    values: kit::values_or_empty(leaf, "value"),
                },
            )?;
            ctx.print(
                &entities::field_enum(&created),
                &format!(
                    "Created enum '{}' ({}) {}",
                    created.name,
                    values_text(&created),
                    guid_d(&created.id)
                ),
            );
        }
        "list" => {
            let list = kit::Paging::load(leaf, |page| Ok(ws.enums().get_range(&project_id, page)?))?;
            ctx.print_list(&list, entities::field_enum, |x| kit::row(&x.id, &[&x.name, &values_text(x)]));
        }
        "get" => {
            let value = find_enum(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let lines: Vec<String> = value.values.iter().map(|x| format!("  {}  {}", x.name, guid_d(&x.id))).collect();
            let text = kit::fields(&[
                ("id", Some(guid_d(&value.id))),
                ("name", Some(value.name.clone())),
                ("version", Some(value.version.clone())),
            ]);
            ctx.print(&entities::field_enum(&value), &format!("{text}\nvalues:\n{}", lines.join("\n")));
        }
        "update" => {
            kit::require_change(leaf, &["name", "values", "add-value", "remove-value", "rename-value"])?;
            let current = find_enum(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let edited = edited_values(
                &current,
                kit::values(leaf, "values"),
                kit::values(leaf, "add-value"),
                kit::values(leaf, "remove-value"),
                kit::values(leaf, "rename-value"),
            )?;
            let removing = edited
                .as_ref()
                .is_some_and(|e| current.values.iter().any(|x| e.iter().all(|v| v.id != Some(x.id))));
            let drop = kit::flag(leaf, "drop");
            let replace_with = kit::text(leaf, "replace-with");
            if drop && replace_with.is_some() {
                return Err(CliError::new("Use either --drop or --replace-with, not both"));
            }
            let mut choice: Option<RemovedEnumValues> = None;
            if drop {
                choice = Some(RemovedEnumValues::clear());
            } else if let Some(target_reference) = &replace_with {
                let target = find_value(&current, target_reference)?;
                if edited.as_ref().is_none_or(|e| e.iter().all(|x| x.id != Some(target.id))) {
                    return Err(CliError::new(format!(
                        "--replace-with must be a value the enum keeps, but '{}' is being removed",
                        target.name
                    )));
                }
                choice = Some(RemovedEnumValues::reassign(target.id));
            }
            let result = ws.enums().update(
                &project_id,
                &current.id,
                &UpdateFieldEnum {
                    name: kit::text(leaf, "name"),
                    values: edited,
                    version: Some(kit::text(leaf, "expected-version").unwrap_or(current.version.clone())),
                    removed: choice.clone(),
                },
            );
            let result = match result {
                Err(e) if e.is_in_use() && removing && choice.is_none() => {
                    return Err(TaskerError::conflict(
                        ConflictCode::InUse,
                        format!("{}: repeat with --drop or --replace-with <value>", e.message()),
                    )
                    .into());
                }
                other => other?,
            }
            .ok_or_else(|| CliError::new(format!("No enum '{}'", guid_d(&current.id))))?;
            let updated = &result.value;
            let mut text = format!("Updated enum '{}' ({}) {}", updated.name, values_text(updated), guid_d(&updated.id));
            if removing && let Some(choice) = &choice {
                let names: Vec<String> = current
                    .values
                    .iter()
                    .filter(|x| updated.values.iter().all(|y| y.id != x.id))
                    .map(|x| format!("'{}'", x.name))
                    .collect();
                // Условия колонок досок на убираемое значение тоже переписаны: их число — после задач, если они были.
                let mut tasks = kit::tasks(result.affected_tasks);
                if result.affected_columns > 0 {
                    tasks.push_str(&format!(" and {} board column condition(s)", result.affected_columns));
                }
                text.push('\n');
                text.push_str(&match choice.reassign_to {
                    Some(to) => format!(
                        "Reassigned {} → '{}' in {tasks}",
                        names.join(", "),
                        updated.values.iter().find(|x| x.id == to).map(|x| x.name.as_str()).unwrap_or("")
                    ),
                    None => format!("Removed {} from {tasks}", names.join(", ")),
                });
            }
            ctx.print(
                &entities::cascade(entities::field_enum(updated), result.affected_tasks, result.affected_columns),
                &text,
            );
        }
        "delete" => {
            let value = find_enum(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let version = kit::text(leaf, "expected-version").unwrap_or(value.version.clone());
            ws.enums().delete(&project_id, &value.id, Some(&version))?;
            ctx.print(&entities::deleted(&value.id), &kit::deleted("enum", &value.name, &value.id));
        }
        _ => return Err(CliError::new("not implemented in this build")),
    }
    Ok(())
}
