using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tasker.Daemon;
using Xunit;

namespace Tasker.Tests;

/// <summary>Демон MCP: настоящие процессы <c>tasker mcp start | stop | status | run</c>.</summary>
public class DaemonTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    private static string KeyOf(WorkspaceStatus workspace) => workspace.Key ?? throw new InvalidOperationException("The workspace is not open");

    [Fact]
    public async Task Start_serves_every_workspace_from_the_settings()
    {
        await _daemon.Workspace("a", "Alpha");
        await _daemon.Workspace("b", "Beta");

        var status = await _daemon.Start();

        Assert.Equal(_daemon.Port, status.Port);
        Assert.Equal(2, status.Workspaces.Length);
        Assert.All(status.Workspaces, x => Assert.Equal(WorkspaceState.Open, x.State));
        Assert.Equal(["Alpha"], DaemonFixture.ProjectNames(await _daemon.CallTool(KeyOf(status.Workspaces[0]), "list_projects")));
        Assert.Equal(["Beta"], DaemonFixture.ProjectNames(await _daemon.CallTool(KeyOf(status.Workspaces[1]), "list_projects")));
    }

    [Fact]
    public async Task Daemon_keeps_running_after_start_returns_and_stop_ends_it()
    {
        await _daemon.Workspace("a", "Alpha");

        var started = await _daemon.Start();
        Assert.True(DaemonFiles.IsRunning());
        Assert.True(Process.GetProcessById(started.Pid) is { HasExited: false });

        var stopped = await _daemon.Mcp("stop");
        Assert.Equal(0, stopped.Code);
        Assert.Equal("The MCP server is stopped", stopped.Out.Trim());

        Assert.False(DaemonFiles.IsRunning());
        Assert.False(File.Exists(DaemonFiles.InfoFile));
        Assert.Null(await _daemon.Status());
        // Процесс действительно завершился, а не завис после остановки сервера.
        await Eventually(() => Task.FromResult(!IsAlive(started.Pid)));
        await Assert.ThrowsAnyAsync<Exception>(() => _daemon.CallTool("a", "list_projects"));
    }

    [Fact]
    public async Task Second_start_reports_the_running_server()
    {
        var first = await _daemon.Start();

        var second = await _daemon.Mcp("start");

        Assert.Equal(0, second.Code);
        Assert.StartsWith("The MCP server is already running", second.Out);
        Assert.Equal(first.Pid, (await _daemon.Status())!.Pid);
    }

    [Fact]
    public async Task Stop_and_status_of_a_stopped_server_are_calm()
    {
        var stop = await _daemon.Mcp("stop");
        Assert.Equal(0, stop.Code);
        Assert.Equal("The MCP server is not running", stop.Out.Trim());

        var status = await _daemon.Mcp("status");
        Assert.Equal(3, status.Code);
        Assert.Contains("The MCP server is not running", status.Out);
        Assert.Contains("autostart: off", status.Out);
        Assert.Empty(status.Err);
    }

    [Fact]
    public async Task Workspaces_added_and_removed_in_the_settings_apply_without_a_restart()
    {
        await _daemon.Workspace("a", "Alpha");
        var pid = (await _daemon.Start()).Pid;

        await _daemon.Workspace("b", "Beta");
        var withBoth = await WaitFor(x => x.Workspaces.Length == 2 && x.Workspaces.All(w => w.State == WorkspaceState.Open));
        var beta = withBoth.Workspaces.Single(x => x.Path.EndsWith("/b"));
        Assert.Equal(["Beta"], DaemonFixture.ProjectNames(await _daemon.CallTool(KeyOf(beta), "list_projects")));

        await _daemon.Mcp("workspace", "remove", beta.Path);
        var withOne = await WaitFor(x => x.Workspaces.Length == 1);
        Assert.Equal(pid, withOne.Pid);
        // Убранная из настроек область для агента — как несуществующая.
        Assert.StartsWith("[not_found]", await _daemon.CallTool(KeyOf(beta), "list_projects"));
        Assert.Equal(["Alpha"], DaemonFixture.ProjectNames(await _daemon.CallTool(KeyOf(withOne.Workspaces[0]), "list_projects")));
    }

    [Fact]
    public async Task Changes_made_by_the_cli_are_visible_through_mcp_right_away()
    {
        var folder = await _daemon.Workspace("a", "Alpha");
        var status = await _daemon.Start();
        var key = KeyOf(status.Workspaces[0]);

        // Демон держит папку открытой, а CLI пишет в неё другим процессом — блокировки короткие.
        var created = await TaskerProcess.Run("project", "create", "Gamma", "-w", folder);
        Assert.Equal(0, created.Code);

        await Eventually(async () => (await _daemon.CallTool(key, "list_projects")).Contains("Gamma"));
        Assert.Equal(["Alpha", "Gamma"], DaemonFixture.ProjectNames(await _daemon.CallTool(key, "list_projects")));

        // И наоборот: то, что создал агент через MCP, видит CLI.
        await _daemon.CallTool(key, "create_project", new { name = "FromAgent" });
        Assert.Contains("FromAgent", (await TaskerProcess.Run("project", "list", "-w", folder)).Out);
    }

    [Fact]
    public async Task A_workspace_that_cannot_be_opened_is_reported_and_does_not_stop_the_others()
    {
        var gone = await _daemon.Workspace("gone", "Gone");
        await _daemon.Workspace("kept", "Kept");
        Directory.Delete(gone, recursive: true);

        var status = await _daemon.Start();

        var failed = status.Workspaces.Single(x => x.State == WorkspaceState.Failed);
        Assert.Equal(Tasker.Storage.Files.Workspaces.WorkspaceLocation.Files(gone).Path, failed.Path);
        Assert.Contains("does not exist", failed.Error);
        var kept = status.Workspaces.Single(x => x.State == WorkspaceState.Open);
        Assert.Equal(["Kept"], DaemonFixture.ProjectNames(await _daemon.CallTool(KeyOf(kept), "list_projects")));
        Assert.False(Directory.Exists(gone), "a failed workspace must not be created on disk");

        var text = (await _daemon.Mcp("status")).Out;
        Assert.Contains("failed", text);
    }

    [Fact]
    public async Task A_workspace_that_appears_later_is_opened_by_the_next_change()
    {
        var later = Path.Combine(_daemon.Root, "later");
        Directory.CreateDirectory(later);
        await _daemon.Mcp("workspace", "add", later);
        Directory.Delete(later);
        await _daemon.Start();
        Assert.Equal(WorkspaceState.Failed, (await _daemon.Status())!.Workspaces.Single().State);

        Directory.CreateDirectory(later);
        await _daemon.Workspace("other", "Other");

        var status = await WaitFor(x => x.Workspaces.All(w => w.State == WorkspaceState.Open));
        Assert.Equal(2, status.Workspaces.Length);
    }

    [Fact]
    public async Task Busy_port_is_reported_and_nothing_is_left_running()
    {
        using var listener = new TcpListener(IPAddress.Loopback, _daemon.Port);
        listener.Start();

        var result = await _daemon.Mcp("start");

        Assert.Equal(1, result.Code);
        Assert.Contains($"Port {_daemon.Port} is busy", result.Err);
        Assert.False(DaemonFiles.IsRunning());
    }

    [Fact]
    public async Task A_port_released_by_the_desktop_right_after_the_start_is_taken()
    {
        // Десктоп держит порт, но видит блокировку демона и отпускает его — демон ждёт, а не отказывается.
        var listener = new TcpListener(IPAddress.Loopback, _daemon.Port);
        listener.Start();
        _ = Task.Delay(1500).ContinueWith(_ => listener.Stop());

        var status = await _daemon.Start();

        Assert.Equal(_daemon.Port, status.Port);
    }

    [Fact]
    public async Task Only_one_server_runs_at_a_time()
    {
        await _daemon.Start();

        var second = await TaskerProcess.Run("mcp", "run");

        Assert.Equal(1, second.Code);
        Assert.Contains("already running", second.Err);
    }

    [Fact]
    public async Task Control_endpoints_need_the_secret_and_a_local_host()
    {
        var status = await _daemon.Start();
        var token = DaemonFiles.ReadInfo()!.Token;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{status.Port}") };

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/daemon/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/daemon/stop", null)).StatusCode);

        var wrong = new HttpRequestMessage(HttpMethod.Get, "/daemon/status");
        wrong.Headers.Add(DaemonClient.ControlHeader, "not-the-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);

        // Страница на чужом домене, указывающем на 127.0.0.1, — DNS rebinding: Host не локальный.
        var rebinding = new HttpRequestMessage(HttpMethod.Get, "/daemon/status");
        rebinding.Headers.Add(DaemonClient.ControlHeader, token);
        rebinding.Headers.Host = "evil.example.com";
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(rebinding)).StatusCode);

        // Сервер всё это пережил.
        Assert.NotNull(await _daemon.Status());
    }

    [Fact]
    public async Task The_control_secret_is_readable_only_by_the_owner()
    {
        if (OperatingSystem.IsWindows())
            return;

        await _daemon.Start();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(DaemonFiles.InfoFile));
    }

    [Fact]
    public async Task A_killed_daemon_leaves_nothing_that_blocks_the_next_start()
    {
        await _daemon.Workspace("a", "Alpha");
        var first = await _daemon.Start();

        Process.GetProcessById(first.Pid).Kill();
        await Eventually(() => Task.FromResult(!DaemonFiles.IsRunning()));

        Assert.Null(await _daemon.Status());
        Assert.Equal(3, (await _daemon.Mcp("status")).Code);

        var second = await _daemon.Start();
        Assert.NotEqual(first.Pid, second.Pid);
        Assert.Equal(WorkspaceState.Open, second.Workspaces.Single().State);
    }

    [Fact]
    public async Task Restart_applies_a_changed_port()
    {
        await _daemon.Workspace("a", "Alpha");
        var first = await _daemon.Start();

        var newPort = DaemonFixture.FreePort();
        await _daemon.Mcp("port", newPort.ToString());
        var beforeRestart = await _daemon.Mcp("status");
        Assert.Contains($"the port in the settings is {newPort}", beforeRestart.Out);

        var restarted = await _daemon.Mcp("restart");
        Assert.Equal(0, restarted.Code);

        var status = (await _daemon.Status())!;
        Assert.Equal(newPort, status.Port);
        Assert.NotEqual(first.Pid, status.Pid);
        Assert.Equal(WorkspaceState.Open, status.Workspaces.Single().State);
    }

    [Fact]
    public async Task Foreground_run_stops_on_interrupt_and_cleans_up()
    {
        await _daemon.Workspace("a", "Alpha");
        using var process = TaskerProcess.Start("mcp", "run");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        await Eventually(async () => await DaemonClient.GetStatus() is { } s && s.Workspaces.All(w => w.State == WorkspaceState.Open));

        using var kill = Process.Start("kill", ["-INT", process.Id.ToString()])!;
        await kill.WaitForExitAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("Stopping the MCP server", await output);
        Assert.False(File.Exists(DaemonFiles.InfoFile));
        Assert.False(DaemonFiles.IsRunning());
        _ = await error;
    }

    [Fact]
    public async Task Status_json_describes_the_daemon_and_autostart()
    {
        await _daemon.Workspace("a", "Alpha");
        await _daemon.Start();

        var json = (await _daemon.Mcp("status", "--json")).Json;

        Assert.Equal(_daemon.Port, json["daemon"]!["port"]!.GetValue<int>());
        Assert.Equal("open", json["daemon"]!["workspaces"]![0]!["state"]!.GetValue<string>());
        Assert.False(json["autostart"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_settings_file_from_another_run_is_reused_by_the_daemon()
    {
        // Порт и список областей лежат в файле: демон, запущенный позже другим процессом, берёт их оттуда.
        await _daemon.Workspace("a", "Alpha");
        var first = await _daemon.Start();
        await _daemon.Mcp("stop");

        var second = await _daemon.Start();

        Assert.Equal(first.Port, second.Port);
        Assert.Equal(first.Workspaces.Single().Path, second.Workspaces.Single().Path);
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

    private async Task<DaemonStatus> WaitFor(Func<DaemonStatus, bool> condition)
    {
        DaemonStatus? last = null;
        await Eventually(async () =>
        {
            last = await _daemon.Status();
            return last != null && condition(last);
        });
        return last!;
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 150; i++)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }

        Assert.True(await condition(), "the condition did not become true in 15 seconds");
    }
}
