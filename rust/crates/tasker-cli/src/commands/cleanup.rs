//! `tasker cleanup [--resolve-conflicts] [--dry-run] [--check]` (`CleanupCommands` в .NET): единственная команда, которая правит
//! данные серий и связей после слияния веток git. Работает напрямую с рабочей областью.
use super::{id, ids, object, opt_id, opt_str, sync::prefix_conflicts_json};
use crate::CliError;
use clap::ArgMatches;
use serde_json::Value;
use std::collections::HashMap;
use std::io::Write;
use tasker_core::ids::guid_d;
use tasker_core::model::Project;
use tasker_services::cleanup::{CleanupChange, CleanupOptions, CleanupReport};
use tasker_services::health::label;
use uuid::Uuid;

pub const DESCRIPTION: &str = "Removes references to missing series and links to missing tasks or link types after a git merge (with --resolve-conflicts also renumbers duplicate numbers); link cycles are only reported, remove one link with 'task unlink'";

/// Код выхода `--check`: чистка что-то изменила бы или что-то требует внимания.
pub const NEEDS_CLEANUP: i32 = 2;

pub const HELP: &str = "Description:
  Removes references to missing series and links to missing tasks or link types after a git merge (with --resolve-conflicts also renumbers duplicate numbers); link cycles are only reported, remove one link with 'task unlink'

Usage:
  tasker cleanup [options]

Options:
  --resolve-conflicts          Also give duplicate numbers new ones: the earliest task keeps its number, the others get the next numbers
  --dry-run                    Write nothing, only show what would change
  --check                      Write nothing; exit with code 2 if the cleanup would change something or something needs attention (for scripts and CI)
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

struct ProjectCleanup {
    project: Project,
    report: CleanupReport,
    prefixes: HashMap<Uuid, String>,
}

impl ProjectCleanup {
    fn needs_attention(&self) -> bool {
        let r = &self.report;
        !r.changes.is_empty()
            || !r.remaining_number_conflicts.is_empty()
            || !r.prefix_conflicts.is_empty()
            || r.skipped
            || r.links_skip_reason.is_some()
            || !r.link_cycles.is_empty()
    }

    fn label(&self, series_id: &Uuid, number: i32) -> String {
        format!("{}-{number}", label(&self.prefixes, series_id))
    }
}

/// Идёт ли в репозитории git слияние, rebase, cherry-pick или revert (`GitOperations.InProgress`); None — ничего не идёт, папка не в
/// репозитории или git не установлен.
fn git_in_progress(folder: &str) -> Option<&'static str> {
    const MARKERS: [(&str, &str); 5] = [
        ("MERGE_HEAD", "merge"),
        ("rebase-merge", "rebase"),
        ("rebase-apply", "rebase"),
        ("CHERRY_PICK_HEAD", "cherry-pick"),
        ("REVERT_HEAD", "revert"),
    ];
    for (marker, name) in MARKERS {
        let output = std::process::Command::new("git")
            .args(["-C", folder, "rev-parse", "--path-format=absolute", "--git-path", marker])
            .output()
            .ok()?;
        if !output.status.success() {
            return None;
        }
        let path = String::from_utf8_lossy(&output.stdout).trim().to_string();
        if !path.is_empty() && std::path::Path::new(&path).exists() {
            return Some(name);
        }
    }
    None
}

pub fn run(matches: &ArgMatches, sub: &ArgMatches, out: &mut dyn Write, err: &mut dyn Write) -> Result<i32, CliError> {
    let dry_run = sub.get_flag("dry-run");
    let check = sub.get_flag("check");
    let resolve = sub.get_flag("resolve-conflicts");
    let read_only = dry_run || check;
    if !read_only && matches.get_one::<String>("sqlite").is_none() {
        let folder = crate::locate_folder(matches)?;
        if let Some(operation) = git_in_progress(folder.path()) {
            return Err(CliError(format!(
                "A git {operation} is in progress: cleanup would treat half-merged files as final. Finish or abort it, then run cleanup again (--dry-run and --check work meanwhile)"
            )));
        }
    }

    let ws = super::open(matches)?;
    let projects = match super::optional_project(&ws, matches)? {
        Some(project) => vec![project],
        None => super::all_projects(&ws)?,
    };
    let options = CleanupOptions {
        resolve_conflicts: resolve,
        dry_run: read_only,
    };

    let mut ordered = projects;
    ordered.sort_by_key(|x| tasker_core::validate::to_lower_invariant(&x.name));
    let mut results: Vec<ProjectCleanup> = Vec::new();
    for project in ordered {
        let report = ws.cleanup().run(&project.id, options)?;
        let prefixes = ws.series().prefixes(&project.id)?;
        results.push(ProjectCleanup { project, report, prefixes });
    }

    let attention = results.iter().any(ProjectCleanup::needs_attention);
    show(&results, dry_run, check, resolve, attention, matches.get_flag("json"), out);

    if check {
        return Ok(if attention { NEEDS_CLEANUP } else { 0 });
    }
    if !read_only && results.iter().any(|x| x.report.skipped) {
        let _ = writeln!(err, "Error: the cleanup was skipped, nothing was changed");
        return Ok(1);
    }
    if !read_only && results.iter().any(|x| x.report.links_skip_reason.is_some()) {
        let _ = writeln!(
            err,
            "Error: the links between tasks were not checked, the rest was cleaned up (see above)"
        );
        return Ok(1);
    }
    Ok(0)
}

fn show(results: &[ProjectCleanup], dry_run: bool, check: bool, resolve: bool, attention: bool, json: bool, out: &mut dyn Write) {
    let read_only = dry_run || check;
    let changes: usize = results.iter().map(|x| x.report.changes.len()).sum();

    if json {
        let value = object(vec![
            ("dryRun", Value::Bool(dry_run)),
            ("check", Value::Bool(check)),
            ("resolveConflicts", Value::Bool(resolve)),
            ("needsAttention", Value::Bool(attention)),
            ("changeCount", Value::from(changes)),
            ("projects", Value::Array(results.iter().map(project_json).collect())),
        ]);
        let _ = writeln!(out, "{}", tasker_core::json::to_string(&value));
        return;
    }

    let mut lines: Vec<String> = Vec::new();
    if read_only {
        lines.push("Nothing is written (dry run):".into());
    }
    for result in results.iter().filter(|x| x.needs_attention()) {
        lines.push(format!("Project '{}':", result.project.name));
        lines.extend(result.report.changes.iter().map(|x| format!("  {}", x.description)));
        for conflict in &result.report.remaining_number_conflicts {
            lines.push(format!(
                "  {}: tasks {} (duplicate number: run 'tasker cleanup --resolve-conflicts' or 'tasker series renumber-task')",
                result.label(&conflict.series_id, conflict.number),
                conflict.task_ids.iter().map(guid_d).collect::<Vec<_>>().join(", ")
            ));
        }
        for conflict in &result.report.prefix_conflicts {
            lines.push(format!(
                "  series prefix '{}' is used by several series: {}: rename with 'tasker series update <id> --prefix ...'",
                conflict.prefix,
                conflict.series_ids.iter().map(guid_d).collect::<Vec<_>>().join(", ")
            ));
        }
        for cycle in &result.report.link_cycles {
            lines.push(format!(
                "  link cycle: {} (link type {}): remove one of the links with 'tasker task unlink' (cleanup does not remove links of a cycle)",
                cycle.format(&result.prefixes),
                cycle.type_name
            ));
        }
        if result.report.skipped {
            lines.push(format!("  skipped: {}", result.report.skip_reason.as_deref().unwrap_or("")));
        }
        if let Some(reason) = &result.report.links_skip_reason {
            lines.push(format!("  links skipped: {reason}"));
        }
    }
    if !attention {
        lines.push("Nothing to clean up".into());
    } else if changes > 0 {
        lines.push(if read_only {
            format!("{changes} change(s) would be made")
        } else {
            format!("{changes} change(s) made")
        });
    }
    let _ = writeln!(out, "{}", lines.join("\n"));
}

fn change_json(c: &CleanupChange) -> Value {
    object(vec![
        ("taskId", id(&c.task_id)),
        ("taskTitle", Value::String(c.task_title.clone())),
        ("kind", Value::String(c.kind.json_name().into())),
        ("seriesId", opt_id(c.series_id.as_ref())),
        ("oldNumber", c.old_number.map(Value::from).unwrap_or(Value::Null)),
        ("newNumber", c.new_number.map(Value::from).unwrap_or(Value::Null)),
        ("description", Value::String(c.description.clone())),
        ("linkTypeId", opt_id(c.link_type_id.as_ref())),
        ("linkTargetId", opt_id(c.link_target_id.as_ref())),
    ])
}

fn project_json(x: &ProjectCleanup) -> Value {
    let r = &x.report;
    object(vec![
        ("projectId", id(&x.project.id)),
        ("project", Value::String(x.project.name.clone())),
        ("changes", Value::Array(r.changes.iter().map(change_json).collect())),
        (
            "remainingNumberConflicts",
            Value::Array(
                r.remaining_number_conflicts
                    .iter()
                    .map(|c| {
                        object(vec![
                            ("seriesId", id(&c.series_id)),
                            ("reference", Value::String(x.label(&c.series_id, c.number))),
                            ("number", Value::from(c.number)),
                            ("taskIds", ids(&c.task_ids)),
                        ])
                    })
                    .collect(),
            ),
        ),
        ("prefixConflicts", prefix_conflicts_json(&r.prefix_conflicts)),
        ("skipped", Value::Bool(r.skipped)),
        ("skipReason", opt_str(r.skip_reason.as_deref())),
        ("linksSkipReason", opt_str(r.links_skip_reason.as_deref())),
        (
            "linkCycles",
            Value::Array(
                r.link_cycles
                    .iter()
                    .map(|c| {
                        object(vec![
                            ("typeId", id(&c.type_id)),
                            ("linkType", Value::String(c.type_name.clone())),
                            ("path", Value::String(c.format(&x.prefixes))),
                            ("taskIds", Value::Array(c.path.iter().map(|t| id(&t.id)).collect())),
                        ])
                    })
                    .collect(),
            ),
        ),
    ])
}
