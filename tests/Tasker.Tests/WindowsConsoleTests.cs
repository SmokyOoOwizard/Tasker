using System.Diagnostics;
using System.Text;
using Tasker.Global;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Консоль на Windows (TSK-119): всё, что можно проверить без Windows, — общие функции путей и имени пользователя, кодировка вывода,
/// подсказки в диалекте PowerShell, установка блока в <c>$PROFILE</c> командой и скрипт автодополнения PowerShell (golden-проверка, а при
/// наличии pwsh или образа Docker с pwsh — настоящий прогон). Разбор строки PowerShell, ширина окна и правка профиля по шагам — модульные
/// тесты Rust (<c>suggest/mod.rs</c>, <c>terminal.rs</c>, <c>commands/completion.rs</c>).
/// </summary>
public class WindowsConsoleTests
{
    // ---- пути и имя пользователя ----

    [Theory]
    [InlineData("~", "/home/u")]
    [InlineData("~/work", "/home/u/work")]
    [InlineData("~\\work\\проект", "/home/u/work\\проект")]
    [InlineData("~user/work", "~user/work")]
    [InlineData("work/~", "work/~")]
    [InlineData("C:\\work\\проект x", "C:\\work\\проект x")]
    [InlineData("", "")]
    public void The_tilde_is_expanded_with_either_slash(string path, string expected)
    {
        var home = Path.DirectorySeparatorChar == '/' ? "/home/u" : "C:\\Users\\u";
        var result = UserPath.Expand(path, home)!;
        // Склеивание делает Path.Combine системы: сравниваем без учёта вида разделителя.
        Assert.Equal(expected.Replace('\\', '/').Replace("/home/u", home.Replace('\\', '/')), result.Replace('\\', '/'));
    }

    [Fact]
    public void An_unknown_home_leaves_the_path_as_it_is() => Assert.Equal("~/x", UserPath.Expand("~/x", ""));

    [Fact]
    public void Null_path_stays_null() => Assert.Null(UserPath.Expand(null));

    [Theory]
    [InlineData("user", "user")]
    [InlineData("DOMAIN\\user", "user")]
    [InlineData("CORP\\Иван Петров", "Иван Петров")]
    [InlineData("user@corp.example.com", "user")]
    [InlineData("CORP\\user@corp", "user")]
    [InlineData("  ", "user")]
    [InlineData(null, "user")]
    public void The_domain_is_dropped_from_the_user_name(string? name, string expected) => Assert.Equal(expected, OsUser.Normalize(name));

    // ---- кодировка вывода ----

    [Fact]
    public async Task Redirected_output_is_utf8_without_a_byte_order_mark()
    {
        var info = TaskerProcess.StartInfo(null, "manual", "--json");
        using var process = Process.Start(info)!;
        using var bytes = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(bytes);
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await copy;

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await error);
        var output = bytes.ToArray();
        Assert.NotEqual(0xEF, output[0]);
        Assert.Contains("Проект и первая доска", new UTF8Encoding(false, true).GetString(output));
    }

    // ---- разбор строки PowerShell ----

    [Fact]
    public async Task The_directive_takes_the_PowerShell_dialect_from_its_value()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tasker windows no workspace " + Guid.NewGuid().ToString("N"));
        var text = $"manual -w '{folder}' fir";

        var result = await TestWorkspace.Invoke([$"[suggest:{text.Length}:pwsh]", text]);

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Err);
        Assert.Equal("first-project", result.Out.Trim());
        Assert.False(Directory.Exists(folder));
    }

    // ---- блок в $PROFILE: команда ----

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tasker-pwsh-" + Guid.NewGuid().ToString("N"))).FullName;

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose() => System.IO.Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public async Task The_install_command_saves_the_script_and_connects_it_and_uninstall_undoes_it()
    {
        using var folder = new Folder();
        var profile = folder.File("profile dir/Microsoft.PowerShell_profile.ps1");
        var script = folder.File("completions/tasker.ps1");

        var installed = await TestWorkspace.Invoke(["completion", "pwsh", "--install", "--profile", profile, "--script", script]);
        var again = await TestWorkspace.Invoke(["completion", "powershell", "--install", "--profile", profile, "--script", script]);

        Assert.True(installed.Code == 0, installed.Err);
        Assert.Contains("Register-ArgumentCompleter", File.ReadAllText(script));
        Assert.Contains("already has the tasker completion block", again.Out);
        Assert.Contains("'" + Path.GetFullPath(script).Replace("'", "''") + "'", File.ReadAllText(profile));
        Assert.Contains("cmd.exe", installed.Out);

        var removed = await TestWorkspace.Invoke(["completion", "pwsh", "--uninstall", "--profile", profile, "--script", script]);

        Assert.True(removed.Code == 0, removed.Err);
        Assert.DoesNotContain("# >>> tasker completion >>>", File.ReadAllText(profile));
        Assert.False(File.Exists(script));
    }

    [Fact]
    public async Task Install_is_for_pwsh_only_and_flags_need_it()
    {
        var zsh = await TestWorkspace.Invoke(["completion", "zsh", "--install"]);
        var stray = await TestWorkspace.Invoke(["completion", "pwsh", "--profile", "x"]);
        var both = await TestWorkspace.Invoke(["completion", "pwsh", "--install", "--uninstall"]);

        Assert.NotEqual(0, zsh.Code);
        Assert.Contains("pwsh only", zsh.Err);
        Assert.NotEqual(0, stray.Code);
        Assert.NotEqual(0, both.Code);
    }

    // ---- скрипт автодополнения ----

    [Fact]
    public async Task The_pwsh_script_registers_a_native_completer_that_calls_the_directive()
    {
        var result = await TestWorkspace.Invoke(["completion", "pwsh"]);
        var alias = await TestWorkspace.Invoke(["completion", "powershell"]);

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Err);
        Assert.Equal(result.Out, alias.Out);
        Assert.Contains("Register-ArgumentCompleter -Native -CommandName 'tasker', 'tasker.exe'", result.Out);
        Assert.Contains("[suggest:", result.Out);
        Assert.Contains(":pwsh]", result.Out);
        Assert.Contains("CompletionResult", result.Out);
        Assert.Contains("StandardOutputEncoding = [System.Text.Encoding]::UTF8", result.Out);
    }

    [Fact]
    public async Task The_pwsh_script_is_ascii_and_uses_nothing_newer_than_Windows_PowerShell_5_1()
    {
        var script = (await TestWorkspace.Invoke(["completion", "pwsh"])).Out;

        // Windows PowerShell 5.1 читает скрипт без BOM в ANSI: только ASCII. И никакого синтаксиса PowerShell 7.
        Assert.All(script, c => Assert.True(c < 0x80, $"non-ASCII character U+{(int)c:X4}"));
        foreach (var newer in new[] { "?.", "??", "?[", "&&", "||", "ForEach-Object -Parallel", "-AsHashtable", "$IsWindows", "-Raw " })
            Assert.DoesNotContain(newer, script.Replace("'||'", ""));
        Assert.Equal(CountOf(script, '{'), CountOf(script, '}'));
        Assert.Equal(CountOf(script, '('), CountOf(script, ')'));
    }

    private static int CountOf(string text, char c) => text.Count(x => x == c);

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Path.Combine(x, name)).FirstOrDefault(File.Exists);

    /// <summary>
    /// Настоящий PowerShell разбирает и выполняет скрипт: <c>pwsh</c> из PATH или (на машине без него) контейнер Docker из переменной
    /// <c>PWSH_DOCKER_IMAGE</c> (образ с pwsh, например собранный из <c>mcr.microsoft.com/dotnet/sdk</c> и <c>dotnet tool install --global
    /// PowerShell</c>). Нет ни того, ни другого — проверка пропускается. Подставной <c>tasker</c> — sh-скрипт, поэтому только не Windows.
    /// </summary>
    [Fact]
    public async Task The_script_runs_in_a_real_PowerShell_and_quotes_what_tasker_answers()
    {
        var image = Environment.GetEnvironmentVariable("PWSH_DOCKER_IMAGE");
        var pwsh = FindOnPath("pwsh");
        if (OperatingSystem.IsWindows() || pwsh == null && string.IsNullOrEmpty(image))
            return;

        using var folder = new Folder();
        Directory.CreateDirectory(folder.File("bin"));
        var record = folder.File("record.txt");
        var fake = folder.File("bin/tasker");
        var answer = new[] { "Основная доска", "Plain", "It's", "Name=", "Story points=", "--json" };
        await File.WriteAllTextAsync(fake,
            "#!/bin/sh\nprintf '%s|%s\\n' \"$1\" \"$2\" >> \"$(dirname \"$0\")/../record.txt\"\n"
            + string.Join("", answer.Select(x => $"printf '%s\\n' '{x.Replace("'", "'\\''")}'\n")));
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(folder.File("tasker.ps1"), (await TestWorkspace.Invoke(["completion", "pwsh"])).Out);

        // Контейнер видит папку как /work, настоящий pwsh — как есть.
        var root = string.IsNullOrEmpty(image) || pwsh != null ? folder.Path : "/work";
        await File.WriteAllTextAsync(folder.File("drive.ps1"),
            $$"""
            $env:PATH = '{{root}}/bin' + [IO.Path]::PathSeparator + $env:PATH
            . '{{root}}/tasker.ps1'
            $cases = @(
                @{ Line = 'tasker task list --status '; Cursor = -1 },
                @{ Line = 'tasker task get TSK-1 --json'; Cursor = 17 },
                @{ Line = 'tasker task list -w "C:\my dir\x" --st'; Cursor = -1 },
                @{ Line = "tasker task list --field 'Story"; Cursor = -1 })
            foreach ($case in $cases) {
                $cursor = if ($case.Cursor -lt 0) { $case.Line.Length } else { $case.Cursor }
                $result = [System.Management.Automation.CommandCompletion]::CompleteInput($case.Line, $cursor, $null)
                'LINE ' + $case.Line + ' @' + $cursor
                foreach ($match in $result.CompletionMatches) { 'MATCH ' + $match.CompletionText + ' | ' + $match.ListItemText + ' | ' + $match.ResultType }
            }
            """);

        var info = pwsh != null
            ? new ProcessStartInfo(pwsh)
            : new ProcessStartInfo("docker");
        if (pwsh == null)
            foreach (var argument in new[] { "run", "--rm", "-v", $"{folder.Path}:/work", image!, "pwsh" })
                info.ArgumentList.Add(argument);
        foreach (var argument in new[] { "-NoProfile", "-File", pwsh != null ? folder.File("drive.ps1") : "/work/drive.ps1" })
            info.ArgumentList.Add(argument);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = (await output).Replace("\r\n", "\n");
        Assert.True(process.ExitCode == 0, await error + text);

        var matches = new[]
        {
            "MATCH 'Основная доска' | Основная доска | ParameterValue",
            "MATCH Plain | Plain | ParameterValue",
            "MATCH 'It''s' | It's | ParameterValue",
            "MATCH Name= | Name= | ParameterValue",
            "MATCH 'Story points= | Story points= | ParameterValue", // знак «=» продолжает слово: кавычка остаётся открытой
            "MATCH --json | --json | ParameterName"
        };
        var expected = string.Join("\n", (string[])
        [
            "LINE tasker task list --status  @26", .. matches,
            "LINE tasker task get TSK-1 --json @17", .. matches,
            "LINE tasker task list -w \"C:\\my dir\\x\" --st @38", .. matches,
            "LINE tasker task list --field 'Story @31", .. matches
        ]) + "\n";
        Assert.Equal(expected, text);

        // Директива получила строку после имени программы и позицию курсора в ней, в кавычках Windows (а не в кавычках PowerShell).
        Assert.Equal(
            "[suggest:19:pwsh]|task list --status \n"
            + "[suggest:10:pwsh]|task get T\n"
            + "[suggest:31:pwsh]|task list -w \"C:\\my dir\\x\" --st\n"
            + "[suggest:24:pwsh]|task list --field 'Story\n",
            (await File.ReadAllTextAsync(record)).Replace("\r\n", "\n"));
    }
}
