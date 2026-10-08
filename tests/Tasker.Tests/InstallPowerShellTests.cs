using System.Diagnostics;
using System.IO.Compression;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// <c>scripts/install.ps1</c> (Windows) без Windows: настоящий PowerShell — <c>pwsh</c> из PATH или контейнер Docker из переменной
/// <c>PWSH_DOCKER_IMAGE</c> (как в WindowsConsoleTests); нет ни того, ни другого — проверки пропускаются. Чистые функции скрипта
/// (PATH, аргументы, разбор release.txt) проверяются подключением скрипта точкой, остальное — режимом сухого прогона (<c>-DryRun</c>,
/// <c>-WhatIf</c>), который ничего не меняет в системе. Что делает установка на настоящей Windows (реестр, Планировщик, профиль) —
/// в списке «ПРОВЕРИТЬ НА WINDOWS» задачи TSK-120.
/// </summary>
public class InstallPowerShellTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tasker-ps-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PowerShellSession? _pwsh;

    public InstallPowerShellTests()
    {
        Directory.CreateDirectory(_folder);
        _pwsh = PowerShellSession.TryCreate(_folder);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>Каталог релиза <c>tasker-9.9.9-rid/{app/tasker.exe, app/tasker-mcpd.exe, release.txt}</c> и его zip (как делает release.sh).</summary>
    private (string Folder, string Zip) Release(string rid = "win-x64", bool daemon = true)
    {
        var name = $"tasker-9.9.9-{rid}";
        var root = Directory.CreateDirectory(Path.Combine(_folder, "zip-source", name)).FullName;
        Directory.CreateDirectory(Path.Combine(root, "app"));
        File.WriteAllText(Path.Combine(root, "app", "tasker.exe"), "MZ");
        if (daemon)
            File.WriteAllText(Path.Combine(root, "app", "tasker-mcpd.exe"), "MZ");
        File.WriteAllText(Path.Combine(root, "release.txt"), $"version=9.9.9\nrid={rid}\ncommit=test\n");
        var zip = Path.Combine(_folder, name + ".zip");
        if (File.Exists(zip))
            File.Delete(zip);
        ZipFile.CreateFromDirectory(Path.Combine(_folder, "zip-source"), zip, CompressionLevel.Fastest, includeBaseDirectory: false);
        return (root, zip);
    }

    private Task<CliResult> Install(string[] arguments, string architecture = "AMD64") =>
        _pwsh!.Run(["-File", _pwsh.Scripts + "/install.ps1", .. arguments], new Dictionary<string, string> { ["PROCESSOR_ARCHITECTURE"] = architecture });

    private string Prefix => _pwsh!.Work + "/prefix";

    [Fact]
    public async Task The_script_parses_in_PowerShell_and_its_pure_functions_give_the_expected_answers()
    {
        if (_pwsh == null)
            return;

        await File.WriteAllTextAsync(Path.Combine(_folder, "pure.ps1"), $$"""
            $errors = $null; $tokens = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile('{{_pwsh.Scripts}}/install.ps1', [ref]$tokens, [ref]$errors)
            'parse errors: ' + $errors.Count
            . '{{_pwsh.Scripts}}/install.ps1'
            'rid: ' + (ConvertTo-WindowsRid 'AMD64') + ' ' + (ConvertTo-WindowsRid 'ARM64') + ' [' + (ConvertTo-WindowsRid 'x86') + ']'
            'add: ' + (Add-PathEntry 'C:\a;%USERPROFILE%\b' 'C:\Tasker\app')
            'add-twice: ' + (Add-PathEntry 'C:\a;C:\TASKER\app\' 'C:\Tasker\app')
            'add-empty: ' + (Add-PathEntry '' 'C:\Tasker\app')
            'add-semicolon: ' + (Add-PathEntry 'C:\a;' 'C:\Tasker\app')
            'remove: ' + (Remove-PathEntry 'C:\a;c:\tasker\APP;%X%\b' 'C:\Tasker\app')
            'has: ' + (Test-PathEntry 'C:\a;C:\Tasker\app\' 'c:\tasker\app') + ' ' + (Test-PathEntry 'C:\a' 'C:\Tasker\app')
            'arg: ' + (ConvertTo-Argument 'plain') + ' | ' + (ConvertTo-Argument 'C:\my dir\x\') + ' | ' + (ConvertTo-Argument 'say "hi"')
            'release: ' + (Get-ReleaseInfoValue "version=1.2.3`r`nrid=win-arm64`n" 'rid') + ' [' + (Get-ReleaseInfoValue 'a=b' 'rid') + ']'
            'hash: ' + (Get-ExpectedHash "ABCDEF  tasker.zip`n")
            'problem: ' + (Get-ParameterProblem 'a' 'b' $false) + ' / ' + (Get-ParameterProblem '' 'b' $true) + ' / [' + (Get-ParameterProblem 'a' '' $false) + ']'
            """);

        var result = await _pwsh.Run(["-File", _pwsh.Work + "/pure.ps1"]);

        Assert.True(result.Code == 0, result.Out + result.Err);
        var lines = result.Out.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            [
                "parse errors: 0",
                "rid: win-x64 win-arm64 []",
                @"add: C:\a;%USERPROFILE%\b;C:\Tasker\app",
                @"add-twice: C:\a;C:\TASKER\app\",
                @"add-empty: C:\Tasker\app",
                @"add-semicolon: C:\a;C:\Tasker\app",
                @"remove: C:\a;%X%\b",
                "has: True False",
                @"arg: plain | ""C:\my dir\x\\"" | ""say \""hi\""""",
                "release: win-arm64 []",
                "hash: abcdef",
                "problem: choose one of -From and -Url / -Uninstall does not take -From or -Url / []"
            ], lines);
    }

    [Fact]
    public async Task A_dry_run_from_a_folder_and_from_a_zip_describes_the_install_and_changes_nothing()
    {
        if (_pwsh == null)
            return;
        var (folder, zip) = Release();

        foreach (var source in new[] { zip, folder })
        {
            var result = await Install(["-From", PowerShellSession.Map(_pwsh, source), "-Prefix", Prefix, "-DryRun"]);

            Assert.True(result.Code == 0, result.Out + result.Err);
            Assert.Contains("[dry-run] platform: win-x64", result.Out);
            Assert.Contains("[dry-run] install into:", result.Out);
            Assert.Contains("[dry-run] PATH: add", result.Out);
            Assert.Contains("tasker completion pwsh --install", result.Out);
            Assert.Contains("tasker mcp autostart enable", result.Out);
            Assert.Contains("[dry-run] nothing was changed", result.Out);
            Assert.False(Directory.Exists(Path.Combine(_folder, "prefix")), "a dry run must not create the install folder");
        }
    }

    [Fact]
    public async Task The_switches_change_the_plan_and_WhatIf_is_the_same_as_DryRun()
    {
        if (_pwsh == null)
            return;
        var (folder, _) = Release();
        var source = PowerShellSession.Map(_pwsh, folder);

        var switches = await Install(["-From", source, "-Prefix", Prefix, "-DryRun", "-NoPath", "-NoCompletion", "-NoAutostart"]);
        Assert.True(switches.Code == 0, switches.Out + switches.Err);
        Assert.Contains("PATH: not changed (-NoPath)", switches.Out);
        Assert.Contains("completion: not connected (-NoCompletion)", switches.Out);
        Assert.Contains("autostart: not enabled (-NoAutostart)", switches.Out);

        var whatIf = await Install(["-From", source, "-Prefix", Prefix, "-WhatIf"]);
        Assert.True(whatIf.Code == 0, whatIf.Out + whatIf.Err);
        Assert.Contains("[dry-run] nothing was changed", whatIf.Out);
        Assert.False(Directory.Exists(Path.Combine(_folder, "prefix")));
    }

    [Fact]
    public async Task A_dry_run_of_a_download_shows_the_plan_without_touching_the_network()
    {
        if (_pwsh == null)
            return;

        var result = await Install(["-Url", "https://example.invalid/tasker-0.1.0-win-x64.zip", "-Prefix", Prefix, "-DryRun"]);

        Assert.True(result.Code == 0, result.Out + result.Err);
        Assert.Contains("download https://example.invalid/tasker-0.1.0-win-x64.zip and https://example.invalid/tasker-0.1.0-win-x64.zip.sha256", result.Out);
    }

    [Fact]
    public async Task The_architecture_is_checked_against_the_build()
    {
        if (_pwsh == null)
            return;
        var (_, zip) = Release("win-x64");
        var source = PowerShellSession.Map(_pwsh, zip);

        var arm = await Install(["-From", source, "-Prefix", Prefix, "-DryRun"], "ARM64");
        Assert.Equal(1, arm.Code);
        Assert.Contains("this build is for win-x64, but this machine is win-arm64", arm.Err);

        var x86 = await Install(["-From", source, "-Prefix", Prefix, "-DryRun"], "x86");
        Assert.Equal(1, x86.Code);
        Assert.Contains("unsupported processor architecture: x86", x86.Err);
    }

    [Fact]
    public async Task Wrong_parameters_and_bad_sources_are_reported_before_anything_is_changed()
    {
        if (_pwsh == null)
            return;
        var (folder, _) = Release();
        var noDaemon = Release("win-arm64", daemon: false);
        var missing = Path.Combine(_folder, "nothing-here");

        var both = await Install(["-From", "a", "-Url", "b", "-DryRun"]);
        Assert.Equal(2, both.Code);
        Assert.Contains("choose one of -From and -Url", both.Err);

        var uninstallFrom = await Install(["-Uninstall", "-From", "a", "-DryRun"]);
        Assert.Equal(2, uninstallFrom.Code);
        Assert.Contains("-Uninstall does not take -From or -Url", uninstallFrom.Err);

        var absent = await Install(["-From", PowerShellSession.Map(_pwsh, missing), "-DryRun"]);
        Assert.Equal(1, absent.Code);
        Assert.Contains("does not exist", absent.Err);

        var noExe = await Install(["-From", PowerShellSession.Map(_pwsh, noDaemon.Folder), "-DryRun"], "ARM64");
        Assert.Equal(1, noExe.Code);
        Assert.Contains("has no tasker-mcpd.exe", noExe.Err);

        var bare = await Install(["-DryRun"]);
        Assert.Equal(1, bare.Code);
        Assert.Contains("nothing to install", bare.Err);

        var unknown = await Install(["-Bogus"]);
        Assert.NotEqual(0, unknown.Code);

        // Не Windows и не сухой прогон: установка отказывается и ничего не создаёт.
        if (!OperatingSystem.IsWindows())
        {
            var real = await Install(["-From", PowerShellSession.Map(_pwsh, folder), "-Prefix", Prefix]);
            Assert.Equal(1, real.Code);
            Assert.Contains("this script is for Windows", real.Err);
            Assert.False(Directory.Exists(Path.Combine(_folder, "prefix")));
        }
    }

    [Fact]
    public async Task Uninstall_dry_run_reports_what_it_would_do_and_an_empty_prefix_is_calm()
    {
        if (_pwsh == null)
            return;

        var empty = await Install(["-Uninstall", "-Prefix", Prefix, "-DryRun"]);
        Assert.Equal(0, empty.Code);
        Assert.Contains("Nothing to uninstall", empty.Out);

        Directory.CreateDirectory(Path.Combine(_folder, "prefix", "app"));
        File.WriteAllText(Path.Combine(_folder, "prefix", "app", "tasker.exe"), "MZ");
        var plan = await Install(["-Uninstall", "-Prefix", Prefix, "-DryRun"]);

        Assert.True(plan.Code == 0, plan.Out + plan.Err);
        Assert.Contains("tasker mcp autostart disable", plan.Out);
        Assert.Contains("tasker completion pwsh --uninstall", plan.Out);
        Assert.Contains("from the user PATH", plan.Out);
        Assert.Contains("your data (%LOCALAPPDATA%\\Tasker) is kept", plan.Out);
        Assert.True(File.Exists(Path.Combine(_folder, "prefix", "app", "tasker.exe")), "a dry run must not delete anything");
    }
}

/// <summary>Настоящий PowerShell для тестов: <c>pwsh</c> из PATH или контейнер Docker с образом из <c>PWSH_DOCKER_IMAGE</c>.</summary>
internal sealed class PowerShellSession
{
    private readonly string? _pwsh;
    private readonly string? _image;
    private readonly string _folder;

    private PowerShellSession(string? pwsh, string? image, string folder, string scripts, string work)
    {
        _pwsh = pwsh;
        _image = image;
        _folder = folder;
        Scripts = scripts;
        Work = work;
    }

    /// <summary>Каталог scripts репозитория и рабочая папка теста так, как их видит PowerShell (в контейнере — /scripts и /work).</summary>
    public string Scripts { get; }

    public string Work { get; }

    public static PowerShellSession? TryCreate(string folder)
    {
        var scripts = Path.GetDirectoryName(ScriptsFile())!;
        var pwsh = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(x => new[] { Path.Combine(x, "pwsh"), Path.Combine(x, "pwsh.exe") }).FirstOrDefault(File.Exists);
        if (pwsh != null)
            return new PowerShellSession(pwsh, null, folder, scripts.Replace('\\', '/'), folder.Replace('\\', '/'));

        var image = Environment.GetEnvironmentVariable("PWSH_DOCKER_IMAGE");
        return string.IsNullOrEmpty(image) ? null : new PowerShellSession(null, image, folder, "/scripts", "/work");
    }

    /// <summary>Путь на машине теста, как его видит PowerShell (в контейнере папка теста — /work).</summary>
    public static string Map(PowerShellSession session, string path) =>
        session._pwsh != null ? path.Replace('\\', '/') : "/work/" + System.IO.Path.GetRelativePath(session._folder, path).Replace('\\', '/');

    private static string ScriptsFile()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var path = System.IO.Path.Combine(directory.FullName, "scripts", "install.ps1");
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException("scripts/install.ps1 not found above the test binaries");
    }

    public async Task<CliResult> Run(IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(_pwsh ?? "docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        if (_pwsh == null)
        {
            var scripts = System.IO.Path.GetDirectoryName(ScriptsFile())!;
            foreach (var argument in new[] { "run", "--rm", "-v", $"{_folder}:/work", "-v", $"{scripts}:/scripts:ro" })
                info.ArgumentList.Add(argument);
            foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            {
                info.ArgumentList.Add("-e");
                info.ArgumentList.Add($"{name}={value}");
            }

            info.ArgumentList.Add("-e");
            info.ArgumentList.Add("LOCALAPPDATA=/tmp/localappdata");
            info.ArgumentList.Add(_image!);
            info.ArgumentList.Add("pwsh");
        }
        else
        {
            foreach (var (name, value) in environment ?? new Dictionary<string, string>())
                info.Environment[name] = value;
            info.Environment["LOCALAPPDATA"] = System.IO.Path.Combine(_folder, "localappdata");
        }

        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await output, await error);
    }
}
