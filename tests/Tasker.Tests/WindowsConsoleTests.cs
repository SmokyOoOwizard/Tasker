using System.Diagnostics;
using System.Text;
using Tasker.Cli;
using Tasker.Cli.Completion;
using Tasker.Global;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Консоль на Windows (TSK-119): всё, что можно проверить без Windows, — чистые функции (пути, имя пользователя, ширина, кодировка,
/// разбор строки PowerShell, блок в <c>$PROFILE</c>) и скрипт автодополнения PowerShell (golden-проверка, а при наличии pwsh или образа
/// Docker с pwsh — настоящий прогон). Что требует самой Windows, перечислено в разделе «ПРОВЕРИТЬ НА WINDOWS» задачи TSK-119.
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

    // ---- ширина окна и кодировка ----

    [Theory]
    [InlineData(120, true, 119)]
    [InlineData(120, false, 120)]
    [InlineData(1, true, 1)]
    [InlineData(0, true, 0)]
    public void The_last_column_is_left_free_only_on_windows(int window, bool windows, int expected) =>
        Assert.Equal(expected, Terminal.UsableColumns(window, windows));

    [Fact]
    public void Redirected_output_is_utf8_without_a_byte_order_mark()
    {
        Assert.Empty(ConsoleSetup.Utf8.GetPreamble());

        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, ConsoleSetup.Utf8, 16, leaveOpen: true))
            writer.WriteLine("Задача №1 ✓");
        var bytes = stream.ToArray();

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("Задача №1 ✓\n", Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n"));
    }

    [Fact]
    public void Everything_redirected_does_not_touch_the_console_code_page()
    {
        // Три перенаправления: кодовую страницу консоли не меняем (её может не быть), пишем напрямую в потоки.
        using var setup = ConsoleSetup.Open(true, true, true);

        Assert.NotNull(setup.Input);
        Assert.NotSame(Console.Out, setup.Output);
    }

    // ---- разбор строки PowerShell ----

    [Theory]
    [InlineData("task list --status 'В работе' ", "task list --status \"В работе\" ")]
    [InlineData("task list --status 'В р", "task list --status \"В р")]
    [InlineData("task list --status \"В р", "task list --status \"В р")]
    [InlineData("task list --status В` р", "task list --status \"В р")]
    [InlineData("task get 'It''s'", "task get It's")]
    [InlineData("task get \"a\"\"b\"", "task get a\"b")]
    [InlineData("task list -w C:\\work\\проект", "task list -w C:\\work\\проект")]
    [InlineData("task list -w 'C:\\my dir\\x' ", "task list -w \"C:\\my dir\\x\" ")]
    [InlineData("task list -w \"C:\\my dir\\x\" --st", "task list -w \"C:\\my dir\\x\" --st")]
    [InlineData("task list --status ‘В работе’ ", "task list --status \"В работе\" ")]
    public void PowerShell_words_are_rewritten_for_the_parser(string typed, string expected) =>
        Assert.Equal(expected, ShellWords.Normalize(typed, ShellDialect.PowerShell));

    [Fact]
    public void A_backslash_is_an_escape_for_zsh_and_bash_but_an_ordinary_character_for_PowerShell()
    {
        Assert.Equal("C:workproj", ShellWords.Normalize("C:\\work\\proj"));
        Assert.Equal("C:\\work\\proj", ShellWords.Normalize("C:\\work\\proj", ShellDialect.PowerShell));
    }

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

    // ---- блок в $PROFILE ----

    private const string Script = "C:\\Users\\Иван\\AppData\\Local\\Tasker\\completions\\tasker.ps1";

    [Fact]
    public void The_block_loads_the_script_if_it_exists()
    {
        var block = PowerShellProfile.Block("C:\\Users\\O'Brien\\tasker.ps1", "\n");

        Assert.Equal(
            "# >>> tasker completion >>>\n"
            + "# Tab completion for tasker, added by 'tasker completion pwsh --install'. To undo: delete this block or run 'tasker completion pwsh --uninstall'.\n"
            + "if (Test-Path -LiteralPath 'C:\\Users\\O''Brien\\tasker.ps1') { . 'C:\\Users\\O''Brien\\tasker.ps1' }\n"
            + "# <<< tasker completion <<<", block);
    }

    [Fact]
    public void Installing_into_an_empty_profile_writes_only_the_block()
    {
        var block = PowerShellProfile.Block(Script, "\r\n");

        var (content, change) = PowerShellProfile.Install("", block, "\r\n");

        Assert.Equal(ProfileChange.Added, change);
        Assert.Equal(block + "\r\n", content);
    }

    [Theory]
    [InlineData("Set-Alias g git\n", "\n")]
    [InlineData("Set-Alias g git\r\n", "\r\n")]
    [InlineData("Set-Alias g git", "\n")]
    public void The_block_is_appended_after_the_existing_content_and_removed_without_a_trace(string original, string newline)
    {
        var block = PowerShellProfile.Block(Script, newline);

        var (installed, added) = PowerShellProfile.Install(original, block, newline);
        var (removed, change) = PowerShellProfile.Uninstall(installed, newline);

        Assert.Equal(ProfileChange.Added, added);
        Assert.StartsWith(original, installed);
        Assert.EndsWith(block + newline, installed);
        Assert.Equal(ProfileChange.Removed, change);
        Assert.Equal(original.EndsWith('\n') ? original : original + newline, removed);
    }

    [Fact]
    public void Installing_twice_changes_nothing_and_a_new_path_replaces_the_block()
    {
        var first = PowerShellProfile.Block(Script, "\n");
        var (installed, _) = PowerShellProfile.Install("# mine\n", first, "\n");

        var (again, unchanged) = PowerShellProfile.Install(installed, first, "\n");
        var (moved, replaced) = PowerShellProfile.Install(installed + "# after\n", PowerShellProfile.Block("D:\\t.ps1", "\n"), "\n");

        Assert.Equal(ProfileChange.Unchanged, unchanged);
        Assert.Equal(installed, again);
        Assert.Equal(ProfileChange.Replaced, replaced);
        Assert.Equal(1, Count(moved, PowerShellProfile.Begin));
        Assert.Contains("D:\\t.ps1", moved);
        Assert.DoesNotContain("Иван", moved);
        Assert.StartsWith("# mine\n", moved);
        Assert.EndsWith("# after\n", moved);
    }

    [Fact]
    public void A_file_with_only_one_mark_is_left_alone()
    {
        var broken = "x\n" + PowerShellProfile.Begin + "\ny\n";

        Assert.Equal((broken, ProfileChange.Broken), PowerShellProfile.Install(broken, "block", "\n"));
        Assert.Equal((broken, ProfileChange.Broken), PowerShellProfile.Uninstall(broken, "\n"));
    }

    [Fact]
    public void Removing_from_a_profile_without_the_block_changes_nothing() =>
        Assert.Equal(("Set-Alias g git\n", ProfileChange.Absent), PowerShellProfile.Uninstall("Set-Alias g git\n", "\n"));

    [Fact]
    public void Smart_quotes_in_a_path_are_doubled_for_PowerShell() =>
        Assert.Equal("'a''b\u2019\u2019c'", PowerShellProfile.Quote("a'b\u2019c"));

    [Fact]
    public void Profiles_are_found_by_platform()
    {
        Assert.Equal(
            [Path.Combine("D:\\Docs", "PowerShell", "Microsoft.PowerShell_profile.ps1"), Path.Combine("D:\\Docs", "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1")],
            PowerShellProfile.DefaultProfiles(true, "D:\\Docs", "C:\\Users\\u", null));
        Assert.Equal([Path.Combine("/home/u", ".config", "powershell", "Microsoft.PowerShell_profile.ps1")], PowerShellProfile.DefaultProfiles(false, "", "/home/u", null));
        Assert.Equal([Path.Combine("/cfg", "powershell", "Microsoft.PowerShell_profile.ps1")], PowerShellProfile.DefaultProfiles(false, "", "/home/u", "/cfg"));
    }

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    // ---- файлы профиля ----

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tasker-pwsh-" + Guid.NewGuid().ToString("N"))).FullName;

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose() => System.IO.Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public void A_profile_is_backed_up_once_and_keeps_its_byte_order_mark()
    {
        using var folder = new Folder();
        var profile = folder.File("p.ps1");
        File.WriteAllBytes(profile, [.. Encoding.UTF8.GetPreamble(), .. "Set-Alias g git\r\n"u8.ToArray()]);

        var (change, report) = PowerShellProfileFiles.Apply(profile, "C:\\t\\tasker.ps1", "\r\n");
        var (second, _) = PowerShellProfileFiles.Apply(profile, "C:\\t\\tasker.ps1", "\r\n");

        Assert.Equal(ProfileChange.Added, change);
        Assert.Equal(ProfileChange.Unchanged, second);
        Assert.Contains("backup:", report);
        Assert.Equal("Set-Alias g git\r\n", File.ReadAllText(profile + PowerShellProfileFiles.BackupSuffix));
        var bytes = File.ReadAllBytes(profile);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        Assert.Contains("\r\n# >>> tasker completion >>>\r\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void A_new_profile_gets_a_byte_order_mark_only_when_it_has_non_ascii_text()
    {
        using var folder = new Folder();
        var ascii = folder.File("a/p.ps1");
        var cyrillic = folder.File("b/p.ps1");

        PowerShellProfileFiles.Apply(ascii, "C:\\t\\tasker.ps1", "\n");
        PowerShellProfileFiles.Apply(cyrillic, "C:\\Users\\Иван\\tasker.ps1", "\n");

        Assert.NotEqual(0xEF, File.ReadAllBytes(ascii)[0]);
        Assert.Equal(Encoding.UTF8.GetPreamble(), File.ReadAllBytes(cyrillic)[..3]);
        Assert.False(File.Exists(ascii + PowerShellProfileFiles.BackupSuffix));
    }

    [Fact]
    public void A_profile_in_another_code_page_is_not_touched()
    {
        using var folder = new Folder();
        var profile = folder.File("p.ps1");
        byte[] ansi = [0x23, 0x20, 0xC8, 0xE2, 0xE0, 0xED, 0x0A]; // «# Иван» в Windows-1251
        File.WriteAllBytes(profile, ansi);

        var (change, report) = PowerShellProfileFiles.Apply(profile, "C:\\t\\tasker.ps1", "\n");

        Assert.Equal(ProfileChange.Broken, change);
        Assert.Contains("not UTF-8", report);
        Assert.Equal(ansi, File.ReadAllBytes(profile));
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
        Assert.Contains(PowerShellProfile.Quote(script), File.ReadAllText(profile));
        Assert.Contains("cmd.exe", installed.Out);

        var removed = await TestWorkspace.Invoke(["completion", "pwsh", "--uninstall", "--profile", profile, "--script", script]);

        Assert.True(removed.Code == 0, removed.Err);
        Assert.DoesNotContain(PowerShellProfile.Begin, File.ReadAllText(profile));
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
