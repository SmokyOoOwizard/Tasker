using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Tasker.Daemon;
using Tasker.Global;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Обновление демона (<c>tasker mcp upgrade</c>): настоящие процессы на своём порту и своём TASKER_HOME.
/// Новая сборка проверяется пробным запуском, затем демон перезапускается на ней.
/// Настоящий демон (порт 5719) и настоящая служба launchd тестам не нужны и не затрагиваются.
/// </summary>
public class DaemonUpgradeTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    private sealed record Area(string Folder, string Key, Guid ProjectId, Guid TypeId);

    /// <summary>Область с проектом, типом задач и запущенный демон.</summary>
    private async Task<Area> Prepare()
    {
        var folder = await _daemon.Workspace("ws", "Demo");
        async Task<JsonNode> Json(params string[] args)
        {
            var result = await TaskerProcess.Run([.. args, "-w", folder, "--json"]);
            Assert.True(result.Code == 0, result.Err);
            return result.Json;
        }

        var project = (await TaskerProcess.Run("project", "list", "-w", folder, "--json")).Json["data"]![0]!["id"]!.GetValue<string>();
        await TaskerProcess.Run("status", "create", "Todo", "-p", "Demo", "-w", folder);
        await TaskerProcess.Run("status-set", "create", "Flow", "--status", "Todo", "-p", "Demo", "-w", folder);
        var type = await Json("task-type", "create", "Bug", "--status-set", "Flow", "-p", "Demo");
        var status = await _daemon.Start();
        return new Area(folder, status.Workspaces[0].Key!, Guid.Parse(project), Guid.Parse(type["id"]!.GetValue<string>()));
    }

    private static async Task Eventually(Func<Task<bool>> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not become true in time");
            await Task.Delay(50);
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            return !Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<DaemonStatus> StatusOf() =>
        await _daemon.Status() ?? throw new InvalidOperationException("tasker mcp status has no daemon: " + await Diagnose());

    private static async Task<string> Diagnose()
    {
        var running = DaemonFiles.IsRunning();
        if (DaemonFiles.ReadInfo() is not { } info)
            return $"lock held {running}, no {DaemonFiles.InfoFile}";
        var where = $"lock held {running}, pid {info.Pid}, port {info.Port}";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{info.Port}/daemon/status");
            request.Headers.Add(DaemonClient.ControlHeader, info.Token);
            var watch = Stopwatch.StartNew();
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            string parsed;
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize<DaemonStatus>(body, Tasker.Core.TaskerJson.Options) is null ? "null" : "ok";
            }
            catch (System.Text.Json.JsonException e)
            {
                parsed = e.Message;
            }
            return $"{where}: HTTP {(int)response.StatusCode} in {watch.ElapsedMilliseconds} ms, parse {parsed}: {body}";
        }
        catch (Exception e)
        {
            return $"{where}: {e.GetType().Name}: {e.Message}";
        }
    }

    // ---- клиент: поток вызовов по общему HttpClient (соединения живут долго, как у агента) ----

    private sealed class Load(DaemonFixture daemon, Area area)
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _workers = [];
        private int _id;

        public ConcurrentBag<string> Acknowledged { get; } = [];
        public ConcurrentBag<string> Failures { get; } = [];

        public void Start(int clients)
        {
            for (var i = 0; i < clients; i++)
            {
                var index = i;
                _workers.Add(Task.Run(() => Write(index)));
            }
        }

        public async Task Stop()
        {
            _stop.Cancel();
            await Task.WhenAll(_workers);
        }

        private async Task Write(int client)
        {
            for (var n = 0; !_stop.IsCancellationRequested; n++)
            {
                var title = $"load {client}-{n}";
                try
                {
                    var args = new JsonObject { ["workspace"] = area.Key, ["projectId"] = area.ProjectId.ToString(), ["typeId"] = area.TypeId.ToString(), ["title"] = title };
                    var body = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = Interlocked.Increment(ref _id),
                        ["method"] = "tools/call",
                        ["params"] = new JsonObject { ["name"] = "create_task", ["arguments"] = args }
                    };
                    using var request = new HttpRequestMessage(HttpMethod.Post, daemon.McpUrl)
                    {
                        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
                    };
                    request.Headers.Accept.ParseAdd("application/json");
                    request.Headers.Accept.ParseAdd("text/event-stream");
                    using var response = await _http.SendAsync(request);
                    var text = await response.Content.ReadAsStringAsync();
                    var data = text.Split('\n').FirstOrDefault(x => x.StartsWith("data:"));
                    var result = data == null ? null : JsonNode.Parse(data["data:".Length..].Trim())?["result"];
                    if (response.IsSuccessStatusCode && result != null && !(result["isError"]?.GetValue<bool>() ?? false))
                        Acknowledged.Add(title);
                    else
                        Failures.Add($"{title}: HTTP {(int)response.StatusCode} {text}");
                }
                catch (Exception e)
                {
                    Failures.Add($"{title}: {e.GetType().Name} {e.Message}");
                }
            }
        }

    }

    private async Task<string[]> TaskTitles(Area area)
    {
        var list = await TaskerProcess.Run("task", "list", "--all", "-p", "Demo", "-w", area.Folder, "--json");
        Assert.Equal(0, list.Code);
        return list.Json["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray();
    }

    [Fact]
    public async Task Upgrade_restarts_the_whole_daemon_and_the_status_shows_its_build()
    {
        var area = await Prepare();
        var before = await StatusOf();
        var (file, arguments) = TaskerProcess.DaemonCommand();

        var result = await _daemon.Mcp("upgrade");

        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        Assert.Contains("restarted", result.Out);
        var after = await StatusOf();
        Assert.NotEqual(before.Pid, after.Pid);
        Assert.Equal(before.Port, after.Port);
        await Eventually(() => Task.FromResult(!IsAlive(before.Pid)));
        Assert.NotEmpty(after.Build);
        Assert.Contains($"build {after.Build}", (await _daemon.Mcp("status")).Out);
        Assert.Equal(["Demo"], DaemonFixture.ProjectNames(await _daemon.CallTool(area.Key, "list_projects")));

        var info = await Launcher.ReadBuild((file, arguments), TimeSpan.FromSeconds(30));
        Assert.Equal(info.Build, after.Build);
        Assert.Equal(info.Version, after.Version);
    }

    [Fact]
    public async Task Calls_refused_during_the_upgrade_are_the_only_loss_and_acknowledged_writes_survive()
    {
        var area = await Prepare();
        var load = new Load(_daemon, area);
        load.Start(clients: 4);
        await Task.Delay(1000);

        var result = await _daemon.Mcp("upgrade");
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        var acknowledgedAtRestart = load.Acknowledged.Count;
        await Eventually(() => Task.FromResult(load.Acknowledged.Count > acknowledgedAtRestart));
        await load.Stop();

        var titles = await TaskTitles(area);
        Assert.Empty(load.Acknowledged.Except(titles));
        Assert.Equal(titles.Length, titles.Distinct().Count());
    }

    [UnixFact]
    public async Task A_new_program_that_fails_the_check_leaves_the_daemon_running()
    {
        var area = await Prepare();
        var before = await StatusOf();

        var result = await _daemon.Mcp("upgrade", "--daemon", Script("broken-daemon", "echo boom >&2\nexit 3"));

        Assert.NotEqual(0, result.Code);
        Assert.Contains("exited with code 3", result.Err);
        Assert.Contains("boom", result.Err);
        Assert.Contains("untouched", result.Err);
        Assert.Equal(before.Pid, (await StatusOf()).Pid);
        Assert.Equal(["Demo"], DaemonFixture.ProjectNames(await _daemon.CallTool(area.Key, "list_projects")));
    }

    [UnixFact]
    public async Task A_new_program_that_never_answers_is_removed_after_the_timeout()
    {
        var area = await Prepare();
        var before = await StatusOf();
        var pidFile = Path.Combine(_daemon.Root, "stuck.pid");

        var result = await _daemon.Mcp("upgrade", "--daemon", Script("stuck-daemon", $"echo $$ > '{pidFile}'\nexec sleep 60"), "--timeout", "2");

        Assert.NotEqual(0, result.Code);
        Assert.Contains("did not answer in 2 s", result.Err);
        var stuckPid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
        await Eventually(() => Task.FromResult(!IsAlive(stuckPid)));
        Assert.Equal(before.Pid, (await StatusOf()).Pid);
        Assert.Equal(["Demo"], DaemonFixture.ProjectNames(await _daemon.CallTool(area.Key, "list_projects")));
    }

    [UnixFact]
    public async Task A_new_program_that_reports_no_build_leaves_the_daemon_running()
    {
        await Prepare();
        var before = await StatusOf();

        var result = await _daemon.Mcp("upgrade", "--daemon", Script("silent-daemon", "echo not json"));

        Assert.NotEqual(0, result.Code);
        Assert.Contains("did not report its build", result.Err);
        Assert.Equal(before.Pid, (await StatusOf()).Pid);
    }

    [Fact]
    public async Task A_missing_new_program_is_refused_and_the_daemon_keeps_working()
    {
        var area = await Prepare();

        var result = await _daemon.Mcp("upgrade", "--daemon", Path.Combine(_daemon.Root, "no-such-daemon"));

        Assert.NotEqual(0, result.Code);
        Assert.Contains("not found", result.Err);
        Assert.Equal(["Demo"], DaemonFixture.ProjectNames(await _daemon.CallTool(area.Key, "list_projects")));
    }

    [UnixFact]
    public async Task A_workspace_that_stops_opening_after_the_restart_is_reported_with_a_failing_exit_code()
    {
        var area = await Prepare();
        var tasker = Path.Combine(area.Folder, ".tasker");
        File.SetUnixFileMode(tasker, UnixFileMode.None);
        try
        {
            var result = await _daemon.Mcp("upgrade");

            Assert.Equal(1, result.Code);
            Assert.Contains("was open and is not now", result.Out);
            Assert.True(DaemonFiles.IsRunning());
        }
        finally
        {
            File.SetUnixFileMode(tasker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private string Script(string name, string body)
    {
        var path = Path.Combine(_daemon.Root, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task Upgrade_of_a_stopped_server_says_so_and_exits_with_3()
    {
        var result = await _daemon.Mcp("upgrade");

        Assert.Equal(3, result.Code);
        Assert.Contains("not running", result.Out);
    }

    [Fact]
    public async Task Ready_answers_only_when_the_workspaces_are_open_and_health_always()
    {
        await Prepare();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_daemon.Port}") };

        var ready = await http.GetAsync("/ready");
        var health = await http.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(true, await DaemonClient.IsReady());
    }

    [Fact]
    public async Task When_the_daemon_is_killed_the_port_and_the_lock_are_free()
    {
        await Prepare();
        var before = await StatusOf();

        Process.GetProcessById(before.Pid).Kill();

        await Eventually(() =>
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, _daemon.Port);
                listener.Start();
                listener.Stop();
                return Task.FromResult(true);
            }
            catch (SocketException)
            {
                return Task.FromResult(false);
            }
        });
        Assert.False(DaemonFiles.IsRunning());
    }

    [Fact]
    public async Task Stop_ends_the_daemon()
    {
        await Prepare();
        var before = await StatusOf();

        var stopped = await _daemon.Mcp("stop");

        Assert.True(stopped.Code == 0, $"exit {stopped.Code}: {stopped.Err}");
        await Eventually(() => Task.FromResult(!IsAlive(before.Pid)));
        Assert.False(File.Exists(DaemonFiles.InfoFile));
    }

    [UnixFact]
    public async Task Sigterm_from_a_service_stops_the_daemon_in_order()
    {
        await Prepare();
        var before = await StatusOf();

        using (var kill = Process.Start("kill", ["-TERM", before.Pid.ToString()]))
            await kill.WaitForExitAsync();

        await Eventually(() => Task.FromResult(!IsAlive(before.Pid)));
        Assert.False(File.Exists(DaemonFiles.InfoFile));
        Assert.False(DaemonFiles.IsRunning());
    }

    [Fact]
    public async Task The_status_json_has_the_build_and_the_daemon_is_one_process()
    {
        await Prepare();

        var status = (await _daemon.Mcp("status", "--json")).Json["daemon"]!;

        Assert.NotEmpty(status["build"]!.GetValue<string>());
        Assert.NotEmpty(status["version"]!.GetValue<string>());
        Assert.Null(status["workers"]);
    }

    [Fact]
    public void The_status_of_a_daemon_of_an_earlier_build_is_read_without_a_build()
    {
        const string json = """
            {"pid":10,"port":5719,"settingsPort":5719,"startedAt":"2026-11-03T22:26:50+00:00","workspaces":[],
             "workers":[{"pid":11,"version":"0.1.0","build":"ab12","role":"active","startedAt":"2026-11-03T22:26:50+00:00"}]}
            """;

        var status = System.Text.Json.JsonSerializer.Deserialize<DaemonStatus>(json, Tasker.Core.TaskerJson.Options)!;

        Assert.Equal(10, status.Pid);
        Assert.Equal("", status.Build);
    }
}
