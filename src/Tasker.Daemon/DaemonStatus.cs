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
