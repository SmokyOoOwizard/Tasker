using System.Diagnostics;
using Tasker.Daemon;
using Xunit;

namespace Tasker.Tests;

/// <summary>Где консольная утилита ищет программу демона (tasker-mcpd) и что делает, когда её нет.</summary>
public class DaemonLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));

    public DaemonLauncherTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static string Touch(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void The_daemon_is_taken_from_the_directory_of_the_running_program()
    {
        var app = Dir("app");
        var tasker = Touch(app, "tasker");
        var daemon = Touch(app, "tasker-mcpd");

        var (file, arguments) = Launcher.DaemonCommand(tasker, null);

        Assert.Equal(daemon, file);
        Assert.Empty(arguments);
    }

    [Fact]
    public void A_link_to_tasker_is_resolved_to_the_real_directory()
    {
        if (OperatingSystem.IsWindows())
            return;

        var app = Dir("app");
        var tasker = Touch(app, "tasker");
        var daemon = Touch(app, "tasker-mcpd");
        var bin = Dir("bin");
        var link = Path.Combine(bin, "tasker");
        File.CreateSymbolicLink(link, tasker);

        var (file, _) = Launcher.DaemonCommand(link, null);

        Assert.Equal(daemon, file);
    }

    [Fact]
    public void Under_dotnet_the_daemon_dll_next_to_the_entry_assembly_is_run_by_dotnet()
    {
        var app = Dir("app");
        var dll = Touch(app, "tasker.dll");
        var daemon = Touch(app, "tasker-mcpd.dll");

        var (file, arguments) = Launcher.DaemonCommand("/usr/local/share/dotnet/dotnet", dll);

        Assert.Equal("/usr/local/share/dotnet/dotnet", file);
        Assert.Equal([daemon], arguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_missing_daemon_is_explained(bool viaDotnet)
    {
        var app = Dir("app");
        var tasker = Touch(app, viaDotnet ? "tasker.dll" : "tasker");

        var error = Assert.Throws<DaemonStartException>(() =>
            viaDotnet ? Launcher.DaemonCommand("/usr/local/share/dotnet/dotnet", tasker) : Launcher.DaemonCommand(tasker, null));

        Assert.Contains("tasker-mcpd", error.Message);
        Assert.Contains("is not installed next to tasker", error.Message);
        Assert.Contains(app, error.Message);
        Assert.Contains("--no-daemon", error.Message);
    }

    [Fact]
    public void The_daemon_of_the_test_run_is_found_next_to_the_tests()
    {
        // Тесты запускают tasker и демон рядом (ссылки на оба проекта): в этом каталоге они и лежат.
        var (file, arguments) = Launcher.DaemonCommand();

        Assert.True(File.Exists(arguments.Length > 0 ? arguments[0] : file));
        Assert.Contains("tasker-mcpd", arguments.Length > 0 ? arguments[0] : file);
    }

    [Fact]
    public async Task Tasker_without_the_daemon_next_to_it_says_so_instead_of_failing_obscurely()
    {
        // Копия tasker без tasker-mcpd — как установка с --no-daemon.
        var copy = Dir("cli-only");
        foreach (var file in Directory.EnumerateFiles(TaskerProcess.ProgramDirectory)
                     .Where(x => !Path.GetFileName(x).StartsWith("tasker-mcpd", StringComparison.Ordinal)))
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));

        using var home = new IsolatedHome();
        home.IsolateDaemon(); // на машине может работать настоящий демон: порт по умолчанию и служба автозапуска заняты им
        using var process = Process.Start(TaskerProcess.StartInfoFrom(copy, "mcp", "start"))!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(1, process.ExitCode);
        Assert.Contains("is not installed next to tasker", await error);
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
