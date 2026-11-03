using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace Tasker.Daemon;

/// <summary>
/// Остановка демона без сигналов (их в Windows нет): именованное событие. Супервизор (или одиночный демон) создаёт его и ждёт,
/// <c>tasker mcp stop</c> и <c>tasker mcp run</c> по Ctrl+C его взводят. Запасной путь рядом с HTTP-запросом <c>/daemon/stop</c>:
/// работает, даже когда рабочий процесс не отвечает. Событие в пространстве имён сеанса (<c>Local\</c>); из другого сеанса его не видно,
/// там остаётся HTTP. На macOS и Linux не используется (там SIGTERM).
/// </summary>
public static class ShutdownSignal
{
    /// <summary>Имя события для данного каталога данных: разные TASKER_HOME (тесты, несколько профилей) не мешают друг другу.</summary>
    public static string EventName(string daemonDirectory)
    {
        var normalized = Path.GetFullPath(daemonDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        return "Local\\tasker-mcp-stop-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
    }

    public static string CurrentName => EventName(Tasker.Global.AppDirectories.Daemon);

    /// <summary>Начинает ждать события; <paramref name="onSignal"/> вызывается один раз. null — на этой системе события не используются.</summary>
    public static IDisposable? Listen(Action onSignal) =>
        OperatingSystem.IsWindows() ? WindowsShutdownEvent.Listen(CurrentName, onSignal) : null;

    /// <summary>Взводит событие работающего демона.</summary>
    /// <returns>false — события нет (демон не работает, он в другом сеансе) или система без событий.</returns>
    public static bool Raise() => OperatingSystem.IsWindows() && WindowsShutdownEvent.Raise(CurrentName);
}

[SupportedOSPlatform("windows")]
internal static class WindowsShutdownEvent
{
    public static IDisposable Listen(string name, Action onSignal)
    {
        var handle = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        var registered = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => onSignal(), null, Timeout.Infinite, executeOnlyOnce: true);
        return new Registration(handle, registered);
    }

    public static bool Raise(string name)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(name);
            return handle.Set();
        }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private sealed class Registration(EventWaitHandle handle, RegisteredWaitHandle registered) : IDisposable
    {
        public void Dispose()
        {
            registered.Unregister(null);
            handle.Dispose();
        }
    }
}
