using Tasker.Global;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Daemon;

public enum WorkspaceState
{
    /// <summary>Открывается: первая сверка индекса ещё идёт.</summary>
    Opening,
    Open,
    Failed
}

/// <param name="Key">Имя области для агента MCP (аргумент <c>workspace</c>); null, пока область не открыта.</param>
public sealed record WorkspaceStatus(WorkspaceKind Kind, string Path, WorkspaceState State, string? Key, string? Error);

/// <summary>Что делает рабочий процесс демона: принимает вызовы (<see cref="Active"/>), готовится принимать (<see cref="Starting"/>) или заканчивает начатые (<see cref="Draining"/>).</summary>
public enum WorkerRole
{
    Starting,
    Active,
    Draining
}

/// <summary>
/// Рабочий процесс демона. Обычно он один; на время замены (<c>tasker mcp upgrade</c>) их два: новый (<see cref="WorkerRole.Starting"/>
/// или уже <see cref="WorkerRole.Active"/>) и старый (<see cref="WorkerRole.Draining"/>).
/// </summary>
/// <param name="Build">Короткий идентификатор сборки программы демона: по нему видно, что процесс заменён на новую сборку.</param>
public sealed record WorkerStatus(int Pid, string Version, string Build, WorkerRole Role, DateTimeOffset StartedAt);

/// <param name="Pid">Процесс демона — супервизор (его держат launchd/systemd и <c>daemon.lock</c>); рабочие процессы — в <see cref="Workers"/>.</param>
/// <param name="SettingsPort">Порт в настройках: отличается от <paramref name="Port"/> — нужен перезапуск.</param>
public sealed record DaemonStatus(int Pid, int Port, int SettingsPort, DateTimeOffset StartedAt, WorkspaceStatus[] Workspaces)
{
    /// <summary>
    /// Рабочие процессы под супервизором. Пусто — демон запущен по-старому, одним процессом (его нельзя заменить на лету:
    /// <c>tasker mcp upgrade</c> перезапустит его). Новый член: старый JSON без него читается.
    /// </summary>
    public WorkerStatus[] Workers { get; init; } = [];

    /// <summary>Демон под супервизором: его можно заменить на лету (<c>tasker mcp upgrade</c>).</summary>
    public bool Supervised => Workers.Length > 0;

    /// <summary>Адрес MCP: один на все области (область — аргумент вызова).</summary>
    public string McpUrl => $"http://127.0.0.1:{Port}/mcp";
}

/// <summary>Что сейчас делает демон: его процесс, порт и рабочие области. Отдаётся по <c>/daemon/status</c>.</summary>
public sealed class DaemonState(int port, int settingsPort)
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

/// <summary>Итог сверки области (<c>/daemon/sync</c>): файлы, которые не удалось прочитать, и состояние серий.</summary>
/// <param name="Problems">Первые <see cref="ProblemsShown"/> из <paramref name="ProblemCount"/>.</param>
public sealed record SyncResult(string Path, int ProblemCount, Tasker.Core.Workspace.WorkspaceProblem[] Problems)
{
    public const int ProblemsShown = 10;

    /// <summary>Проекты, у которых что-то не в порядке с сериями (пусто — всё хорошо). Новый член: старый JSON без него читается.</summary>
    public ProjectSeriesHealth[] Series { get; init; } = [];
}

/// <summary>Просьба сверить область: <c>POST /daemon/sync</c>.</summary>
public sealed record SyncRequest(WorkspaceKind Kind, string Path);

/// <summary>Просьба заменить рабочий процесс демона на лету: <c>POST /daemon/upgrade</c>.</summary>
/// <param name="File">Программа нового рабочего процесса (<c>tasker-mcpd</c> новой сборки) и её начальные аргументы.</param>
/// <param name="TimeoutSeconds">Сколько ждать, пока новый процесс откроет области и будет готов.</param>
public sealed record UpgradeRequest(string File, string[] Arguments, int TimeoutSeconds);

/// <summary>Итог замены. <c>Ok = false</c> — старый процесс продолжает работать, <c>Message</c> объясняет почему.</summary>
public sealed record UpgradeResult(bool Ok, string Message, int? OldPid = null, int? NewPid = null, string? OldBuild = null, string? NewBuild = null);
