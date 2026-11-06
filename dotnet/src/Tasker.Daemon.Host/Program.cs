using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Tasker.Core;
using Tasker.Daemon;
using Tasker.Daemon.Host;

// tasker-mcpd — демон MCP: отдельная программа, чтобы консольной утилите tasker не нужен был веб-слой (ASP.NET Core).
// Её запускают tasker (mcp start, mcp run), launchd и systemd; руками — тоже можно.
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        tasker-mcpd — the Tasker MCP server (daemon).

        Usage: tasker-mcpd [--detached] [--build-info]

          --detached    run in the background: detach from the terminal and log only to the file
          --build-info  print the version and the build of this program as JSON and exit

        Normally started with 'tasker mcp start' or 'tasker mcp run'; settings are managed with 'tasker mcp ...'.
        """);
    return 0;
}

if (args.Contains(Launcher.BuildInfoArgument))
{
    _ = typeof(WebApplication).Assembly;
    Console.WriteLine(JsonSerializer.Serialize(new DaemonBuildInfo(DaemonBuild.Version, DaemonBuild.Id), TaskerJson.Options));
    return 0;
}

var unknown = args.Where(x => x is not ("--detached" or "--single" or "--supervised")).ToArray();
if (unknown.Length > 0)
{
    Console.Error.WriteLine($"Unknown argument '{unknown[0]}': see 'tasker-mcpd --help'");
    return 2;
}

return await McpDaemon.Run(args.Contains("--detached"), Console.Error);
