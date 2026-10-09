//! `WorkspaceTools.list_workspaces`: области, к которым этот сервер даёт доступ, со статусом и числом проектов у открытых.
use crate::call::{Call, Output};
use crate::error::Result;
use crate::json;
use crate::workspaces::McpWorkspaceStatus;
use serde_json::Value;
use tasker_core::tasks::Page;

pub fn list_workspaces(call: &Call) -> Result<Output> {
    let mut result = Vec::new();
    for workspace in call.catalog.list() {
        let projects = if workspace.status == McpWorkspaceStatus::Open {
            match call.catalog.enter(&workspace.name) {
                Some(scope) => Some(scope.workspace.projects().get_range(Page::new(0, 1))?.total_count),
                None => None,
            }
        } else {
            None
        };
        result.push(json::object(vec![
            ("name", Value::String(workspace.name.clone())),
            ("path", Value::String(workspace.path.clone())),
            ("status", Value::String(workspace.status.json_name().to_string())),
            ("projects", projects.map(Value::from).unwrap_or(Value::Null)),
            ("error", json::string_opt(&workspace.error)),
        ]));
    }
    Ok(Output::Json(json::object(vec![("workspaces", Value::Array(result))])))
}
