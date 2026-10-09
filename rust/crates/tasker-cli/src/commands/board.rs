//! `tasker board …` (`BoardCommands` в .NET).
use super::find_board;
use super::task::LineFormat;
use crate::context::Context;
use crate::entities;
use crate::errors::{CliError, Result};
use crate::kit;
use crate::session::refs;
use clap::ArgMatches;
use serde_json::Value;
use tasker_core::ids::guid_d;
use tasker_core::model::{Board, BoardColumn, Status, StatusSet};
use tasker_core::tasks::{ListPage, Page};
use tasker_services::board::{BoardColumnInput, CreateBoard, UpdateBoard};
use tasker_services::preview::TaskListItem;
use uuid::Uuid;

/// Колонка доски по id или названию (без учёта регистра).
fn find_column<'a>(board: &'a Board, reference: &str) -> Result<&'a BoardColumn> {
    refs::find_item(
        &board.columns,
        reference,
        |x| x.id,
        |x| &x.name,
        &format!("column of board '{}'", board.name),
        false,
    )
}

/// Задачи колонки страницей.
#[allow(clippy::too_many_arguments)]
fn column_tasks(
    ctx: &Context<'_>,
    project_id: &Uuid,
    board: &Board,
    column: &BoardColumn,
    page: Page,
    field_filters: Option<&[String]>,
    description_length: i32,
    sort: Option<&str>,
) -> Result<ListPage<TaskListItem>> {
    ctx.session()
        .workspace()
        .boards()
        .column_tasks(project_id, &board.id, &column.id, page, field_filters, description_length, sort)?
        .ok_or_else(|| CliError::new(format!("No column '{}' on board '{}'", column.name, board.name)))
}

/// Все задачи колонки: страницами по 200, одним списком.
fn read_all(mut load_page: impl FnMut(Page) -> Result<ListPage<TaskListItem>>) -> Result<ListPage<TaskListItem>> {
    let mut items: Vec<TaskListItem> = Vec::new();
    loop {
        let page = load_page(Page::new(items.len(), kit::MAX_LIMIT))?;
        let got = page.data.len();
        items.extend(page.data);
        if got == 0 || items.len() >= page.total_count {
            break;
        }
    }
    Ok(ListPage {
        total_count: items.len(),
        offset: 0,
        limit: items.len(),
        data: items,
    })
}

/// Название колонки в `--column "Название=статус,статус"`.
fn column_name(spec: &str) -> String {
    match spec.find('=') {
        Some(0) | None => spec.trim().to_string(),
        Some(at) => spec[..at].trim().to_string(),
    }
}

/// Раскладывает `--column-filter "Колонка:условие"` по колонкам: название — самое длинное из названий, за которым стоит «:».
/// Пустое условие («Колонка:») — убрать условия колонки. None у колонки — для неё ничего не задано.
fn assign_filters(specs: Option<&[String]>, names: &[String]) -> Result<Vec<Option<Vec<String>>>> {
    let mut result: Vec<Option<Vec<String>>> = vec![None; names.len()];
    for spec in specs.unwrap_or_default() {
        let text = spec.trim_start();
        let mut index: Option<usize> = None;
        for (i, name) in names.iter().enumerate() {
            let head = format!("{name}:");
            if text.len() >= head.len()
                && text.is_char_boundary(head.len())
                && tasker_core::validate::eq_ignore_case(&text[..head.len()], &head)
                && index.is_none_or(|current| name.len() > names[current].len())
            {
                index = Some(i);
            }
        }
        let Some(index) = index else {
            return Err(CliError::new(format!(
                "--column-filter '{spec}': expected \"Column:condition\" with the name of one of the columns ({})",
                names.join(", ")
            )));
        };
        let condition = text[names[index].len() + 1..].trim();
        let entry = result[index].get_or_insert_with(Vec::new);
        if !condition.is_empty() {
            entry.push(condition.to_string());
        }
    }
    Ok(result)
}

fn parse_column(
    spec: &str,
    sets: &[StatusSet],
    statuses: &[Status],
    board_set_ids: &[Uuid],
    existing: &[BoardColumn],
    filters: Option<Vec<String>>,
) -> Result<BoardColumnInput> {
    let Some(separator) = spec.find('=').filter(|at| *at > 0) else {
        return Err(CliError::new(format!("Column '{spec}': expected \"Name=status,status\"")));
    };
    let references: Vec<String> = spec[separator + 1..]
        .split(',')
        .map(str::trim)
        .filter(|x| !x.is_empty())
        .map(str::to_string)
        .collect();
    let status_ids = refs::find_all(statuses, &references, |x| x.id, |x| &x.name, "status")?;

    // Правило переноса: для каждого набора доски — первый статус колонки из этого набора.
    let mut drop: Vec<(Uuid, Uuid)> = Vec::new();
    for set_id in board_set_ids {
        if let Some(set) = sets.iter().find(|x| x.id == *set_id)
            && let Some(status_id) = status_ids.iter().find(|x| set.status_ids.contains(x))
        {
            drop.push((*set_id, *status_id));
        }
    }

    // Колонка с именем существующей остаётся той же колонкой, а не удаляется и создаётся заново.
    let name = spec[..separator].trim().to_string();
    let same = existing.iter().find(|x| tasker_core::validate::eq_ignore_case(&x.name, &name));
    Ok(BoardColumnInput {
        id: same.map(|x| x.id),
        name,
        status_ids,
        drop_statuses: Some(drop),
        field_filters: filters,
    })
}

pub fn run(ctx: &mut Context<'_>, command: &str, leaf: &ArgMatches) -> Result<()> {
    let project_id = ctx.project_id()?;
    let ws = ctx.session().workspace().clone();
    match command {
        "create" => {
            let all_sets = ws.status_sets().get_all(&project_id)?;
            let all_statuses = ws.statuses().get_all(&project_id)?;
            let set_ids = refs::find_all(
                &all_sets,
                &kit::values_or_empty(leaf, "status-set"),
                |x| x.id,
                |x| &x.name,
                "status set",
            )?;
            let specs = kit::values_or_empty(leaf, "column");
            let names: Vec<String> = specs.iter().map(|x| column_name(x)).collect();
            let filters = assign_filters(kit::values(leaf, "column-filter").as_deref(), &names)?;
            let mut inputs = Vec::new();
            for (spec, filter) in specs.iter().zip(filters) {
                inputs.push(parse_column(spec, &all_sets, &all_statuses, &set_ids, &[], filter)?);
            }
            let board = ws.boards().create(
                &project_id,
                &CreateBoard {
                    name: kit::required(leaf, "name"),
                    status_set_ids: set_ids,
                    columns: inputs,
                },
            )?;
            ctx.print(
                &entities::board(&board),
                &format!("Created board '{}' {}", board.name, guid_d(&board.id)),
            );
        }
        "list" => {
            let list = kit::Paging::load(leaf, |page| Ok(ws.boards().get_range(&project_id, page)?))?;
            ctx.print_list(&list, entities::board, |x| {
                let columns: Vec<&str> = x.columns.iter().map(|c| c.name.as_str()).collect();
                kit::row(&x.id, &[&x.name, &format!("({})", columns.join(" | "))])
            });
        }
        "get" => {
            let board = find_board(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let all_sets = ws.status_sets().get_all(&project_id)?;
            let all_statuses = ws.statuses().get_all(&project_id)?;
            let mut column_lines: Vec<String> = Vec::new();
            for c in &board.columns {
                let where_ = ws.boards().describe_filters(&project_id, c)?;
                let statuses: Vec<String> = c
                    .status_ids
                    .iter()
                    .map(|s| kit::named(&all_statuses, s, |x| x.id, |x| &x.name))
                    .collect();
                column_lines.push(format!(
                    "  {}: {}{}",
                    c.name,
                    statuses.join(", "),
                    if where_.is_empty() {
                        String::new()
                    } else {
                        format!("; where {}", where_.join(" and "))
                    }
                ));
            }
            let sets: Vec<String> = board
                .status_set_ids
                .iter()
                .map(|x| format!("  {}", kit::named(&all_sets, x, |s| s.id, |s| &s.name)))
                .collect();
            let text = format!(
                "{}\nstatus sets:\n{}\ncolumns:\n{}",
                kit::fields(&[
                    ("id", Some(guid_d(&board.id))),
                    ("name", Some(board.name.clone())),
                    ("version", Some(board.version.clone())),
                ]),
                sets.join("\n"),
                column_lines.join("\n")
            );
            ctx.print(&entities::board(&board), &text);
        }
        "tasks" => {
            let board = find_board(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let column = find_column(&board, &kit::required(leaf, "column"))?.clone();
            let line = LineFormat::load(ctx, &project_id)?;
            let fields = kit::values_or_empty(leaf, "field");
            let length = kit::description_length(leaf);
            let sort = kit::text(leaf, "sort");
            let list = kit::Paging::load(leaf, |page| {
                column_tasks(ctx, &project_id, &board, &column, page, Some(&fields), length, sort.as_deref())
            })?;
            ctx.print_list(&list, entities::task_list_item, |x| line.cells(&x.task));
        }
        "show" => {
            let all = kit::flag(leaf, "all");
            if all && kit::given(leaf, "limit") {
                return Err(CliError::new("Use either --all or --limit, not both"));
            }
            let board = find_board(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let line = LineFormat::load(ctx, &project_id)?;
            let fields = kit::values_or_empty(leaf, "field");
            let length = kit::description_length(leaf);
            let sort = kit::text(leaf, "sort");
            let limit = kit::int(leaf, "limit", kit::DEFAULT_LIMIT as i64);

            let mut shown: Vec<(BoardColumn, ListPage<TaskListItem>, Vec<String>)> = Vec::new();
            for item in &board.columns {
                // С --all читаем колонку целиком (страницами по 200), иначе — первые --limit задач.
                let tasks = if all {
                    read_all(|page| column_tasks(ctx, &project_id, &board, item, page, Some(&fields), length, sort.as_deref()))?
                } else {
                    column_tasks(
                        ctx,
                        &project_id,
                        &board,
                        item,
                        kit::page_of(0, limit),
                        Some(&fields),
                        length,
                        sort.as_deref(),
                    )?
                };
                let where_ = ws.boards().describe_filters(&project_id, item)?;
                shown.push((item.clone(), tasks, where_));
            }

            // Колонки таблицы общие на всю доску: ссылки, статусы и типы выровнены и между колонками доски.
            let all_rows: Vec<Vec<String>> = shown
                .iter()
                .flat_map(|(_, tasks, _)| tasks.data.iter().map(|x| line.cells(&x.task)))
                .collect();
            let rows = ctx.table(&all_rows, "  ");
            let mut next = 0;
            let mut lines: Vec<String> = Vec::new();
            for (item, tasks, where_) in &shown {
                if !lines.is_empty() {
                    lines.push(String::new());
                }
                lines.push(format!(
                    "{}{} ({})",
                    item.name,
                    if where_.is_empty() {
                        String::new()
                    } else {
                        format!(" [{}]", where_.join(" and "))
                    },
                    tasks.total_count
                ));
                lines.extend(rows[next..next + tasks.data.len()].iter().cloned());
                next += tasks.data.len();
                if tasks.data.is_empty() {
                    lines.push("  (no tasks)".into());
                } else if tasks.data.len() < tasks.total_count {
                    lines.push(format!(
                        "  ... and {} more (use --all, or 'board tasks {} \"{}\"')",
                        tasks.total_count - tasks.data.len(),
                        board.name,
                        item.name
                    ));
                }
            }

            let value = crate::json::object(vec![
                (
                    "board",
                    crate::json::object(vec![
                        ("id", crate::json::id(&board.id)),
                        ("name", Value::String(board.name.clone())),
                    ]),
                ),
                (
                    "columns",
                    Value::Array(
                        shown
                            .iter()
                            .map(|(column, tasks, where_)| {
                                crate::json::object(vec![
                                    ("id", crate::json::id(&column.id)),
                                    ("name", Value::String(column.name.clone())),
                                    (
                                        "fieldFilters",
                                        Value::Array(where_.iter().map(|w| Value::String(w.clone())).collect()),
                                    ),
                                    ("totalCount", Value::from(tasks.total_count)),
                                    ("tasks", Value::Array(tasks.data.iter().map(entities::task_list_item).collect())),
                                ])
                            })
                            .collect(),
                    ),
                ),
            ]);
            ctx.print(&value, &lines.join("\n"));
        }
        "update" => {
            kit::require_change(leaf, &["name", "status-set", "column", "column-filter"])?;
            let board = find_board(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let all_sets = ws.status_sets().get_all(&project_id)?;
            let all_statuses = ws.statuses().get_all(&project_id)?;
            let set_ids = match kit::values(leaf, "status-set") {
                Some(references) => Some(refs::find_all(&all_sets, &references, |x| x.id, |x| &x.name, "status set")?),
                None => None,
            };
            let specs = kit::values(leaf, "column");
            let filter_specs = kit::values(leaf, "column-filter");
            let mut inputs: Option<Vec<BoardColumnInput>> = None;
            if let Some(specs) = &specs {
                let names: Vec<String> = specs.iter().map(|x| column_name(x)).collect();
                let filters = assign_filters(filter_specs.as_deref(), &names)?;
                let board_sets = set_ids.clone().unwrap_or_else(|| board.status_set_ids.clone());
                let mut list = Vec::new();
                for (spec, filter) in specs.iter().zip(filters) {
                    list.push(parse_column(spec, &all_sets, &all_statuses, &board_sets, &board.columns, filter)?);
                }
                inputs = Some(list);
            } else if let Some(filter_specs) = &filter_specs {
                // Только условия: колонки остаются как есть, условия меняются у названных.
                let names: Vec<String> = board.columns.iter().map(|x| x.name.clone()).collect();
                let filters = assign_filters(Some(filter_specs), &names)?;
                inputs = Some(
                    board
                        .columns
                        .iter()
                        .zip(filters)
                        .map(|(x, filter)| BoardColumnInput {
                            id: Some(x.id),
                            name: x.name.clone(),
                            status_ids: x.status_ids.clone(),
                            drop_statuses: Some(x.drop_statuses.clone()),
                            field_filters: filter,
                        })
                        .collect(),
                );
            }
            let updated = ws
                .boards()
                .update(
                    &project_id,
                    &board.id,
                    &UpdateBoard {
                        name: kit::text(leaf, "name"),
                        status_set_ids: set_ids,
                        columns: inputs,
                        version: Some(kit::text(leaf, "expected-version").unwrap_or(board.version.clone())),
                    },
                )?
                .ok_or_else(|| CliError::new(format!("No board '{}'", guid_d(&board.id))))?;
            ctx.print(
                &entities::board(&updated),
                &format!("Updated board '{}' {}", updated.name, guid_d(&updated.id)),
            );
        }
        "delete" => {
            let board = find_board(ctx, &project_id, &kit::required(leaf, "id-or-name"))?;
            let version = kit::text(leaf, "expected-version").unwrap_or(board.version.clone());
            ws.boards().delete(&project_id, &board.id, Some(&version))?;
            ctx.print(&entities::deleted(&board.id), &kit::deleted("board", &board.name, &board.id));
        }
        _ => return Err(CliError::new("not implemented in this build")),
    }
    Ok(())
}
