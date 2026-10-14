//! `tasker cleanup [--resolve-conflicts] [--dry-run] [--check]` (`CleanupCommands` в .NET): единственная команда, которая правит
//! данные серий и связей после слияния веток git. Работает напрямую с рабочей областью.
use crate::context::Context;
use crate::errors::{CliError, Result};
use crate::json::{id, ids, object, opt_id, opt_int, opt_str};
use crate::sync::prefix_conflicts_json;
use serde_json::Value;
use std::collections::HashMap;
use std::io::Write;
use tasker_core::ids::guid_d;
use tasker_core::model::Project;
use tasker_files::project_folders::{self, FolderReport, TidyOptions};
use tasker_services::cleanup::{CleanupChange, CleanupOptions, CleanupReport};
use tasker_services::health::label;
use uuid::Uuid;

/// Код выхода `--check`: чистка что-то изменила бы или что-то требует внимания.
pub const NEEDS_CLEANUP: i32 = 2;

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

/// `read_only` (`--dry-run`/`--check`) не трогает файлы; иначе идущее слияние git — ошибка, чистка переждёт.
pub fn run(ctx: &mut Context<'_>, dry_run: bool, check: bool, resolve: bool) -> Result<()> {
    let read_only = dry_run || check;
    // Слияние git касается только папки: у файла SQLite его нет (`--sqlite` в .NET проверку пропускает).
    let folder = ctx.session().location().kind() == tasker_core::settings::WorkspaceKind::Files;
    if !read_only
        && folder
        && let Some(operation) = git_in_progress(ctx.session().location().path())
    {
        return Err(CliError::new(format!(
            "A git {operation} is in progress: cleanup would treat half-merged files as final. Finish or abort it, then run cleanup again (--dry-run and --check work meanwhile)"
        )));
    }

    let only = ctx.optional_project()?;
    let ws = ctx.session().workspace();
    // Вторые папки проектов (слияние веток git) — сначала: чистка ниже видит сущности уже в одной папке.
    let folders = match ws.files() {
        Some(files) => project_folders::tidy(
            files.directory(),
            files.index(),
            TidyOptions {
                dry_run: read_only,
                rename: false,
                project: only.as_ref().map(|x| x.id),
            },
        )?,
        None => FolderReport::default(),
    };
    let projects = match only {
        Some(project) => vec![project],
        None => ctx.session().all_projects()?,
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

    let attention = !folders.is_empty() || results.iter().any(ProjectCleanup::needs_attention);
    let json = ctx.json;
    show(&results, &folders, dry_run, check, resolve, attention, json, ctx.out());

    if check {
        ctx.exit_code = if attention { NEEDS_CLEANUP } else { 0 };
        return Ok(());
    }
    if !read_only && !folders.problems.is_empty() {
        let _ = writeln!(ctx.err(), "Error: some files of second project folders were not merged (see above)");
        ctx.exit_code = 1;
    }
    if !read_only && results.iter().any(|x| x.report.skipped) {
        let _ = writeln!(ctx.err(), "Error: the cleanup was skipped, nothing was changed");
        ctx.exit_code = 1;
        return Ok(());
    }
    if !read_only && results.iter().any(|x| x.report.links_skip_reason.is_some()) {
        let _ = writeln!(
            ctx.err(),
            "Error: the links between tasks were not checked, the rest was cleaned up (see above)"
        );
        ctx.exit_code = 1;
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn show(
    results: &[ProjectCleanup],
    folders: &FolderReport,
    dry_run: bool,
    check: bool,
    resolve: bool,
    attention: bool,
    json: bool,
    out: &mut dyn Write,
) {
    let read_only = dry_run || check;
    let changes: usize = results.iter().map(|x| x.report.changes.len()).sum();

    if json {
        let value = object(vec![
            ("dryRun", Value::Bool(dry_run)),
            ("check", Value::Bool(check)),
            ("resolveConflicts", Value::Bool(resolve)),
            ("needsAttention", Value::Bool(attention)),
            ("changeCount", Value::from(changes)),
            ("projectFolders", folders_json(folders)),
            ("projects", Value::Array(results.iter().map(project_json).collect())),
        ]);
        let _ = writeln!(out, "{}", tasker_core::json::to_string(&value));
        return;
    }

    let mut lines: Vec<String> = Vec::new();
    if read_only {
        lines.push("Nothing is written (dry run):".into());
    }
    if !folders.is_empty() {
        lines.push("Project folders:".into());
        for rename in &folders.renamed {
            let verb = if read_only { "would rename" } else { "renamed" };
            lines.push(format!("  {verb}  {}/  ->  {}/", rename.from, rename.to));
        }
        for merge in &folders.merged {
            let verb = if read_only { "would merge" } else { "merged" };
            lines.push(format!("  {verb}  {}/  into  {}/", merge.from, merge.to));
        }
        for problem in &folders.problems {
            lines.push(format!("  not merged  {}  ({})", problem.path, problem.reason));
        }
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

fn folders_json(folders: &FolderReport) -> Value {
    let moves = |items: &[tasker_files::migration::MigrationRename]| {
        Value::Array(
            items
                .iter()
                .map(|x| object(vec![("from", Value::String(x.from.clone())), ("to", Value::String(x.to.clone()))]))
                .collect(),
        )
    };
    object(vec![
        ("renamed", moves(&folders.renamed)),
        ("merged", moves(&folders.merged)),
        (
            "notMerged",
            Value::Array(
                folders
                    .problems
                    .iter()
                    .map(|x| {
                        object(vec![
                            ("path", Value::String(x.path.clone())),
                            ("reason", Value::String(x.reason.clone())),
                        ])
                    })
                    .collect(),
            ),
        ),
    ])
}

fn change_json(c: &CleanupChange) -> Value {
    object(vec![
        ("taskId", id(&c.task_id)),
        ("taskTitle", Value::String(c.task_title.clone())),
        ("kind", Value::String(c.kind.json_name().into())),
        ("seriesId", opt_id(c.series_id.as_ref())),
        ("oldNumber", opt_int(c.old_number)),
        ("newNumber", opt_int(c.new_number)),
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
