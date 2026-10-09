//! `TaskTools`: список с фильтрами и порядком, задача с видами полей и связей, создание, правка, удаление.
use super::{DELETED, description_length, page_of};
use crate::args::Args;
use crate::call::{Call, Output};
use crate::error::{Result, ToolError};
use crate::json;
use tasker_core::model::FieldType;
use tasker_core::tasks::TaskFilter;
use tasker_services::task::{CreateTask, UpdateTask};
use tasker_services::task_fields::{NewOwnField, TaskFieldChanges, TaskFieldValueInput};
use uuid::Uuid;

/// `TaskTools.Merge`: единичный параметр и массив одного фильтра вместе; None, если не задано ни то, ни другое.
pub(crate) fn merge(single: Option<Uuid>, many: Option<Vec<Uuid>>) -> Option<Vec<Uuid>> {
    match single {
        None => many,
        Some(one) => {
            let mut all = many.unwrap_or_default();
            all.push(one);
            Some(all)
        }
    }
}

/// `TaskFieldChanges` из объекта `fields`.
pub(crate) fn field_changes(args: Option<Args>) -> Result<Option<TaskFieldChanges>> {
    let Some(args) = args else {
        return Ok(None);
    };
    Ok(Some(TaskFieldChanges {
        values: args.objects("values", |item| {
            Ok(TaskFieldValueInput {
                field_id: item.required_guid("fieldId")?,
                values: item.string_array("values")?,
            })
        })?,
        add_fields: args.guid_array("addFields")?,
        new_own_fields: args.objects("newOwnFields", |item| {
            Ok(NewOwnField {
                name: item.string("name")?.unwrap_or_default(),
                field_type: field_type(&item.required_string("type")?)?,
                values: item.string_array("values")?,
                required: item.bool("required")?.unwrap_or(false),
                multiple: item.bool("multiple")?.unwrap_or(false),
                enum_id: item.guid("enumId")?,
            })
        })?,
        remove_fields: args.guid_array("removeFields")?,
    }))
}

/// `FieldType` из текста аргумента (`JsonStringEnumConverter`: без учёта регистра).
pub(crate) fn field_type(text: &str) -> Result<FieldType> {
    FieldType::parse(text).ok_or(ToolError::Failed)
}

pub fn list_tasks(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let a = call.args;
    let filter = TaskFilter::of(
        merge(a.guid("typeId")?, a.guid_array("typeIds")?).as_deref(),
        merge(a.guid("statusId")?, a.guid_array("statusIds")?).as_deref(),
        merge(a.guid("seriesId")?, a.guid_array("seriesIds")?).as_deref(),
    );
    let field = a.string_array("field")?;
    let sort = a.string("sort")?;
    let page = call.ws()?.tasks().list(
        &project_id,
        filter.as_ref(),
        field.as_deref(),
        page_of(call)?,
        description_length(call)?,
        sort.as_deref(),
    )?;
    Ok(Output::Json(json::page(&page, json::task_list_item)))
}

pub fn get_task(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let text = call.args.required_string("taskId")?;
    let id = call.task_id(&project_id, &text)?;
    let details = Call::found(call.ws()?.tasks().describe_by_id(&project_id, &id)?, format!("Task {text}"))?;
    Ok(Output::Json(json::task_details(&details)))
}

pub fn create_task(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let a = call.args;
    let command = CreateTask {
        title: a.required_string("title")?,
        description: a.string("description")?,
        type_id: a.required_guid("typeId")?,
        status_id: a.guid("statusId")?,
        series_ids: a.guid_array("seriesIds")?,
        fields: field_changes(a.object("fields")?)?,
    };
    let tasks = call.ws()?.tasks();
    let created = tasks.create(&project_id, &command)?;
    Ok(Output::Json(json::task_details(&tasks.describe(&project_id, &created)?)))
}

pub fn update_task(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let a = call.args;
    let text = a.required_string("taskId")?;
    let id = call.task_id(&project_id, &text)?;
    let command = UpdateTask {
        title: a.string("title")?,
        description: a.string("description")?,
        type_id: a.guid("typeId")?,
        status_id: a.guid("statusId")?,
        version: Some(a.required_string("version")?),
        fields: field_changes(a.object("fields")?)?,
    };
    let tasks = call.ws()?.tasks();
    let updated = Call::found(tasks.update(&project_id, &id, &command)?, format!("Task {text}"))?;
    Ok(Output::Json(json::task_details(&tasks.describe(&project_id, &updated)?)))
}

pub fn delete_task(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let text = call.args.required_string("taskId")?;
    let version = call.args.required_string("version")?;
    let id = call.task_id(&project_id, &text)?;
    let deleted = call.ws()?.tasks().delete(&project_id, &id, Some(&version))?;
    Call::found(deleted.then_some(()), format!("Task {text}"))?;
    Ok(Output::Text(DELETED.to_string()))
}
