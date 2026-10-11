using System.Diagnostics;
using Tasker.Daemon;
using Xunit;

namespace Tasker.Tests;

/// <summary>Где консоль ищет программу демона (tasker-mcpd: рядом с собой, ссылка раскрывается) и что делает, когда её нет.</summary>
public class DaemonLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));

    public DaemonLauncherTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    [Fact]
    public void The_daemon_of_the_test_run_is_found_next_to_the_console()
    {
        var (file, arguments) = TaskerProcess.DaemonCommand();

        Assert.True(File.Exists(file), file);
        Assert.Empty(arguments);
        Assert.StartsWith("tasker-mcpd", Path.GetFileName(file));
    }

    [Fact]
    public async Task A_link_to_tasker_starts_the_daemon_from_the_real_directory()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Как установка: в bin — ссылка на tasker, а tasker-mcpd лежит только рядом с настоящей программой.
        var link = Path.Combine(Dir("bin"), "tasker");
        File.CreateSymbolicLink(link, TaskerProcess.Binary);
        using var home = new IsolatedHome();
        home.IsolateDaemon();

        var started = await Run(link, "mcp", "start", "--json");
        var stopped = await Run(link, "mcp", "stop");

        Assert.True(started.Code == 0, started.Err);
        Assert.Equal(0, stopped.Code);
        Assert.False(DaemonFiles.IsRunning());
    }

    private static async Task<CliResult> Run(string program, params string[] args)
    {
        var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        Tasker.Global.AppEnvironment.Apply(info);
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await output, await error);
    }

    [Fact]
    public async Task Tasker_without_the_daemon_next_to_it_says_so_instead_of_failing_obscurely()
    {
        // Копия tasker без tasker-mcpd — как установка с --no-daemon.
        var copy = Dir("cli-only");
        File.Copy(TaskerProcess.Binary, Path.Combine(copy, Path.GetFileName(TaskerProcess.Binary)));

        using var home = new IsolatedHome();
        home.IsolateDaemon(); // на машине может работать настоящий демон: порт по умолчанию и служба автозапуска заняты им
        using var process = Process.Start(TaskerProcess.StartInfoFrom(copy, "mcp", "start"))!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(1, process.ExitCode);
        var message = await error;
        Assert.Contains("tasker-mcpd", message);
        Assert.Contains("is not installed next to tasker", message);
        Assert.Contains(Path.GetFileName(copy), message);
        Assert.Contains("--no-daemon", message);
    }

    [Fact]
    public async Task Sigterm_to_a_foreground_tasker_stops_the_daemon_too()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var home = new IsolatedHome();
        var port = DaemonFixture.FreePort();
        await new Tasker.Global.SettingsStore(home.Path).SetPort(port);
        using var process = TaskerProcess.Start("mcp", "run");
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();

        for (var i = 0; i < 150 && await DaemonClient.GetStatus() == null; i++)
            await Task.Delay(100);
        Assert.NotNull(await DaemonClient.GetStatus());

        using (var kill = Process.Start("kill", ["-TERM", process.Id.ToString()])!)
            await kill.WaitForExitAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // Не осталось ни демона без хозяина, ни его файлов.
        for (var i = 0; i < 100 && DaemonFiles.IsRunning(); i++)
            await Task.Delay(100);
        Assert.False(DaemonFiles.IsRunning());
        Assert.False(File.Exists(DaemonFiles.InfoFile));
        Assert.Contains("Stopping the MCP server", await output);
    }
}
