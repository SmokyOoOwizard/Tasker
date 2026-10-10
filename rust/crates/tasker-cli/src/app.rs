//! Выполнение строки `tasker …` (`CliApp` и `GlobalOptions` в .NET): предупреждения `TASKER_*`, разбор clap по дереву [`spec`],
//! справка и `--version`, проверки и тексты ошибок разбора как у System.CommandLine, открытие области и выполнение команды с
//! переводом ошибок в сообщение и код 1.
use crate::context::Context;
use crate::errors::{ERROR_EXIT_CODE, Result};
use crate::hints::Hints;
use crate::session::Session;
use crate::spec::{self, Arity, CommandSpec, GLOBAL_OPTIONS, OptKind, ValueKind};
use crate::terminal::{self, Terminal};
use crate::{cleanup, help, migrate, sync};
use clap::ArgMatches;
use clap::error::{ContextKind, ContextValue, ErrorKind};
use std::io::Write;

/// Версия сборки, как `--version` у .NET (`InformationalVersion`): `0.1.0` у релиза, `0.1.0+<коммит>` у сборки из исходников
/// (`tasker-version`, `build.rs`).
const VERSION: &str = tasker_version::VERSION;

/// Общие параметры (`GlobalOptions`) из разобранной строки.
struct Globals {
    workspace: Option<String>,
    sqlite: Option<String>,
    project: Option<String>,
    json: bool,
    quiet: bool,
    truncate: bool,
    no_truncate: bool,
    width: Option<String>,
    help: bool,
}

impl Globals {
    fn from(matches: &ArgMatches) -> Globals {
        let text = |id: &str| matches.get_one::<String>(id).cloned();
        Globals {
            workspace: text("workspace"),
            sqlite: text("sqlite"),
            project: text("project"),
            json: matches.get_flag("json"),
            quiet: matches.get_flag("quiet"),
            truncate: matches.get_flag("truncate"),
            no_truncate: matches.get_flag("no-truncate"),
            width: text("width"),
            help: matches.get_flag("help"),
        }
    }
}

/// Выполняет команду. Вывод передаётся снаружи — так её проверяют тесты.
pub fn run(args: &[String], out: &mut dyn Write, err: &mut dyn Write) -> i32 {
    // Переменная TASKER_PROJCT вместо TASKER_PROJECT иначе молча не подействует. В stderr: stdout (в том числе --json) не портим.
    // Дополнение по Tab (директива [suggest]) молчит всегда: на каждое нажатие клавиши предупреждение было бы лишним шумом.
    let completing = args.first().is_some_and(|a| a.starts_with("[suggest"));
    if completing {
        return crate::suggest::run(args, out);
    }
    if !completing {
        for warning in tasker_core::config::check_process_environment() {
            let _ = writeln!(err, "Warning: {warning}");
        }
    }

    crate::perf::mark("env-check");
    let root = spec::root();
    crate::perf::mark("build-root");
    let terminal = Terminal::current();
    let help_width = if terminal.is_output && terminal.columns > 0 {
        terminal.columns
    } else {
        usize::MAX
    };
    let help_requested = args
        .iter()
        .take_while(|a| *a != "--")
        .any(|a| matches!(a.as_str(), "-h" | "-?" | "--help"));

    let parsed = spec::clap_root(&root).try_get_matches_from(std::iter::once("tasker".to_string()).chain(args.iter().cloned()));
    crate::perf::mark("parse");
    let code = match parsed {
        Ok(matches) => invoke(&root, &matches, args, out, err, &terminal, help_width),
        Err(error) => {
            let (path, rest) = walk(&root, args);
            if help_requested {
                // System.CommandLine показывает справку, если её просили, даже когда разбор не прошёл (нет обязательного аргумента).
                let _ = out.write_all(help::render(&path, &mut Hints::from_args(args).resolver(), help_width).as_bytes());
                return 0;
            }
            parse_error(&error, &path, &rest, args, out, err, help_width)
        }
    };
    crate::perf::mark("end");
    crate::perf::dump(err);
    code
}

impl Hints {
    fn resolver(&mut self) -> impl FnMut(spec::Hint) -> Vec<String> + '_ {
        move |hint| self.resolve(hint)
    }
}

/// Разобранная строка без ошибок clap: справка, версия, проверки System.CommandLine, выполнение.
fn invoke(
    root: &CommandSpec,
    matches: &ArgMatches,
    args: &[String],
    out: &mut dyn Write,
    err: &mut dyn Write,
    terminal: &Terminal,
    help_width: usize,
) -> i32 {
    // Цепочка команд и совпадений: общие параметры clap копирует в самое глубокое совпадение.
    let mut path: Vec<&CommandSpec> = vec![root];
    let mut leaf = matches;
    while let Some((name, sub)) = leaf.subcommand() {
        let command = path.last().expect("a command").subcommand(name).expect("a known subcommand");
        path.push(command);
        leaf = sub;
    }
    let command = *path.last().expect("a command");
    let globals = Globals::from(leaf);

    if globals.help {
        let type_references: Vec<String> = match command.options.iter().find(|o| o.id() == "type") {
            Some(option) if matches!(option.kind, OptKind::Value { .. }) => {
                leaf.get_many::<String>("type").map(|v| v.cloned().collect()).unwrap_or_default()
            }
            _ => Vec::new(),
        };
        let mut hints = Hints::new(
            globals.workspace.clone(),
            globals.sqlite.clone(),
            globals.project.clone(),
            type_references,
        );
        let _ = out.write_all(help::render(&path, &mut hints.resolver(), help_width).as_bytes());
        return 0;
    }

    if matches.get_flag("version") {
        if args.len() > 1 {
            let _ = writeln!(err, "--version option cannot be combined with other arguments.\n");
            return ERROR_EXIT_CODE;
        }
        let _ = writeln!(out, "{VERSION}");
        return 0;
    }

    let mut errors: Vec<String> = Vec::new();
    if command.is_group() {
        errors.push("Required command was not provided.".into());
    }
    errors.extend(validate(command, leaf));
    if !errors.is_empty() {
        let _ = out.write_all(help::render(&path, &mut Hints::from_args(args).resolver(), help_width).as_bytes());
        for error in errors {
            let _ = writeln!(err, "{error}");
        }
        let _ = writeln!(err);
        return ERROR_EXIT_CODE;
    }

    let names: Vec<&str> = path.iter().skip(1).map(|c| c.name).collect();
    dispatch(&names, leaf, &globals, terminal, out, err)
}

/// Выполнение команды по её пути (`CliApp.BuildRoot`: группа → модуль команд).
fn dispatch(names: &[&str], leaf: &ArgMatches, globals: &Globals, terminal: &Terminal, out: &mut dyn Write, err: &mut dyn Write) -> i32 {
    use crate::commands;
    let sub = names.get(1).copied().unwrap_or_default();
    match names {
        ["migrate"] => {
            let (dry_run, check) = (leaf.get_flag("dry-run"), leaf.get_flag("check"));
            with_workspace(globals, terminal, out, err, |ctx| migrate::run(ctx, dry_run, check))
        }
        ["cleanup"] => {
            let (dry_run, check, resolve) = (leaf.get_flag("dry-run"), leaf.get_flag("check"), leaf.get_flag("resolve-conflicts"));
            with_workspace(globals, terminal, out, err, |ctx| cleanup::run(ctx, dry_run, check, resolve))
        }
        ["sync"] => plain(globals, terminal, out, err, |ctx| {
            sync::run(ctx, globals.workspace.as_deref(), globals.sqlite.as_deref())
        }),
        ["project", _] => with_workspace(globals, terminal, out, err, |ctx| commands::project::run(ctx, sub, leaf)),
        ["status", _] => with_workspace(globals, terminal, out, err, |ctx| commands::status::run_status(ctx, sub, leaf)),
        ["status-set", _] => with_workspace(globals, terminal, out, err, |ctx| commands::status::run_status_set(ctx, sub, leaf)),
        ["task-type", _] => with_workspace(globals, terminal, out, err, |ctx| commands::task_type::run(ctx, sub, leaf)),
        ["link-type", _] => with_workspace(globals, terminal, out, err, |ctx| commands::link_type::run(ctx, sub, leaf)),
        ["field", _] => with_workspace(globals, terminal, out, err, |ctx| commands::field::run_field(ctx, sub, leaf)),
        ["enum", _] => with_workspace(globals, terminal, out, err, |ctx| commands::field::run_enum(ctx, sub, leaf)),
        ["board", _] => with_workspace(globals, terminal, out, err, |ctx| commands::board::run(ctx, sub, leaf)),
        ["task", _] => with_workspace(globals, terminal, out, err, |ctx| commands::task::run(ctx, sub, leaf)),
        ["series", _] => with_workspace(globals, terminal, out, err, |ctx| commands::series::run(ctx, sub, leaf)),
        ["user", _] => with_workspace(globals, terminal, out, err, |ctx| commands::user::run(ctx, "user", sub, leaf)),
        ["agent", _] => with_workspace(globals, terminal, out, err, |ctx| commands::user::run(ctx, "agent", sub, leaf)),
        ["lock", _] => with_workspace(globals, terminal, out, err, |ctx| commands::lock::run(ctx, sub, leaf)),
        ["whoami"] => plain(globals, terminal, out, err, |ctx| commands::lock::whoami(ctx, leaf)),
        ["hooks", _] => plain(globals, terminal, out, err, |ctx| {
            commands::hooks::run(ctx, sub, leaf, globals.workspace.as_deref())
        }),
        ["manual"] => plain(globals, terminal, out, err, |ctx| commands::manual::run(ctx, leaf)),
        ["completion"] => plain(globals, terminal, out, err, |ctx| commands::completion::run(ctx, leaf)),
        ["mcp", ..] => plain(globals, terminal, out, err, |ctx| commands::mcp::run(ctx, &names[1..], leaf)),
        _ => ERROR_EXIT_CODE,
    }
}

/// Открывает рабочую область, выполняет команду и переводит ожидаемые ошибки в сообщение и код 1 (`GlobalOptions.Run`).
fn with_workspace(
    globals: &Globals,
    terminal: &Terminal,
    out: &mut dyn Write,
    err: &mut dyn Write,
    action: impl FnOnce(&mut Context<'_>) -> Result<()>,
) -> i32 {
    let project = globals.project.clone().or_else(|| std::env::var("TASKER_PROJECT").ok());
    let run = |out: &mut dyn Write, err: &mut dyn Write| -> Result<i32> {
        crate::perf::mark("invoke");
        let session = Session::open(globals.workspace.as_deref(), globals.sqlite.as_deref(), false)?;
        crate::perf::mark("session-open");
        let max_width = terminal.limit(globals.truncate, globals.no_truncate, globals.width.as_deref())?;
        let mut ctx = Context::new(out, err, Some(session), project);
        ctx.json = globals.json;
        ctx.quiet = globals.quiet;
        ctx.max_width = max_width;
        ctx.is_interactive = terminal::stdin_is_terminal();
        action(&mut ctx)?;
        crate::perf::mark("action");
        Ok(ctx.exit_code)
    };
    guard(run(out, err), err)
}

/// Команда без рабочей области (настройки, демон): только вывод и обработка ошибок (`GlobalOptions.RunPlain`).
fn plain(
    globals: &Globals,
    terminal: &Terminal,
    out: &mut dyn Write,
    err: &mut dyn Write,
    action: impl FnOnce(&mut Context<'_>) -> Result<()>,
) -> i32 {
    let run = |out: &mut dyn Write, err: &mut dyn Write| -> Result<i32> {
        let max_width = terminal.limit(globals.truncate, globals.no_truncate, globals.width.as_deref())?;
        let mut ctx = Context::new(out, err, None, None);
        ctx.json = globals.json;
        ctx.quiet = globals.quiet;
        ctx.max_width = max_width;
        ctx.is_interactive = terminal::stdin_is_terminal();
        action(&mut ctx)?;
        Ok(ctx.exit_code)
    };
    guard(run(out, err), err)
}

fn guard(result: Result<i32>, err: &mut dyn Write) -> i32 {
    match result {
        Ok(code) => code,
        Err(error) => {
            let _ = writeln!(err, "{}", error.text());
            ERROR_EXIT_CODE
        }
    }
}

/// Проверки, которые System.CommandLine делает после разбора и пропускает при `--help`: обязательные параметры, числа,
/// допустимые значения аргументов.
fn validate(command: &CommandSpec, leaf: &ArgMatches) -> Vec<String> {
    let mut errors = Vec::new();
    for option in &command.options {
        let values: Vec<&String> = match option.kind {
            OptKind::Flag => Vec::new(),
            _ => leaf.get_many::<String>(option.id()).map(|v| v.collect()).unwrap_or_default(),
        };
        match option.kind {
            OptKind::Value { required, value, .. } => {
                if required && !leaf.contains_id(option.id()) {
                    errors.push(format!("Option '{}' is required.", option.name));
                }
                if value == ValueKind::Int {
                    for text in values.iter().filter(|v| terminal::parse_int(v).is_none()) {
                        errors.push(format!(
                            "Cannot parse argument '{text}' for option '{}' as expected type 'System.Int32'.",
                            option.name
                        ));
                    }
                }
            }
            OptKind::OptionalBool => {
                for text in values
                    .iter()
                    .filter(|v| !v.eq_ignore_ascii_case("true") && !v.eq_ignore_ascii_case("false"))
                {
                    errors.push(format!(
                        "Cannot parse argument '{text}' for option '{}' as expected type 'System.Boolean'.",
                        option.name
                    ));
                }
            }
            OptKind::Flag => {}
        }
    }
    for argument in &command.arguments {
        let values: Vec<&String> = leaf.get_many::<String>(argument.id()).map(|v| v.collect()).unwrap_or_default();
        if argument.value == ValueKind::Int {
            for text in values.iter().filter(|v| terminal::parse_int(v).is_none()) {
                errors.push(format!(
                    "Cannot parse argument '{text}' for command '{}' as expected type 'System.Int32'.",
                    command.name
                ));
            }
        }
        if !argument.accepted.is_empty() {
            for text in values.iter().filter(|v| !argument.accepted.contains(&v.as_str())) {
                let allowed: String = argument.accepted.iter().map(|a| format!("\n\t'{a}'")).collect();
                errors.push(format!("Argument '{text}' not recognized. Must be one of:{allowed}"));
            }
        }
    }
    errors
}

/// Цепочка команд по словам строки, пока они совпадают с подкомандами (параметры и их значения пропускаются), и остальные
/// слова — аргументы или неизвестные команды.
fn walk<'a>(root: &'a CommandSpec, args: &[String]) -> (Vec<&'a CommandSpec>, Vec<String>) {
    let mut path = vec![root];
    let mut rest = Vec::new();
    let mut i = 0;
    while i < args.len() {
        let token = &args[i];
        if token == "--" {
            rest.extend(args[i + 1..].iter().cloned());
            break;
        }
        let command = *path.last().expect("a command");
        if token.starts_with('-') && token.len() > 1 {
            if takes_value(&path, token) && !token.contains('=') && !token.contains(':') {
                i += 1; // значение параметра
            }
        } else if rest.is_empty()
            && let Some(sub) = command.subcommand(token)
        {
            path.push(sub);
        } else {
            rest.push(token.clone());
        }
        i += 1;
    }
    (path, rest)
}

/// Принимает ли параметр с таким именем значение (у текущей команды, её предков или среди общих).
fn takes_value(path: &[&CommandSpec], token: &str) -> bool {
    path.iter()
        .flat_map(|c| c.options.iter())
        .chain(GLOBAL_OPTIONS.iter())
        .any(|o| o.all_names().any(|n| n == token) && matches!(o.kind, OptKind::Value { .. }))
}

/// Ошибка разбора clap в виде System.CommandLine (`ParseErrorAction`): в stdout — подсказки опечаток и справка команды,
/// в stderr — сообщения об ошибках и пустая строка, код 1.
fn parse_error(
    error: &clap::Error,
    path: &[&CommandSpec],
    rest: &[String],
    args: &[String],
    out: &mut dyn Write,
    err: &mut dyn Write,
    help_width: usize,
) -> i32 {
    let command = *path.last().expect("a command");
    let mut errors: Vec<String> = Vec::new();
    let mut unmatched: Vec<String> = Vec::new();
    let invalid = |kind: ContextKind| match error.get(kind) {
        Some(ContextValue::String(s)) => Some(s.clone()),
        _ => None,
    };

    if command.is_group() {
        errors.push("Required command was not provided.".into());
    }
    match error.kind() {
        ErrorKind::UnknownArgument | ErrorKind::InvalidSubcommand | ErrorKind::TooManyValues => {
            if command.is_group() {
                unmatched.extend(rest.iter().cloned());
            } else if let Some(token) = invalid(ContextKind::InvalidArg).or_else(|| invalid(ContextKind::InvalidSubcommand)) {
                unmatched.push(token.split_whitespace().next().unwrap_or_default().to_string());
            }
            for token in &unmatched {
                errors.push(format!("Unrecognized command or argument '{token}'."));
            }
        }
        ErrorKind::MissingRequiredArgument => {
            let required = command.arguments.iter().filter(|a| a.arity == Arity::ExactlyOne).count();
            let missing = required.saturating_sub(rest.len()).max(1);
            for _ in 0..missing {
                errors.push(format!("Required argument missing for command: '{}'.", command.name));
            }
        }
        ErrorKind::InvalidValue
        | ErrorKind::TooFewValues
        | ErrorKind::WrongNumberOfValues
        | ErrorKind::NoEquals
        | ErrorKind::ValueValidation => match invalid(ContextKind::InvalidArg) {
            Some(option) => errors.push(format!(
                "Required argument missing for option: '{}'.",
                option.split_whitespace().next().unwrap_or_default()
            )),
            None => errors.push(error.to_string().trim_start_matches("error: ").trim().to_string()),
        },
        _ => errors.push(error.to_string().trim_start_matches("error: ").trim().to_string()),
    }

    for token in &unmatched {
        let _ = out.write_all(help::typo_block(path, token).as_bytes());
    }
    let _ = out.write_all(help::render(path, &mut Hints::from_args(args).resolver(), help_width).as_bytes());
    for error in errors {
        let _ = writeln!(err, "{error}");
    }
    let _ = writeln!(err);
    ERROR_EXIT_CODE
}

#[cfg(test)]
mod tests {
    use super::*;

    fn run_text(args: &[&str]) -> (String, String, i32) {
        let args: Vec<String> = args.iter().map(|a| a.to_string()).collect();
        let (mut out, mut err) = (Vec::new(), Vec::new());
        let code = run(&args, &mut out, &mut err);
        (String::from_utf8(out).unwrap(), String::from_utf8(err).unwrap(), code)
    }

    #[test]
    fn walk_follows_subcommands_and_skips_option_values() {
        let root = spec::root();
        let (path, rest) = walk(
            &root,
            &["-w".into(), "x".into(), "task".into(), "get".into(), "T-1".into(), "--json".into()],
        );
        assert_eq!(path.iter().map(|c| c.name).collect::<Vec<_>>(), ["tasker", "task", "get"]);
        assert_eq!(rest, ["T-1"]);
        let (path, rest) = walk(&root, &["nope".into()]);
        assert_eq!(path.len(), 1);
        assert_eq!(rest, ["nope"]);
    }

    #[test]
    fn an_unknown_command_prints_suggestions_help_and_errors() {
        let (out, err, code) = run_text(&["nope"]);
        assert_eq!(code, 1);
        assert_eq!(
            err,
            "Required command was not provided.\nUnrecognized command or argument 'nope'.\n\n"
        );
        assert!(out.starts_with("'nope' was not matched. Did you mean one of the following?\nlock\nmcp\n-p\n\nDescription:\n"));
    }

    #[test]
    fn a_group_without_a_subcommand_and_a_missing_argument_are_errors_of_system_commandline() {
        let (out, err, code) = run_text(&["task"]);
        assert_eq!((code, err.as_str()), (1, "Required command was not provided.\n\n"));
        assert!(out.starts_with("Description:\n  Tasks\n\nUsage:\n  tasker task [command] [options]\n"));

        let (out, err, code) = run_text(&["task", "get"]);
        assert_eq!((code, err.as_str()), (1, "Required argument missing for command: 'get'.\n\n"));
        assert!(out.contains("Usage:\n  tasker task get <task> [options]\n"));

        let (_, err, code) = run_text(&["task", "create", "Title", "-w", "/nonexistent"]);
        assert_eq!((code, err.as_str()), (1, "Option '--type' is required.\n\n"));

        let (_, err, code) = run_text(&["task", "list", "--limit", "x", "-w", "/nonexistent"]);
        assert_eq!(
            (code, err.as_str()),
            (
                1,
                "Cannot parse argument 'x' for option '--limit' as expected type 'System.Int32'.\n\n"
            )
        );

        let (_, err, code) = run_text(&["completion", "fish"]);
        assert_eq!(
            (code, err.as_str()),
            (
                1,
                "Argument 'fish' not recognized. Must be one of:\n\t'zsh'\n\t'bash'\n\t'pwsh'\n\t'powershell'\n\n"
            )
        );

        let (_, err, code) = run_text(&["project", "update", "x", "--name"]);
        assert_eq!((code, err.as_str()), (1, "Required argument missing for option: '--name'.\n\n"));
    }

    #[test]
    fn version_help_and_not_implemented() {
        let (out, _, code) = run_text(&["--version"]);
        assert_eq!((code, out.as_str()), (0, format!("{}\n", tasker_version::VERSION).as_str()));
        assert!(out.starts_with(include_str!("../../../../VERSION").trim()));
        let (out, err, code) = run_text(&["task", "create", "-h"]);
        assert_eq!((code, err.as_str()), (0, ""));
        assert!(out.starts_with("Description:\n  Creates a task\n"));
        let (out, err, code) = run_text(&["sync", "-w", "/nonexistent"]);
        assert_eq!(
            (code, out.as_str(), err.as_str()),
            (1, "", "Error: Folder not found: /nonexistent\n")
        );
        let (_, err, code) = run_text(&["task", "list", "-w", "/nonexistent"]);
        assert_eq!((code, err.as_str()), (1, "Error: Folder not found: /nonexistent\n"));
    }
}
