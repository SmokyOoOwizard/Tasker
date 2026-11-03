using System.Text;
using System.Text.RegularExpressions;
using Tasker.Daemon.Services;
using Tasker.Global;
using Xunit;

namespace Tasker.Tests;

public partial class GoldenAutostartTests
{
    private const string Root = "/tmp/golden-root";
    private const string MacDefaultData = "<HOME>/Library/Application Support/Tasker";
    private const string LaunchdLabel = "com.tasker.golden-autostart";
    private const string SystemdUnit = "tasker-golden.service";
    private const string WindowsTask = "Tasker MCP Golden";
    private const string WindowsUser = "PC\\Артём";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    [GeneratedRegex(@"gui/\d+")]
    private static partial Regex GuiDomain();

    private static bool Updating => Environment.GetEnvironmentVariable("TASKER_GOLDEN_UPDATE") is { Length: > 0 } value && value != "0";

    private static string GoldenDirectory
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "tests", "golden", "autostart")))
                    return Path.Combine(directory.FullName, "tests", "golden", "autostart");
            }
            throw new DirectoryNotFoundException("tests/golden/autostart not found above the test binaries");
        }
    }

    private sealed class Recorder : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public Task<ProcessResult> Run(string file, params string[] arguments)
        {
            Calls.Add(string.Join('\t', [file, .. arguments]));
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }

    private static (string Dotnet, string Bin) Daemon()
    {
        var command = ServiceManagers.DaemonCommand();
        Assert.True(command.Length == 3 && command[1].EndsWith("tasker-mcpd.dll", StringComparison.Ordinal) && command[2] == "--detached",
            "the golden files describe the daemon started as 'dotnet <bin>/tasker-mcpd.dll --detached': " + string.Join(' ', command));
        return (command[0], Path.GetDirectoryName(command[1])!);
    }

    private static string Placeholders(string text, string root)
    {
        var (dotnet, bin) = Daemon();
        var data = AppEnvironment.Get(AppDirectories.HomeVariable) is { Length: > 0 } ? null : AppDirectories.Data;
        if (data != null)
            text = text.Replace(data, MacDefaultData);
        return text.Replace(bin, "<BIN>").Replace(dotnet, "<DOTNET>").Replace(root, "<ROOT>");
    }

    private static string Expected(string name, string file) => File.ReadAllText(Path.Combine(GoldenDirectory, name, file), Utf8);

    private static void CheckText(string name, string file, string actual, string root)
    {
        var normalized = Placeholders(actual, root);
        if (Updating)
            File.WriteAllText(Path.Combine(GoldenDirectory, name, file), normalized, Utf8);
        Assert.Equal(Expected(name, file), normalized);
    }

    private static string Utf16Text(byte[] bytes)
    {
        Assert.True(bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xFE, "no UTF-16 LE byte order mark");
        return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
    }

    private static void CheckXml(string name, byte[] actualBytes, string root)
    {
        var file = Path.Combine(GoldenDirectory, name, WindowsTask + ".xml");
        var normalized = Placeholders(Utf16Text(actualBytes), root);
        if (Updating)
            File.WriteAllBytes(file, [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(normalized)]);
        var expected = Utf16Text(File.ReadAllBytes(file));
        Assert.Equal(expected, normalized);
        Assert.Equal([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(normalized)], File.ReadAllBytes(file));
    }

    private static string[] NormalizedCalls(IEnumerable<string> calls, string root, string separator) =>
        calls.Select(x => GuiDomain().Replace(x.Replace(root, "<ROOT>"), "gui/<UID>")).Select(x => separator == "\t" ? x : x.Replace('\t', ' ')).ToArray();

    private static string[] ExpectedCalls(string name, string file) =>
        Expected(name, file).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static void CheckCalls(string name, string file, IEnumerable<string> calls, string root, string separator = "\t")
    {
        var actual = NormalizedCalls(calls, root, separator);
        if (Updating)
            File.WriteAllText(Path.Combine(GoldenDirectory, name, file), string.Concat(actual.Select(x => x + "\n")), Utf8);
        Assert.Equal(ExpectedCalls(name, file), actual);
    }

    private static IDisposable Home(string? home) => AppEnvironment.Override(AppDirectories.HomeVariable, home);

    [PlatformFact(TestPlatform.Unix)]
    public void Launchd_plist_matches_the_corpus_byte_for_byte()
    {
        var home = $"{Root}/home/data";
        var special = $"{Root}/tasker home/a&b <c>";
        var cliHome = $"{Root}/home";
        foreach (var (name, setting) in new (string, string?)[] { ("launchd", home), ("launchd-nohome", null), ("launchd-special", special), ("launchd-cli", cliHome) })
        {
            using var _ = Home(setting);
            var service = new LaunchdService(new Recorder(), Root + "/service", LaunchdLabel);
            CheckText(name, LaunchdLabel + ".plist", service.Plist(), Root);
        }
    }

    [PlatformFact(TestPlatform.Unix)]
    public void Systemd_unit_matches_the_corpus_byte_for_byte()
    {
        var home = $"{Root}/home/data";
        var space = $"{Root}/tasker home";
        foreach (var (name, setting) in new (string, string?)[] { ("systemd", home), ("systemd-nohome", null), ("systemd-space", space) })
        {
            using var _ = Home(setting);
            var service = new SystemdService(new Recorder(), Root + "/service", SystemdUnit);
            CheckText(name, SystemdUnit, service.UnitFile(), Root);
        }
    }

    [PlatformFact(TestPlatform.Unix)]
    public void Windows_task_xml_matches_the_corpus_byte_for_byte()
    {
        var home = $"{Root}/home/data";
        foreach (var (name, setting) in new (string, string?)[] { ("windows", home), ("windows-nohome", null) })
        {
            using var _ = Home(setting);
            var service = new WindowsTaskService(new Recorder(), Root + "/service", WindowsTask, WindowsUser);
            CheckXml(name, [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(service.Xml())], Root);
        }
    }

    private static string TempRoot() => Directory.CreateTempSubdirectory("tasker-golden-autostart-").FullName;

    [PlatformFact(TestPlatform.Unix)]
    public async Task Launchd_commands_match_the_corpus()
    {
        var root = TempRoot();
        try
        {
            foreach (var name in new[] { "launchd", "launchd-nohome", "launchd-special" })
            {
                using var _ = Home($"{root}/home/data");
                var recorder = new Recorder();
                var service = new LaunchdService(recorder, $"{root}/out/{name}", LaunchdLabel);
                await service.Enable();
                CheckCalls(name, "calls.txt", recorder.Calls, root);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [PlatformFact(TestPlatform.Unix)]
    public async Task Launchd_cli_enable_and_status_match_the_recorded_console_run()
    {
        var root = TempRoot();
        try
        {
            using var _ = Home($"{root}/home");
            var recorder = new Recorder();
            var service = new LaunchdService(recorder, $"{root}/service", LaunchdLabel);
            await service.Enable();
            Assert.True(await service.IsLoaded());

            CheckCalls("launchd-cli", "launchctl-calls.txt", recorder.Calls, root, " ");
            CheckText("launchd-cli", LaunchdLabel + ".plist", await File.ReadAllTextAsync(Path.Combine(root, "service", LaunchdLabel + ".plist")), root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [PlatformFact(TestPlatform.Unix)]
    public async Task Systemd_commands_match_the_corpus()
    {
        var root = TempRoot();
        try
        {
            foreach (var name in new[] { "systemd", "systemd-nohome", "systemd-space" })
            {
                using var _ = Home($"{root}/home/data");
                var recorder = new Recorder();
                var service = new SystemdService(recorder, $"{root}/out/{name}", SystemdUnit);
                await service.Enable();
                CheckCalls(name, "calls.txt", recorder.Calls, root);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [PlatformFact(TestPlatform.Unix)]
    public async Task Windows_commands_match_the_corpus()
    {
        var root = TempRoot();
        try
        {
            foreach (var name in new[] { "windows", "windows-nohome" })
            {
                using var _ = Home($"{root}/home/data");
                var recorder = new Recorder();
                var service = new WindowsTaskService(recorder, $"{root}/out/{name}", WindowsTask, WindowsUser);
                await service.Enable();
                CheckCalls(name, "calls.txt", recorder.Calls, root);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
