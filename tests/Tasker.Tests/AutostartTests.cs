using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tasker.Daemon;
using Tasker.Global;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Автозапуск и управление демоном через службу системы — командами <c>tasker mcp …</c>. Служба поддельная: в PATH процессов
/// <c>tasker</c> первыми стоят свои <c>launchctl</c> и <c>systemctl</c> (<see cref="FakeService"/>), которые делают то, что launchd и
/// systemd, — запускают и останавливают настоящий демон. Настоящие launchd и systemd не трогаются; описания служб и команды системы
/// проверяют модульные тесты Rust (<c>tasker-daemon/src/autostart</c>) и эталоны <c>rust/tests/golden/autostart</c>.
/// </summary>
public class AutostartTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();
    private readonly FakeService _service = new();

    public void Dispose()
    {
        _service.Dispose();
        _daemon.Dispose();
    }

    private static bool Supported => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private static string Manager => OperatingSystem.IsMacOS() ? "launchd" : "systemd";

    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    private static async Task<bool> Wait(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++)
            await Task.Delay(100);
        return condition();
    }

    private static int PidOf(CliResult started) => started.Json["status"]!["pid"]!.GetValue<int>();

    [Fact]
    public async Task Enabling_autostart_starts_the_server_through_the_service()
    {
        if (!Supported)
            return;
        await _daemon.Workspace("a", "Alpha");

        await Ok(_daemon.Mcp("autostart", "enable", "--json"));

        Assert.Contains("start", _service.Calls);
        var status = await _daemon.Status();
        Assert.Equal(WorkspaceState.Open, status!.Workspaces.Single().State);
        var info = (await Ok(_daemon.Mcp("autostart", "status", "--json"))).Json;
        Assert.True(info["enabled"]!.GetValue<bool>());
        Assert.True(info["loaded"]!.GetValue<bool>());
        Assert.Equal(Manager, info["manager"]!.GetValue<string>());
    }

    [Fact]
    public async Task Enabling_takes_over_a_server_that_was_started_by_hand()
    {
        if (!Supported)
            return;
        var manual = await _daemon.Start();

        var enabled = await Ok(_daemon.Mcp("autostart", "enable", "--json"));

        Assert.NotEqual(manual.Pid, PidOf(enabled));
        Assert.Contains("start", _service.Calls);
        Assert.True(await Wait(() => !IsAlive(manual.Pid)));
    }

    [Fact]
    public async Task With_autostart_on_start_and_stop_go_through_the_service()
    {
        if (!Supported)
            return;
        await Ok(_daemon.Mcp("autostart", "enable"));
        _service.ClearCalls();

        await Ok(_daemon.Mcp("stop"));
        Assert.False(DaemonFiles.IsRunning());
        Assert.Contains("stop", _service.Calls);
        Assert.DoesNotContain("start", _service.Calls);

        // Автозапуск при этом остаётся включённым, а сервер поднимается службой.
        Assert.StartsWith($"enabled ({Manager})", (await Ok(_daemon.Mcp("autostart", "status"))).Out.Trim());
        _service.ClearCalls();
        await Ok(_daemon.Mcp("start"));
        Assert.Contains("start", _service.Calls);
        Assert.True(DaemonFiles.IsRunning());
    }

    [Fact]
    public async Task Stop_reaches_a_server_that_the_service_does_not_own()
    {
        if (!Supported)
            return;
        // Автозапуск включён, но сервер запущен вручную (tasker mcp run в терминале): служба про него не знает.
        await Ok(_daemon.Mcp("autostart", "enable"));
        await Ok(_daemon.Mcp("stop"));
        using var manual = TaskerProcess.Start("mcp", "run", "--detached");
        Assert.True(await Wait(() => DaemonFiles.IsRunning()));

        await Ok(_daemon.Mcp("stop"));

        Assert.False(DaemonFiles.IsRunning());
        Assert.True(manual.WaitForExit(20_000));
    }

    [Fact]
    public async Task Disabling_autostart_keeps_a_running_server_running()
    {
        if (!Supported)
            return;
        var enabled = await Ok(_daemon.Mcp("autostart", "enable", "--json"));

        var disabled = await Ok(_daemon.Mcp("autostart", "disable", "--json"));

        Assert.True(DaemonFiles.IsRunning());
        Assert.NotEqual(PidOf(enabled), PidOf(disabled));
        Assert.Contains("unload", _service.Calls);
        Assert.Equal("off", (await Ok(_daemon.Mcp("autostart", "status"))).Out.Trim());
    }

    [Fact]
    public async Task Disabling_autostart_of_a_stopped_server_starts_nothing()
    {
        if (!Supported)
            return;
        await Ok(_daemon.Mcp("autostart", "enable"));
        await Ok(_daemon.Mcp("stop"));

        var disabled = await Ok(_daemon.Mcp("autostart", "disable", "--json"));

        Assert.Null(disabled.Json["status"]);
        Assert.False(DaemonFiles.IsRunning());
    }

    [Fact]
    public async Task A_server_that_the_service_never_starts_is_reported_after_the_timeout()
    {
        if (!Supported)
            return;
        _service.Silent = true;

        var result = await _daemon.Mcp("autostart", "enable");

        Assert.Equal(1, result.Code);
        Assert.Contains("did not answer in 30 s", result.Err);
    }

    [Fact]
    public async Task A_server_that_exits_at_start_explains_why()
    {
        using var listener = new TcpListener(IPAddress.Loopback, _daemon.Port);
        listener.Start();

        var result = await _daemon.Mcp("start");

        Assert.Equal(1, result.Code);
        Assert.Contains($"Port {_daemon.Port} is busy", result.Err);
    }

    [Fact]
    public async Task Autostart_commands_enable_report_and_disable()
    {
        if (!Supported)
            return;
        await _daemon.Workspace("a", "Alpha");

        Assert.Equal("off", (await Ok(_daemon.Mcp("autostart", "status"))).Out.Trim());

        var enabled = await Ok(_daemon.Mcp("autostart", "enable"));
        Assert.Contains($"Autostart is enabled ({Manager})", enabled.Out);
        Assert.Contains("open", enabled.Out);

        Assert.Equal($"enabled ({Manager})", (await Ok(_daemon.Mcp("autostart", "status"))).Out.Trim());
        Assert.Contains($"autostart: enabled ({Manager})", (await Ok(_daemon.Mcp("status"))).Out);

        await Ok(_daemon.Mcp("stop"));
        Assert.Equal("Autostart is disabled", (await Ok(_daemon.Mcp("autostart", "disable"))).Out.Trim());
        Assert.Equal("off", (await Ok(_daemon.Mcp("autostart", "status"))).Out.Trim());
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

    /// <summary>
    /// Поддельные <c>launchctl</c> и <c>systemctl</c> первыми в PATH процессов <c>tasker</c>: «загрузка» службы — файл-отметка и запуск
    /// демона (если его ещё нет), «выгрузка» и остановка — запрос <c>/daemon/stop</c> по <c>daemon.json</c>. Что делала служба, видно по
    /// <see cref="Calls"/>: <c>load</c>, <c>unload</c>, <c>start</c>, <c>stop</c>.
    /// </summary>
    private sealed class FakeService : IDisposable
    {
        private const string Script = """
            #!/usr/bin/env python3
            import json, os, subprocess, sys, urllib.request
            state = os.environ["FAKE_SERVICE_STATE"]
            loaded = os.path.join(state, "loaded")
            def log(action):
                with open(os.path.join(state, "calls"), "a") as f:
                    f.write(action + "\n")
            def info():
                try:
                    with open(os.environ["FAKE_SERVICE_INFO"]) as f:
                        return json.load(f)
                except (OSError, ValueError):
                    return None
            def start():
                log("start")
                if os.path.exists(os.path.join(state, "silent")):
                    return
                subprocess.Popen([os.environ["FAKE_SERVICE_DAEMON"]], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                 stderr=subprocess.DEVNULL, start_new_session=True)
            def stop():
                log("stop")
                daemon = info()
                if daemon:
                    request = urllib.request.Request(f"http://127.0.0.1:{daemon['port']}/daemon/stop", method="POST",
                                                     headers={"X-Tasker-Control": daemon["token"]})
                    try:
                        urllib.request.urlopen(request, timeout=5).read()
                    except OSError:
                        pass
            def load():
                log("load")
                open(loaded, "w").close()
            def unload():
                if os.path.exists(loaded):
                    log("unload")
                    os.remove(loaded)
                    stop()
            args = [a for a in sys.argv[1:] if a not in ("--user", "--now", "--quiet")]
            verb = args[0] if args else ""
            if os.path.basename(sys.argv[0]) == "launchctl":
                if verb == "bootstrap":
                    load(); start()
                elif verb == "bootout":
                    unload()
                elif verb == "kickstart":
                    if not os.path.exists(loaded):
                        sys.exit(113)
                    start()
                elif verb == "print":
                    sys.exit(0 if os.path.exists(loaded) else 113)
            else:
                if verb == "enable":
                    load(); start()
                elif verb == "disable":
                    unload()
                elif verb == "start":
                    start()
                elif verb == "stop":
                    stop()
                elif verb == "is-active":
                    sys.exit(0 if os.path.exists(loaded) and info() else 3)
            """;

        private readonly string _root = Directory.CreateTempSubdirectory("tasker-fake-service-").FullName;
        private readonly List<IDisposable> _overrides = [];

        public FakeService()
        {
            var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
            State = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
            if (!OperatingSystem.IsWindows())
            {
                foreach (var name in new[] { "launchctl", "systemctl" })
                {
                    var file = Path.Combine(bin, name);
                    File.WriteAllText(file, Script.Replace("\r\n", "\n") + "\n");
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            _overrides.Add(AppEnvironment.Override("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")));
            _overrides.Add(AppEnvironment.Override("FAKE_SERVICE_STATE", State));
            _overrides.Add(AppEnvironment.Override("FAKE_SERVICE_INFO", DaemonFiles.InfoFile));
            _overrides.Add(AppEnvironment.Override("FAKE_SERVICE_DAEMON", TaskerProcess.DaemonCommand().File));
        }

        private string State { get; }

        /// <summary>Служба «загружается», но демон не запускает.</summary>
        public bool Silent
        {
            set
            {
                if (value)
                    File.WriteAllText(Path.Combine(State, "silent"), "");
            }
        }

        public string[] Calls => File.Exists(Path.Combine(State, "calls")) ? File.ReadAllLines(Path.Combine(State, "calls")) : [];

        public void ClearCalls() => File.Delete(Path.Combine(State, "calls"));

        public void Dispose()
        {
            for (var i = _overrides.Count - 1; i >= 0; i--)
                _overrides[i].Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
