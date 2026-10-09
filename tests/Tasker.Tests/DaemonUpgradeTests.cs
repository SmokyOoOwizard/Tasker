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
/// Бесшовная замена демона (TSK-103, <c>tasker mcp upgrade</c>): настоящие процессы на своём порту и своём TASKER_HOME.
/// Новый рабочий процесс поднимается рядом со старым и принимает вызовы, когда открыл области; старый заканчивает начатое.
/// Настоящий демон (порт 5719) и настоящая служба launchd тестам не нужны и не затрагиваются.
/// </summary>
public class DaemonUpgradeTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();
    private readonly List<IDisposable> _env = [];

    public DaemonUpgradeTests()
    {
        // Старый процесс ждёт тишины недолго: тесты не должны ждать по две секунды на каждую замену.
        _env.Add(AppEnvironment.Override("TASKER_MCP_DRAIN_QUIET_MS", "300"));
        // На Windows супервизор по умолчанию выключен (страница вики «Windows»): тесты включают его, как это сделает пользователь. На Unix он и так включён.
        _env.Add(AppEnvironment.Override(DaemonPlatform.SupervisorVariable, "1"));
    }

    public void Dispose()
    {
        _daemon.Dispose();
        foreach (var x in _env)
            x.Dispose();
    }

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

    private async Task<DaemonStatus> StatusOf() => (await _daemon.Status())!;

    // ---- клиент: поток вызовов по общему HttpClient (соединения живут долго, как у агента) ----

    private sealed class Load(DaemonFixture daemon, Area area)
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _workers = [];
        private int _id;

        public ConcurrentBag<string> Acknowledged { get; } = [];
        public ConcurrentBag<string> Failures { get; } = [];
        public ConcurrentDictionary<int, int> ServedBy { get; } = [];

        public void Start(int clients)
        {
            for (var i = 0; i < clients; i++)
            {
                var index = i;
                _workers.Add(Task.Run(() => Write(index)));
            }
            _workers.Add(Task.Run(Probe));
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

        // Кто отвечает: pid рабочего процесса из /health (по тому же общему соединению, что и вызовы).
        private async Task Probe()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var pid = JsonNode.Parse(await _http.GetStringAsync($"http://127.0.0.1:{daemon.Port}/health"))!["pid"]!.GetValue<int>();
                    ServedBy.AddOrUpdate(pid, 1, (_, count) => count + 1);
                }
                catch (Exception e)
                {
                    Failures.Add($"health: {e.GetType().Name} {e.Message}");
                }
                await Task.Delay(20);
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
    public async Task Upgrade_under_load_loses_no_call_and_no_write_and_duplicates_none()
    {
        var area = await Prepare();
        var before = await StatusOf();
        var oldWorker = before.Workers.Single();
        Assert.Equal(WorkerRole.Active, oldWorker.Role);

        var load = new Load(_daemon, area);
        load.Start(clients: 6);
        await Task.Delay(1500);

        // Две замены подряд, обе под нагрузкой.
        var first = await _daemon.Mcp("upgrade");
        Assert.True(first.Code == 0, first.Err + first.Out);
        Assert.Contains("replaced without downtime", first.Out);
        await Task.Delay(500);
        var second = await _daemon.Mcp("upgrade");
        Assert.True(second.Code == 0, second.Err + second.Out);
        await Task.Delay(1500);
        await load.Stop();

        // Ни одного отказа: ни обрыва соединения, ни 5xx, ни «unavailable» — без единого повтора на стороне клиента.
        Assert.True(load.Failures.IsEmpty, string.Join("\n", load.Failures.Take(10)));

        // Каждая подтверждённая запись есть ровно один раз; лишних (повторов) нет.
        var titles = await TaskTitles(area);
        Assert.Equal(load.Acknowledged.Count, titles.Length);
        Assert.Equal(titles.Length, titles.Distinct().Count());
        Assert.Empty(load.Acknowledged.Except(titles));
        Assert.True(titles.Length > 50, $"too little load to prove anything: {titles.Length}");

        // Сначала отвечал старый процесс, потом два новых; демон и порт те же, рабочий процесс один и новый.
        Assert.True(load.ServedBy.Count >= 3, "served by: " + string.Join(", ", load.ServedBy));
        Assert.True(load.ServedBy.ContainsKey(oldWorker.Pid));
        var after = await StatusOf();
        Assert.Equal(before.Pid, after.Pid);
        Assert.Equal(before.Port, after.Port);
        var worker = after.Workers.Single();
        Assert.NotEqual(oldWorker.Pid, worker.Pid);
        Assert.Equal(WorkerRole.Active, worker.Role);
        await Eventually(() => Task.FromResult(!IsAlive(oldWorker.Pid)));
        Assert.True(DaemonFiles.IsRunning());
    }

    [Fact]
    public async Task A_call_in_progress_is_finished_by_the_old_process_and_both_are_shown_in_the_status()
    {
        var area = await Prepare();
        var oldWorker = (await StatusOf()).Workers.Single();

        // Вызов «в работе»: заголовки и половина тела отправлены, остальное придёт позже — обработчик ждёт его в старом процессе.
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "list_projects", ["arguments"] = new JsonObject { ["workspace"] = area.Key } }
        }.ToJsonString();
        var bytes = Encoding.UTF8.GetBytes(body);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _daemon.Port);
        var stream = client.GetStream();
        var head = $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{_daemon.Port}\r\nContent-Type: application/json\r\nAccept: application/json, text/event-stream\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bytes.AsMemory(0, 10));
        await stream.FlushAsync();
        await Task.Delay(300);

        var upgrade = _daemon.Mcp("upgrade");
        var both = await WaitStatus(x => x.Workers.Length == 2 && x.Workers.Any(w => w.Role == WorkerRole.Draining));
        Assert.Equal(WorkerRole.Draining, both.Workers.Single(x => x.Pid == oldWorker.Pid).Role);
        Assert.Equal(WorkerRole.Active, both.Workers.Single(x => x.Pid != oldWorker.Pid).Role);
        Assert.Contains("finishing its calls", (await _daemon.Mcp("status")).Out);

        // Тишины (300 мс) давно хватило бы — но вызов не закончен, и старый процесс его ждёт.
        await Task.Delay(1500);
        Assert.True(IsAlive(oldWorker.Pid));
        Assert.False(upgrade.IsCompleted);

        // Дослали остаток: вызов доделан старым процессом и получил свой ответ, потом старый процесс ушёл.
        await stream.WriteAsync(bytes.AsMemory(10));
        await stream.FlushAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.StartsWith("HTTP/1.1 200", response);
        Assert.Contains("Demo", response);

        var result = await upgrade;
        Assert.Equal(0, result.Code);
        await Eventually(() => Task.FromResult(!IsAlive(oldWorker.Pid)));
        Assert.Single((await StatusOf()).Workers);
    }

    private async Task<DaemonStatus> WaitStatus(Func<DaemonStatus, bool> condition)
    {
        DaemonStatus? last = null;
        await Eventually(async () => (last = await _daemon.Status()) is { } s && condition(s));
        return last!;
    }

    [UnixFact]
    public async Task A_new_process_that_dies_at_the_start_leaves_the_old_one_serving()
    {
        var area = await Prepare();
        var before = await StatusOf();
        var broken = Script("broken-daemon", "exit 3");

        var result = await _daemon.Mcp("upgrade", "--daemon", broken);

        Assert.NotEqual(0, result.Code);
        Assert.Contains("exited with code 3 before it was ready", result.Err);
        Assert.Contains("untouched", result.Err);
        var after = await StatusOf();
        Assert.Equal(before.Workers.Single().Pid, after.Workers.Single().Pid);
        Assert.Equal(WorkerRole.Active, after.Workers.Single().Role);
        Assert.Equal(["Demo"], DaemonFixture.ProjectNames(await _daemon.CallTool(area.Key, "list_projects")));
    }

    [UnixFact]
    public async Task A_new_process_that_never_gets_ready_is_removed_after_the_timeout()
    {
        var area = await Prepare();
        var before = await StatusOf();
        var pidFile = Path.Combine(_daemon.Root, "stuck.pid");
        var stuck = Script("stuck-daemon", $"echo $$ > '{pidFile}'\nexec sleep 60");

        var result = await _daemon.Mcp("upgrade", "--daemon", stuck, "--timeout", "2");

        Assert.NotEqual(0, result.Code);
        Assert.Contains("not ready in 2 s", result.Err);
        var stuckPid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
        await Eventually(() => Task.FromResult(!IsAlive(stuckPid)));
        Assert.Equal(before.Workers.Single().Pid, (await StatusOf()).Workers.Single().Pid);
        Assert.Equal(["Demo"], DaemonFixture.ProjectNames(await _daemon.CallTool(area.Key, "list_projects")));
    }

    [Fact]
    public async Task A_missing_program_of_the_new_process_is_refused_and_the_old_one_keeps_working()
    {
        var area = await Prepare();

        var result = await _daemon.Mcp("upgrade", "--daemon", Path.Combine(_daemon.Root, "no-such-daemon"));

        Assert.NotEqual(0, result.Code);
        Assert.Contains("not found", result.Err);
        Assert.Equal(["Demo"], DaemonFixture.ProjectNames(await _daemon.CallTool(area.Key, "list_projects")));
    }

    [UnixFact]
    public async Task A_new_process_that_cannot_open_a_workspace_open_in_the_old_one_is_rolled_back()
    {
        var area = await Prepare();
        var before = await StatusOf();
        var tasker = Path.Combine(area.Folder, ".tasker");
        File.SetUnixFileMode(tasker, UnixFileMode.None);
        try
        {
            var result = await _daemon.Mcp("upgrade");

            Assert.NotEqual(0, result.Code);
            Assert.Contains("cannot open workspaces that are open now", result.Err);
            Assert.Equal(before.Workers.Single().Pid, (await StatusOf()).Workers.Single().Pid);
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
    public async Task A_server_started_as_one_process_is_restarted_by_upgrade_and_is_seamless_afterwards()
    {
        await _daemon.Workspace("ws", "Demo");
        var (file, arguments) = TaskerProcess.DaemonCommand();
        var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        AppEnvironment.Apply(info);
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--single");
        info.ArgumentList.Add("--detached");
        using var single = Process.Start(info)!;
        var started = await WaitStatus(x => x.Workspaces.All(w => w.State == WorkspaceState.Open) && x.Workspaces.Length == 1);
        Assert.Empty(started.Workers);
        Assert.Equal(single.Id, started.Pid);

        var result = await _daemon.Mcp("upgrade");

        Assert.Equal(0, result.Code);
        Assert.Contains("earlier build", result.Out);
        await single.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var now = await StatusOf();
        Assert.NotEqual(single.Id, now.Pid);
        var worker = Assert.Single(now.Workers);

        var again = await _daemon.Mcp("upgrade");
        Assert.Equal(0, again.Code);
        Assert.Contains("replaced without downtime", again.Out);
        Assert.NotEqual(worker.Pid, (await StatusOf()).Workers.Single().Pid);
    }

    [Fact]
    public async Task Upgrade_of_a_stopped_server_says_so_and_exits_with_3()
    {
        var result = await _daemon.Mcp("upgrade");

        Assert.Equal(3, result.Code);
        Assert.Contains("not running", result.Out);
    }

    [Fact]
    public async Task Upgrade_restart_is_the_hard_path_with_a_new_server_process()
    {
        await Prepare();
        var before = await StatusOf();

        var result = await _daemon.Mcp("upgrade", "--restart");

        Assert.Equal(0, result.Code);
        var after = await StatusOf();
        Assert.NotEqual(before.Pid, after.Pid);
        Assert.NotEqual(before.Workers.Single().Pid, after.Workers.Single().Pid);
        await Eventually(() => Task.FromResult(!IsAlive(before.Pid)));
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
    public async Task A_worker_that_crashes_is_started_again_by_the_supervisor()
    {
        var area = await Prepare();
        var before = await StatusOf();

        Process.GetProcessById(before.Workers.Single().Pid).Kill();

        var after = await WaitStatus(x => x.Workers.Length == 1 && x.Workers[0].Pid != before.Workers[0].Pid && x.Workspaces.All(w => w.State == WorkspaceState.Open));
        Assert.Equal(before.Pid, after.Pid);
        Assert.Equal(["Demo"], DaemonFixture.ProjectNames(await _daemon.CallTool(area.Key, "list_projects")));
    }

    [Fact]
    public async Task When_the_supervisor_is_killed_its_worker_leaves_and_the_port_is_free()
    {
        await Prepare();
        var before = await StatusOf();

        Process.GetProcessById(before.Pid).Kill();

        await Eventually(() => Task.FromResult(!IsAlive(before.Workers.Single().Pid)));
        await Eventually(() =>
        {
            try
            {
                new TcpListener(IPAddress.Loopback, _daemon.Port).Start();
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
    public async Task Stop_ends_the_supervisor_and_the_worker()
    {
        await Prepare();
        var before = await StatusOf();

        var stopped = await _daemon.Mcp("stop");

        Assert.Equal(0, stopped.Code);
        await Eventually(() => Task.FromResult(!IsAlive(before.Pid) && !IsAlive(before.Workers.Single().Pid)));
        Assert.False(File.Exists(DaemonFiles.InfoFile));
    }

    [Fact]
    public async Task Status_json_lists_the_worker_with_its_build()
    {
        await Prepare();

        var status = (await _daemon.Mcp("status", "--json")).Json["daemon"]!;

        var worker = status["workers"]!.AsArray().Single()!;
        Assert.Equal("active", worker["role"]!.GetValue<string>());
        Assert.NotEmpty(worker["build"]!.GetValue<string>());
        Assert.NotEqual(status["pid"]!.GetValue<int>(), worker["pid"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_request_that_lost_its_connection_is_repeated_once()
    {
        var calls = 0;
        var result = await DaemonClient.Retrying(() =>
        {
            calls++;
            return calls == 1 ? throw new HttpRequestException("connection reset") : Task.FromResult("answer");
        });
        Assert.Equal("answer", result);
        Assert.Equal(2, calls);

        calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => DaemonClient.Retrying<string>(() =>
        {
            calls++;
            throw new HttpRequestException("still down");
        }));
        Assert.Equal(2, calls);
    }
}
