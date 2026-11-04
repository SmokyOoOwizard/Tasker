using System.Diagnostics;

namespace Tasker.Daemon;

/// <summary>
/// Тот ли это процесс, который записал <c>daemon.json</c>. Номер процесса после его смерти получает другая программа,
/// поэтому перед тем, как снять процесс «по файлу», сверяем ещё и время старта.
/// </summary>
public static class DaemonProcessIdentity
{
    /// <summary>Демон записывает время старта уже после запуска процесса: между ними проходит секунды, но не минуты.</summary>
    public static readonly TimeSpan MaxStartupGap = TimeSpan.FromMinutes(5);

    /// <summary>Часы процесса и записанное время могут разойтись на доли секунды.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(2);

    /// <param name="processStart">Когда процесс с этим номером запустила система.</param>
    /// <param name="recorded">Время старта демона из <c>daemon.json</c>.</param>
    public static bool StartMatches(DateTimeOffset processStart, DateTimeOffset recorded)
    {
        var gap = recorded - processStart;
        return gap >= -ClockTolerance && gap <= MaxStartupGap;
    }

    /// <returns>Процесс демона; null — такого процесса нет или это другой (номер достался чужой программе). Освободить — <c>Dispose</c>.</returns>
    public static Process? Find(DaemonInfo info)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(info.Pid);
            if (process.HasExited || !StartMatches(new DateTimeOffset(process.StartTime), info.StartedAt))
            {
                process.Dispose();
                return null;
            }

            return process;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process?.Dispose();
            return null;
        }
    }
}
