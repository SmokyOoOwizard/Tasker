//! Команды дерева по группам (`Commands/*.cs` в .NET). Каждая группа — функция `run(ctx, subcommand, matches)`, которая
//! выполняет подкоманду по имени; вывод и ошибки — через [`crate::context::Context`].
pub mod board;
pub mod completion;
pub mod field;
pub mod hooks;
pub mod link_type;
pub mod lock;
pub mod manual;
pub mod mcp;
pub mod project;
pub mod series;
pub mod status;
pub mod task;
pub mod task_fields;
pub mod task_type;
pub mod user;

use crate::context::Context;
use crate::errors::{CliError, Result};
use crate::session::refs;
use tasker_core::model::{Board, FieldDefinition, FieldEnum, LinkType, Series, Status, StatusSet, TaskType};
use uuid::Uuid;

/// Сущность проекта по ссылке (`Refs.FindItem` над `GetAll`).
pub fn find_status(ctx: &Context<'_>, project_id: &Uuid, reference: &str) -> Result<Status> {
    let all = ctx.session().workspace().statuses().get_all(project_id)?;
    refs::find_item(&all, reference, |x| x.id, |x| &x.name, "status", false).cloned()
}

pub fn find_status_set(ctx: &Context<'_>, project_id: &Uuid, reference: &str) -> Result<StatusSet> {
    let all = ctx.session().workspace().status_sets().get_all(project_id)?;
    refs::find_item(&all, reference, |x| x.id, |x| &x.name, "status set", false).cloned()
}

pub fn find_task_type(ctx: &Context<'_>, project_id: &Uuid, reference: &str) -> Result<TaskType> {
    let all = ctx.session().workspace().task_types().get_all(project_id)?;
    refs::find_item(&all, reference, |x| x.id, |x| &x.name, "task type", false).cloned()
}

pub fn find_link_type(ctx: &Context<'_>, project_id: &Uuid, reference: &str) -> Result<LinkType> {
    let all = ctx.session().workspace().link_types().get_all(project_id)?;
    refs::find_item(&all, reference, |x| x.id, |x| &x.name, "link type", false).cloned()
}

pub fn find_field(ctx: &Context<'_>, project_id: &Uuid, reference: &str) -> Result<FieldDefinition> {
    let all = ctx.session().workspace().fields().get_all(project_id)?;
    refs::find_item(&all, reference, |x| x.id, |x| &x.name, "field", false).cloned()
}

pub fn find_enum(ctx: &Context<'_>, project_id: &Uuid, reference: &str) -> Result<FieldEnum> {
    let all = ctx.session().workspace().enums().get_all(project_id)?;
    refs::find_item(&all, reference, |x| x.id, |x| &x.name, "enum", false).cloned()
}

pub fn find_board(ctx: &Context<'_>, project_id: &Uuid, reference: &str) -> Result<Board> {
    let all = ctx.session().workspace().boards().get_all(project_id)?;
    refs::find_item(&all, reference, |x| x.id, |x| &x.name, "board", false).cloned()
}

/// Серия по id или по префиксу (регистр важен); затем — по короткому id (`SeriesCommands.Find`).
pub fn find_series(ctx: &Context<'_>, project_id: &Uuid, reference: &str) -> Result<Series> {
    let ws = ctx.session().workspace();
    if let Some(found) = ws.series().find(project_id, reference)? {
        return Ok(found);
    }
    let all = ws.series().get_all(project_id)?;
    if let Some(found) = refs::by_id_prefix(&all, reference, |x| x.id, "series")? {
        return Ok(found.clone());
    }
    Err(CliError::new(format!(
        "No series '{reference}': give the id, the short id or the exact prefix (case matters)"
    )))
}

/// Серии для фильтра списка («ИЛИ» внутри параметра); повторы убираются, неизвестная — ошибка с перечнем префиксов.
pub fn find_series_distinct(ctx: &Context<'_>, project_id: &Uuid, references: &[String]) -> Result<Vec<Uuid>> {
    let ws = ctx.session().workspace();
    let mut found: Vec<Uuid> = Vec::new();
    for reference in references {
        let series = match ws.series().find(project_id, reference)? {
            Some(series) => series,
            None => {
                let all = ws.series().get_all(project_id)?;
                match refs::by_id_prefix(&all, reference, |x| x.id, "series")? {
                    Some(series) => series.clone(),
                    None => {
                        let mut prefixes: Vec<&str> = all.iter().map(|x| x.prefix.as_str()).collect();
                        prefixes.sort_unstable();
                        return Err(CliError::new(format!(
                            "No series '{reference}': give the id, the short id or the exact prefix (case matters). Available: {}",
                            prefixes.join(", ")
                        )));
                    }
                }
            }
        };
        if !found.contains(&series.id) {
            found.push(series.id);
        }
    }
    Ok(found)
}

/// Названия перечислений проекта по id (`FieldCommands.EnumNames`).
pub fn enum_names(ctx: &Context<'_>, project_id: &Uuid) -> Result<std::collections::HashMap<Uuid, String>> {
    Ok(ctx
        .session()
        .workspace()
        .enums()
        .get_all(project_id)?
        .into_iter()
        .map(|x| (x.id, x.name))
        .collect())
}

/// Тип поля одной строкой: `string`, `int[]` (несколько значений), `enum Priority`.
pub fn type_text(field_type: tasker_core::model::FieldType, multiple: bool, enum_name: Option<&str>) -> String {
    format!(
        "{}{}{}",
        field_type.name(),
        if multiple { "[]" } else { "" },
        enum_name.map(|n| format!(" {n}")).unwrap_or_default()
    )
}

pub fn field_type_text(field: &FieldDefinition, enum_names: &std::collections::HashMap<Uuid, String>) -> String {
    let enum_name = field
        .enum_id
        .map(|id| enum_names.get(&id).cloned().unwrap_or_else(|| tasker_core::ids::guid_d(&id)));
    type_text(field.field_type, field.multiple, enum_name.as_deref())
}
