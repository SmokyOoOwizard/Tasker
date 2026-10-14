using Tasker.Global;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Daemon;

/// <summary>Состояние демона MCP глазами десктопа.</summary>
public enum DaemonState
{
    /// <summary>Не работает: его остановили после запуска десктопа (<c>tasker mcp stop</c>) или ещё не запускали.</summary>
    Stopped,

    /// <summary>Десктоп запустил демон и ждёт готовности.</summary>
    Starting,
    Running,

    /// <summary>Запустить не удалось: причина — в <see cref="DaemonView.Error"/>.</summary>
    Failed
}

/// <param name="Status">Статус работающего демона; есть только при <see cref="DaemonState.Running"/>.</param>
public sealed record DaemonView(DaemonState State, DaemonStatus? Status, string? Error);

/// <summary>
/// Десктоп и демон MCP: своего MCP у десктопа нет, его обслуживает демон <c>tasker-mcpd</c>. При старте десктоп запускает демон, если тот
/// не работает (<see cref="Start"/>), а открытые во вкладках папки добавляет в области демона в <c>settings.json</c> (<see cref="Allow"/>):
/// демон следит за настройками и открывает их без перезапуска. Закрытие десктопа демон не останавливает.
/// </summary>
/// <param name="findProgram">Где взять <c>tasker-mcpd</c> (<see cref="DaemonLauncher.Find()"/>); null — не найден.</param>
public sealed class DesktopDaemon(Func<string?> findProgram, SettingsStore settings)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _starting;
    private volatile string? _error;

    /// <summary>Запускает демон, если он не работает, и ждёт готовности. Одновременные вызовы ждут один запуск.</summary>
    public async Task<DaemonStart> Start(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _starting = true;
            _error = null;
            var result = await DaemonLauncher.EnsureRunning(findProgram(), ct);
            _error = result.Error;
            return result;
        }
        finally
        {
            _starting = false;
            _gate.Release();
        }
    }

    /// <summary>Что сейчас с демоном.</summary>
    public async Task<DaemonView> GetView(CancellationToken ct = default)
    {
        if (await DaemonClient.GetStatus(ct) is { } status)
            return new DaemonView(DaemonState.Running, status, null);
        if (_starting)
            return new DaemonView(DaemonState.Starting, null, null);
        return _error is { } error ? new DaemonView(DaemonState.Failed, null, error) : new DaemonView(DaemonState.Stopped, null, null);
    }

    /// <summary>
    /// Папка открыта во вкладке: добавляет её в области MCP (<c>mcp.workspaces</c> в <c>settings.json</c>), как
    /// <c>tasker mcp workspace add</c>. Запись остаётся и после закрытия вкладки; убрать — <c>tasker mcp workspace remove</c>.
    /// </summary>
    /// <returns>true — добавлена сейчас; false — уже была в списке.</returns>
    public async Task<bool> Allow(WorkspaceLocation location, CancellationToken ct = default) =>
        (await settings.AddWorkspace(location, ct)).Added;
}
