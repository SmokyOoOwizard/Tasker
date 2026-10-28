using System.Text.Json;
using Tasker.Core;
using Tasker.Global;

namespace Tasker.Daemon;

/// <summary>Как подключиться к запущенному демону: процесс, порт и секрет управления. Файл читает только владелец.</summary>
public sealed record DaemonInfo(int Pid, int Port, string Token, DateTimeOffset StartedAt);

/// <summary>Файлы состояния демона в <see cref="AppDirectories.Daemon"/>.</summary>
public static class DaemonFiles
{
    private static readonly JsonSerializerOptions Json = new(TaskerJson.Options) { WriteIndented = true };

    /// <summary>Держится открытым на всё время жизни демона: по нему видно, работает ли он, и второй экземпляр не запустится.</summary>
    public static string InstanceLock => Path.Combine(AppDirectories.Daemon, "daemon.lock");

    public static string InfoFile => Path.Combine(AppDirectories.Daemon, "daemon.json");

    /// <summary>Берёт «единственный экземпляр»; null — демон уже работает. Освободить — <c>Dispose</c>.</summary>
    public static FileStream? TryHold()
    {
        Directory.CreateDirectory(AppDirectories.Daemon);
        try
        {
            return new FileStream(InstanceLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Работает ли демон: блокировка единственного экземпляра занята. Упавший демон блокировку не оставляет.</summary>
    public static bool IsRunning()
    {
        using var probe = TryHold();
        return probe == null;
    }

    public static DaemonInfo? ReadInfo()
    {
        try
        {
            return JsonSerializer.Deserialize<DaemonInfo>(File.ReadAllText(InfoFile), Json);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void WriteInfo(DaemonInfo info)
    {
        Directory.CreateDirectory(AppDirectories.Daemon);
        var temp = InfoFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(info, Json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, InfoFile, overwrite: true);
    }

    /// <summary>Убирает файл, только если он про этот процесс: новый демон мог уже записать свой.</summary>
    public static void DeleteInfo(int pid)
    {
        if (ReadInfo()?.Pid == pid)
            File.Delete(InfoFile);
    }
}
