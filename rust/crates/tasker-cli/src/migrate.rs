//! `tasker migrate [--dry-run] [--check]` (`MigrateCommands` в .NET): вывод, коды выхода (0; 2 у `--check`; 1 при ошибке)
//! — как у .NET-консоли, эталоны `expected/cli/*migrate*` и `expected/migrate`.
use crate::context::Context;
use crate::errors::Result;
use serde_json::{Map, Value};
use tasker_files::migration::{self, MigrationOptions, MigrationReport};

/// Код выхода `migrate --check`: есть файлы старого или нового формата либо нечитаемые.
pub const NEEDS_MIGRATION: i32 = 2;

/// Сколько файлов каждого рода показывать в тексте; дальше — `... and N more`.
const SHOWN_FILES: usize = 10;

pub fn run(ctx: &mut Context<'_>, dry_run: bool, check: bool) -> Result<()> {
    let session = ctx.session();
    let report = migration::run(session.directory(), session.index(), MigrationOptions { dry_run: dry_run || check })?;
    let json = ctx.json;
    let text = show(&report, dry_run, check, json);
    let _ = std::io::Write::write_all(ctx.out(), text.as_bytes());

    if check {
        ctx.exit_code = if report.needs_attention() { NEEDS_MIGRATION } else { 0 };
        return Ok(());
    }
    if !report.newer.is_empty() || !report.unreadable.is_empty() {
        // Часть файлов осталась как была — скрипт должен это заметить.
        let _ = std::io::Write::write_all(ctx.err(), b"Error: some files were not migrated (see above)\n");
        ctx.exit_code = 1;
    }
    Ok(())
}

fn file_name(path: &str) -> &str {
    path.rsplit('/').next().unwrap_or(path)
}

/// Текст отчёта (с завершающим переводом строки) или его JSON.
pub fn show(report: &MigrationReport, dry_run: bool, check: bool, json: bool) -> String {
    if json {
        return format!("{}\n", tasker_core::json::to_string(&report_json(report, dry_run, check)));
    }

    let read_only = dry_run || check;
    let current = report.current_format;
    let mut lines: Vec<String> = Vec::new();
    if read_only {
        lines.push("Nothing is written (dry run):".into());
    }

    for file in report.migrated.iter().take(SHOWN_FILES) {
        let verb = if read_only { "would migrate" } else { "migrated" };
        lines.push(format!("  {verb}  {}  (format {} -> {current})", file.path, file.version));
    }
    if report.migrated.len() > SHOWN_FILES {
        lines.push(format!("  ... and {} more", report.migrated.len() - SHOWN_FILES));
    }
    for rename in report.renamed.iter().take(SHOWN_FILES) {
        let verb = if read_only { "would rename" } else { "renamed" };
        lines.push(format!("  {verb}  {}  ->  {}", rename.from, file_name(&rename.to)));
    }
    if report.renamed.len() > SHOWN_FILES {
        lines.push(format!("  ... and {} more", report.renamed.len() - SHOWN_FILES));
    }
    for file in &report.newer {
        lines.push(format!(
            "  skipped  {}  (format {} is newer than this Tasker supports ({current}): update Tasker)",
            file.path, file.version
        ));
    }
    for problem in &report.unreadable {
        lines.push(format!("  skipped  {}  ({})", problem.path, problem.reason));
    }

    if !report.migrated.is_empty() {
        lines.push(if read_only {
            format!("{} file(s) would be migrated to format {current}", report.migrated.len())
        } else {
            format!("Migrated {} file(s) to format {current}", report.migrated.len())
        });
    }
    if !report.renamed.is_empty() {
        lines.push(if read_only {
            format!(
                "{} file(s) would be renamed after the names of their entities",
                report.renamed.len()
            )
        } else {
            format!("Renamed {} file(s) after the names of their entities", report.renamed.len())
        });
    }
    if !report.needs_attention() {
        lines.push(format!(
            "Nothing to migrate: all {} file(s) are in the current format ({current})",
            report.scanned
        ));
    }

    format!("{}\n", lines.join("\n"))
}

/// Форма `--json` отчёта (анонимный объект .NET в camelCase, порядок полей тот же).
fn report_json(report: &MigrationReport, dry_run: bool, check: bool) -> Value {
    let object = |pairs: Vec<(&str, Value)>| Value::Object(pairs.into_iter().map(|(k, v)| (k.to_string(), v)).collect::<Map<_, _>>());
    let files = |items: &[migration::MigrationFile]| {
        Value::Array(
            items
                .iter()
                .map(|f| object(vec![("path", Value::String(f.path.clone())), ("version", Value::from(f.version))]))
                .collect(),
        )
    };
    object(vec![
        ("applicable", Value::Bool(true)),
        ("dryRun", Value::Bool(dry_run)),
        ("check", Value::Bool(check)),
        ("currentFormat", Value::from(report.current_format)),
        ("scanned", Value::from(report.scanned)),
        ("upToDate", Value::from(report.up_to_date)),
        ("needsAttention", Value::Bool(report.needs_attention())),
        ("migrated", files(&report.migrated)),
        (
            "renamed",
            Value::Array(
                report
                    .renamed
                    .iter()
                    .map(|r| object(vec![("from", Value::String(r.from.clone())), ("to", Value::String(r.to.clone()))]))
                    .collect(),
            ),
        ),
        ("newer", files(&report.newer)),
        (
            "unreadable",
            Value::Array(
                report
                    .unreadable
                    .iter()
                    .map(|p| {
                        object(vec![
                            ("path", Value::String(p.path.clone())),
                            ("reason", Value::String(p.reason.clone())),
                        ])
                    })
                    .collect(),
            ),
        ),
    ])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_text_report_lists_at_most_ten_files_of_each_kind() {
        let report = MigrationReport {
            current_format: 9,
            scanned: 30,
            up_to_date: 3,
            migrated: (0..12)
                .map(|i| migration::MigrationFile {
                    path: format!("projects/p/tasks/t{i}-0000000{i:x}.yaml"),
                    version: 1,
                })
                .collect(),
            renamed: vec![migration::MigrationRename {
                from: "projects/p/tasks/a.yaml".into(),
                to: "projects/p/tasks/b-00000001.yaml".into(),
            }],
            newer: vec![],
            unreadable: vec![],
        };
        let text = show(&report, false, false, false);
        assert!(text.contains("  ... and 2 more\n  renamed  projects/p/tasks/a.yaml  ->  b-00000001.yaml\n"));
        assert!(text.ends_with("Migrated 12 file(s) to format 9\nRenamed 1 file(s) after the names of their entities\n"));
        assert_eq!(text.matches("migrated  ").count(), 10);
    }
}
