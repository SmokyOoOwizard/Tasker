using System.Diagnostics;
using Tasker.Cli;
using Tasker.Daemon;
using Tasker.Daemon.Services;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Автозапуск и управление демоном через службу системы. Служба здесь поддельная: она делает то, что launchd и systemd
/// (запускает и останавливает настоящий демон), а её команды проверены отдельно в <see cref="ServiceManagerTests"/>.
/// </summary>
public class AutostartTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();
    private readonly FakeService _service = new();

    public void Dispose() => _daemon.Dispose();

    /// <summary>Ведёт себя как launchd/systemd: «загруженная» служба держит демон, «выгруженная» — останавливает.</summary>
    private sealed class FakeService : IServiceManager
    {
        public List<string> Calls { get; } = [];
        public string Name => "fake";
        public bool IsEnabled { get; private set; }

        public Task Enable()
        {
            Calls.Add("enable");
            IsEnabled = true;
            Spawn();
            return Task.CompletedTask;
        }

        public async Task Disable()
        {
            Calls.Add("disable");
            IsEnabled = false;
            await DaemonClient.RequestStop();
        }

        public Task Start()
        {
            Calls.Add("start");
            Spawn();
            return Task.CompletedTask;
        }

        public async Task Stop()
        {
            Calls.Add("stop");
            await DaemonClient.RequestStop();
        }

        public Task<bool> IsLoaded() => Task.FromResult(DaemonFiles.IsRunning());

        private static void Spawn() => TaskerProcess.Start("mcp", "run", "--detached");
    }

    private DaemonController Controller(TimeSpan? ready = null) =>
        new(_service, ready ?? TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15), () => TaskerProcess.Start("mcp", "run", "--detached"));

    private static async Task<bool> Wait(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++)
            await Task.Delay(100);
        return condition();
    }

    [Fact]
    public async Task Enabling_autostart_starts_the_server_through_the_service()
    {
        await _daemon.Workspace("a", "Alpha");

        var status = await Controller().EnableAutostart();

        Assert.Equal(["enable"], _service.Calls);
        Assert.Equal(WorkspaceState.Open, status.Workspaces.Single().State);
        var info = (await Controller().Status()).Autostart;
        Assert.True(info.Enabled);
        Assert.True(info.Loaded);
        Assert.Equal("fake", info.Manager);
    }

    [Fact]
    public async Task Enabling_takes_over_a_server_that_was_started_by_hand()
    {
        var manual = await _daemon.Start();

        var status = await Controller().EnableAutostart();

        Assert.NotEqual(manual.Pid, status.Pid);
        Assert.Equal(["enable"], _service.Calls);
        Assert.True(await Wait(() => !IsAlive(manual.Pid)));
    }

    [Fact]
    public async Task With_autostart_on_start_and_stop_go_through_the_service()
    {
        var controller = Controller();
        await controller.EnableAutostart();
        _service.Calls.Clear();

        Assert.True(await controller.Stop());
        Assert.False(DaemonFiles.IsRunning());
        Assert.Equal(["stop"], _service.Calls);

        // Автозапуск при этом остаётся включённым, а сервер поднимается службой.
        Assert.True(_service.IsEnabled);
        var (status, already) = await controller.Start();
        Assert.False(already);
        Assert.Equal(["stop", "start"], _service.Calls);
        Assert.NotNull(status);
    }

    [Fact]
    public async Task Stop_reaches_a_server_that_the_service_does_not_own()
    {
        // Автозапуск включён, но сервер запущен вручную (tasker mcp run в терминале): служба про него не знает.
        var controller = Controller();
        await controller.EnableAutostart();
        await controller.Stop();
        using var manual = TaskerProcess.Start("mcp", "run", "--detached");
        Assert.True(await Wait(() => DaemonFiles.IsRunning()));

        await controller.Stop();

        Assert.False(DaemonFiles.IsRunning());
    }

    [Fact]
    public async Task Disabling_autostart_keeps_a_running_server_running()
    {
        var controller = Controller();
        var enabled = await controller.EnableAutostart();

        var status = await controller.DisableAutostart();

        Assert.NotNull(status);
        Assert.False(_service.IsEnabled);
        Assert.True(DaemonFiles.IsRunning());
        Assert.NotEqual(enabled.Pid, status.Pid);
        Assert.Contains("disable", _service.Calls);
    }

    [Fact]
    public async Task Disabling_autostart_of_a_stopped_server_starts_nothing()
    {
        var controller = Controller();
        await controller.EnableAutostart();
        await controller.Stop();

        var status = await controller.DisableAutostart();

        Assert.Null(status);
        Assert.False(DaemonFiles.IsRunning());
    }

    [Fact]
    public async Task A_server_that_never_answers_is_reported_after_the_timeout()
    {
        var silent = new SilentService();
        var controller = new DaemonController(silent, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        await silent.Enable();

        var error = await Assert.ThrowsAsync<DaemonStartException>(() => controller.Start());

        Assert.Contains("did not answer in 1 s", error.Message);
    }

    private sealed class SilentService : IServiceManager
    {
        public string Name => "silent";
        public bool IsEnabled { get; private set; }
        public Task Enable() { IsEnabled = true; return Task.CompletedTask; }
        public Task Disable() { IsEnabled = false; return Task.CompletedTask; }
        public Task Start() => Task.CompletedTask;
        public Task Stop() => Task.CompletedTask;
        public Task<bool> IsLoaded() => Task.FromResult(false);
    }

    [Fact]
    public async Task A_server_that_exits_at_start_explains_why()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, _daemon.Port);
        listener.Start();
        var controller = new DaemonController(new UnsupportedNoService(), spawn: () => TaskerProcess.Start("mcp", "run", "--detached"));

        var error = await Assert.ThrowsAsync<DaemonStartException>(() => controller.Start());

        Assert.Contains($"Port {_daemon.Port} is busy", error.Message);
    }

    private sealed class UnsupportedNoService : IServiceManager
    {
        public string Name => "none";
        public bool IsEnabled => false;
        public Task Enable() => Task.CompletedTask;
        public Task Disable() => Task.CompletedTask;
        public Task Start() => Task.CompletedTask;
        public Task Stop() => Task.CompletedTask;
        public Task<bool> IsLoaded() => Task.FromResult(false);
    }

    // ---- через команды tasker ----

    private async Task<CliResult> Cli(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApp.Run(args, output, error, _service);
        return new CliResult(code, output.ToString(), error.ToString());
    }

    [InProcess]
    [Fact]
    public async Task Autostart_commands_enable_report_and_disable()
    {
        await _daemon.Workspace("a", "Alpha");

        var before = await Cli("mcp", "autostart", "status");
        Assert.Equal("off", before.Out.Trim());

        var enabled = await Cli("mcp", "autostart", "enable");
        Assert.Equal(0, enabled.Code);
        Assert.Contains("Autostart is enabled (fake)", enabled.Out);
        Assert.Contains("open", enabled.Out);

        Assert.Equal("enabled (fake)", (await Cli("mcp", "autostart", "status")).Out.Trim());
        var status = await Cli("mcp", "status");
        Assert.Contains("autostart: enabled (fake)", status.Out);

        Assert.Equal(0, (await Cli("mcp", "stop")).Code);
        var disabled = await Cli("mcp", "autostart", "disable");
        Assert.Equal(0, disabled.Code);
        Assert.Equal("Autostart is disabled", disabled.Out.Trim());
        Assert.Equal("off", (await Cli("mcp", "autostart", "status")).Out.Trim());
    }

    [InProcess]
    [Fact]
    public async Task Autostart_on_an_unsupported_system_is_a_clear_error()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await CliApp.Run(["mcp", "autostart", "enable"], output, error, new UnsupportedNoServiceThatThrows());

        Assert.Equal(1, code);
        Assert.Contains("tasker mcp run", error.ToString());
    }

    private sealed class UnsupportedNoServiceThatThrows : IServiceManager
    {
        public string Name => "none";
        public bool IsEnabled => false;
        public Task Enable() => throw new ServiceException("Autostart is supported on macOS (launchd) and Linux (systemd) only; on this system run 'tasker mcp run' yourself");
        public Task Disable() => Task.CompletedTask;
        public Task Start() => Task.CompletedTask;
        public Task Stop() => Task.CompletedTask;
        public Task<bool> IsLoaded() => Task.FromResult(false);
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
}
