//! `tasker sync` (`SyncCommands` в .NET): привести кэш рабочей папки в соответствие с её файлами — после `git pull`, `checkout`,
//! слияния. Демона в этой сборке нет: сверка всегда прямая (`via direct`). Показывает файлы, которые не удалось прочитать, и
//! состояние серий и связей проектов.
use super::object;
use crate::CliError;
use clap::ArgMatches;
use serde_json::Value;
use std::io::Write;
use tasker_core::tasks::Page;
use tasker_files::layout::NAME;
use tasker_services::Workspace;
use tasker_services::health::{self, ProjectSeriesHealth};

pub const DESCRIPTION: &str = "Brings the cache of a workspace up to date with its files (after git pull, checkout, merge)";

/// Сколько проблемных файлов показывается (`SyncResult.ProblemsShown`).
pub const PROBLEMS_SHOWN: usize = 10;

pub const HELP: &str = "Description:
  Brings the cache of a workspace up to date with its files (after git pull, checkout, merge)

Usage:
  tasker sync [options]

Options:
  -?, -h, --help               Show help and usage information
  -w, --workspace <workspace>  Folder with the workspace (data lives in <folder>/.tasker). Default: the current folder
  --sqlite <sqlite>            SQLite file with the workspace instead of a folder
  -p, --project <Tasker>       Project (id or name) for commands inside a project. Default: the TASKER_PROJECT variable, or the only project of the workspace
  --json                       Print the result as JSON
  -q, --quiet                  Print less: lists skip the first line with the number of found items ('Found N'), sync prints nothing unless something needs attention
  --no-wrap, --truncate        Cut long lines of list output to the window width with an ellipsis instead of wrapping (default: on in a terminal and under watch - COLUMNS and LINES both set; off when redirected)
  --no-truncate                Never cut lines of list output, even in a terminal
  --width                      Line width for --truncate in characters (minimum 20); 0 or 'auto' - the terminal width, 'off' - never cut. Default: the TASKER_WIDTH variable (off or 0 - never cut), else the terminal

";

pub fn run(matches: &ArgMatches, out: &mut dyn Write) -> Result<i32, CliError> {
    let quiet = matches.get_flag("quiet");
    let json = matches.get_flag("json");

    // Не рабочая папка — с -q молчим (режим хука git).
    let location = match crate::locate_folder(matches) {
        Ok(location) => location,
        Err(_) if quiet && matches.get_one::<String>("sqlite").is_none() => return Ok(0),
        Err(e) => return Err(e),
    };
    if !std::path::Path::new(location.path()).join(NAME).is_dir() {
        if quiet {
            return Ok(0);
        }
        return Err(CliError(format!(
            "{} is not a Tasker workspace: there is no {NAME} folder",
            location.path()
        )));
    }

    // Открытие области уже сверяет индекс; повторная сверка — явная гарантия.
    let ws = Workspace::open(location.path())?;
    ws.index().sync()?;
    let problems = ws.index().problems(Page::first(PROBLEMS_SHOWN))?;
    let series = health::compute(&ws);

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
        return Ok(0);
    }
    if json {
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
        let _ = writeln!(out, "{}", tasker_core::json::to_string(&value));
    } else {
        let _ = writeln!(out, "{}", lines.join("\n"));
    }
    Ok(0)
}

/// `ProjectSeriesHealth` в JSON: порядок полей — как у записи .NET (общая с демоном форма — `health::to_json`).
pub(crate) fn health_json(h: &ProjectSeriesHealth) -> Value {
    health::to_json(h)
}

pub(crate) fn prefix_conflicts_json(conflicts: &[health::PrefixConflict]) -> Value {
    health::prefix_conflicts_to_json(conflicts)
}
