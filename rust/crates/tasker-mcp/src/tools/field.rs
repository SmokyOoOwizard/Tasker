//! `FieldTools`: каталог полей задач проекта и перечисления.
use super::task::field_type;
use super::{DELETED, page_of};
use crate::call::{Call, Output};
use crate::error::{Result, ToolError};
use crate::json;
use tasker_core::ids::guid_d;
use tasker_services::field::{CreateField, UpdateField};
use tasker_services::field_conversion::{FieldChangeChoice, FieldValueMapping, SeveralValues};
use tasker_services::field_enum::{CreateFieldEnum, FieldEnumValueInput, RemovedEnumValues, UpdateFieldEnum};

pub fn list_fields(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let page = call.ws()?.fields().get_range(&project_id, page_of(call)?)?;
    Ok(Output::Json(json::page(&page, json::field)))
}

pub fn get_field(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let text = call.args.required_string("field")?;
    let field = Call::found(call.ws()?.fields().find(&project_id, &text)?, format!("Field '{text}'"))?;
    Ok(Output::Json(json::field(&field)))
}

pub fn create_field(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let command = CreateField {
        name: call.args.required_string("name")?,
        field_type: field_type(&call.args.required_string("type")?)?,
        multiple: call.args.bool("multiple")?,
        enum_id: call.args.guid("enumId")?,
    };
    Ok(Output::Json(json::field(&call.ws()?.fields().create(&project_id, &command)?)))
}

pub fn update_field(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let a = call.args;
    let id = a.required_guid("fieldId")?;
    let clear_unconvertible = a.bool("clearUnconvertible")?;
    let several = match a.string("several")? {
        None => None,
        Some(text) if text.eq_ignore_ascii_case("keepFirst") => Some(SeveralValues::KeepFirst),
        Some(text) if text.eq_ignore_ascii_case("clear") => Some(SeveralValues::Clear),
        Some(_) => return Err(ToolError::Failed),
    };
    let mapping = a.objects("mapping", |item| {
        Ok(FieldValueMapping {
            from: item.string("from")?.unwrap_or_default(),
            to: item.string("to")?.unwrap_or_default(),
        })
    })?;
    let choice = if clear_unconvertible == Some(true) || several.is_some() || mapping.is_some() {
        Some(FieldChangeChoice {
            clear_unconvertible: clear_unconvertible == Some(true),
            several,
            mapping,
        })
    } else {
        None
    };
    let command = UpdateField {
        name: a.string("name")?,
        version: Some(a.required_string("version")?),
        field_type: a.string("type")?.map(|t| field_type(&t)).transpose()?,
        multiple: a.bool("multiple")?,
        enum_id: a.guid("enumId")?,
        choice,
    };
    let field = Call::found(
        call.ws()?.fields().update(&project_id, &id, &command)?,
        format!("Field {}", guid_d(&id)),
    )?;
    Ok(Output::Json(json::field(&field)))
}

pub fn delete_field(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let id = call.args.required_guid("fieldId")?;
    let version = call.args.required_string("version")?;
    let deleted = call.ws()?.fields().delete(&project_id, &id, Some(&version))?;
    Call::found(deleted.then_some(()), format!("Field {}", guid_d(&id)))?;
    Ok(Output::Text(DELETED.to_string()))
}

pub fn list_enums(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let page = call.ws()?.enums().get_range(&project_id, page_of(call)?)?;
    Ok(Output::Json(json::page(&page, json::field_enum)))
}

pub fn get_enum(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let text = call.args.required_string("enumeration")?;
    let found = Call::found(call.ws()?.enums().find(&project_id, &text)?, format!("Enum '{text}'"))?;
    Ok(Output::Json(json::field_enum(&found)))
}

pub fn create_enum(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let command = CreateFieldEnum {
        name: call.args.required_string("name")?,
        values: call.args.string_array("values")?.ok_or(ToolError::Failed)?,
    };
    Ok(Output::Json(json::field_enum(&call.ws()?.enums().create(&project_id, &command)?)))
}

pub fn update_enum(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let a = call.args;
    let id = a.required_guid("enumId")?;
    let reassign_to = a.guid("reassignTo")?;
    let removed = match a.string("removedValues")?.map(|t| t.to_ascii_lowercase()) {
        None if reassign_to.is_some() => {
            return Err(ToolError::invalid("ReassignTo: only together with removedValues=reassign"));
        }
        None => None,
        Some(choice) if choice == "clear" && reassign_to.is_some() => {
            return Err(ToolError::invalid("ReassignTo: not with removedValues=clear"));
        }
        Some(choice) if choice == "clear" => Some(RemovedEnumValues::clear()),
        Some(choice) if choice == "reassign" => match reassign_to {
            None => return Err(ToolError::invalid("ReassignTo: required with removedValues=reassign")),
            Some(to) => Some(RemovedEnumValues::reassign(to)),
        },
        Some(_) => return Err(ToolError::Failed),
    };
    let command = UpdateFieldEnum {
        name: a.string("name")?,
        values: a.objects("values", |item| {
            Ok(FieldEnumValueInput {
                id: item.guid("id")?,
                name: item.string("name")?.unwrap_or_default(),
            })
        })?,
        version: Some(a.required_string("version")?),
        removed,
    };
    let result = Call::found(
        call.ws()?.enums().update(&project_id, &id, &command)?,
        format!("Enum {}", guid_d(&id)),
    )?;
    Ok(Output::Json(json::cascade(&result, json::field_enum)))
}

pub fn delete_enum(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let id = call.args.required_guid("enumId")?;
    let version = call.args.required_string("version")?;
    let deleted = call.ws()?.enums().delete(&project_id, &id, Some(&version))?;
    Call::found(deleted.then_some(()), format!("Enum {}", guid_d(&id)))?;
    Ok(Output::Text(DELETED.to_string()))
}
