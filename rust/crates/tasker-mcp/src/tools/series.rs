//! `SeriesTools`: серии задач (`ПРЕФИКС-номер`), номера у задач, состояние серий и поиск по ссылке.
use super::{DELETED, description_length, page_of};
use crate::call::{Call, Output};
use crate::error::Result;
use crate::json;
use serde_json::Value;
use tasker_core::ids::guid_d;
use tasker_core::model::Series;
use tasker_services::series::{CreateSeries, UpdateSeries};
use uuid::Uuid;

pub fn list_series(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let page = call.ws()?.series().get_range(&project_id, page_of(call)?)?;
    Ok(Output::Json(json::page(&page, json::series)))
}

/// Серия по id или префиксу; нет — `[not_found] Series '<text>' not found`.
fn find_series(call: &Call, project_id: &Uuid) -> Result<(Series, String)> {
    let text = call.args.required_string("series")?;
    let found = Call::found(call.ws()?.series().find(project_id, &text)?, format!("Series '{text}'"))?;
    Ok((found, text))
}

pub fn get_series(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let (series, _) = find_series(call, &project_id)?;
    Ok(Output::Json(json::series(&series)))
}

pub fn create_series(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let command = CreateSeries {
        name: call.args.required_string("name")?,
        prefix: call.args.required_string("prefix")?,
    };
    Ok(Output::Json(json::series(&call.ws()?.series().create(&project_id, &command)?)))
}

pub fn update_series(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let id = call.args.required_guid("seriesId")?;
    let command = UpdateSeries {
        name: call.args.string("name")?,
        prefix: call.args.string("prefix")?,
        version: Some(call.args.required_string("version")?),
    };
    let series = Call::found(
        call.ws()?.series().update(&project_id, &id, &command)?,
        format!("Series {}", guid_d(&id)),
    )?;
    Ok(Output::Json(json::series(&series)))
}

pub fn delete_series(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let id = call.args.required_guid("seriesId")?;
    let version = call.args.required_string("version")?;
    let deleted = call.ws()?.series().delete(&project_id, &id, Some(&version))?;
    Call::found(deleted.then_some(()), format!("Series {}", guid_d(&id)))?;
    Ok(Output::Text(DELETED.to_string()))
}

pub fn series_health(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let health = call.ws()?.series_health().check(&project_id)?;
    Ok(Output::Json(json::series_health(&health)))
}

pub fn add_task_to_series(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let (series, _) = find_series(call, &project_id)?;
    let text = call.args.required_string("taskId")?;
    let version = call.args.required_string("version")?;
    let task_id = call.task_id(&project_id, &text)?;
    let task = Call::found(
        call.ws()?
            .tasks()
            .add_to_series(&project_id, &task_id, &series.id, Some(&version))?,
        format!("Task {text}"),
    )?;
    Ok(Output::Json(json::task(&task)))
}

pub fn remove_task_from_series(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let (series, _) = find_series(call, &project_id)?;
    let text = call.args.required_string("taskId")?;
    let version = call.args.required_string("version")?;
    let task_id = call.task_id(&project_id, &text)?;
    let task = Call::found(
        call.ws()?
            .tasks()
            .remove_from_series(&project_id, &task_id, &series.id, Some(&version))?,
        format!("Task {text}"),
    )?;
    Ok(Output::Json(json::task(&task)))
}

pub fn renumber_task(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let (series, series_text) = find_series(call, &project_id)?;
    let text = call.args.required_string("taskId")?;
    let version = call.args.required_string("version")?;
    let to = call.args.int("to")?;
    let task_id = call.task_id(&project_id, &text)?;
    let task = Call::found(
        call.ws()?.tasks().renumber(&project_id, &task_id, &series.id, to, Some(&version))?,
        format!("Task {text} in series '{series_text}'"),
    )?;
    Ok(Output::Json(json::task(&task)))
}

pub fn find_tasks_by_reference(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let reference = call.args.required_string("reference")?;
    let tasks = call.ws()?.tasks();
    let found = tasks.resolve(&project_id, &reference, true)?;
    let items = tasks.preview(&project_id, found, description_length(call)?)?;
    Ok(Output::Json(json::object(vec![(
        "tasks",
        Value::Array(items.iter().map(json::task_list_item).collect()),
    )])))
}
