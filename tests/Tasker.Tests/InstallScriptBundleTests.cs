using Xunit;

namespace Tasker.Tests;

/// <summary>
/// <c>scripts/install.sh</c> с готовой сборкой: каталог, архив, адрес. Вместо настоящей сборки — подставные программы, которые пишут
/// свои вызовы в файл: так проверяются порядок и условия вызовов (автозапуск, <c>mcp upgrade</c>, отказ от молчаливого перезапуска),
/// на которые настоящий демон тестов не нужен и которые не должны трогать настоящую службу автозапуска.
/// </summary>
public partial class InstallScriptTests
{
    /// <summary>Платформа этой машины так, как её называет релиз (RID).</summary>
    private static string HostRid =>
        (OperatingSystem.IsMacOS() ? "osx-" : "linux-")
        + (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");

    private string StubLog => Path.Combine(_root, "stub.log");

    private const string StubTasker = """
        #!/bin/sh
        echo "$@" >> "$STUB_LOG"
        case "$1" in
          --version) echo 9.9.9 ;;
          mcp)
            case "$2" in
              status) exit "${STUB_RUNNING_EXIT:-3}" ;;
              autostart)
                case "$3" in
                  status) echo "{\"manager\":\"stub\",\"enabled\":${STUB_AUTOSTART:-false},\"loaded\":true}" ;;
                  enable) echo "autostart enable output"; exit "${STUB_ENABLE_EXIT:-0}" ;;
                esac ;;
              upgrade)
                echo "upgrade says: switched"
                if [ "${STUB_UPGRADE_EXIT:-0}" != 0 ]; then echo "upgrade failed: new build did not start" >&2; exit "$STUB_UPGRADE_EXIT"; fi ;;
            esac ;;
          completion)
            case "$2" in
              zsh) echo "#compdef tasker" ;;
              bash) echo "complete -o default -F _tasker tasker" ;;
            esac ;;
        esac
        exit 0
        """;

    /// <summary>
    /// Каталог релиза <c>name/{app/tasker, app/tasker-mcpd, release.txt}</c> с подставными программами. Подставной tasker пишет свои
    /// аргументы в <see cref="StubLog"/>, а ответы берёт из переменных STUB_* (<see cref="StubEnvironment"/>).
    /// </summary>
    private string Bundle(string name = "release", string? rid = null)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        WriteExecutable(Path.Combine(app, "tasker"), StubTasker);
        WriteExecutable(Path.Combine(app, "tasker-mcpd"), "#!/bin/sh\nexit 0\n");
        File.WriteAllText(Path.Combine(root, "release.txt"), $"version=9.9.9\nrid={rid ?? HostRid}\ncommit=test\n");
        return root;
    }

    private static void WriteExecutable(string path, string text)
    {
        File.WriteAllText(path, text.Replace("\r\n", "\n") + (text.EndsWith('\n') ? "" : "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private Dictionary<string, string> StubEnvironment(params (string Name, string Value)[] more)
    {
        var result = new Dictionary<string, string> { ["STUB_LOG"] = StubLog };
        foreach (var (name, value) in more)
            result[name] = value;
        return result;
    }

    /// <summary>Вызовы подставного tasker по порядку (по строке на вызов, аргументы через пробел).</summary>
    private string[] StubCalls() => File.Exists(StubLog) ? File.ReadAllLines(StubLog) : [];

    /// <summary>Архив релиза <c>tasker-9.9.9-rid.tar.gz</c> с каталогом <c>tasker-9.9.9-rid/</c> внутри, как делает scripts/release.sh.</summary>
    private async Task<string> Archive(string bundle, string rid)
    {
        var name = $"tasker-9.9.9-{rid}";
        var parent = Directory.CreateDirectory(Path.Combine(_root, "archive-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        Directory.Move(bundle, Path.Combine(parent, name));
        var archive = Path.Combine(_root, name + ".tar.gz");
        var tar = await Shell("tar", ["-czf", archive, "-C", parent, name], _root);
        Assert.True(tar.Code == 0, tar.Err);
        return archive;
    }

    [UnixFact]
    public async Task A_ready_folder_is_installed_without_dotnet_and_autostart_is_enabled()
    {
        var home = Home();
        var result = await Run(["--prefix", Prefix, "--from", Bundle()], home, environment: StubEnvironment());

        Assert.True(result.Code == 0, result.Out + result.Err);
        Assert.Contains("Installed tasker 9.9.9", result.Out);
        var app = Path.Combine(Prefix, "share", "tasker", "app");
        Assert.True(File.Exists(Path.Combine(app, "tasker")) && File.Exists(Path.Combine(app, "tasker-mcpd")));
        Assert.Equal(Path.Combine(app, "tasker"), new FileInfo(Path.Combine(Prefix, "bin", "tasker")).LinkTarget);
        Assert.True(File.Exists(Path.Combine(Completions, "_tasker")) && File.Exists(Path.Combine(Completions, "tasker.bash")));
        Assert.Contains(Begin, await File.ReadAllTextAsync(Path.Combine(home, ".zshrc")));

        // Автозапуск включается; демон не работал (код 3) — переводить на новую сборку нечего.
        var calls = StubCalls();
        Assert.Contains("mcp autostart status --json", calls);
        Assert.Contains("mcp autostart enable", calls);
        Assert.DoesNotContain(calls, x => x.StartsWith("mcp upgrade"));
        Assert.Contains("autostart enable output", result.Out);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(Prefix, "share", "tasker"), ".staging*"));
    }

    [UnixFact]
    public async Task No_autostart_does_not_touch_the_service_and_an_enabled_one_is_left_alone()
    {
        var home = Home();
        var skipped = await Run(["--prefix", Prefix, "--from", Bundle(), "--no-autostart"], home, environment: StubEnvironment());
        Assert.True(skipped.Code == 0, skipped.Out + skipped.Err);
        Assert.DoesNotContain(StubCalls(), x => x.StartsWith("mcp autostart"));

        File.Delete(StubLog);
        var enabled = await Run(["--prefix", Prefix, "--from", Bundle("second")], home, environment: StubEnvironment(("STUB_AUTOSTART", "true")));
        Assert.True(enabled.Code == 0, enabled.Out + enabled.Err);
        Assert.DoesNotContain("mcp autostart enable", StubCalls());
        Assert.Contains("already enabled", enabled.Out);
    }

    [UnixFact]
    public async Task A_failed_autostart_is_a_warning_with_a_hint_and_the_install_stays()
    {
        var result = await Run(["--prefix", Prefix, "--from", Bundle()], Home(), environment: StubEnvironment(("STUB_ENABLE_EXIT", "1")));

        Assert.Equal(0, result.Code);
        Assert.Contains("autostart was not enabled", result.Err);
        Assert.Contains("tasker mcp autostart enable", result.Err);
        Assert.True(File.Exists(Path.Combine(Prefix, "share", "tasker", "app", "tasker")));
    }

    [UnixFact]
    public async Task A_running_daemon_is_switched_with_upgrade_and_never_restarted_silently()
    {
        var home = Home();

        // Автозапуск уже включён, демон работает: замена на лету, без перезапуска.
        var ok = await Run(["--prefix", Prefix, "--from", Bundle()], home, environment: StubEnvironment(("STUB_RUNNING_EXIT", "0"), ("STUB_AUTOSTART", "true")));
        Assert.True(ok.Code == 0, ok.Out + ok.Err);
        Assert.Contains("mcp upgrade", StubCalls());
        Assert.DoesNotContain("mcp upgrade --restart", StubCalls());
        Assert.DoesNotContain("mcp autostart enable", StubCalls());
        Assert.Contains("without downtime", ok.Out);

        // Замена не удалась: установка осталась, перезапуска нет, причина и подсказка напечатаны.
        File.Delete(StubLog);
        var failed = await Run(["--prefix", Prefix, "--from", Bundle("again"), "--no-autostart"], home,
            environment: StubEnvironment(("STUB_RUNNING_EXIT", "0"), ("STUB_UPGRADE_EXIT", "1")));
        Assert.Equal(0, failed.Code);
        Assert.DoesNotContain("mcp upgrade --restart", StubCalls());
        Assert.Contains("upgrade failed: new build did not start", failed.Err);
        Assert.Contains("it keeps running with the previous build", failed.Err);
        Assert.Contains("tasker mcp upgrade --restart", failed.Err);

        // --restart: перезапуск по просьбе.
        File.Delete(StubLog);
        var restart = await Run(["--prefix", Prefix, "--from", Bundle("third"), "--no-autostart", "--restart"], home,
            environment: StubEnvironment(("STUB_RUNNING_EXIT", "0")));
        Assert.True(restart.Code == 0, restart.Out + restart.Err);
        Assert.Contains("mcp upgrade --restart", StubCalls());
    }

    [UnixFact]
    public async Task An_archive_is_unpacked_and_installed_and_a_build_for_another_platform_is_refused()
    {
        var archive = await Archive(Bundle(), HostRid);
        var result = await Run(["--prefix", Prefix, "--from", archive, "--no-autostart"], Home(), environment: StubEnvironment());
        Assert.True(result.Code == 0, result.Out + result.Err);
        Assert.Contains("Installed tasker 9.9.9", result.Out);
        Assert.True(File.Exists(Path.Combine(Prefix, "share", "tasker", "app", "tasker")));

        // Сборка не для этой машины: понятная ошибка, установка не тронута.
        var other = HostRid == "linux-x64" ? "osx-arm64" : "linux-x64";
        var otherPrefix = Path.Combine(_root, "other-prefix");
        var foreign = await Run(["--prefix", otherPrefix, "--from", Bundle("foreign", other), "--no-autostart"], Home(), environment: StubEnvironment());
        Assert.Equal(1, foreign.Code);
        Assert.Contains($"this build is for {other}, but this machine is {HostRid}", foreign.Err);
        Assert.False(Directory.Exists(Path.Combine(otherPrefix, "share", "tasker", "app")));

        // Windows-архив на macOS и Linux — подсказка про .tar.gz.
        var zip = Path.Combine(_root, "tasker-9.9.9-win-x64.zip");
        await File.WriteAllTextAsync(zip, "x");
        var windows = await Run(["--prefix", Prefix, "--from", zip], Home(), environment: StubEnvironment());
        Assert.Equal(1, windows.Code);
        Assert.Contains("Windows archive", windows.Err);
    }

    [UnixFact]
    public async Task A_download_is_checked_against_its_sha256()
    {
        var archive = await Archive(Bundle(), HostRid);
        var url = new Uri(archive).AbsoluteUri;   // file:// — curl скачивает его так же, как адрес в сети
        var home = Home();
        var sum = await Shell(OperatingSystem.IsMacOS() ? "shasum" : "sha256sum", OperatingSystem.IsMacOS() ? ["-a", "256", archive] : [archive], _root);
        var good = sum.Out.Split(' ')[0];
        var appDir = Path.Combine(Prefix, "share", "tasker", "app");

        // Нет файла .sha256: без --no-verify установки нет.
        var missing = await Run(["--prefix", Prefix, "--url", url, "--no-autostart"], home, environment: StubEnvironment());
        Assert.Equal(1, missing.Code);
        Assert.Contains(".sha256 to check the archive", missing.Err);
        Assert.False(Directory.Exists(appDir));

        // Неверная сумма — отказ.
        await File.WriteAllTextAsync(archive + ".sha256", new string('0', 64) + "  " + Path.GetFileName(archive) + "\n");
        var wrong = await Run(["--prefix", Prefix, "--url", url, "--no-autostart"], home, environment: StubEnvironment());
        Assert.Equal(1, wrong.Code);
        Assert.Contains("checksum of the download does not match", wrong.Err);
        Assert.False(Directory.Exists(appDir));

        // Верная — установка.
        await File.WriteAllTextAsync(archive + ".sha256", good + "  " + Path.GetFileName(archive) + "\n");
        var right = await Run(["--prefix", Prefix, "--url", url, "--no-autostart"], home, environment: StubEnvironment());
        Assert.True(right.Code == 0, right.Out + right.Err);
        Assert.Contains("SHA256 checked", right.Out);
        Assert.True(File.Exists(Path.Combine(appDir, "tasker")));

        // --no-verify: без файла суммы можно, с предупреждением.
        File.Delete(archive + ".sha256");
        var skipped = await Run(["--prefix", Path.Combine(_root, "p2"), "--url", url, "--no-autostart", "--no-verify"], home, environment: StubEnvironment());
        Assert.True(skipped.Code == 0, skipped.Out + skipped.Err);
        Assert.Contains("without the checksum check", skipped.Err);
    }

    [UnixFact]
    public async Task Without_arguments_the_release_next_to_the_script_is_installed_and_a_repository_asks_for_from_source()
    {
        // Релиз: install.sh лежит рядом с app/ (как в архиве).
        var bundle = Bundle();
        var script = Path.Combine(bundle, "install.sh");
        File.Copy(Script(), script);
        var result = await Run(["--prefix", Prefix, "--no-autostart"], Home(), environment: StubEnvironment(), script: script);
        Assert.True(result.Code == 0, result.Out + result.Err);
        Assert.Contains("Installed tasker 9.9.9", result.Out);

        // Репозиторий (скрипт в scripts/, исходники рядом): сборка только с --from-source.
        var repo = await Run(["--prefix", Path.Combine(_root, "p3")], Home(), environment: StubEnvironment());
        Assert.Equal(1, repo.Code);
        Assert.Contains("--from-source", repo.Err);
        Assert.False(Directory.Exists(Path.Combine(_root, "p3", "share", "tasker", "app")));
    }

    [PlatformTheory(TestPlatform.Unix)]
    [InlineData("--from x --url y", "choose one of --from, --url, --from-source")]
    [InlineData("--from-source --from x", "choose one of --from, --url, --from-source")]
    [InlineData("--no-daemon", "--no-daemon works with --from-source only")]
    [InlineData("--framework-dependent", "--framework-dependent works with --from-source only")]
    public async Task Conflicting_options_are_rejected_with_the_usage(string arguments, string message)
    {
        var result = await Run(arguments.Split(' '));

        Assert.Equal(2, result.Code);
        Assert.Contains(message, result.Err);
        Assert.Contains("Usage:", result.Err);
    }

    [UnixFact]
    public async Task A_folder_without_the_daemon_or_without_tasker_is_refused_before_anything_is_installed()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_root, "empty-folder")).FullName;
        var none = await Run(["--prefix", Prefix, "--from", empty], Home(), environment: StubEnvironment());
        Assert.Equal(1, none.Code);
        Assert.Contains("no tasker program in", none.Err);

        var bundle = Bundle();
        File.Delete(Path.Combine(bundle, "app", "tasker-mcpd"));
        var noDaemon = await Run(["--prefix", Prefix, "--from", bundle], Home(), environment: StubEnvironment());
        Assert.Equal(1, noDaemon.Code);
        Assert.Contains("has no tasker-mcpd", noDaemon.Err);
        Assert.False(Directory.Exists(Path.Combine(Prefix, "share", "tasker", "app")));
    }

    [UnixFact]
    public async Task A_broken_build_does_not_replace_the_working_installation()
    {
        var home = Home();
        var good = await Run(["--prefix", Prefix, "--from", Bundle(), "--no-autostart"], home, environment: StubEnvironment());
        Assert.True(good.Code == 0, good.Out + good.Err);
        var app = Path.Combine(Prefix, "share", "tasker", "app");

        // Новая сборка, которая не запускается (дымовая проверка падает): прежняя остаётся.
        var broken = Bundle("broken");
        WriteExecutable(Path.Combine(broken, "app", "tasker"), "#!/bin/sh\nexit 1\n");
        var result = await Run(["--prefix", Prefix, "--from", broken, "--no-autostart"], home, environment: StubEnvironment());

        Assert.Equal(1, result.Code);
        Assert.Contains("smoke test failed (nothing was installed)", result.Err);
        Assert.Contains("STUB_LOG", await File.ReadAllTextAsync(Path.Combine(app, "tasker")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(Prefix, "share", "tasker"), ".staging*"));
    }

    [UnixFact]
    public async Task Uninstall_switches_autostart_off_and_stops_the_daemon_first()
    {
        var home = Home();
        await Run(["--prefix", Prefix, "--from", Bundle(), "--no-autostart"], home, environment: StubEnvironment());
        File.Delete(StubLog);

        var result = await Run(["--uninstall", "--prefix", Prefix], home, environment: StubEnvironment());

        Assert.Equal(0, result.Code);
        Assert.Equal(["mcp autostart disable", "mcp stop"], StubCalls());
        Assert.False(Directory.Exists(Path.Combine(Prefix, "share", "tasker", "app")));
        Assert.Contains("Your data in", result.Out);
    }

    [UnixFact]
    public async Task The_path_hint_names_the_profile_of_the_shell_and_add_to_path_writes_it_once()
    {
        // bash: login-файл macOS или ~/.profile (Linux); строка не повторяется при повторной установке.
        var home = Home();
        var file = OperatingSystem.IsMacOS() ? ".bash_profile" : ".profile";
        var hint = await Run(["--prefix", Prefix, "--from", Bundle(), "--no-autostart", "--no-completion"], home, "/bin/bash", StubEnvironment());
        Assert.Contains($">> ~/{file}", hint.Out);
        Assert.False(File.Exists(Path.Combine(home, file)));

        for (var i = 0; i < 2; i++)
            await Run(["--prefix", Prefix, "--from", Bundle($"again{i}"), "--no-autostart", "--no-completion", "--add-to-path"], home, "/bin/bash", StubEnvironment());
        var text = await File.ReadAllTextAsync(Path.Combine(home, file));
        Assert.Equal(1, Count(text, "# tasker"));
        Assert.Contains($"export PATH=\"{Path.Combine(Prefix, "bin")}:$PATH\"", text);
    }
}
