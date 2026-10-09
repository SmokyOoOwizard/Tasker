//! `StatusTools`: статусы, наборы статусов и типы задач — задача → тип → набор статусов → статусы.
use super::{DELETED, description_length, page_of};
use crate::call::{Call, Output};
use crate::error::{Result, ToolError};
use crate::json;
use tasker_core::ids::guid_d;
use tasker_core::model::TaskTypeField;
use tasker_services::status::{CreateStatus, UpdateStatus};
use tasker_services::status_set::{CreateStatusSet, UpdateStatusSet};
use tasker_services::task_type::{CreateTaskType, RemovedFieldValues, UpdateTaskType};

pub fn list_statuses(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let page = call.ws()?.statuses().list(&project_id, page_of(call)?, description_length(call)?)?;
    Ok(Output::Json(json::page(&page, json::status_list_item)))
}

pub fn create_status(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let command = CreateStatus {
        name: call.args.required_string("name")?,
        color: call.args.required_string("color")?,
        description: call.args.string("description")?,
    };
    Ok(Output::Json(json::status(&call.ws()?.statuses().create(&project_id, &command)?)))
}

pub fn update_status(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let id = call.args.required_guid("statusId")?;
    let command = UpdateStatus {
        name: call.args.string("name")?,
        color: call.args.string("color")?,
        version: Some(call.args.required_string("version")?),
        description: call.args.string("description")?,
    };
    let status = Call::found(
        call.ws()?.statuses().update(&project_id, &id, &command)?,
        format!("Status {}", guid_d(&id)),
    )?;
    Ok(Output::Json(json::status(&status)))
}

pub fn delete_status(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let id = call.args.required_guid("statusId")?;
    let version = call.args.required_string("version")?;
    let deleted = call.ws()?.statuses().delete(&project_id, &id, Some(&version))?;
    Call::found(deleted.then_some(()), format!("Status {}", guid_d(&id)))?;
    Ok(Output::Text(DELETED.to_string()))
}

pub fn list_status_sets(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let page = call.ws()?.status_sets().get_range(&project_id, page_of(call)?)?;
    Ok(Output::Json(json::page(&page, json::status_set)))
}

pub fn create_status_set(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let command = CreateStatusSet {
        name: call.args.required_string("name")?,
        status_ids: call.args.guid_array("statusIds")?.ok_or(ToolError::Failed)?,
    };
    Ok(Output::Json(json::status_set(
        &call.ws()?.status_sets().create(&project_id, &command)?,
    )))
}

pub fn update_status_set(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let id = call.args.required_guid("statusSetId")?;
    let command = UpdateStatusSet {
        name: call.args.string("name")?,
        status_ids: call.args.guid_array("statusIds")?,
        version: Some(call.args.required_string("version")?),
    };
    let set = Call::found(
        call.ws()?.status_sets().update(&project_id, &id, &command)?,
        format!("Status set {}", guid_d(&id)),
    )?;
    Ok(Output::Json(json::status_set(&set)))
}

pub fn delete_status_set(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let id = call.args.required_guid("statusSetId")?;
    let version = call.args.required_string("version")?;
    let deleted = call.ws()?.status_sets().delete(&project_id, &id, Some(&version))?;
    Call::found(deleted.then_some(()), format!("Status set {}", guid_d(&id)))?;
    Ok(Output::Text(DELETED.to_string()))
}

pub fn list_task_types(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let page = call
        .ws()?
        .task_types()
        .list(&project_id, page_of(call)?, description_length(call)?)?;
    Ok(Output::Json(json::page(&page, json::task_type_list_item)))
}

/// `TaskTypeField[]` из аргумента `fields`; пропущенное свойство записи — значение по умолчанию (`Guid.Empty`, `false`), как у
/// `JsonSerializer`.
fn type_fields(call: &Call) -> Result<Option<Vec<TaskTypeField>>> {
    call.args.objects("fields", |item| {
        Ok(TaskTypeField {
            field_id: item.guid("fieldId")?.unwrap_or_default(),
            required: item.bool("required")?.unwrap_or(false),
        })
    })
}

pub fn create_task_type(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let command = CreateTaskType {
        name: call.args.required_string("name")?,
        status_set_id: call.args.required_guid("statusSetId")?,
        fields: type_fields(call)?,
        description: call.args.string("description")?,
    };
    Ok(Output::Json(json::task_type(
        &call.ws()?.task_types().create(&project_id, &command)?,
    )))
}

pub fn update_task_type(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let id = call.args.required_guid("taskTypeId")?;
    let removed_fields = match call.args.string("removedFields")? {
        None => None,
        Some(text) if text.eq_ignore_ascii_case("clear") => Some(RemovedFieldValues::Clear),
        Some(text) if text.eq_ignore_ascii_case("keep") => Some(RemovedFieldValues::Keep),
        Some(_) => return Err(ToolError::Failed),
    };
    let command = UpdateTaskType {
        name: call.args.string("name")?,
        status_set_id: call.args.guid("statusSetId")?,
        version: Some(call.args.required_string("version")?),
        fields: type_fields(call)?,
        removed_fields,
        description: call.args.string("description")?,
    };
    let result = Call::found(
        call.ws()?.task_types().update(&project_id, &id, &command)?,
        format!("Task type {}", guid_d(&id)),
    )?;
    Ok(Output::Json(json::cascade(&result, json::task_type)))
}

pub fn delete_task_type(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let id = call.args.required_guid("taskTypeId")?;
    let version = call.args.required_string("version")?;
    let deleted = call.ws()?.task_types().delete(&project_id, &id, Some(&version))?;
    Call::found(deleted.then_some(()), format!("Task type {}", guid_d(&id)))?;
    Ok(Output::Text(DELETED.to_string()))
}
