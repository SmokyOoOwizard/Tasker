//! Консоль `tasker` на Rust (TSK-130, фаза 1 плана `docs/rust-migration-plan.md`): команды `migrate`, `sync` и `cleanup` — средство
//! проверки паритета сервисов (TSK-132) на реальной области. Разбор аргументов — clap (builder API), синхронно, без tokio. Тексты
//! вывода, коды выхода и справка команд — как у .NET-консоли (`Tasker.Cli`, System.CommandLine): эталоны в `rust/tests/golden/expected`.
//!
//! Полное дерево команд, справка корня и разбор ошибок System.CommandLine — фаза 3.
mod commands;
use clap::{Arg, ArgAction, ArgMatches, Command};
use serde_json::{Map, Value};
use std::io::Write;
use std::path::Path;
use tasker_core::settings::{WorkspaceLocation, expand_user_path};
use tasker_files::layout::TaskerDirectory;
use tasker_files::migration::{self, MigrationOptions, MigrationReport};
use tasker_files::write::NoIndex;

pub(crate) use commands::{cleanup, sync};

/// Код выхода `migrate --check`: есть файлы старого или нового формата либо нечитаемые.
const NEEDS_MIGRATION: i32 = 2;

/// Сколько файлов каждого рода показывать в тексте; дальше — `... and N more`.
const SHOWN_FILES: usize = 10;

const ROOT_DESCRIPTION: &str = "Tasker command line: works with a workspace folder (data in <folder>/.tasker) or a SQLite file";
const MIGRATE_DESCRIPTION: &str = "Brings the files of the workspace to the current file format version (formatVersion) and names the files of project entities after their names (titles of tasks)";

/// Справка `tasker migrate --help` — в формате System.CommandLine, слово в слово как у .NET-консоли (`expected/cli/195-help-migrate.out`).
const MIGRATE_HELP: &str = "Description:
  Brings the files of the workspace to the current file format version (formatVersion) and names the files of project entities after their names (titles of tasks)

Usage:
  tasker migrate [options]

Options:
  --dry-run                    Write nothing, only show which files would be migrated
  --check                      Write nothing; exit with code 2 if any file is not in the current format or needs attention (for scripts and CI)
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

const ROOT_HELP: &str = "Description:
  Tasker command line: works with a workspace folder (data in <folder>/.tasker) or a SQLite file

Usage:
  tasker [command] [options]

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

Commands:
  cleanup  Removes references to missing series and links to missing tasks or link types after a git merge (with --resolve-conflicts also renumbers duplicate numbers); link cycles are only reported, remove one link with 'task unlink'
  migrate  Brings the files of the workspace to the current file format version (formatVersion) and names the files of project entities after their names (titles of tasks)
  sync     Brings the cache of a workspace up to date with its files (after git pull, checkout, merge)

";

/// Дерево команд. Глобальные параметры — те же, что у .NET (`GlobalOptions`): принимаются везде, в любом месте строки.
fn command() -> Command {
    let global = |arg: Arg| arg.global(true);
    Command::new("tasker")
        .about(ROOT_DESCRIPTION)
        .disable_help_flag(true)
        .disable_help_subcommand(true)
        .disable_version_flag(true)
        .arg(global(
            Arg::new("help").short('h').short_alias('?').long("help").action(ArgAction::SetTrue),
        ))
        .arg(global(Arg::new("workspace").short('w').long("workspace").value_name("workspace")))
        .arg(global(Arg::new("sqlite").long("sqlite").value_name("sqlite")))
        .arg(global(Arg::new("project").short('p').long("project").value_name("Tasker")))
        .arg(global(Arg::new("json").long("json").action(ArgAction::SetTrue)))
        .arg(global(Arg::new("quiet").short('q').long("quiet").action(ArgAction::SetTrue)))
        .arg(global(
            Arg::new("truncate").long("truncate").alias("no-wrap").action(ArgAction::SetTrue),
        ))
        .arg(global(Arg::new("no-truncate").long("no-truncate").action(ArgAction::SetTrue)))
        .arg(global(Arg::new("width").long("width").value_name("width")))
        .subcommand(
            Command::new("migrate")
                .about(MIGRATE_DESCRIPTION)
                .arg(Arg::new("dry-run").long("dry-run").action(ArgAction::SetTrue))
                .arg(Arg::new("check").long("check").action(ArgAction::SetTrue)),
        )
        .subcommand(Command::new("sync").about(sync::DESCRIPTION))
        .subcommand(
            Command::new("cleanup")
                .about(cleanup::DESCRIPTION)
                .arg(Arg::new("resolve-conflicts").long("resolve-conflicts").action(ArgAction::SetTrue))
                .arg(Arg::new("dry-run").long("dry-run").action(ArgAction::SetTrue))
                .arg(Arg::new("check").long("check").action(ArgAction::SetTrue)),
        )
}

/// Ошибка пользователя команды (`CliException`): сообщение выводится как есть, без стека, код 1.
pub(crate) struct CliError(pub(crate) String);

impl From<std::io::Error> for CliError {
    fn from(e: std::io::Error) -> Self {
        CliError(e.to_string())
    }
}

impl From<tasker_services::Error> for CliError {
    fn from(e: tasker_services::Error) -> Self {
        CliError(e.message())
    }
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let mut out = std::io::stdout().lock();
    let mut err = std::io::stderr().lock();
    let code = run(&args, &mut out, &mut err);
    let _ = out.flush();
    let _ = err.flush();
    std::process::exit(code);
}

/// Выполняет команду. Вывод передаётся снаружи — так её проверяют тесты.
fn run(args: &[String], out: &mut dyn Write, err: &mut dyn Write) -> i32 {
    // Переменная TASKER_PROJCT вместо TASKER_PROJECT иначе молча не подействует. В stderr: stdout (в том числе --json) не портим.
    for warning in tasker_core::config::check_process_environment() {
        let _ = writeln!(err, "Warning: {warning}");
    }

    let matches = match command().try_get_matches_from(std::iter::once("tasker".to_string()).chain(args.iter().cloned())) {
        Ok(matches) => matches,
        Err(e) => {
            let _ = writeln!(err, "{}", e.render());
            return 1;
        }
    };

    let result = match matches.subcommand() {
        Some(("migrate", sub)) => {
            if sub.get_flag("help") {
                let _ = out.write_all(MIGRATE_HELP.as_bytes());
                return 0;
            }
            migrate(&matches, sub, out, err)
        }
        Some(("sync", sub)) => {
            if sub.get_flag("help") {
                let _ = out.write_all(sync::HELP.as_bytes());
                return 0;
            }
            sync::run(&matches, out)
        }
        Some(("cleanup", sub)) => {
            if sub.get_flag("help") {
                let _ = out.write_all(cleanup::HELP.as_bytes());
                return 0;
            }
            cleanup::run(&matches, sub, out, err)
        }
        _ => {
            let _ = out.write_all(ROOT_HELP.as_bytes());
            return if matches.get_flag("help") || args.is_empty() { 0 } else { 1 };
        }
    };

    match result {
        Ok(code) => code,
        Err(CliError(message)) => {
            let _ = writeln!(err, "Error: {message}");
            1
        }
    }
}

/// Папка рабочей области из параметров (`Session.Locate`): по умолчанию текущая. Папка должна существовать.
pub(crate) fn locate_folder(matches: &ArgMatches) -> Result<WorkspaceLocation, CliError> {
    if matches.get_one::<String>("sqlite").is_some() {
        if matches.get_one::<String>("workspace").is_some() {
            return Err(CliError("Use either --workspace or --sqlite, not both".into()));
        }
        // Расхождение с .NET (зафиксировано в отчёте TSK-130): рабочие области SQLite в этой сборке ещё не открываются.
        return Err(CliError("--sqlite is not supported by this build yet".into()));
    }
    let folder = match matches.get_one::<String>("workspace") {
        Some(folder) => expand_user_path(folder),
        None => std::env::current_dir()?.to_string_lossy().into_owned(),
    };
    let location = WorkspaceLocation::files(&folder);
    if !Path::new(location.path()).is_dir() {
        return Err(CliError(format!("Folder not found: {}", location.path())));
    }
    Ok(location)
}

/// Рабочая область из параметров: каталог `.tasker` существующей папки.
fn locate(matches: &ArgMatches) -> Result<TaskerDirectory, CliError> {
    Ok(TaskerDirectory::new(locate_folder(matches)?.path()))
}

/// `tasker migrate [--dry-run] [--check]` (`MigrateCommands` в .NET).
fn migrate(matches: &ArgMatches, sub: &ArgMatches, out: &mut dyn Write, err: &mut dyn Write) -> Result<i32, CliError> {
    let dry_run = sub.get_flag("dry-run");
    let check = sub.get_flag("check");
    let directory = locate(matches)?;
    // Как FileStorageModule: открытие области создаёт projects/, users/ и дописывает .gitignore.
    directory.ensure_created()?;

    let report = migration::run(&directory, &NoIndex, MigrationOptions { dry_run: dry_run || check })?;
    show(&report, dry_run, check, matches.get_flag("json"), out);

    if check {
        return Ok(if report.needs_attention() { NEEDS_MIGRATION } else { 0 });
    }
    if !report.newer.is_empty() || !report.unreadable.is_empty() {
        // Часть файлов осталась как была — скрипт должен это заметить.
        let _ = writeln!(err, "Error: some files were not migrated (see above)");
        return Ok(1);
    }
    Ok(0)
}

fn file_name(path: &str) -> &str {
    path.rsplit('/').next().unwrap_or(path)
}

fn show(report: &MigrationReport, dry_run: bool, check: bool, json: bool, out: &mut dyn Write) {
    if json {
        let _ = writeln!(out, "{}", tasker_core::json::to_string(&report_json(report, dry_run, check)));
        return;
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

    let _ = writeln!(out, "{}", lines.join("\n"));
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
    fn the_command_tree_is_consistent() {
        command().debug_assert();
    }

    #[test]
    fn global_options_are_accepted_anywhere_and_the_help_flags_are_recognised() {
        let m = command()
            .try_get_matches_from(["tasker", "-w", "x", "migrate", "--check", "--json", "-q"])
            .unwrap();
        let (name, sub) = m.subcommand().unwrap();
        assert_eq!(name, "migrate");
        assert!(sub.get_flag("check") && !sub.get_flag("dry-run"));
        assert!(m.get_flag("json") && m.get_flag("quiet"));
        assert_eq!(m.get_one::<String>("workspace").map(String::as_str), Some("x"));

        for flag in ["-?", "-h", "--help"] {
            let m = command().try_get_matches_from(["tasker", "migrate", flag]).unwrap();
            assert!(m.subcommand().unwrap().1.get_flag("help"), "{flag}");
        }
        assert!(command().try_get_matches_from(["tasker", "migrate", "--nope"]).is_err());
    }

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
        let mut out = Vec::new();
        show(&report, false, false, false, &mut out);
        let text = String::from_utf8(out).unwrap();
        assert!(text.contains("  ... and 2 more\n  renamed  projects/p/tasks/a.yaml  ->  b-00000001.yaml\n"));
        assert!(text.ends_with("Migrated 12 file(s) to format 9\nRenamed 1 file(s) after the names of their entities\n"));
        assert_eq!(text.matches("migrated  ").count(), 10);
    }
}
