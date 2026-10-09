//! Точка входа `tasker-mcpd`. Аргументы и коды выхода — как у .NET `Program.cs`: `--help`, `--detached`, `--single`; неизвестный
//! аргумент — 2; `--worker` и `--supervised` в этой сборке отвергаются (TSK-139).
use tasker_mcpd::daemon;

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
    // Рабочий процесс супервизора и сам супервизор — следующая задача (TSK-139): честный отказ вместо тихого одиночного режима.
    if has("--worker") {
        eprintln!("--worker is not supported by this build of tasker-mcpd: the supervisor and its worker processes come with TSK-139");
        return 2;
    }
    if let Some(unknown) = args
        .iter()
        .find(|a| !matches!(a.as_str(), "--detached" | "--single" | "--supervised"))
    {
        eprintln!("Unknown argument '{unknown}': see 'tasker-mcpd --help'");
        return 2;
    }
    if has("--supervised") {
        eprintln!(
            "--supervised is not supported by this build of tasker-mcpd: the supervisor comes with TSK-139; run without it (one process)"
        );
        return 2;
    }
    daemon::run(has("--detached"))
}
