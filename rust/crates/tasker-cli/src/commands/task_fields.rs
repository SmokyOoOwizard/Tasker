//! Параметры полей у `task create` и `task update` (`TaskFieldOptions` в .NET): значения (`--field Имя=значение`), поля из
//! каталога без значений (`--add-field`), собственные поля задачи (`--custom-field`) и, при правке, снятие полей (`--remove-field`).
use super::find_enum;
use crate::context::Context;
use crate::errors::{CliError, Result};
use crate::kit;
use crate::session::refs;
use clap::ArgMatches;
use tasker_core::model::{FieldType, TaskItem};
use tasker_core::validate::eq_ignore_case;
use tasker_services::task_fields::{NewOwnField, TaskFieldChanges, TaskFieldSource, TaskFieldValueInput};
use uuid::Uuid;

/// Идентификаторы параметров: при правке есть ещё `--remove-field`.
pub fn all(update: bool) -> &'static [&'static str] {
    if update {
        &["field", "custom-field", "add-field", "remove-field"]
    } else {
        &["field", "custom-field", "add-field"]
    }
}

/// Правка полей из параметров; None — ни один не указан. Поля называются по имени (или id): сначала поля самой задачи
/// (в том числе собственные — их нет в каталоге), потом каталог. `task` None — создаваемая задача.
pub fn build(
    leaf: &ArgMatches,
    ctx: &Context<'_>,
    project_id: &Uuid,
    task: Option<&TaskItem>,
    update: bool,
) -> Result<Option<TaskFieldChanges>> {
    let values = kit::values(leaf, "field");
    let custom = kit::values(leaf, "custom-field");
    let add = kit::values(leaf, "add-field");
    let remove = if update { kit::values(leaf, "remove-field") } else { None };
    if values.is_none() && custom.is_none() && add.is_none() && remove.is_none() {
        return Ok(None);
    }

    let ws = ctx.session().workspace();
    let catalog = ws.fields().get_all(project_id)?;
    let views = match task {
        Some(task) => ws.tasks().fields_of(project_id, task)?,
        None => Vec::new(),
    };

    // Поле по имени или id: у задачи (поля типа, дополнительные, собственные), иначе в каталоге.
    let resolve = |reference: &str| -> Result<Uuid> {
        let wanted = reference.trim();
        let own: Vec<Uuid> = views
            .iter()
            .filter(|x| tasker_core::ids::guid_d(&x.field_id) == wanted.to_lowercase() || eq_ignore_case(&x.name, wanted))
            .map(|x| x.field_id)
            .collect();
        if own.len() == 1 {
            return Ok(own[0]);
        }
        refs::find(&catalog, reference, |x| x.id, |x| &x.name, "field")
    };

    let mut changes = TaskFieldChanges::default();

    if let Some(values) = values {
        let mut by_field: Vec<(Uuid, Vec<String>)> = Vec::new();
        for text in &values {
            let (name, value) = match text.split_once('=') {
                Some((name, value)) if !name.trim().is_empty() => (name, value),
                _ => {
                    return Err(CliError::new(format!(
                        "--field expects Name=value (Name= clears the values), not '{text}'"
                    )));
                }
            };
            let id = resolve(name)?;
            let entry = match by_field.iter().position(|x| x.0 == id) {
                Some(index) => index,
                None => {
                    by_field.push((id, Vec::new()));
                    by_field.len() - 1
                }
            };
            if !value.is_empty() {
                by_field[entry].1.push(value.to_string());
            }
        }
        changes.values = Some(
            by_field
                .into_iter()
                .map(|(field_id, values)| TaskFieldValueInput {
                    field_id,
                    values: Some(values),
                })
                .collect(),
        );
    }

    if let Some(add) = add {
        let mut ids: Vec<Uuid> = Vec::new();
        for reference in &add {
            let id = resolve(reference)?;
            if !ids.contains(&id) {
                ids.push(id);
            }
        }
        changes.add_fields = Some(ids);
    }

    if let Some(remove) = remove {
        let mut ids: Vec<Uuid> = Vec::new();
        for reference in &remove {
            let id = resolve(reference)?;
            let view = views
                .iter()
                .find(|x| x.field_id == id)
                .ok_or_else(|| CliError::new(format!("The task has no additional or own field '{reference}'")))?;
            if view.source == TaskFieldSource::Type {
                return Err(CliError::new(format!(
                    "'{}' is a field of the task's type and cannot be removed from the task: change the task type instead",
                    view.name
                )));
            }
            if !ids.contains(&id) {
                ids.push(id);
            }
        }
        changes.remove_fields = Some(ids);
    }

    if let Some(custom) = custom {
        changes.new_own_fields = Some(parse_custom(ctx, project_id, &custom)?);
    }

    Ok(Some(changes))
}

/// Собственные поля из `--custom-field`; одно и то же поле, повторенное с тем же определением, собирает значения.
fn parse_custom(ctx: &Context<'_>, project_id: &Uuid, texts: &[String]) -> Result<Vec<NewOwnField>> {
    let mut result: Vec<NewOwnField> = Vec::new();
    for text in texts {
        let parsed = parse_custom_field(text)?;
        let enum_id = match &parsed.enumeration {
            Some(reference) => Some(find_enum(ctx, project_id, reference)?.id),
            None => None,
        };
        if parsed.field_type == FieldType::Enum && enum_id.is_none() {
            return Err(CliError::new(format!(
                "Field '{}' is of the type enum: name its enum with ':enum=<enum>' (see 'enum list')",
                parsed.name
            )));
        }
        if parsed.field_type != FieldType::Enum && enum_id.is_some() {
            return Err(CliError::new(format!(
                "Field '{}': ':enum=' is only for the type enum",
                parsed.name
            )));
        }
        let field = NewOwnField {
            name: parsed.name.clone(),
            field_type: parsed.field_type,
            values: None,
            required: parsed.required,
            multiple: parsed.multiple,
            enum_id,
        };
        match result.iter().position(|x| eq_ignore_case(&x.name, &parsed.name)) {
            None => {
                result.push(NewOwnField {
                    values: Some(parsed.value.iter().cloned().collect()),
                    ..field
                });
            }
            Some(index) => {
                let first = &result[index];
                let same = NewOwnField {
                    values: None,
                    ..first.clone()
                };
                if same != field {
                    return Err(CliError::new(format!(
                        "--custom-field '{}' is repeated with a different definition",
                        parsed.name
                    )));
                }
                let mut values = first.values.clone().unwrap_or_default();
                values.extend(parsed.value.iter().cloned());
                result[index].values = Some(values);
            }
        }
    }
    Ok(result)
}

#[derive(Debug)]
pub struct CustomField {
    pub name: String,
    pub field_type: FieldType,
    pub required: bool,
    pub multiple: bool,
    pub enumeration: Option<String>,
    pub value: Option<String>,
}

/// `Имя:тип[:required][:multiple][:enum=Перечисление][=значение]`. Значение — всё после первого `=`, не считая `enum=…`;
/// модификаторы — в любом порядке.
pub fn parse_custom_field(text: &str) -> Result<CustomField> {
    let syntax = || {
        CliError::new(format!(
            "--custom-field expects Name:type[:required][:multiple][:enum=Enum][=value], not '{text}'"
        ))
    };
    let chars: Vec<char> = text.chars().collect();
    let mut position = 0;
    let word = |position: &mut usize| -> String {
        let start = *position;
        while *position < chars.len() && chars[*position] != ':' && chars[*position] != '=' {
            *position += 1;
        }
        chars[start..*position].iter().collect()
    };

    let name = word(&mut position).trim().to_string();
    if name.is_empty() || position >= chars.len() || chars[position] != ':' {
        return Err(syntax());
    }
    position += 1;

    let field_type = super::field::parse_type(word(&mut position).trim())?;
    let (mut required, mut multiple) = (false, false);
    let mut enumeration: Option<String> = None;
    while position < chars.len() && chars[position] == ':' {
        position += 1;
        let modifier = word(&mut position).trim().to_string();
        match modifier.to_lowercase().as_str() {
            "required" => required = true,
            "multiple" => multiple = true,
            "enum" if position < chars.len() && chars[position] == '=' => {
                position += 1;
                let value = word(&mut position).trim().to_string();
                if value.is_empty() {
                    return Err(syntax());
                }
                enumeration = Some(value);
            }
            _ => {
                return Err(CliError::new(format!(
                    "Unknown modifier '{modifier}' in --custom-field '{text}': use required, multiple or enum=<enum>"
                )));
            }
        }
    }

    let mut value: Option<String> = None;
    if position < chars.len() {
        if chars[position] != '=' {
            return Err(syntax());
        }
        let rest: String = chars[position + 1..].iter().collect();
        if !rest.is_empty() {
            value = Some(rest);
        }
    }
    Ok(CustomField {
        name,
        field_type,
        required,
        multiple,
        enumeration,
        value,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn custom_field_syntax() {
        let f = parse_custom_field("Risk:enum:required:enum=Priority=High").unwrap();
        assert_eq!(
            (f.name.as_str(), f.field_type, f.required, f.multiple),
            ("Risk", FieldType::Enum, true, false)
        );
        assert_eq!(f.enumeration.as_deref(), Some("Priority"));
        assert_eq!(f.value.as_deref(), Some("High"));
        let f = parse_custom_field("Tags:string:multiple=a=b").unwrap();
        assert_eq!(f.value.as_deref(), Some("a=b"));
        assert!(parse_custom_field("Tags").is_err());
        assert!(
            parse_custom_field("Tags:string:nope")
                .unwrap_err()
                .text()
                .contains("Unknown modifier 'nope'")
        );
    }
}
