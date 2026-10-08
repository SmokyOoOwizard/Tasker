using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Номер версии и <c>scripts/release.sh</c>. Лежит в классе установщика, а не отдельно: release.sh и установка из исходников публикуют
/// проекты репозитория (один и тот же каталог obj), поэтому такие тесты должны идти по очереди, а не параллельно.
/// </summary>
public partial class InstallScriptTests
{
    private static string RepoFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException(Path.Combine(parts) + " not found above the test binaries");
    }

    [Fact]
    public void The_version_is_one_number_from_the_VERSION_file_and_every_program_carries_it()
    {
        var version = File.ReadAllText(RepoFile("VERSION")).Trim();
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$"), version);

        foreach (var type in new[] { typeof(Tasker.Cli.CliApp), typeof(Tasker.Daemon.DaemonController) })
            Assert.Equal(version, type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
    }

    [Fact]
    public void The_installer_for_Windows_is_ASCII_only_so_that_Windows_PowerShell_5_reads_it_whatever_the_code_page()
    {
        var bytes = File.ReadAllBytes(RepoFile("scripts", "install.ps1"));

        Assert.DoesNotContain(bytes, x => x > 127);
    }

    private static async Task<CliResult> RunScript(string script, params string[] args)
    {
        var info = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(script);
        foreach (var argument in args)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await output, await error);
    }

    [UnixFact]
    public async Task Release_script_rejects_what_it_cannot_build_before_building_anything()
    {
        var script = RepoFile("scripts", "release.sh");

        var help = await RunScript(script, "--help");
        Assert.Equal(0, help.Code);
        foreach (var option in new[] { "--rid", "--version", "--out", "--no-r2r", "win-x64", "osx-arm64", "linux-arm64" })
            Assert.Contains(option, help.Out);

        var rid = await RunScript(script, "--rid", "freebsd-x64", "--out", Path.Combine(_root, "out"));
        Assert.Equal(2, rid.Code);
        Assert.Contains("unknown platform 'freebsd-x64'", rid.Err);

        var version = await RunScript(script, "--rid", HostRid, "--version", "v1", "--out", Path.Combine(_root, "out"));
        Assert.Equal(1, version.Code);
        Assert.Contains("bad version 'v1'", version.Err);
        Assert.False(Directory.Exists(Path.Combine(_root, "out", "work")));
    }

    /// <summary>
    /// Весь путь: release.sh собирает настоящие самодостаточные tasker и tasker-mcpd для этой машины, кладёт в архив с контрольной суммой,
    /// install.sh ставит из архива (без .NET SDK в работе установщика) и установленная программа работает; удаление всё убирает.
    /// </summary>
    [UnixFact]
    public async Task A_release_archive_for_this_machine_installs_runs_and_uninstalls()
    {
        var script = RepoFile("scripts", "release.sh");
        var output = Path.Combine(_root, "release");
        var release = await RunScript(script, "--rid", HostRid, "--version", "0.0.1-test", "--no-r2r", "--out", output);
        Assert.True(release.Code == 0, release.Out + release.Err);

        var name = $"tasker-0.0.1-test-{HostRid}";
        var archive = Path.Combine(output, name + ".tar.gz");
        Assert.True(File.Exists(archive), release.Out);
        Assert.True(File.Exists(Path.Combine(output, "install.sh")) && File.Exists(Path.Combine(output, "install.ps1")));

        // Контрольная сумма сходится, в архиве каталог релиза с app/, установщиком и описанием.
        var sums = await File.ReadAllTextAsync(Path.Combine(output, "SHA256SUMS"));
        Assert.Contains(name + ".tar.gz", sums);
        var check = OperatingSystem.IsMacOS()
            ? await Shell("shasum", ["-a", "256", "-c", "SHA256SUMS"], output, output)
            : await Shell("sha256sum", ["-c", "SHA256SUMS"], output, output);
        Assert.True(check.Code == 0, check.Out + check.Err);
        var list = (await Shell("tar", ["-tzf", archive], _root)).Out.Split('\n');
        foreach (var entry in new[] { "app/tasker", "app/tasker-mcpd", "install.sh", "release.txt" })
            Assert.Contains($"{name}/{entry}", list);

        // Установка из архива и работа установленного.
        var fakeHome = Home("release-home");
        var installed = await Run(["--prefix", Prefix, "--from", archive, "--no-autostart"], fakeHome);
        Assert.True(installed.Code == 0, installed.Out + installed.Err);
        Assert.Contains("Installed tasker 0.0.1-test", installed.Out);
        var link = Path.Combine(Prefix, "bin", "tasker");
        Assert.Equal("0.0.1-test", (await TaskerCommand(link, "--version")).Out.Trim());
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        Assert.Equal(0, (await TaskerCommand(link, "project", "create", "FromRelease", "-w", workspace)).Code);
        Assert.Contains("FromRelease", (await TaskerCommand(link, "project", "list", "-w", workspace)).Out);

        // Демон из релиза поднимается (свой порт и каталог данных) и останавливается.
        _home.IsolateDaemon();
        Assert.Equal(0, (await TaskerCommand(link, "mcp", "workspace", "add", workspace)).Code);
        var started = await TaskerCommand(link, "mcp", "start");
        Assert.True(started.Code == 0, started.Out + started.Err);
        Assert.Equal(0, (await TaskerCommand(link, "mcp", "stop")).Code);

        var removed = await Run(["--uninstall", "--prefix", Prefix], fakeHome);
        Assert.Equal(0, removed.Code);
        Assert.False(Directory.Exists(Path.Combine(Prefix, "share", "tasker", "app")));
        Assert.True(Directory.Exists(Path.Combine(workspace, ".tasker")));
    }
}
