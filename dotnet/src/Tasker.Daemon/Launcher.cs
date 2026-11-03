using System.Diagnostics;
using System.Reflection;
using Tasker.Global;

namespace Tasker.Daemon;

/// <summary>Как запустить сам <c>tasker</c>: исполняемый файл и, если он запущен через <c>dotnet</c>, путь к dll.</summary>
public static class Launcher
{
    /// <summary>Команда и её начальные аргументы (для <c>dotnet tasker.dll</c> — путь к dll).</summary>
    public static (string File, string[] Arguments) Command()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the path of the running program");
        if (!Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (path, []);

        var dll = Assembly.GetEntryAssembly()?.Location;
        return dll is { Length: > 0 } ? (path, [dll]) : throw new InvalidOperationException("Cannot find tasker.dll");
    }

    /// <summary>Имя программы демона (<c>tasker-mcpd</c>) — она лежит рядом с <c>tasker</c>.</summary>
    public const string DaemonName = "tasker-mcpd";

    /// <summary>
    /// Команда запуска демона: программа <c>tasker-mcpd</c> из того же каталога, что и текущая (для <c>dotnet tasker.dll</c> —
    /// <c>tasker-mcpd.dll</c> рядом с dll). Ссылка (<c>~/.local/bin/tasker</c>) раскрывается до настоящего пути.
    /// </summary>
    /// <exception cref="DaemonStartException">Демона рядом нет (установлена только консольная утилита).</exception>
    public static (string File, string[] Arguments) DaemonCommand() =>
        DaemonCommand(
            Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the path of the running program"),
            Assembly.GetEntryAssembly()?.Location);

    /// <param name="processPath">Исполняемый файл текущего процесса.</param>
    /// <param name="entryAssembly">Главная сборка (нужна, когда процесс — <c>dotnet</c>).</param>
    internal static (string File, string[] Arguments) DaemonCommand(string processPath, string? entryAssembly)
    {
        var path = processPath;
        var viaDotnet = Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        var current = viaDotnet ? entryAssembly : path;
        if (string.IsNullOrEmpty(current))
            throw new InvalidOperationException("Cannot find the directory of the running program");
        var directory = Path.GetDirectoryName(new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current)!;

        var program = viaDotnet
            ? Path.Combine(directory, DaemonName + ".dll")
            : Path.Combine(directory, DaemonName + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!File.Exists(program))
        {
            throw new DaemonStartException(
                $"The MCP server program ({DaemonName}) is not installed next to tasker (looked in {directory}). " +
                "Reinstall tasker with the MCP server: scripts/install.sh (macOS, Linux) or scripts/install.ps1 (Windows), without --no-daemon");
        }

        return viaDotnet ? (path, [program]) : (program, []);
    }

    /// <summary>
    /// Запускает демон в фоне и не ждёт его. На Unix процесс отвязан от терминала: stdio уходит в /dev/null,
    /// а новый сеанс он создаёт сам (<c>tasker-mcpd --detached</c>). На Windows — без окна, в своей группе процессов
    /// и без унаследованных дескрипторов (<see cref="WindowsDetachedProcess"/>).
    /// </summary>
    public static Process SpawnDaemon()
    {
        var (file, arguments) = DaemonCommand();
        if (OperatingSystem.IsWindows())
            return WindowsDetachedProcess.Start(file, [.. arguments, "--detached"]);

        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        AppEnvironment.Apply(info);
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("exec \"$0\" \"$@\" >/dev/null 2>&1 </dev/null");
        info.ArgumentList.Add(file);
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        foreach (var argument in new[] { "--detached" })
            info.ArgumentList.Add(argument);

        return Process.Start(info) ?? throw new InvalidOperationException("Cannot start the MCP server process");
    }
}
