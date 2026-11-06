using System.Diagnostics;
using Tasker.Daemon.Services;
using Tasker.Global;

namespace Tasker.Daemon;

/// <summary>Демон не поднялся: причина — из его журнала, если удалось найти.</summary>
public class DaemonStartException(string message) : Exception(message);

public sealed record AutostartInfo(string Manager, bool Enabled, bool Loaded);

public sealed record ControllerStatus(DaemonStatus? Daemon, AutostartInfo Autostart);

/// <summary>Новая сборка не прошла проверку: работающий демон не тронут, сообщение объясняет почему.</summary>
public class DaemonUpgradeException(string message) : Exception(message);

public enum UpgradeKind
{
    /// <summary>Демон не работает: обновлять нечего.</summary>
    NotRunning,

    /// <summary>Демон перезапущен на новой сборке.</summary>
    Restarted
}

/// <param name="Daemon">Свою программу демона вместо соседней с <c>tasker</c> (тесты, нестандартная установка): файл и начальные аргументы.</param>
public sealed record UpgradeOptions(int TimeoutSeconds = DaemonController.DefaultUpgradeTimeoutSeconds, (string File, string[] Arguments)? Daemon = null);

/// <param name="Problems">Что после перезапуска не так: область больше не открывается или служба запустила другую сборку.</param>
public sealed record UpgradeOutcome(UpgradeKind Kind, string Message, DaemonStatus? Status, string[] Problems);

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

    /// <summary>Сколько ждать проверки новой сборки и открытия областей после перезапуска, по умолчанию.</summary>
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

        return (await WaitReady(process, _readyTimeout, ct), false);
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
    /// Переводит работающий демон на новую сборку: сначала проверяет её пробным запуском (не прошла — <see cref="DaemonUpgradeException"/>,
    /// демон не тронут), потом перезапускает демон и ждёт, пока он откроет области. На время перезапуска порт закрыт, начатые вызовы обрываются.
    /// </summary>
    public async Task<UpgradeOutcome> Upgrade(UpgradeOptions options, CancellationToken ct = default)
    {
        if (!DaemonFiles.IsRunning())
            return new UpgradeOutcome(UpgradeKind.NotRunning, "The MCP server is not running: nothing to upgrade (start it with 'tasker mcp start')", null, []);

        var timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        var program = options.Daemon ?? Launcher.DaemonCommand();
        var target = await Launcher.ReadBuild(program, timeout, ct);
        var before = await DaemonClient.GetStatus(ct);

        await Stop(ct);
        Process? process = null;
        if (service.IsEnabled)
            await service.Start();
        else
            process = spawn?.Invoke() ?? Launcher.SpawnDaemon(program);
        var status = await WaitReady(process, timeout, ct);

        var problems = (before?.Workspaces ?? [])
            .Where(x => x.State == WorkspaceState.Open)
            .Select(x => (x.Path, Now: status.Workspaces.FirstOrDefault(w => w.Path == x.Path)))
            .Where(x => x.Now is not { State: WorkspaceState.Open })
            .Select(x => $"the workspace {x.Path} was open and is not now: {x.Now?.Error ?? "not served"}")
            .ToList();
        if (status.Build != target.Build)
            problems.Add($"the MCP server came back on build {DescribeBuild(status.Build)}, expected {target.Build}: the service starts another program");

        var old = DescribeBuild(before?.Build);
        return new UpgradeOutcome(UpgradeKind.Restarted, $"The MCP server is restarted: build {old} -> {DescribeBuild(status.Build)}", status, [.. problems]);
    }

    private static string DescribeBuild(string? build) => build is { Length: > 0 } ? build : "?";

    /// <summary>Включает автозапуск: служба запускает демон сейчас и при каждом входе в систему, перезапускает при падении.</summary>
    public async Task<DaemonStatus> EnableAutostart(CancellationToken ct = default)
    {
        if (Launcher.RunsFromTemporaryLocation())
            throw new ServiceException("Tasker.app runs from a temporary location: move it to Applications and open it, it enables the autostart itself");

        // Демон, запущенный не службой, держит порт и блокировку — освобождаем их для службы.
        await Stop(ct);
        await service.Enable();
        return await WaitReady(null, _readyTimeout, ct);
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
    private static async Task<DaemonStatus> WaitReady(Process? process, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await DaemonClient.GetStatus(ct) is { } status && status.Workspaces.All(x => x.State != WorkspaceState.Opening))
                return status;

            if (process is { HasExited: true })
                throw new DaemonStartException(LogTail() ?? "The MCP server exited right after start: see the log in " + AppDirectories.Logs);

            await Task.Delay(150, ct);
        }

        throw new DaemonStartException(LogTail() ?? $"The MCP server did not answer in {timeout.TotalSeconds:0} s: see the log in {AppDirectories.Logs}");
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
