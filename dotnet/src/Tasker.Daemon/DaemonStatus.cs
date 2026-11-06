using Tasker.Global;

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

/// <param name="SettingsPort">Порт в настройках: отличается от <paramref name="Port"/> — нужен перезапуск.</param>
public sealed record DaemonStatus(int Pid, int Port, int SettingsPort, DateTimeOffset StartedAt, WorkspaceStatus[] Workspaces)
{
    /// <summary>Версия программы демона. Новый член: старый JSON без него читается.</summary>
    public string Version { get; init; } = "";

    /// <summary>Короткий идентификатор сборки демона: меняется с каждой сборкой, в отличие от версии.</summary>
    public string Build { get; init; } = "";

    /// <summary>Адрес MCP: один на все области (область — аргумент вызова).</summary>
    public string McpUrl => $"http://127.0.0.1:{Port}/mcp";

    /// <summary>Все области из настроек уже открыты или не открылись: ни одна не в <see cref="WorkspaceState.Opening"/>.</summary>
    public bool Settled => Workspaces.All(x => x.State != WorkspaceState.Opening);
}
