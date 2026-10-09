//! `tasker series …` (`SeriesCommands` в .NET).
use super::find_series;
use crate::commands::task::find_one;
use crate::context::Context;
use crate::entities;
use crate::errors::{CliError, Result};
use crate::kit;
use clap::ArgMatches;
use std::collections::HashMap;
use tasker_core::ids::guid_d;
use tasker_core::model::TaskSeriesNumber;
use tasker_core::tasks::TaskFilter;
use tasker_services::series::{CreateSeries, UpdateSeries};
use uuid::Uuid;

/// Префиксы серий проекта по id — чтобы показывать номера задач как `TSK-5`.
pub fn prefixes(ctx: &Context<'_>, project_id: &Uuid) -> Result<HashMap<Uuid, String>> {
    Ok(ctx.session().workspace().series().prefixes(project_id)?)
}

/// Ссылки задачи: `TSK-5`; серии нет — недействительная ссылка, помечается явно. Порядок: по префиксу, недействительные в конце.
pub fn references(numbers: &[TaskSeriesNumber], prefixes: &HashMap<Uuid, String>) -> Vec<String> {
    let mut sorted: Vec<&TaskSeriesNumber> = numbers.iter().collect();
    sorted.sort_by(|a, b| {
        let (pa, pb) = (prefixes.get(&a.series_id), prefixes.get(&b.series_id));
        pa.is_none()
            .cmp(&pb.is_none())
            .then_with(|| pa.cmp(&pb))
            .then_with(|| guid_d(&a.series_id).cmp(&guid_d(&b.series_id)))
    });
    sorted
        .iter()
        .map(|x| match prefixes.get(&x.series_id) {
            Some(prefix) => format!("{prefix}-{}", x.number),
            None => format!("(invalid series {}) #{}", guid_d(&x.series_id), x.number),
        })
        .collect()
}

pub fn run(ctx: &mut Context<'_>, command: &str, leaf: &ArgMatches) -> Result<()> {
    let project_id = ctx.project_id()?;
    let ws = ctx.session().workspace().clone();
    match command {
        "create" => {
            let series = ws.series().create(
                &project_id,
                &CreateSeries {
                    name: kit::required(leaf, "name"),
                    prefix: kit::required(leaf, "prefix"),
                },
            )?;
            ctx.print(
                &entities::series(&series),
                &format!("Created series '{}' ({}) {}", series.name, series.prefix, guid_d(&series.id)),
            );
        }
        "list" => {
            let list = kit::Paging::load(leaf, |page| Ok(ws.series().get_range(&project_id, page)?))?;
            ctx.print_list(&list, entities::series, |x| kit::row(&x.id, &[&x.prefix, &x.name]));
        }
        "get" => {
            let series = find_series(ctx, &project_id, &kit::required(leaf, "series"))?;
            let text = kit::fields(&[
                ("id", Some(guid_d(&series.id))),
                ("name", Some(series.name.clone())),
                ("prefix", Some(series.prefix.clone())),
                ("version", Some(series.version.clone())),
            ]);
            ctx.print(&entities::series(&series), &text);
        }
        "update" => {
            kit::require_change(leaf, &["name", "prefix"])?;
            let series = find_series(ctx, &project_id, &kit::required(leaf, "series"))?;
            let updated = ws
                .series()
                .update(
                    &project_id,
                    &series.id,
                    &UpdateSeries {
                        name: kit::text(leaf, "name"),
                        prefix: kit::text(leaf, "prefix"),
                        version: Some(kit::text(leaf, "expected-version").unwrap_or(series.version.clone())),
                    },
                )?
                .ok_or_else(|| CliError::new(format!("No series '{}'", guid_d(&series.id))))?;
            ctx.print(
                &entities::series(&updated),
                &format!("Updated series '{}' ({}) {}", updated.name, updated.prefix, guid_d(&updated.id)),
            );
        }
        "delete" => {
            let series = find_series(ctx, &project_id, &kit::required(leaf, "series"))?;
            let filter = TaskFilter {
                series_ids: Some(vec![series.id]),
                ..TaskFilter::default()
            };
            let tasks = ws.tasks().count(&project_id, &filter)?;
            let version = kit::text(leaf, "expected-version").unwrap_or(series.version.clone());
            if !ws.series().delete(&project_id, &series.id, Some(&version))? {
                return Err(CliError::new(format!("No series '{}'", guid_d(&series.id))));
            }
            let mut text = kit::deleted("series", &series.name, &series.id);
            if tasks > 0 {
                text.push_str(&format!("\nRemoved the series from {tasks} task(s)"));
            }
            ctx.print(
                &crate::json::object(vec![
                    ("deleted", crate::json::id(&series.id)),
                    ("tasksAffected", serde_json::Value::from(tasks)),
                ]),
                &text,
            );
        }
        "add-task" => {
            let series = find_series(ctx, &project_id, &kit::required(leaf, "series"))?;
            let task = find_one(ctx, &project_id, &kit::required(leaf, "task"))?;
            let updated = ws
                .tasks()
                .add_to_series(&project_id, &task.id, &series.id, Some(&task.version))?
                .ok_or_else(|| CliError::new(format!("No task '{}' or series '{}'", guid_d(&task.id), guid_d(&series.id))))?;
            let number = updated
                .series_numbers
                .iter()
                .find(|x| x.series_id == series.id)
                .map(|x| x.number)
                .unwrap_or_default();
            ctx.print(
                &entities::task(&updated),
                &format!("Task '{}' is {}-{number} {}", updated.title, series.prefix, guid_d(&updated.id)),
            );
        }
        "remove-task" => {
            let series = find_series(ctx, &project_id, &kit::required(leaf, "series"))?;
            let number = kit::int(leaf, "number", 0) as i32;
            let updated = ws
                .tasks()
                .remove_from_series_by_number(&project_id, &series.id, number)?
                .ok_or_else(|| CliError::new(format!("No task {}-{number}", series.prefix)))?;
            ctx.print(
                &entities::task(&updated),
                &format!(
                    "Removed task '{}' from series {} (was {}-{number}) {}",
                    updated.title,
                    series.prefix,
                    series.prefix,
                    guid_d(&updated.id)
                ),
            );
        }
        "renumber-task" => {
            let series = find_series(ctx, &project_id, &kit::required(leaf, "series"))?;
            let task = find_one(ctx, &project_id, &kit::required(leaf, "task"))?;
            let to = kit::int_opt(leaf, "to").map(|x| x as i32);
            let updated = ws
                .tasks()
                .renumber(&project_id, &task.id, &series.id, to, Some(&task.version))?
                .ok_or_else(|| {
                    CliError::new(format!(
                        "Task '{}' {} is not in series {}",
                        task.title,
                        guid_d(&task.id),
                        series.prefix
                    ))
                })?;
            let number = updated
                .series_numbers
                .iter()
                .find(|x| x.series_id == series.id)
                .map(|x| x.number)
                .unwrap_or_default();
            ctx.print(
                &entities::task(&updated),
                &format!("Task '{}' is {}-{number} {}", updated.title, series.prefix, guid_d(&updated.id)),
            );
        }
        _ => return Err(CliError::new("not implemented in this build")),
    }
    Ok(())
}
