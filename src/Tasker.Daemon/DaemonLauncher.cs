using System.Diagnostics;
using System.Runtime.InteropServices;
using Tasker.Global;

namespace Tasker.Daemon;

/// <summary>
/// Запуск демона MCP так же, как <c>tasker mcp start</c>: программа <c>tasker-mcpd --detached</c> отдельным процессом в фоне
/// (без окна и без унаследованного вывода), тот же каталог данных (<c>daemon.json</c>, порт из <c>settings.json</c>), затем ожидание
/// готовности. Запустивший процесс демон не держит: демон работает и после его выхода.
/// </summary>
public static class DaemonLauncher
{
    /// <summary>Имя программы демона без расширения.</summary>
    public const string ProgramName = "tasker-mcpd";

    /// <summary>Сколько ждать, пока демон ответит и откроет области из настроек (как у <c>tasker mcp start</c>).</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(150);

    public static string FileName => OperatingSystem.IsWindows() ? ProgramName + ".exe" : ProgramName;

    /// <summary>
    /// Где искать <c>tasker-mcpd</c>, по порядку: рядом с консолью <c>tasker</c> из <c>PATH</c> (по настоящему пути, не по ссылке),
    /// в каталоге установки консоли по умолчанию, рядом с программой <paramref name="ownDirectory"/> (своя сборка или пакет десктопа).
    /// Демон установленной консоли главнее: его же обновляет <c>tasker upgrade</c>.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string ownDirectory, string? path = null, string? home = null)
    {
        var result = new List<string>();
        foreach (var directory in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var console = Path.Combine(directory, OperatingSystem.IsWindows() ? "tasker.exe" : "tasker");
            if (File.Exists(console) && Path.GetDirectoryName(RealPath(console)) is { } real)
                result.Add(Path.Combine(real, FileName));
        }

        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            if (local.Length > 0)
                result.Add(Path.Combine(local, "Programs", "Tasker", "app", FileName));
        }
        else if (home.Length > 0)
        {
            result.Add(Path.Combine(home, ".local", "share", "tasker", "app", FileName));
        }

        result.Add(Path.Combine(ownDirectory, FileName));
        return result.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }

    /// <summary>Первая существующая программа из <paramref name="candidates"/>; null — нигде нет.</summary>
    public static string? Find(IEnumerable<string> candidates) => candidates.FirstOrDefault(File.Exists);

    /// <summary>Программа демона для этого процесса: см. <see cref="Candidates"/>.</summary>
    public static string? Find() => Find(Candidates(AppContext.BaseDirectory));

    /// <summary>
    /// Запускает <paramref name="program"/> в фоне и ждёт готовности (<see cref="DaemonStatus.Settled"/>). Демон уже работает или
    /// стартует (блокировка экземпляра занята) — ничего не запускает, только ждёт.
    /// </summary>
    public static async Task<DaemonStart> EnsureRunning(string? program, CancellationToken ct = default)
    {
        if (await DaemonClient.GetStatus(ct) is { } running)
            return new DaemonStart(running, Started: false, null);

        Process? process = null;
        if (!DaemonFiles.IsRunning())
        {
            if (program == null)
            {
                return new DaemonStart(null, false,
                    $"{FileName} is not found: install the tasker command line tool (it brings the MCP server) or put {FileName} next to Tasker");
            }

            try
            {
                process = Spawn(program);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                return new DaemonStart(null, false, $"Cannot start the MCP server {program}: {e.Message}");
            }
        }

        using (process)
        {
            var deadline = DateTime.UtcNow + ReadyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await DaemonClient.GetStatus(ct) is { Settled: true } status)
                    return new DaemonStart(status, process != null, null);

                if (process is { HasExited: true })
                {
                    return new DaemonStart(null, false,
                        $"The MCP server exited right after start (code {process.ExitCode}): see the log in {AppDirectories.Logs}");
                }

                await Task.Delay(Poll, ct);
            }
        }

        return new DaemonStart(null, false, $"The MCP server did not answer in {ReadyTimeout.TotalSeconds:0} s: see the log in {AppDirectories.Logs}");
    }

    /// <summary>
    /// Процесс демона в фоне. Unix: через <c>/bin/sh</c> с вводом и выводом в <c>/dev/null</c> (сеанс демон создаёт сам).
    /// Windows: без окна, и свои стандартные дескрипторы ему не достаются — иначе канал вывода запустившей программы не закрылся бы,
    /// пока жив демон.
    /// </summary>
    private static Process Spawn(string program)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            NotInheritStandardHandles();
            info = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--detached");
        }
        else
        {
            info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("exec \"$0\" \"$@\" >/dev/null 2>&1 </dev/null");
            info.ArgumentList.Add(program);
            info.ArgumentList.Add("--detached");
        }

        info.WorkingDirectory = Path.GetDirectoryName(program) ?? "";
        AppEnvironment.Apply(info);
        return Process.Start(info) ?? throw new InvalidOperationException("the process did not start");
    }

    private static string RealPath(string file)
    {
        try
        {
            return File.ResolveLinkTarget(file, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(file);
        }
        catch (IOException)
        {
            return Path.GetFullPath(file);
        }
    }

    private static void NotInheritStandardHandles()
    {
        foreach (var which in new[] { StdInput, StdOutput, StdError })
        {
            var handle = GetStdHandle(which);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                SetHandleInformation(handle, HandleFlagInherit, 0);
        }
    }

    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;
    private const uint HandleFlagInherit = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}

/// <summary>Чем закончился запуск демона.</summary>
/// <param name="Status">Статус готового демона; null — не удалось (<paramref name="Error"/>).</param>
/// <param name="Started">Демона запустил этот вызов (а не застал работающим или стартующим).</param>
public sealed record DaemonStart(DaemonStatus? Status, bool Started, string? Error);
