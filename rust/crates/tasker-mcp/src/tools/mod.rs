//! Инструменты по группам — как `Tasker.Mcp/Tools/*.cs`: имена, аргументы и тексты ответов те же.
use crate::call::{Call, Output};
use crate::error::Result;

pub mod board;
pub mod field;
pub mod link;
pub mod lock;
pub mod project;
pub mod series;
pub mod status;
pub mod task;
pub mod workspace;

/// Вызов инструмента по имени; имена — из таблицы ([`crate::catalog`]), поэтому неизвестного здесь быть не может.
pub fn dispatch(call: &Call) -> Result<Output> {
    match call.tool.name.as_str() {
        "list_workspaces" => workspace::list_workspaces(call),

        "whoami" => project::whoami(call),
        "list_projects" => project::list_projects(call),
        "get_project" => project::get_project(call),
        "create_project" => project::create_project(call),
        "update_project" => project::update_project(call),
        "list_users" => project::list_users(call),
        "list_project_members" => project::list_project_members(call),
        "list_workspace_problems" => project::list_workspace_problems(call),

        "list_tasks" => task::list_tasks(call),
        "get_task" => task::get_task(call),
        "create_task" => task::create_task(call),
        "update_task" => task::update_task(call),
        "delete_task" => task::delete_task(call),

        "list_statuses" => status::list_statuses(call),
        "create_status" => status::create_status(call),
        "update_status" => status::update_status(call),
        "delete_status" => status::delete_status(call),
        "list_status_sets" => status::list_status_sets(call),
        "create_status_set" => status::create_status_set(call),
        "update_status_set" => status::update_status_set(call),
        "delete_status_set" => status::delete_status_set(call),
        "list_task_types" => status::list_task_types(call),
        "create_task_type" => status::create_task_type(call),
        "update_task_type" => status::update_task_type(call),
        "delete_task_type" => status::delete_task_type(call),

        "list_series" => series::list_series(call),
        "get_series" => series::get_series(call),
        "create_series" => series::create_series(call),
        "update_series" => series::update_series(call),
        "delete_series" => series::delete_series(call),
        "series_health" => series::series_health(call),
        "add_task_to_series" => series::add_task_to_series(call),
        "remove_task_from_series" => series::remove_task_from_series(call),
        "renumber_task" => series::renumber_task(call),
        "find_tasks_by_reference" => series::find_tasks_by_reference(call),

        "list_link_types" => link::list_link_types(call),
        "create_link_type" => link::create_link_type(call),
        "update_link_type" => link::update_link_type(call),
        "delete_link_type" => link::delete_link_type(call),
        "get_task_links" => link::get_task_links(call),
        "link_tasks" => link::link_tasks(call),
        "unlink_tasks" => link::unlink_tasks(call),

        "list_fields" => field::list_fields(call),
        "get_field" => field::get_field(call),
        "create_field" => field::create_field(call),
        "update_field" => field::update_field(call),
        "delete_field" => field::delete_field(call),
        "list_enums" => field::list_enums(call),
        "get_enum" => field::get_enum(call),
        "create_enum" => field::create_enum(call),
        "update_enum" => field::update_enum(call),
        "delete_enum" => field::delete_enum(call),

        "list_boards" => board::list_boards(call),
        "get_board" => board::get_board(call),
        "move_task" => board::move_task(call),
        "create_board" => board::create_board(call),
        "update_board" => board::update_board(call),
        "delete_board" => board::delete_board(call),

        "lock_entity" => lock::lock_entity(call),
        "unlock_entity" => lock::unlock_entity(call),
        "get_lock" => lock::get_lock(call),

        _ => Err(crate::error::ToolError::Failed),
    }
}

/// Страница из аргументов `offset`/`limit`.
pub(crate) fn page_of(call: &Call) -> Result<tasker_core::tasks::Page> {
    Ok(crate::args::page(call.args.int("offset")?, call.args.int("limit")?))
}

/// `descriptionLength` с умолчанием MCP (200).
pub(crate) fn description_length(call: &Call) -> Result<i32> {
    call.args.int_or("descriptionLength", tasker_services::preview::MCP_DEFAULT)
}

pub(crate) const DELETED: &str = "deleted";
