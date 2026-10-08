using System.Diagnostics;
using Tasker.Global;

namespace Tasker.Daemon.Services;

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Запуск системных команд (launchctl, systemctl). Интерфейс — чтобы проверять команды без настоящего launchd.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> Run(string file, params string[] arguments);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> Run(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        AppEnvironment.Apply(info);
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new ProcessResult(process.ExitCode, await output, await error);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return new ProcessResult(127, "", $"{file}: {e.Message}");
        }
    }
}

/// <summary>Служба, которая запускает и поддерживает демон: launchd на macOS, systemd (пользовательский) на Linux, Планировщик заданий на Windows.</summary>
public interface IServiceManager
{
    string Name { get; }

    /// <summary>Автозапуск включён: описание службы установлено.</summary>
    bool IsEnabled { get; }

    /// <summary>Установить описание службы и запустить её; при входе в систему она стартует сама и перезапускается при падении.</summary>
    Task Enable();

    /// <summary>Остановить службу и убрать описание.</summary>
    Task Disable();

    /// <summary>Запустить службу сейчас (автозапуск при этом остаётся включённым).</summary>
    Task Start();

    /// <summary>Остановить службу до следующего входа в систему; автозапуск остаётся включённым.</summary>
    Task Stop();

    /// <summary>Служба запущена (загружена в систему).</summary>
    Task<bool> IsLoaded();
}

public class ServiceException(string message) : Exception(message);

public static class ServiceManagers
{
    /// <summary>Служба для текущей системы; на остальных — недоступна.</summary>
    public static IServiceManager Create(IProcessRunner? runner = null)
    {
        runner ??= new SystemProcessRunner();
        if (OperatingSystem.IsMacOS())
            return new LaunchdService(runner);
        if (OperatingSystem.IsLinux())
            return new SystemdService(runner);
        if (OperatingSystem.IsWindows())
            return new WindowsTaskService(runner);
        return new UnsupportedService();
    }

    /// <summary>Аргументы запуска демона службой: программа демона (<c>tasker-mcpd</c>) рядом с <c>tasker</c>, с <c>--detached</c>.</summary>
    internal static string[] DaemonCommand()
    {
        var (file, arguments) = Launcher.DaemonCommand();
        return [file, .. arguments, "--detached"];
    }

    /// <summary>Переменные окружения, которые служба должна получить: каталог данных, если он переопределён.</summary>
    internal static IReadOnlyDictionary<string, string> Environment()
    {
        var result = new Dictionary<string, string>();
        if (AppEnvironment.Get(AppDirectories.HomeVariable) is { Length: > 0 } home)
            result[AppDirectories.HomeVariable] = home;
        return result;
    }
}

internal sealed class UnsupportedService : IServiceManager
{
    public string Name => "none";
    public bool IsEnabled => false;

    public Task Enable() => throw Unsupported();
    public Task Disable() => throw Unsupported();
    public Task Start() => throw Unsupported();
    public Task Stop() => throw Unsupported();
    public Task<bool> IsLoaded() => Task.FromResult(false);

    private static ServiceException Unsupported() =>
        new("Autostart is supported on macOS (launchd), Linux (systemd) and Windows (Task Scheduler) only; on this system run 'tasker mcp run' yourself");
}
