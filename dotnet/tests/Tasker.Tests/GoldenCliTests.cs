using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Tasker.Tests;

public partial class GoldenCliTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    [GeneratedRegex(@"\d{4}-\d\d-\d\d[ T]\d\d:\d\d:\d\d(\.\d+)? ?\+00:00")]
    private static partial Regex WallClock();

    private static string Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "golden", "workspace")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("tests/golden not found above the test binaries");
    }

    private static string Golden => Path.Combine(Repository(), "tests", "golden");

    private static bool Updating => Environment.GetEnvironmentVariable("TASKER_GOLDEN_UPDATE") is { Length: > 0 } value && value != "0";

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(from))
            Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    private static string Read(string path) => File.Exists(path) ? File.ReadAllText(path, Utf8) : "";

    private static string[] Names(string directory) =>
        Directory.GetFiles(directory, "*.args").Select(x => Path.GetFileNameWithoutExtension(x)).Order(StringComparer.Ordinal).ToArray();

    private static bool Wanted(string name) =>
        Environment.GetEnvironmentVariable("GOLDEN_FILTER") is not { Length: > 0 } filter || filter.Split(',').Any(name.Contains);

    private static async Task<(string Out, string Err, int Code)> Run(string[] args, string home, string cwd, IReadOnlyDictionary<string, string> env)
    {
        var info = new ProcessStartInfo(TaskerProcess.Binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = cwd
        };
        foreach (var name in new[] { "TASKER_PROJECT", "TASKER_WIDTH", "TASKER_PROFILE", "TASKER_LANG", "COLUMNS", "LINES", "TASKER_GOLDEN_UPDATE" })
            info.Environment.Remove(name);
        info.Environment["TASKER_HOME"] = home;
        foreach (var (key, value) in env)
            info.Environment[key] = value;
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (await output, await error, process.ExitCode);
    }

    private static async Task Check(string directory, string label, string name, string root, string home, string cwd,
        IReadOnlyDictionary<string, string> env, bool scrub, List<string> failures)
    {
        var at = Path.Combine(directory, name);
        var args = Read(at + ".args").Split('\n').Where(x => x.Length > 0).Select(x => x.Replace("<ROOT>", root)).ToArray();
        var (output, error, code) = await Run(args, home, cwd, env);
        if (scrub)
        {
            output = WallClock().Replace(output, "<TIME>");
            error = WallClock().Replace(error, "<TIME>");
        }

        if (Updating)
        {
            File.WriteAllText(at + ".out", output.Replace(root, "<ROOT>"), Utf8);
            File.WriteAllText(at + ".err", error.Replace(root, "<ROOT>"), Utf8);
            File.WriteAllText(at + ".code", code + "\n", Utf8);
        }

        var expectedOut = Read(at + ".out").Replace("<ROOT>", root);
        var expectedErr = Read(at + ".err").Replace("<ROOT>", root);
        var expectedCode = int.Parse(Read(at + ".code").Trim() is { Length: > 0 } text ? text : "0");
        if (output != expectedOut || error != expectedErr || code != expectedCode)
        {
            var detail = new StringBuilder($"{label}/{name}: code {code} (expected {expectedCode})");
            if (output != expectedOut)
                detail.Append($"\n--- expected stdout\n{expectedOut}--- actual stdout\n{output}");
            if (error != expectedErr)
                detail.Append($"\n--- expected stderr\n{expectedErr}--- actual stderr\n{error}");
            failures.Add(detail.ToString());
        }
    }

    [Fact]
    public async Task Every_cli_and_migrate_snapshot_of_the_corpus_matches()
    {
        var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "tasker-golden-" + Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(root);
        root = new DirectoryInfo(root).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(root);
        if (OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal))
            root = "/private" + root;
        var workspace = Path.Combine(root, "golden");
        Copy(Path.Combine(Golden, "workspace"), workspace);
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        var repository = Repository();
        var none = new Dictionary<string, string>();

        var whoami = await Run(["whoami", "golden-user"], home, root, none);
        Assert.True(whoami.Code == 0, whoami.Err);

        var failures = new List<string>();
        var checkedCount = 0;
        var cli = Path.Combine(Golden, "expected", "cli");
        foreach (var name in Names(cli).Where(Wanted))
        {
            checkedCount++;
            var cwd = name.Contains("-help-") || name.Contains("error-unknown-command")
                ? repository
                : name.Contains("-suggest-") ? workspace : root;
            var env = name.Contains("error-env-typo") ? new Dictionary<string, string> { ["TASKER_PROJCET"] = "Golden" } : none;
            await Check(cli, "cli", name, root, home, cwd, env, name.Contains("lock-acquire") || name.Contains("lock-show-held"), failures);
        }

        var migrateRoot = Path.Combine(root, "migrate");
        Copy(Path.Combine(Golden, "workspace"), Path.Combine(migrateRoot, "golden"));
        var migrate = Path.Combine(Golden, "expected", "migrate");
        foreach (var name in Names(migrate).Where(Wanted))
        {
            checkedCount++;
            await Check(migrate, "migrate", name, migrateRoot, home, migrateRoot, none, false, failures);
        }

        if (failures.Count == 0)
            Directory.Delete(root, true);
        Assert.True(checkedCount > 200 || Environment.GetEnvironmentVariable("GOLDEN_FILTER") is { Length: > 0 }, $"only {checkedCount} snapshots");
        Assert.True(failures.Count == 0, $"{failures.Count} of {checkedCount} snapshots differ:\n\n{string.Join("\n\n", failures)}");
    }
}
