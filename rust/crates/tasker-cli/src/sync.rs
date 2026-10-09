//! `tasker sync` (`SyncCommands` в .NET): привести кэш рабочей папки в соответствие с её файлами — после `git pull`, `checkout`,
//! слияния. Демона в этой сборке нет: сверка всегда прямая (`via direct`). Показывает файлы, которые не удалось прочитать, и
//! состояние серий и связей проектов.
use crate::context::Context;
use crate::errors::{CliError, Result};
use crate::json::{id, ids, object, opt_str};
use crate::session::Session;
use serde_json::Value;
use tasker_core::ids::guid_d;
use tasker_core::tasks::Page;
use tasker_files::layout::NAME;
use tasker_services::health::{self, ProjectSeriesHealth};

/// Сколько проблемных файлов показывается (`SyncResult.ProblemsShown`).
pub const PROBLEMS_SHOWN: usize = 10;

/// Команда открывает область сама: не рабочая папка — с `-q` молчит (режим хука git).
pub fn run(ctx: &mut Context<'_>, workspace: Option<&str>, sqlite: Option<&str>) -> Result<()> {
    let quiet = ctx.quiet;
    let location = match Session::locate(workspace, sqlite) {
        Ok(location) => location,
        Err(_) if quiet && sqlite.is_none() => return Ok(()),
        Err(e) => return Err(e),
    };
    if !std::path::Path::new(location.path()).join(NAME).is_dir() {
        if quiet {
            return Ok(());
        }
        return Err(CliError::new(format!(
            "{} is not a Tasker workspace: there is no {NAME} folder",
            location.path()
        )));
    }

    // Открытие области уже сверяет индекс; повторная сверка — явная гарантия.
    let session = Session::open(workspace, sqlite, false)?;
    let ws = session.workspace();
    ws.index().sync()?;
    let problems = ws.index().problems(Page::first(PROBLEMS_SHOWN))?;
    let series = health::compute(ws);

    let mut lines: Vec<String> = Vec::new();
    if !quiet {
        lines.push(format!("Synced {} (via direct)", location.path()));
    }
    if problems.total_count > 0 {
        lines.push(format!(
            "{} file(s) cannot be read and are missing from the lists until fixed:",
            problems.total_count
        ));
        lines.extend(problems.data.iter().map(|x| format!("  {}: {}", x.path, x.error)));
        if problems.total_count > problems.data.len() {
            lines.push(format!(
                "  … and {} more (see the problems list of the workspace)",
                problems.total_count - problems.data.len()
            ));
        }
    }
    for project in &series {
        let series_lines = health::describe(project);
        if !series_lines.is_empty() {
            lines.push(format!("Series problems in project '{}':", project.project_name));
            lines.extend(series_lines.iter().map(|x| format!("  {x}")));
        }
        if project.has_link_problems() {
            lines.push(format!("Link problems in project '{}':", project.project_name));
            lines.extend(health::describe_links(project).iter().map(|x| format!("  {x}")));
        }
    }

    if quiet && problems.total_count == 0 && series.is_empty() {
        return Ok(());
    }
    if ctx.json {
        let value = object(vec![
            ("path", Value::String(location.path().to_string())),
            ("via", Value::String("direct".into())),
            ("problemCount", Value::from(problems.total_count)),
            (
                "problems",
                Value::Array(
                    problems
                        .data
                        .iter()
                        .map(|x| {
                            object(vec![
                                ("path", Value::String(x.path.clone())),
                                ("error", Value::String(x.error.clone())),
                            ])
                        })
                        .collect(),
                ),
            ),
            ("series", Value::Array(series.iter().map(health_json).collect())),
        ]);
        let _ = writeln!(ctx.out(), "{}", tasker_core::json::to_string(&value));
    } else {
        let _ = writeln!(ctx.out(), "{}", lines.join("\n"));
    }
    Ok(())
}

/// `ProjectSeriesHealth` в JSON: порядок полей — как у записи .NET.
pub(crate) fn health_json(h: &ProjectSeriesHealth) -> Value {
    object(vec![
        ("projectId", id(&h.project_id)),
        ("projectName", Value::String(h.project_name.clone())),
        (
            "numberConflicts",
            Value::Array(
                h.number_conflicts
                    .iter()
                    .map(|x| {
                        object(vec![
                            ("reference", Value::String(x.reference.clone())),
                            ("seriesId", id(&x.series_id)),
                            ("number", Value::from(x.number)),
                            ("taskIds", ids(&x.task_ids)),
                        ])
                    })
                    .collect(),
            ),
        ),
        ("prefixConflicts", prefix_conflicts_json(&h.prefix_conflicts)),
        ("tasksWithInvalidSeries", Value::from(h.tasks_with_invalid_series)),
        ("unreadableSeriesFiles", Value::from(h.unreadable_series_files)),
        ("error", opt_str(h.error.as_deref())),
        ("tasksWithInvalidLinks", Value::from(h.tasks_with_invalid_links)),
        ("unreadableLinkFiles", Value::from(h.unreadable_link_files)),
        (
            "linkCycles",
            Value::Array(
                h.link_cycles
                    .iter()
                    .map(|c| {
                        object(vec![
                            ("typeId", id(&c.type_id)),
                            ("typeName", Value::String(c.type_name.clone())),
                            ("path", Value::String(c.path.clone())),
                            ("taskIds", ids(&c.task_ids)),
                        ])
                    })
                    .collect(),
            ),
        ),
        ("hasProblems", Value::Bool(h.has_problems())),
        ("hasLinkProblems", Value::Bool(h.has_link_problems())),
    ])
}

pub(crate) fn prefix_conflicts_json(conflicts: &[health::PrefixConflict]) -> Value {
    Value::Array(
        conflicts
            .iter()
            .map(|x| {
                object(vec![
                    ("prefix", Value::String(x.prefix.clone())),
                    (
                        "seriesIds",
                        Value::Array(x.series_ids.iter().map(|i| Value::String(guid_d(i))).collect()),
                    ),
                ])
            })
            .collect(),
    )
}
