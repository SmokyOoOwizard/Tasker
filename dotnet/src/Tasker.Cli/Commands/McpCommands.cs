using System.CommandLine;
using Tasker.Cli.Completion;
using System.Text.Json;
using Tasker.Daemon;
using Tasker.Global;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Cli.Commands;

/// <summary>
/// <c>tasker mcp</c>: MCP-сервер (демон) и его глобальные настройки. Настройки лежат в общем файле
/// <c>settings.json</c>: то, что изменено здесь, видят демон и десктоп.
/// </summary>
internal static class McpCommands
{
    public static Command Build(GlobalOptions g, SettingsStore settings, DaemonController daemon) =>
        Kit.Group("mcp", "MCP server: global settings and the background service",
            Run(g),
            Start(g, daemon),
            Stop(g, daemon),
            Restart(g, daemon),
            Upgrade(g, daemon),
            Status(g, daemon),
            Autostart(g, daemon),
            Workspaces(g, settings),
            Config(g, settings),
            Port(g, settings));

    private static Command Run(GlobalOptions g)
    {
        var detached = new Option<bool>("--detached") { Description = "Started in the background (used by start and by the system service)", Hidden = true };
        var command = new Command("run", "Runs the MCP server in this terminal until Ctrl+C (the background service runs the same)");
        command.Options.Add(detached);
        command.SetAction((parse, ct) => g.RunPlain(parse, ct, async ctx =>
        {
            ctx.ExitCode = await DaemonHost.Run(parse.GetValue(detached), ct);
        }));
        return command;
    }

    private static Command Start(GlobalOptions g, DaemonController daemon) =>
        Kit.Plain(g, "start", "Starts the MCP server in the background", _ => { }, async (_, ctx) =>
        {
            var (status, already) = await daemon.Start(ctx.Ct);
            ctx.Print(new { alreadyRunning = already, status }, (already ? "The MCP server is already running\n" : "The MCP server is started\n") + Describe(status));
        });

    private static Command Stop(GlobalOptions g, DaemonController daemon) =>
        Kit.Plain(g, "stop", "Stops the MCP server", _ => { }, async (_, ctx) =>
        {
            var wasRunning = await daemon.Stop(ctx.Ct);
            ctx.Print(new { stopped = wasRunning }, wasRunning ? "The MCP server is stopped" : "The MCP server is not running");
        });

    private static Command Restart(GlobalOptions g, DaemonController daemon) =>
        Kit.Plain(g, "restart", "Restarts the MCP server (applies a changed port)", _ => { }, async (_, ctx) =>
        {
            var status = await daemon.Restart(ctx.Ct);
            ctx.Print(new { status }, "The MCP server is restarted\n" + Describe(status));
        });

    private static Command Upgrade(GlobalOptions g, DaemonController daemon)
    {
        var restart = new Option<bool>("--restart") { Description = "Hard path: stop the MCP server and start it again (calls in progress are cut off)" };
        var timeout = new Option<int>("--timeout")
        {
            Description = $"Seconds to wait for the new server process to open the workspaces and be ready (default {DaemonController.DefaultUpgradeTimeoutSeconds})",
            DefaultValueFactory = _ => DaemonController.DefaultUpgradeTimeoutSeconds
        };
        var program = new Option<string?>("--daemon") { Description = "Path of the tasker-mcpd program of the new build (default: the one next to this tasker)", Hidden = true };

        return Kit.Plain(g, "upgrade", "Switches the running MCP server to the installed build without downtime (--restart: restart instead)",
            c =>
            {
                c.Options.Add(restart);
                c.Options.Add(timeout);
                c.Options.Add(program);
            },
            async (parse, ctx) =>
            {
                (string, string[])? daemonProgram = parse.GetValue(program) is { Length: > 0 } path
                    ? (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? Environment.ProcessPath! : path, path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? [path] : [])
                    : null;
                var options = new UpgradeOptions(parse.GetValue(restart), Math.Max(1, parse.GetValue(timeout)), daemonProgram);

                UpgradeOutcome outcome;
                try
                {
                    outcome = await daemon.Upgrade(options, ctx.Ct);
                }
                catch (DaemonUpgradeException e)
                {
                    throw new CliException(e.Message + "\nThe running MCP server is not changed. Look at the log or restart it with 'tasker mcp upgrade --restart'.");
                }

                ctx.Print(new { upgraded = outcome.Kind, outcome.Message, status = outcome.Status },
                    outcome.Message + (outcome.Status == null ? "" : "\n" + Describe(outcome.Status)));
                if (outcome.Kind == UpgradeKind.NotRunning)
                    ctx.ExitCode = 3;
            });
    }

    private static Command Status(GlobalOptions g, DaemonController daemon) =>
        Kit.Plain(g, "status", "Shows whether the MCP server is running (exit code 3 if not)", _ => { }, async (_, ctx) =>
        {
            var status = await daemon.Status(ctx.Ct);
            var autostart = status.Autostart.Enabled
                ? $"autostart: enabled ({status.Autostart.Manager}){(status.Autostart.Loaded ? "" : ", service not loaded")}"
                : "autostart: off";
            ctx.Print(status, (status.Daemon == null ? "The MCP server is not running" : Describe(status.Daemon)) + "\n" + autostart);
            if (status.Daemon == null)
                ctx.ExitCode = 3;
        });

    private static Command Autostart(GlobalOptions g, DaemonController daemon) =>
        Kit.Group("autostart", "Runs the MCP server at login and keeps it running (launchd on macOS, systemd on Linux, Task Scheduler on Windows)",
            Kit.Plain(g, "enable", "Enables autostart and starts the MCP server now", _ => { }, async (_, ctx) =>
            {
                var status = await daemon.EnableAutostart(ctx.Ct);
                ctx.Print(new { autostart = true, status }, $"Autostart is enabled ({daemon.Service.Name})\n" + Describe(status));
            }),
            Kit.Plain(g, "disable", "Disables autostart; a running MCP server keeps running", _ => { }, async (_, ctx) =>
            {
                var status = await daemon.DisableAutostart(ctx.Ct);
                ctx.Print(new { autostart = false, status }, "Autostart is disabled" + (status == null ? "" : "\n" + Describe(status)));
            }),
            Kit.Plain(g, "status", "Shows whether autostart is enabled", _ => { }, async (_, ctx) =>
            {
                var info = (await daemon.Status(ctx.Ct)).Autostart;
                ctx.Print(info, info.Enabled ? $"enabled ({info.Manager}){(info.Loaded ? "" : ", service not loaded")}" : "off");
            }));

    private static string Describe(DaemonStatus status)
    {
        var lines = new List<string>
        {
            $"pid {status.Pid}, port {status.Port}, up {Uptime(DateTimeOffset.UtcNow - status.StartedAt)}",
            $"address {status.McpUrl} (the workspace is the 'workspace' argument of a tool)"
        };
        // Рабочие процессы: обычно один; на время замены на лету — два (новый и старый, заканчивающий начатое).
        foreach (var worker in status.Workers)
        {
            var role = worker.Role switch
            {
                WorkerRole.Starting => ", starting",
                WorkerRole.Draining => ", finishing its calls and leaving",
                _ => status.Workers.Length > 1 ? ", accepting calls" : ""
            };
            lines.Add($"worker pid {worker.Pid}, build {(worker.Build.Length == 0 ? "?" : worker.Build)}{(worker.Version.Length == 0 ? "" : " (" + worker.Version + ")")}, up {Uptime(DateTimeOffset.UtcNow - worker.StartedAt)}{role}");
        }
        if (status.SettingsPort != status.Port)
            lines.Add($"the port in the settings is {status.SettingsPort}: 'tasker mcp restart' applies it");

        if (status.Workspaces.Length == 0)
            lines.Add("no workspaces: add one with 'tasker mcp workspace add <path>'");
        foreach (var workspace in status.Workspaces)
        {
            lines.Add(workspace.State switch
            {
                WorkspaceState.Open => $"  open    {workspace.Path}  workspace \"{workspace.Key}\"",
                WorkspaceState.Opening => $"  opening {workspace.Path}",
                _ => $"  failed  {workspace.Path}  {workspace.Error}"
            });
        }
        return string.Join('\n', lines);
    }

    private static string Uptime(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min"
        : $"{Math.Max(0, (int)span.TotalSeconds)} s";

    private static Command Workspaces(GlobalOptions g, SettingsStore settings)
    {
        var path = new Argument<string?>("path")
        {
            Description = "Workspace folder or SQLite file. Default: the current folder",
            Arity = ArgumentArity.ZeroOrOne
        }.NoSuggestions(); // путь: оболочка предлагает файлы сама
        // Убрать можно только то, что в списке: пути подсказываются из настроек (у «add» — файловое дополнение оболочки).
        var removePath = new Argument<string?>("path")
        {
            Description = "Workspace folder or SQLite file. Default: the current folder",
            Arity = ArgumentArity.ZeroOrOne
        }.Suggests(g, Sources.McpWorkspaces);

        return Kit.Group("workspace", "Workspaces available through MCP",
            Kit.Plain(g, "add", "Allows a workspace (folder or existing SQLite file) in MCP", c => c.Arguments.Add(path), async (parse, ctx) =>
            {
                var location = Locate(parse.GetValue(path));
                var (entry, added) = await settings.AddWorkspace(location, ctx.Ct);
                ctx.Print(new { added, workspace = entry },
                    added ? $"Added {entry.Kind.ToString().ToLowerInvariant()} workspace {entry.Path}" : $"Already in the list: {entry.Path}");
            }),
            Kit.Plain(g, "remove", "Removes a workspace from MCP", c => c.Arguments.Add(removePath), async (parse, ctx) =>
            {
                // Папку могли уже удалить с диска — тогда ищем в списке по пути, любого вида.
                var full = Path.GetFullPath(UserPath.Expand(parse.GetValue(removePath)) ?? Directory.GetCurrentDirectory());
                var candidates = File.Exists(full) || Directory.Exists(full)
                    ? [Locate(full)]
                    : new[] { WorkspaceLocation.Files(full), WorkspaceLocation.Sqlite(full) };

                foreach (var location in candidates)
                {
                    if (await settings.RemoveWorkspace(location, ctx.Ct))
                    {
                        ctx.Print(new { removed = location.Path }, $"Removed {location.Path}");
                        return;
                    }
                }

                throw new CliException($"Not in the list: {candidates[0].Path}");
            }),
            Kit.Plain(g, "list", "Lists workspaces available through MCP", _ => { }, (_, ctx) =>
            {
                var entries = settings.Load().Mcp.Workspaces;
                ctx.PrintAll(entries, entries, x => [x.Kind.ToString().ToLowerInvariant(), x.Path]);
                return Task.CompletedTask;
            }));
    }

    private static Command Config(GlobalOptions g, SettingsStore settings) =>
        Kit.Plain(g, "config", "Shows the global MCP settings and where they are stored", _ => { }, (_, ctx) =>
        {
            var mcp = settings.Load().Mcp;
            ctx.Print(new { file = settings.FilePath, mcp },
                $"file:       {settings.FilePath}\nport:       {mcp.Port}\nworkspaces: {mcp.Workspaces.Count}");
            return Task.CompletedTask;
        });

    private static Command Port(GlobalOptions g, SettingsStore settings)
    {
        var port = new Argument<int?>("port") { Description = "New port; without it the current one is shown", Arity = ArgumentArity.ZeroOrOne }.NoSuggestions();
        return Kit.Plain(g, "port", "Shows or sets the MCP port on 127.0.0.1", c => c.Arguments.Add(port), async (parse, ctx) =>
        {
            if (parse.GetValue(port) is { } value)
            {
                await settings.SetPort(value, ctx.Ct);
                ctx.Print(new { port = value }, $"Port is {value}: a running MCP server applies it after a restart");
                return;
            }

            var current = settings.Load().Mcp.Port;
            ctx.Print(new { port = current }, current.ToString());
        });
    }

    /// <summary>Существующая папка или файл SQLite; без пути — текущая папка.</summary>
    private static WorkspaceLocation Locate(string? path)
    {
        var full = Path.GetFullPath(UserPath.Expand(path) ?? Directory.GetCurrentDirectory());
        if (File.Exists(full))
            return WorkspaceLocation.Sqlite(full);
        if (Directory.Exists(full))
            return WorkspaceLocation.Files(full);

        throw new CliException($"Not found: {full}");
    }
}
