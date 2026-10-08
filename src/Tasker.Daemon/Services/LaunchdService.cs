using System.Security;
using System.Text;
using Tasker.Global;

namespace Tasker.Daemon.Services;

/// <summary>
/// macOS: агент запуска пользователя <c>~/Library/LaunchAgents/com.tasker.mcp.plist</c>. RunAtLoad — стартует при входе
/// в систему, KeepAlive — launchd перезапускает демон, если он упал. <c>TASKER_SERVICE_DIR</c> и <c>TASKER_SERVICE_LABEL</c>
/// переопределяют каталог и имя (тесты, вторая установка).
/// </summary>
public sealed class LaunchdService(IProcessRunner runner, string? directory = null, string? label = null) : IServiceManager
{
    private readonly string _directory = directory
        ?? AppEnvironment.Get("TASKER_SERVICE_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");

    public string Label { get; } = label ?? AppEnvironment.Get("TASKER_SERVICE_LABEL") ?? "com.tasker.mcp";

    public string Name => "launchd";

    public string PlistPath => Path.Combine(_directory, Label + ".plist");

    public bool IsEnabled => File.Exists(PlistPath);

    private static string Domain => $"gui/{GetUserId()}";

    private string Target => $"{Domain}/{Label}";

    public async Task Enable()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(Global.AppDirectories.Logs);
        await File.WriteAllTextAsync(PlistPath, Plist());

        // Уже загружена старая версия описания — перезагружаем.
        await runner.Run("launchctl", "bootout", Target);
        Check(await runner.Run("launchctl", "bootstrap", Domain, PlistPath), "launchctl bootstrap");
    }

    public async Task Disable()
    {
        await runner.Run("launchctl", "bootout", Target);
        if (File.Exists(PlistPath))
            File.Delete(PlistPath);
    }

    public async Task Start()
    {
        if (!IsEnabled)
            throw new ServiceException("Autostart is not enabled: run 'tasker mcp autostart enable'");

        if (await IsLoaded())
            Check(await runner.Run("launchctl", "kickstart", Target), "launchctl kickstart");
        else
            Check(await runner.Run("launchctl", "bootstrap", Domain, PlistPath), "launchctl bootstrap");
    }

    // bootout выгружает службу (демон получает SIGTERM), описание остаётся: при следующем входе она загрузится снова.
    public async Task Stop() => await runner.Run("launchctl", "bootout", Target);

    public async Task<bool> IsLoaded() => (await runner.Run("launchctl", "print", Target)).Success;

    internal string Plist()
    {
        var command = ServiceManagers.DaemonCommand();
        var text = new StringBuilder();
        text.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        text.AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""");
        text.AppendLine("""<plist version="1.0">""");
        text.AppendLine("<dict>");
        text.AppendLine($"    <key>Label</key><string>{Escape(Label)}</string>");
        text.AppendLine("    <key>ProgramArguments</key>");
        text.AppendLine("    <array>");
        foreach (var argument in command)
            text.AppendLine($"        <string>{Escape(argument)}</string>");
        text.AppendLine("    </array>");

        var environment = ServiceManagers.Environment();
        if (environment.Count > 0)
        {
            text.AppendLine("    <key>EnvironmentVariables</key>");
            text.AppendLine("    <dict>");
            foreach (var (key, value) in environment)
                text.AppendLine($"        <key>{Escape(key)}</key><string>{Escape(value)}</string>");
            text.AppendLine("    </dict>");
        }

        var log = Path.Combine(Global.AppDirectories.Logs, "mcp-launchd.log");
        text.AppendLine("    <key>RunAtLoad</key><true/>");
        text.AppendLine("    <key>KeepAlive</key><true/>");
        text.AppendLine("    <key>ProcessType</key><string>Background</string>");
        text.AppendLine($"    <key>StandardOutPath</key><string>{Escape(log)}</string>");
        text.AppendLine($"    <key>StandardErrorPath</key><string>{Escape(log)}</string>");
        text.AppendLine("</dict>");
        text.AppendLine("</plist>");
        return text.ToString();
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? "";

    private static void Check(ProcessResult result, string what)
    {
        if (!result.Success)
            throw new ServiceException($"{what} failed ({result.ExitCode}): {(result.Error + result.Output).Trim()}");
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUserId();
}
