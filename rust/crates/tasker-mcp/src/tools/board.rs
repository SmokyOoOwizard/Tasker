//! `BoardTools`: доски, доска с задачами по колонкам, перенос задачи в колонку.
use super::{DELETED, description_length, page_of};
use crate::args::{Args, guid_value, page};
use crate::call::{Call, Output};
use crate::error::{Result, ToolError};
use crate::json;
use serde_json::Value;
use tasker_core::ids::{guid_d, parse_guid};
use tasker_services::board::{BoardColumnInput, CreateBoard, MoveTask, UpdateBoard};

pub fn list_boards(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let page = call.ws()?.boards().get_range(&project_id, page_of(call)?)?;
    Ok(Output::Json(json::page(&page, json::board)))
}

/// Доска с задачами по колонкам — агенту удобнее одним вызовом, чем колонка за колонкой (`BoardView`).
pub fn get_board(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let board_id = call.args.required_guid("boardId")?;
    let boards = call.ws()?.boards();
    let board = Call::found(boards.get_by_id(&project_id, &board_id)?, format!("Board {}", guid_d(&board_id)))?;
    let page = page(Some(0), Some(call.args.int_or("tasksPerColumn", crate::args::PAGE_DEFAULT_LIMIT)?));
    let field = call.args.string_array("field")?;
    let length = description_length(call)?;
    let sort = call.args.string("sort")?;

    let mut columns = Vec::new();
    for column in &board.columns {
        let tasks = boards.column_tasks(&project_id, &board_id, &column.id, page, field.as_deref(), length, sort.as_deref())?;
        let filters = boards.describe_filters(&project_id, column)?;
        let tasks = Call::found(tasks, format!("Column {}", guid_d(&column.id)))?;
        columns.push(json::object(vec![
            ("columnId", json::guid(&column.id)),
            ("name", Value::String(column.name.clone())),
            ("fieldFilters", json::strings(&filters)),
            ("tasks", json::page(&tasks, json::task_list_item)),
        ]));
    }
    Ok(Output::Json(json::object(vec![
        ("board", json::board(&board)),
        ("columns", Value::Array(columns)),
    ])))
}

pub fn move_task(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let board_id = call.args.required_guid("boardId")?;
    let column_id = call.args.required_guid("columnId")?;
    let text = call.args.required_string("taskId")?;
    let version = call.args.required_string("version")?;
    let command = MoveTask {
        task_id: call.task_id(&project_id, &text)?,
        version: Some(version),
    };
    let task = Call::found(
        call.ws()?.boards().move_task(&project_id, &board_id, &column_id, &command)?,
        "Board, column or task",
    )?;
    Ok(Output::Json(json::task(&task)))
}

/// `BoardColumnInput` из элемента `columns`: `dropStatuses` — объект «набор → статус».
fn column_input(item: Args) -> Result<BoardColumnInput> {
    let drop_statuses = match item.object("dropStatuses")? {
        None => None,
        Some(map) => Some(
            map.0
                .iter()
                .map(|(set, status)| Ok((parse_guid(set).ok_or(ToolError::Failed)?, guid_value(status)?)))
                .collect::<Result<Vec<_>>>()?,
        ),
    };
    Ok(BoardColumnInput {
        id: item.guid("id")?,
        name: item.string("name")?.unwrap_or_default(),
        status_ids: item.guid_array("statusIds")?.unwrap_or_default(),
        drop_statuses,
        field_filters: item.string_array("fieldFilters")?,
    })
}

pub fn create_board(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let command = CreateBoard {
        name: call.args.required_string("name")?,
        status_set_ids: call.args.guid_array("statusSetIds")?.ok_or(ToolError::Failed)?,
        columns: call.args.objects("columns", column_input)?.ok_or(ToolError::Failed)?,
    };
    Ok(Output::Json(json::board(&call.ws()?.boards().create(&project_id, &command)?)))
}

pub fn update_board(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let id = call.args.required_guid("boardId")?;
    let command = UpdateBoard {
        name: call.args.string("name")?,
        status_set_ids: call.args.guid_array("statusSetIds")?,
        columns: call.args.objects("columns", column_input)?,
        version: Some(call.args.required_string("version")?),
    };
    let board = Call::found(
        call.ws()?.boards().update(&project_id, &id, &command)?,
        format!("Board {}", guid_d(&id)),
    )?;
    Ok(Output::Json(json::board(&board)))
}

pub fn delete_board(call: &Call) -> Result<Output> {
    let project_id = call.require_project()?;
    let id = call.args.required_guid("boardId")?;
    let version = call.args.required_string("version")?;
    let deleted = call.ws()?.boards().delete(&project_id, &id, Some(&version))?;
    Call::found(deleted.then_some(()), format!("Board {}", guid_d(&id)))?;
    Ok(Output::Text(DELETED.to_string()))
}
