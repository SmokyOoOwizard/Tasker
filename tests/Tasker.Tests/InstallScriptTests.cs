using System.Diagnostics;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Скрипт <c>scripts/install.sh</c> (macOS и Linux): установка готовой сборки из каталога, архива или адреса, сборка из исходников,
/// автозапуск, переход демона на новую сборку, автодополнение, удаление. Все запуски — в своём HOME и с подменой службы автозапуска;
/// настоящие <c>~/.local</c>, файлы оболочки, служба и демон на порту по умолчанию тестами не затрагиваются.
/// </summary>
public partial class InstallScriptTests : IDisposable
{
    private readonly IsolatedHome _home = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));

    public InstallScriptTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _home.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static string Script()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "scripts", "install.sh");
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException("scripts/install.sh not found above the test binaries");
    }

    /// <param name="home">Свой HOME для скрипта; не задан — тоже свой, пустой: настоящие ~/.zshrc, ~/.bashrc и ~/.local тесты не трогают никогда.</param>
    /// <param name="shell">Значение $SHELL: по нему скрипт выбирает оболочку для автодополнения.</param>
    private async Task<CliResult> Run(string[] args, string? home = null, string shell = "/bin/zsh", IReadOnlyDictionary<string, string>? environment = null, string? script = null)
    {
        home ??= Directory.CreateDirectory(Path.Combine(_root, "default-home")).FullName;
        var info = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(script ?? Script());
        foreach (var argument in args)
            info.ArgumentList.Add(argument);

        // Свой HOME, чтобы не трогать настоящие ~/.zprofile, ~/.zshrc и ~/.bash_profile; cargo (--from-source) должен по-прежнему видеть
        // настоящий кэш пакетов и toolchain (rustup), а не скачивать их заново в подставной HOME.
        var realHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        info.Environment["HOME"] = home;
        info.Environment["SHELL"] = shell;
        info.Environment["CARGO_HOME"] = Environment.GetEnvironmentVariable("CARGO_HOME") ?? Path.Combine(realHome, ".cargo");
        if (Environment.GetEnvironmentVariable("RUSTUP_HOME") is { } rustup)
            info.Environment["RUSTUP_HOME"] = rustup;
        else if (Directory.Exists(Path.Combine(realHome, ".rustup")))
            info.Environment["RUSTUP_HOME"] = Path.Combine(realHome, ".rustup");

        // Настоящая служба автозапуска тестам не нужна: uninstall выключает её у своего (тестового) имени.
        info.Environment["TASKER_SERVICE_DIR"] = Path.Combine(_root, "service");
        info.Environment["TASKER_SERVICE_LABEL"] = "com.tasker.install-tests";

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            info.Environment[name] = value;

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await output, await error);
    }

    [UnixFact]
    public async Task Help_lists_the_options()
    {
        var result = await Run(["--help"]);

        Assert.Equal(0, result.Code);
        foreach (var option in new[] { "--prefix", "--from", "--url", "--from-source", "--no-autostart", "--no-completion", "--no-daemon", "--add-to-path", "--restart", "--uninstall" })
            Assert.Contains(option, result.Out);
    }

    [PlatformTheory(TestPlatform.Unix)]
    [InlineData("--bogus", "unknown option '--bogus'")]
    [InlineData("--prefix", "--prefix needs a directory")]
    [InlineData("--from", "--from needs a file or a directory")]
    [InlineData("--url", "--url needs an address")]
    public async Task Bad_arguments_are_rejected_with_the_usage(string argument, string message)
    {
        var result = await Run([argument]);

        Assert.Equal(2, result.Code);
        Assert.Contains(message, result.Err);
        Assert.Contains("Usage:", result.Err);
    }

    [UnixFact]
    public async Task Uninstall_of_an_empty_prefix_is_calm()
    {
        var result = await Run(["--uninstall", "--prefix", Path.Combine(_root, "empty")]);

        Assert.Equal(0, result.Code);
        Assert.Contains("Nothing to uninstall", result.Out);
    }

    [UnixFact]
    public async Task Install_upgrade_and_uninstall_work_end_to_end()
    {
        var prefix = Path.Combine(_root, "prefix");
        var fakeHome = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
        var link = Path.Combine(prefix, "bin", "tasker");
        var app = Path.Combine(prefix, "share", "tasker", "app");

        // Установка из исходников: cargo build --release в rust/ (повторные сборки берутся из кэша rust/target).
        var installed = await Run(["--prefix", prefix, "--from-source", "--no-autostart", "--add-to-path"], fakeHome);
        Assert.True(installed.Code == 0, installed.Out + installed.Err);
        Assert.Contains("Installed tasker", installed.Out);
        Assert.True(File.Exists(Path.Combine(app, "tasker")));
        Assert.True(File.Exists(Path.Combine(app, "tasker-mcpd")), "the MCP server must be installed next to tasker");
        Assert.Equal(Path.Combine(app, "tasker"), new FileInfo(link).LinkTarget);
        Assert.Equal(["tasker", "tasker-mcpd"], Directory.GetFileSystemEntries(app).Select(Path.GetFileName).Order().ToArray());

        // PATH: bin не в PATH процесса — строка дописана в ~/.zprofile, один раз.
        var profile = Path.Combine(fakeHome, ".zprofile");
        Assert.Contains($"export PATH=\"{Path.Combine(prefix, "bin")}:$PATH\"", await File.ReadAllTextAsync(profile));

        // Установленная утилита работает: версия (из исходников — с коммитом: 0.1.0+abc1234), запись данных, чтение.
        var version = await TaskerCommand(link, "--version");
        Assert.Equal(0, version.Code);
        Assert.StartsWith(File.ReadAllText(RepoFile("VERSION")).Trim(), version.Out.Trim());
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        Assert.Equal(0, (await TaskerCommand(link, "project", "create", "Installed", "-w", workspace)).Code);
        Assert.Contains("Installed", (await TaskerCommand(link, "project", "list", "-w", workspace)).Out);

        // Демон из установки: запускается через ссылку, живёт в фоне, останавливается. (Каталог данных и порт — свои, из теста.)
        _home.IsolateDaemon();
        Assert.Equal(0, (await TaskerCommand(link, "mcp", "workspace", "add", workspace)).Code);
        var started = await TaskerCommand(link, "mcp", "start");
        Assert.True(started.Code == 0, started.Out + started.Err);
        Assert.Contains("The MCP server is started", started.Out);
        Assert.Equal(0, (await TaskerCommand(link, "mcp", "status")).Code);
        Assert.Equal(0, (await TaskerCommand(link, "mcp", "stop")).Code);
        Assert.Equal(3, (await TaskerCommand(link, "mcp", "status")).Code);

        // Обновление поверх: снова успех, в каталоге нет ни старой версии, ни остатков сборки, строка PATH не задвоилась.
        var upgraded = await Run(["--prefix", prefix, "--from-source", "--no-autostart", "--add-to-path"], fakeHome);
        Assert.True(upgraded.Code == 0, upgraded.Out + upgraded.Err);
        Assert.Equal(["app", "completions"], Directory.GetFileSystemEntries(Path.Combine(prefix, "share", "tasker")).Select(Path.GetFileName).Order().ToArray());
        Assert.Equal(1, (await File.ReadAllTextAsync(profile)).Split("# tasker").Length - 1);
        Assert.Contains("Installed", (await TaskerCommand(link, "project", "list", "-w", workspace)).Out);

        // Удаление: команды и программы нет, данные (каталог данных Tasker и рабочая папка) на месте.
        var removed = await Run(["--uninstall", "--prefix", prefix], fakeHome);
        Assert.Equal(0, removed.Code);
        Assert.False(File.Exists(link) || new FileInfo(link).LinkTarget != null);
        Assert.False(Directory.Exists(app));
        Assert.False(Directory.Exists(Path.Combine(prefix, "share", "tasker", "completions")), "uninstall removes the completion scripts too");
        Assert.True(Directory.Exists(Path.Combine(workspace, ".tasker")));
        Assert.True(Directory.Exists(_home.Path));
    }

    [UnixFact]
    public async Task Without_the_daemon_only_the_command_line_tool_is_installed()
    {
        var prefix = Path.Combine(_root, "prefix");
        var fakeHome = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
        var link = Path.Combine(prefix, "bin", "tasker");

        var installed = await Run(["--prefix", prefix, "--from-source", "--no-daemon", "--no-autostart"], fakeHome);

        Assert.True(installed.Code == 0, installed.Out + installed.Err);
        Assert.Contains("command line only", installed.Out);
        var app = Path.Combine(prefix, "share", "tasker", "app");
        // В каталоге программы — одна консольная утилита.
        Assert.Equal(["tasker"], Directory.GetFileSystemEntries(app).Select(Path.GetFileName).ToArray());

        // Остальное работает, а запуск демона объясняет, чего не хватает.
        _home.IsolateDaemon();
        Assert.Equal(0, (await TaskerCommand(link, "mcp", "config")).Code);
        var start = await TaskerCommand(link, "mcp", "start");
        Assert.Equal(1, start.Code);
        Assert.Contains("is not installed next to tasker", start.Err);
    }

    [UnixFact]
    public async Task Uninstall_leaves_a_foreign_tasker_link_alone()
    {
        var prefix = Path.Combine(_root, "prefix");
        var bin = Directory.CreateDirectory(Path.Combine(prefix, "bin")).FullName;
        Directory.CreateDirectory(Path.Combine(prefix, "share", "tasker", "app"));
        var other = Path.Combine(_root, "other-tasker");
        await File.WriteAllTextAsync(other, "#!/bin/sh\n");
        File.CreateSymbolicLink(Path.Combine(bin, "tasker"), other);

        var result = await Run(["--uninstall", "--prefix", prefix]);

        Assert.Equal(0, result.Code);
        Assert.Equal(other, new FileInfo(Path.Combine(bin, "tasker")).LinkTarget);
        Assert.False(Directory.Exists(Path.Combine(prefix, "share", "tasker", "app")));
    }

    // ---- автодополнение по Tab ----

    private const string Begin = "# >>> tasker completion >>>";
    private const string End = "# <<< tasker completion <<<";

    private string Prefix => Path.Combine(_root, "prefix");

    private string Home(string name = "home") => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private string Completions => Path.Combine(Prefix, "share", "tasker", "completions");

    /// <summary>Установка только консольной утилиты (быстрее) в <see cref="Prefix"/>.</summary>
    private async Task<CliResult> Install(string home, string shell = "/bin/zsh", params string[] more)
    {
        var result = await Run(["--prefix", Prefix, "--from-source", "--no-daemon", "--no-autostart", .. more], home, shell);
        Assert.True(result.Code == 0, result.Out + result.Err);
        return result;
    }

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    /// <summary>Строки файла: блок «tasker completion» вместе с маркерами.</summary>
    private static string BlockOf(string text)
    {
        var lines = text.Split('\n');
        var start = Array.IndexOf(lines, Begin);
        var end = Array.IndexOf(lines, End);
        return string.Join('\n', lines[start..(end + 1)]);
    }

    [UnixFact]
    public async Task Install_connects_completion_to_zsh_once_and_uninstall_takes_it_back()
    {
        var home = Home();
        var zshrc = Path.Combine(home, ".zshrc");
        var original = "# my zsh\nalias ll='ls -l'\nexport EDITOR=vim\n";
        await File.WriteAllTextAsync(zshrc, original);

        var first = await Install(home);

        // Скрипты есть для обеих оболочек; в .zshrc один блок после прежнего содержимого, прежнее — без изменений.
        Assert.StartsWith("#compdef tasker", await File.ReadAllTextAsync(Path.Combine(Completions, "_tasker")));
        Assert.Contains("complete -o default -F _tasker tasker", await File.ReadAllTextAsync(Path.Combine(Completions, "tasker.bash")));
        var text = await File.ReadAllTextAsync(zshrc);
        Assert.StartsWith(original + "\n" + Begin + "\n", text);
        Assert.EndsWith(End + "\n", text);
        Assert.Equal(1, Count(text, Begin));
        Assert.Contains($"fpath=(\"{Completions}\" $fpath)", text);

        // Резервная копия — файл до правки; в итоге сказано, что изменено и как отменить.
        Assert.Equal(original, await File.ReadAllTextAsync(zshrc + ".tasker-backup"));
        Assert.Contains("Tab completion", first.Out);
        Assert.Contains(zshrc, first.Out);
        Assert.Contains(".tasker-backup", first.Out);
        Assert.Contains("to undo", first.Out);
        Assert.Contains("--uninstall", first.Out);

        // Подключение работает: zsh, прочитав .zshrc, знает _tasker как функцию дополнения для tasker.
        if (File.Exists("/bin/zsh"))
        {
            var check = await Shell("/bin/zsh", ["-f", "-c", "source \"$HOME/.zshrc\"; print -r -- \"${_comps[tasker]}\""], home);
            Assert.True(check.Code == 0, check.Err);
            Assert.Equal("_tasker", check.Out.Trim());
        }

        // Повторная установка: блок не дублируется и файл не трогается (то же содержимое, резервная копия прежняя).
        var second = await Install(home);
        Assert.Equal(text, await File.ReadAllTextAsync(zshrc));
        Assert.Equal(original, await File.ReadAllTextAsync(zshrc + ".tasker-backup"));
        Assert.Contains("unchanged", second.Out);

        // Удаление: блок и скрипты убраны, .zshrc — как был до установки; копия остаётся, об этом сказано.
        var removed = await Run(["--uninstall", "--prefix", Prefix], home);
        Assert.Equal(0, removed.Code);
        Assert.Equal(original, await File.ReadAllTextAsync(zshrc));
        Assert.False(Directory.Exists(Completions));
        Assert.Contains("Removed the tasker completion block", removed.Out);
        Assert.True(File.Exists(zshrc + ".tasker-backup"));
    }

    [UnixFact]
    public async Task Bash_gets_its_block_in_the_login_file_and_a_changed_block_is_replaced_in_place()
    {
        // macOS: у bash в Terminal login-оболочка, из ~/.bash_profile, ~/.bash_login, ~/.profile читается первый существующий.
        // Linux: интерактивная оболочка читает ~/.bashrc.
        var home = Home();
        var fileName = OperatingSystem.IsMacOS() ? ".profile" : ".bashrc";
        var profile = Path.Combine(home, fileName);
        var original = "# profile\nexport A=1";   // без перевода строки в конце
        await File.WriteAllTextAsync(profile, original);

        await Install(home, "/bin/bash");

        var text = await File.ReadAllTextAsync(profile);
        Assert.StartsWith(original + "\n\n" + Begin + "\n", text);
        if (OperatingSystem.IsMacOS())
            Assert.False(File.Exists(Path.Combine(home, ".bash_profile")), "an existing login file is used, a new one would hide it");
        Assert.False(File.Exists(Path.Combine(home, ".zshrc")), "the other shells' files are not touched");
        Assert.Contains($". \"{Path.Combine(Completions, "tasker.bash")}\"", text);

        // Подключение работает: после чтения файла bash знает функцию дополнения.
        var check = await Shell("/bin/bash", ["-c", $"source \"$HOME/{fileName}\"; complete -p tasker"], home);
        Assert.True(check.Code == 0, check.Err);
        Assert.Contains("_tasker", check.Out);

        // Блок поменяли (старая версия установщика), а после него — своя строка: блок обновляется на месте, остальное цело.
        var edited = text.Replace("tasker.bash", "old-name.bash") + "# after\nexport B=2\n";
        await File.WriteAllTextAsync(profile, edited);

        var upgraded = await Install(home, "/bin/bash");

        var after = await File.ReadAllTextAsync(profile);
        Assert.Equal(1, Count(after, Begin));
        Assert.Equal(1, Count(after, End));
        Assert.DoesNotContain("old-name.bash", after);
        Assert.Contains("tasker.bash", BlockOf(after));
        Assert.StartsWith(original + "\n\n" + Begin, after);
        Assert.EndsWith(End + "\n# after\nexport B=2\n", after);
        Assert.Contains("changed", upgraded.Out);
        // Копия — по-прежнему файл до первой правки.
        Assert.Equal(original, await File.ReadAllTextAsync(profile + ".tasker-backup"));

        // Удаление другой установки (другой --prefix) чужой блок не трогает.
        var other = await Run(["--uninstall", "--prefix", Path.Combine(_root, "elsewhere")], home, "/bin/bash");
        Assert.Equal(0, other.Code);
        Assert.Equal(after, await File.ReadAllTextAsync(profile));

        var removed = await Run(["--uninstall", "--prefix", Prefix], home, "/bin/bash");
        Assert.Equal(0, removed.Code);
        Assert.Equal(original + "\n# after\nexport B=2\n", await File.ReadAllTextAsync(profile));
    }

    [UnixFact]
    public async Task No_completion_leaves_the_shell_files_alone_and_a_broken_block_is_not_touched()
    {
        var home = Home();
        var zshrc = Path.Combine(home, ".zshrc");

        // --no-completion: скрипты есть, файла оболочки нет и не появилось.
        var skipped = await Install(home, "/bin/zsh", "--no-completion");
        Assert.True(File.Exists(Path.Combine(Completions, "_tasker")));
        Assert.False(File.Exists(zshrc));
        Assert.Contains("--no-completion", skipped.Out);
        Assert.DoesNotContain(".tasker-backup", skipped.Out);

        // Остался один маркер (правили руками): файл не трогаем и говорим об этом.
        var broken = $"# mine\n{Begin}\nsomething\n";
        await File.WriteAllTextAsync(zshrc, broken);
        var result = await Install(home);
        Assert.Equal(broken, await File.ReadAllTextAsync(zshrc));
        Assert.Contains("skipped", result.Out);
        Assert.False(File.Exists(zshrc + ".tasker-backup"));
    }

    [UnixFact]
    public async Task An_unsupported_shell_is_reported_and_nothing_is_connected()
    {
        var home = Home();

        var result = await Install(home, "/opt/homebrew/bin/fish");

        Assert.Contains("fish", result.Out);
        Assert.Contains("not supported", result.Out);
        Assert.Empty(Directory.GetFiles(home, ".*").Where(x => !x.EndsWith(".zprofile")));
        Assert.True(File.Exists(Path.Combine(Completions, "_tasker")));
    }

    private static async Task<CliResult> Shell(string shell, string[] args, string home, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["HOME"] = home;
        if (workingDirectory != null)
            info.WorkingDirectory = workingDirectory;
        foreach (var argument in args)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await output, await error);
    }

    private static async Task<CliResult> TaskerCommand(string executable, params string[] args)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        Tasker.Global.AppEnvironment.Apply(info);
        foreach (var argument in args)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await output, await error);
    }
}
