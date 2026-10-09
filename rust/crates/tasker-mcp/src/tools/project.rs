//! `ProjectTools`: whoami, проекты, пользователи, участники проекта (только сервер), проблемы области.
use super::page_of;
use crate::call::{Call, Output};
use crate::error::{Result, ToolError};
use crate::json;
use tasker_core::ids::{guid_d, parse_guid};
use tasker_services::project::{CreateProject, UpdateProject};

/// `UserService.GetCurrent`: пользователь по id из держателя блокировок (`user:<id>`).
pub fn whoami(call: &Call) -> Result<Output> {
    let ws = call.ws()?;
    let user = ws
        .editor()
        .key
        .strip_prefix("user:")
        .and_then(parse_guid)
        .map(|id| ws.users().get_by_id(&id))
        .transpose()?
        .flatten()
        .ok_or_else(|| ToolError::forbidden("Sign in required"))?;
    Ok(Output::Json(json::user(&user)))
}

pub fn list_projects(call: &Call) -> Result<Output> {
    let page = call.ws()?.projects().get_range(page_of(call)?)?;
    Ok(Output::Json(json::page(&page, json::project)))
}

pub fn get_project(call: &Call) -> Result<Output> {
    let id = call.project_id()?;
    let project = Call::found(call.ws()?.projects().get_by_id(&id)?, format!("Project {}", guid_d(&id)))?;
    Ok(Output::Json(json::project(&project)))
}

pub fn create_project(call: &Call) -> Result<Output> {
    let command = CreateProject {
        name: call.args.required_string("name")?,
    };
    Ok(Output::Json(json::project(&call.ws()?.projects().create(&command)?)))
}

pub fn update_project(call: &Call) -> Result<Output> {
    let id = call.project_id()?;
    let command = UpdateProject {
        name: Some(call.args.required_string("name")?),
        version: Some(call.args.required_string("version")?),
    };
    let project = Call::found(call.ws()?.projects().update(&id, &command)?, format!("Project {}", guid_d(&id)))?;
    Ok(Output::Json(json::project(&project)))
}

pub fn list_users(call: &Call) -> Result<Output> {
    let page = call.ws()?.users().get_range(None, page_of(call)?)?;
    Ok(Output::Json(json::page(&page, json::user)))
}

/// Участники есть только на сервере: в десктопе и демоне всякий проект открыт всем.
pub fn list_project_members(call: &Call) -> Result<Output> {
    call.require_project()?;
    Err(ToolError::invalid("Project members exist only on the server"))
}

pub fn list_workspace_problems(call: &Call) -> Result<Output> {
    let page = call.ws()?.index().problems(page_of(call)?)?;
    Ok(Output::Json(json::page(&page, json::workspace_problem)))
}
