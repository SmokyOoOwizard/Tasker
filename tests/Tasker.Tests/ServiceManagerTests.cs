using System.Xml.Linq;
using Tasker.Daemon.Services;
using Xunit;

namespace Tasker.Tests;

/// <summary>Описание службы автозапуска и команды launchctl / systemctl — без настоящего launchd и systemd.</summary>
public class ServiceManagerTests : IDisposable
{
    private readonly IsolatedHome _home = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _runner = new();

    public ServiceManagerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        _home.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        /// <summary>Что вернёт команда, начинающаяся с ключа; по умолчанию — успех.</summary>
        public Dictionary<string, ProcessResult> Results { get; } = new();

        public Task<ProcessResult> Run(string file, params string[] arguments)
        {
            var line = $"{file} {string.Join(' ', arguments)}";
            Calls.Add(line);
            var result = Results.FirstOrDefault(x => line.StartsWith(x.Key)).Value ?? new ProcessResult(0, "", "");
            return Task.FromResult(result);
        }
    }

    // ---- launchd ----

    private LaunchdService Launchd(string label = "com.tasker.test") => new(_runner, _directory, label);

    [Fact]
    public async Task Launchd_plist_runs_the_daemon_at_login_and_keeps_it_alive()
    {
        var service = Launchd();
        await service.Enable();

        var plist = XDocument.Load(service.PlistPath).Root!.Element("dict")!;
        var keys = plist.Elements().Chunk(2).ToDictionary(x => x[0].Value, x => x[1]);

        Assert.Equal("com.tasker.test", keys["Label"].Value);
        Assert.Equal("true", keys["RunAtLoad"].Name.LocalName);
        Assert.Equal("true", keys["KeepAlive"].Name.LocalName);
        var arguments = keys["ProgramArguments"].Elements("string").Select(x => x.Value).ToArray();
        // Службе нужна программа демона (tasker-mcpd) рядом с tasker, а не сам tasker.
        Assert.Equal("--detached", arguments[^1]);
        Assert.Contains(arguments, x => x.Contains("tasker-mcpd"));
        Assert.True(File.Exists(arguments[0]));
        Assert.EndsWith("mcp-launchd.log", keys["StandardOutPath"].Value);
    }

    [Fact]
    public async Task Launchd_plist_carries_the_data_directory_and_escapes_special_characters()
    {
        var special = Path.Combine(_directory, "a&b <c>");
        using var _ = Tasker.Global.AppEnvironment.Override("TASKER_HOME", special);

        var service = Launchd();
        await service.Enable();

        var plist = XDocument.Load(service.PlistPath).Root!.Element("dict")!;
        var environment = plist.Elements().Chunk(2).Single(x => x[0].Value == "EnvironmentVariables")[1];
        Assert.Equal("TASKER_HOME", environment.Element("key")!.Value);
        Assert.Equal(Path.GetFullPath(special), environment.Element("string")!.Value);
    }

    [Fact]
    public async Task Launchd_enable_reloads_the_service_for_the_current_user()
    {
        var service = Launchd();

        await service.Enable();

        Assert.True(service.IsEnabled);
        Assert.Equal(2, _runner.Calls.Count);
        Assert.Matches(@"^launchctl bootout gui/\d+/com\.tasker\.test$", _runner.Calls[0]);
        Assert.Matches($@"^launchctl bootstrap gui/\d+ {System.Text.RegularExpressions.Regex.Escape(service.PlistPath)}$", _runner.Calls[1]);
    }

    [Fact]
    public async Task Launchd_enable_reports_a_failing_launchctl()
    {
        _runner.Results["launchctl bootstrap"] = new ProcessResult(5, "", "Bootstrap failed: 5: Input/output error");

        var error = await Assert.ThrowsAsync<ServiceException>(() => Launchd().Enable());

        Assert.Contains("launchctl bootstrap failed (5)", error.Message);
        Assert.Contains("Input/output error", error.Message);
    }

    [Fact]
    public async Task Launchd_disable_unloads_and_removes_the_plist()
    {
        var service = Launchd();
        await service.Enable();
        _runner.Calls.Clear();

        await service.Disable();

        Assert.False(service.IsEnabled);
        Assert.False(File.Exists(service.PlistPath));
        Assert.Single(_runner.Calls);
        Assert.StartsWith("launchctl bootout", _runner.Calls[0]);
    }

    [Fact]
    public async Task Launchd_disable_is_harmless_when_nothing_was_enabled()
    {
        var service = Launchd();

        await service.Disable();

        Assert.False(service.IsEnabled);
    }

    [Fact]
    public async Task Launchd_start_loads_the_service_or_kicks_a_loaded_one()
    {
        var service = Launchd();
        await service.Enable();

        _runner.Calls.Clear();
        _runner.Results["launchctl print"] = new ProcessResult(113, "", "Could not find service");
        await service.Start();
        Assert.Contains(_runner.Calls, x => x.StartsWith("launchctl bootstrap"));

        _runner.Calls.Clear();
        _runner.Results["launchctl print"] = new ProcessResult(0, "", "");
        await service.Start();
        Assert.Contains(_runner.Calls, x => x.StartsWith("launchctl kickstart"));
        Assert.DoesNotContain(_runner.Calls, x => x.StartsWith("launchctl bootstrap"));
    }

    [Fact]
    public async Task Launchd_stop_unloads_but_keeps_autostart_enabled()
    {
        var service = Launchd();
        await service.Enable();

        await service.Stop();

        Assert.True(service.IsEnabled);
        Assert.StartsWith("launchctl bootout", _runner.Calls[^1]);
    }

    [Fact]
    public async Task Launchd_start_without_autostart_is_an_error()
    {
        var error = await Assert.ThrowsAsync<ServiceException>(() => Launchd().Start());

        Assert.Contains("mcp autostart enable", error.Message);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Launchd_is_loaded_follows_launchctl_print()
    {
        var service = Launchd();

        _runner.Results["launchctl print"] = new ProcessResult(0, "", "");
        Assert.True(await service.IsLoaded());

        _runner.Results["launchctl print"] = new ProcessResult(113, "", "");
        Assert.False(await service.IsLoaded());
    }

    // ---- systemd ----

    private SystemdService Systemd() => new(_runner, _directory, "tasker-test.service");

    [Fact]
    public async Task Systemd_unit_restarts_the_daemon_and_starts_at_login()
    {
        var service = Systemd();
        await service.Enable();

        var text = await File.ReadAllTextAsync(service.UnitPath);

        Assert.Contains("Restart=always", text);
        Assert.Contains("WantedBy=default.target", text);
        var exec = text.Split('\n').Single(x => x.StartsWith("ExecStart="));
        Assert.EndsWith("--detached", exec);
        Assert.Contains("tasker-mcpd", exec);
        Assert.Contains($"TASKER_HOME={_home.Path}", text);
    }

    [Fact]
    public async Task Systemd_unit_quotes_arguments_with_spaces()
    {
        var special = Path.Combine(_directory, "with space");
        using var _ = Tasker.Global.AppEnvironment.Override("TASKER_HOME", special);

        var service = Systemd();
        await service.Enable();

        Assert.Contains($"Environment=\"TASKER_HOME={Path.GetFullPath(special)}\"", await File.ReadAllTextAsync(service.UnitPath));
    }

    [Fact]
    public async Task Systemd_enable_reloads_and_enables_the_user_unit()
    {
        await Systemd().Enable();

        Assert.Equal(["systemctl --user daemon-reload", "systemctl --user enable --now tasker-test.service"], _runner.Calls);
    }

    [Fact]
    public async Task Systemd_disable_stops_removes_and_reloads()
    {
        var service = Systemd();
        await service.Enable();
        _runner.Calls.Clear();

        await service.Disable();

        Assert.False(File.Exists(service.UnitPath));
        Assert.Equal(["systemctl --user disable --now tasker-test.service", "systemctl --user daemon-reload"], _runner.Calls);
    }

    [Fact]
    public async Task Systemd_start_stop_and_state()
    {
        var service = Systemd();
        await service.Enable();
        _runner.Calls.Clear();

        await service.Start();
        await service.Stop();
        _runner.Results["systemctl --user is-active"] = new ProcessResult(3, "", "");

        Assert.False(await service.IsLoaded());
        Assert.Equal(
            ["systemctl --user start tasker-test.service", "systemctl --user stop tasker-test.service", "systemctl --user is-active --quiet tasker-test.service"],
            _runner.Calls);
    }

    [Fact]
    public async Task Systemd_failure_is_reported_with_the_command_output()
    {
        _runner.Results["systemctl --user enable"] = new ProcessResult(1, "", "Failed to connect to bus");

        var error = await Assert.ThrowsAsync<ServiceException>(() => Systemd().Enable());

        Assert.Contains("systemctl enable failed", error.Message);
        Assert.Contains("Failed to connect to bus", error.Message);
        Assert.Contains("login session", error.Message); // подсказка для su / sudo -u / ssh без сеанса (TSK-111)
        Assert.False(Systemd().IsEnabled, "A failed enable must not leave the unit file that looks like enabled autostart");
    }

    [Fact]
    public async Task Unsupported_system_says_what_to_do()
    {
        var service = new UnsupportedService();

        Assert.False(service.IsEnabled);
        Assert.False(await service.IsLoaded());
        var error = await Assert.ThrowsAsync<ServiceException>(() => service.Enable());
        Assert.Contains("tasker mcp run", error.Message);
    }
}
