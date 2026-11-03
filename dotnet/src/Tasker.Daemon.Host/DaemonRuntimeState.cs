using Tasker.Daemon;
using Tasker.Global;

namespace Tasker.Daemon.Host;

/// <summary>Что сейчас делает демон: его процесс, порт и рабочие области. Отдаётся по <c>/daemon/status</c>.</summary>
public sealed class DaemonRuntimeState(int port, int settingsPort)
{
    private readonly object _sync = new();
    private WorkspaceStatus[] _workspaces = [];

    /// <summary>Процесс демона для статуса: у рабочего процесса — супервизор, а не он сам.</summary>
    public int Pid { get; init; } = Environment.ProcessId;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    private WorkerStatus[] _workers = [];

    /// <summary>Рабочие процессы под супервизором (их присылает он сам); у демона в одном процессе пусто.</summary>
    public void SetWorkers(WorkerStatus[] workers)
    {
        lock (_sync)
            _workers = workers;
    }

    public void SetWorkspaces(WorkspaceStatus[] workspaces)
    {
        lock (_sync)
            _workspaces = workspaces;
    }

    public DaemonStatus Snapshot(GlobalSettings? current = null)
    {
        lock (_sync)
            return new DaemonStatus(Pid, port, current?.Mcp.Port ?? settingsPort, StartedAt, _workspaces) { Workers = _workers };
    }
}
