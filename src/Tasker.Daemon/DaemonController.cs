using System.Diagnostics;
using Tasker.Daemon.Services;
using Tasker.Global;

namespace Tasker.Daemon;

/// <summary>Демон не поднялся: причина — из его журнала, если удалось найти.</summary>
public class DaemonStartException(string message) : Exception(message);

public sealed record AutostartInfo(string Manager, bool Enabled, bool Loaded);

public sealed record ControllerStatus(DaemonStatus? Daemon, AutostartInfo Autostart);

/// <summary>Замена на лету не удалась: старый процесс продолжает работать, сообщение объясняет почему.</summary>
public class DaemonUpgradeException(string message) : Exception(message);

public enum UpgradeKind
{
    /// <summary>Демон не работает: заменять нечего.</summary>
    NotRunning,

    /// <summary>Рабочий процесс заменён на лету, вызовы не прерывались.</summary>
    Replaced,

    /// <summary>Демон перезапущен (запрошено <c>--restart</c> или он запущен по-старому, одним процессом).</summary>
    Restarted
}

/// <param name="Daemon">Свою программу демона вместо соседней с <c>tasker</c> (тесты, нестандартная установка): файл и начальные аргументы.</param>
public sealed record UpgradeOptions(bool Restart = false, int TimeoutSeconds = DaemonController.DefaultUpgradeTimeoutSeconds, (string File, string[] Arguments)? Daemon = null);

public sealed record UpgradeOutcome(UpgradeKind Kind, string Message, DaemonStatus? Status, UpgradeResult? Result = null);

/// <summary>
/// Управление демоном для <c>tasker mcp start | stop | restart | status | autostart</c>.
/// Если автозапуск включён, демоном владеет служба системы (launchd, systemd): её и просим запустить и остановить,
/// иначе она бы тут же подняла его снова. Без автозапуска демон запускается фоновым процессом и останавливается запросом к нему.
/// </summary>
public sealed class DaemonController(
    IServiceManager service,
    TimeSpan? readyTimeout = null,
    TimeSpan? stopTimeout = null,
    Func<Process>? spawn = null)
{
    private readonly TimeSpan _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(30);
    private readonly TimeSpan _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(15);

    /// <summary>Сколько ждать, пока новый рабочий процесс откроет области и станет готов, по умолчанию.</summary>
    public const int DefaultUpgradeTimeoutSeconds = 60;

    public IServiceManager Service => service;

    public async Task<ControllerStatus> Status(CancellationToken ct = default) =>
        new(await DaemonClient.GetStatus(ct), await Autostart());

    /// <returns>Статус запущенного демона; <c>AlreadyRunning</c> — он уже работал.</returns>
    public async Task<(DaemonStatus Status, bool AlreadyRunning)> Start(CancellationToken ct = default)
    {
        if (await DaemonClient.GetStatus(ct) is { } running)
            return (running, true);

        Process? process = null;
        if (DaemonFiles.IsRunning())
        {
            // Уже стартует (или запущен другим способом) — просто ждём.
        }
        else if (service.IsEnabled)
        {
            await service.Start();
        }
        else
        {
            process = (spawn ?? Launcher.SpawnDaemon)();
        }

        return (await WaitReady(process, ct), false);
    }

    /// <returns>false — демон не работал.</returns>
    public async Task<bool> Stop(CancellationToken ct = default)
    {
        var wasRunning = DaemonFiles.IsRunning();
        if (service.IsEnabled)
            await service.Stop();
        // Сначала штатно: запрос демону (он закроет области и дождётся начатых вызовов); не принял — именованное событие (Windows).
        if (DaemonFiles.IsRunning() && !await DaemonClient.RequestStop(ct))
            ShutdownSignal.Raise();

        await WaitStopped(ct);
        return wasRunning;
    }

    public async Task<DaemonStatus> Restart(CancellationToken ct = default)
    {
        await Stop(ct);
        return (await Start(ct)).Status;
    }

    /// <summary>
    /// Заменяет работающий демон на новую сборку без простоя: рядом поднимается новый рабочий процесс, он принимает вызовы,
    /// когда открыл все области, а старый заканчивает начатые. Демон, запущенный по-старому (одним процессом), заменить так нельзя —
    /// он перезапускается. Не получилось (новый процесс не поднялся) — <see cref="DaemonUpgradeException"/>, старый продолжает работать.
    /// </summary>
    public async Task<UpgradeOutcome> Upgrade(UpgradeOptions options, CancellationToken ct = default)
    {
        if (!DaemonFiles.IsRunning())
            return new UpgradeOutcome(UpgradeKind.NotRunning, "The MCP server is not running: nothing to upgrade (start it with 'tasker mcp start')", null);

        if (options.Restart)
            return new UpgradeOutcome(UpgradeKind.Restarted, "Restarted as requested", await Restart(ct));

        var status = await DaemonClient.GetStatus(ct)
            ?? throw new DaemonUpgradeException("The MCP server does not answer: wait for it to start, or restart it with 'tasker mcp upgrade --restart'");
        if (!status.Supervised)
        {
            return new UpgradeOutcome(UpgradeKind.Restarted,
                "The running MCP server was started by an earlier build as one process and cannot be replaced on the fly: restarted it (the next upgrade will be seamless)",
                await Restart(ct));
        }

        var (file, arguments) = options.Daemon ?? Launcher.DaemonCommand();
        var result = await DaemonClient.Upgrade(new UpgradeRequest(file, arguments, options.TimeoutSeconds), ct)
            ?? throw new DaemonUpgradeException("The MCP server stopped while it was being upgraded");
        if (!result.Ok)
            throw new DaemonUpgradeException(result.Message);

        // Старый процесс заканчивает начатые вызовы: ждём, пока он уйдёт (но не дольше срока), чтобы статус показал одну сборку.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(options.TimeoutSeconds);
        var latest = await DaemonClient.GetStatus(ct);
        while (latest is { } current && current.Workers.Any(x => x.Role != WorkerRole.Active) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct);
            if (await DaemonClient.GetStatus(ct) is not { } next)
                break;
            latest = next;
        }

        return new UpgradeOutcome(UpgradeKind.Replaced, result.Message, latest, result);
    }

    /// <summary>Включает автозапуск: служба запускает демон сейчас и при каждом входе в систему, перезапускает при падении.</summary>
    public async Task<DaemonStatus> EnableAutostart(CancellationToken ct = default)
    {
        // Демон, запущенный не службой, держит порт и блокировку — освобождаем их для службы.
        await Stop(ct);
        await service.Enable();
        return await WaitReady(null, ct);
    }

    /// <summary>Выключает автозапуск. Работавший демон продолжает работать — уже без службы, до остановки или выхода из системы.</summary>
    /// <returns>Статус демона, если он работал.</returns>
    public async Task<DaemonStatus?> DisableAutostart(CancellationToken ct = default)
    {
        var wasRunning = DaemonFiles.IsRunning();
        await service.Disable();
        await WaitStopped(ct);
        return wasRunning ? (await Start(ct)).Status : null;
    }

    private async Task<AutostartInfo> Autostart() =>
        new(service.Name, service.IsEnabled, service.IsEnabled && await service.IsLoaded());

    // Готов, когда отвечает и все области из настроек уже открыты (или не открылись).
    private async Task<DaemonStatus> WaitReady(Process? process, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _readyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await DaemonClient.GetStatus(ct) is { } status && status.Workspaces.All(x => x.State != WorkspaceState.Opening))
                return status;

            if (process is { HasExited: true })
                throw new DaemonStartException(LogTail() ?? "The MCP server exited right after start: see the log in " + AppDirectories.Logs);

            await Task.Delay(150, ct);
        }

        throw new DaemonStartException(LogTail() ?? $"The MCP server did not answer in {_readyTimeout.TotalSeconds:0} s: see the log in {AppDirectories.Logs}");
    }

    private async Task WaitStopped(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _stopTimeout;
        while (DaemonFiles.IsRunning())
        {
            if (DateTime.UtcNow >= deadline)
            {
                // Штатно не остановился — снимаем процесс в последнюю очередь, и только тот, что записан в daemon.json
                // (номер мог достаться другой программе). На Windows — вместе с рабочими процессами.
                if (DaemonFiles.ReadInfo() is { } info && DaemonProcessIdentity.Find(info) is { } process)
                {
                    using (process)
                    {
                        try
                        {
                            process.Kill(entireProcessTree: OperatingSystem.IsWindows());
                        }
                        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                        }
                    }

                    await Task.Delay(300, ct);
                }
                return;
            }

            await Task.Delay(100, ct);
        }
    }

    /// <summary>Последняя ошибка из журнала демона — почему он не запустился.</summary>
    private static string? LogTail()
    {
        try
        {
            var log = new DirectoryInfo(AppDirectories.Logs).GetFiles("mcp-*.log").OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
            if (log == null)
                return null;

            using var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            // Только последний запуск: ошибки прошлых попыток к этой не относятся.
            var text = reader.ReadToEnd();
            var start = text.LastIndexOf("Starting the MCP server", StringComparison.Ordinal);
            var line = (start < 0 ? text : text[start..]).Split('\n').LastOrDefault(x => x.Contains("[ERR]") || x.Contains("[FTL]"));
            var at = line?.IndexOf(']') ?? -1;
            return at >= 0 ? line![(at + 1)..].Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
