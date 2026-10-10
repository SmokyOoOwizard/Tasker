//! Точка входа `tasker-mcpd`. Аргументы и коды выхода — как у .NET `Program.cs`: `--help`; рабочий процесс супервизора
//! `--worker --listen-fd N | --listen-handoff [--console]` (служебный запуск, остальные аргументы не проверяются); иначе
//! `--detached`, `--single`, `--supervised` (неизвестный аргумент — код 2) и выбор режима ([`tasker_mcpd::use_supervisor`]).
use tasker_mcpd::{daemon, handoff, supervisor, worker};

const HELP: &str = "tasker-mcpd — the Tasker MCP server (daemon).

Usage: tasker-mcpd [--detached] [--single | --supervised]

  --detached   run in the background: detach from the terminal and log only to the file
  --single     run the server in this very process (the default on Windows)
  --supervised run a supervisor that keeps the port and runs the server as a worker process, so that
               'tasker mcp upgrade' can replace it without downtime (the default on macOS and Linux;
               on Windows also enabled by TASKER_MCP_SUPERVISOR=1)

Normally started with 'tasker mcp start' or 'tasker mcp run'; settings are managed with 'tasker mcp ...'.";

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    std::process::exit(run(&args));
}

/// Разбор аргументов и запуск; возвращает код выхода.
fn run(args: &[String]) -> i32 {
    let has = |name: &str| args.iter().any(|a| a == name);
    if has("--help") || has("-h") {
        println!("{HELP}");
        return 0;
    }

    // Рабочий процесс супервизора (служебный запуск): слушающий сокет получен готовым — наследуемый дескриптор (--listen-fd)
    // или придёт в hello (--listen-handoff).
    if has("--worker") {
        let fd = match args.iter().position(|a| a == handoff::FD_ARGUMENT) {
            Some(at) => match args.get(at + 1).and_then(|n| n.parse::<i32>().ok()) {
                Some(fd) => Some(fd),
                None => {
                    eprintln!("--listen-fd needs a descriptor number");
                    return 2;
                }
            },
            None if has(handoff::HANDOFF_ARGUMENT) => None,
            None => {
                eprintln!("--worker needs --listen-fd <descriptor> or --listen-handoff");
                return 2;
            }
        };
        return worker::run(fd, has("--console"));
    }

    if let Some(unknown) = args
        .iter()
        .find(|a| !matches!(a.as_str(), "--detached" | "--single" | "--supervised"))
    {
        eprintln!("Unknown argument '{unknown}': see 'tasker-mcpd --help'");
        return 2;
    }

    let detached = has("--detached");
    let variable = std::env::var(tasker_mcpd::SUPERVISOR_VARIABLE).ok();
    if tasker_mcpd::use_supervisor(cfg!(windows), has("--single"), has("--supervised"), variable.as_deref()) {
        supervisor::run(detached)
    } else {
        daemon::run(detached)
    }
}
