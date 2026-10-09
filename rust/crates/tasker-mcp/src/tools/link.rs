//! `LinkTools`: связи между задачами одного проекта и настраиваемые типы связей.
use super::{DELETED, page_of};
use crate::call::{Call, Output};
use crate::error::Result;
use crate::json;
use tasker_core::ids::guid_d;
use tasker_services::link_type::{CreateLinkType, UpdateLinkType};
use tasker_services::links::LinkDirection;

pub fn list_link_types(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let page = call.ws()?.link_types().get_range(&project_id, page_of(call)?)?;
    Ok(Output::Json(json::page(&page, json::link_type)))
}

pub fn create_link_type(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let command = CreateLinkType {
        name: call.args.required_string("name")?,
        outward_name: call.args.required_string("outwardName")?,
        inward_name: call.args.string("inwardName")?,
        allow_cycles: call.args.bool("allowCycles")?,
        hierarchical: call.args.bool("hierarchical")?,
    };
    Ok(Output::Json(json::link_type(
        &call.ws()?.link_types().create(&project_id, &command)?,
    )))
}

pub fn update_link_type(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let id = call.args.required_guid("linkTypeId")?;
    let command = UpdateLinkType {
        name: call.args.string("name")?,
        outward_name: call.args.string("outwardName")?,
        inward_name: call.args.string("inwardName")?,
        version: Some(call.args.required_string("version")?),
        allow_cycles: call.args.bool("allowCycles")?,
        hierarchical: call.args.bool("hierarchical")?,
    };
    let link_type = Call::found(
        call.ws()?.link_types().update(&project_id, &id, &command)?,
        format!("Link type {}", guid_d(&id)),
    )?;
    Ok(Output::Json(json::link_type(&link_type)))
}

pub fn delete_link_type(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let id = call.args.required_guid("linkTypeId")?;
    let version = call.args.required_string("version")?;
    let deleted = call.ws()?.link_types().delete(&project_id, &id, Some(&version))?;
    Call::found(deleted.then_some(()), format!("Link type {}", guid_d(&id)))?;
    Ok(Output::Text(DELETED.to_string()))
}

fn links_object(views: &[tasker_services::links::TaskLinkView]) -> Output {
    Output::Json(json::object(vec![("links", json::link_views(views))]))
}

pub fn get_task_links(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let text = call.args.required_string("taskId")?;
    let task_id = call.task_id(&project_id, &text)?;
    let views = Call::found(call.ws()?.links().get_links(&project_id, &task_id)?, format!("Task {text}"))?;
    Ok(links_object(&views))
}

/// «A is blocked by B» хранится как «B blocks A»: источник — тот, от кого связь исходит.
fn ends(call: &Call) -> Result<(uuid::Uuid, uuid::Uuid, uuid::Uuid, uuid::Uuid, uuid::Uuid, String)> {
    let project_id = call.require_project()?;
    let text = call.args.required_string("taskId")?;
    let link = call.args.required_string("link")?;
    let other_text = call.args.required_string("otherTaskId")?;
    let task = call.task_id(&project_id, &text)?;
    let other = call.task_id(&project_id, &other_text)?;
    let (link_type, direction) = call.ws()?.link_types().resolve(&project_id, &link)?;
    let (source, target) = if direction == LinkDirection::Outward {
        (task, other)
    } else {
        (other, task)
    };
    Ok((project_id, task, link_type.id, source, target, text))
}

pub fn link_tasks(call: &Call) -> Result<Output> {
    let (project_id, task, type_id, source, target, _) = ends(call)?;
    let links = call.ws()?.links();
    Call::found(
        links.add(&project_id, &source, &type_id, &target, None)?,
        format!("Task {}", guid_d(&source)),
    )?;
    Ok(links_object(&links.get_links(&project_id, &task)?.unwrap_or_default()))
}

pub fn unlink_tasks(call: &Call) -> Result<Output> {
    let (project_id, task, type_id, source, target, text) = ends(call)?;
    let links = call.ws()?.links();
    Call::found(
        links.remove(&project_id, &source, &type_id, &target, None)?,
        format!("Task {}", guid_d(&source)),
    )?;
    let views = Call::found(links.get_links(&project_id, &task)?, format!("Task {text}"))?;
    Ok(links_object(&views))
}
