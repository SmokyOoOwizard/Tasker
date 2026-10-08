using Tasker.Daemon;
using Tasker.Daemon.Host;
using Tasker.Global;

// tasker-mcpd — демон MCP: отдельная программа, чтобы консольной утилите tasker не нужен был веб-слой (ASP.NET Core).
// Её запускают tasker (mcp start, mcp run), launchd и systemd; руками — тоже можно.
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        tasker-mcpd — the Tasker MCP server (daemon).

        Usage: tasker-mcpd [--detached] [--single | --supervised]

          --detached   run in the background: detach from the terminal and log only to the file
          --single     run the server in this very process (the default on Windows)
          --supervised run a supervisor that keeps the port and runs the server as a worker process, so that
                       'tasker mcp upgrade' can replace it without downtime (the default on macOS and Linux;
                       on Windows also enabled by TASKER_MCP_SUPERVISOR=1)

        Normally started with 'tasker mcp start' or 'tasker mcp run'; settings are managed with 'tasker mcp ...'.
        """);
    return 0;
}

// Рабочий процесс супервизора (служебный запуск): слушающий сокет получен готовым.
if (args.Contains("--worker"))
{
    // Сокет — либо наследуемый дескриптор (--listen-fd), либо придёт в hello (--listen-handoff).
    int? fd = null;
    var at = Array.IndexOf(args, ListenerHandoff.FdArgument);
    if (at >= 0)
    {
        if (at + 1 >= args.Length || !int.TryParse(args[at + 1], out var parsed))
        {
            Console.Error.WriteLine("--listen-fd needs a descriptor number");
            return 2;
        }

        fd = parsed;
    }
    else if (!args.Contains(ListenerHandoff.HandoffArgument))
    {
        Console.Error.WriteLine("--worker needs --listen-fd <descriptor> or --listen-handoff");
        return 2;
    }

    return await McpWorker.Run(fd, args.Contains("--console"), Console.Error);
}

var unknown = args.Where(x => x is not ("--detached" or "--single" or "--supervised")).ToArray();
if (unknown.Length > 0)
{
    Console.Error.WriteLine($"Unknown argument '{unknown[0]}': see 'tasker-mcpd --help'");
    return 2;
}

var detached = args.Contains("--detached");
return DaemonPlatform.UseSupervisor(OperatingSystem.IsWindows(), args.Contains("--single"), args.Contains("--supervised"), AppEnvironment.Get(DaemonPlatform.SupervisorVariable))
    ? await Supervisor.Run(detached, Console.Error)
    : await McpDaemon.Run(detached, Console.Error);
