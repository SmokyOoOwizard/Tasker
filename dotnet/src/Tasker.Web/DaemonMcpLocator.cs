using Tasker.Daemon;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Web;

/// <summary>MCP рабочей области у демона: его состояние и, если он обслуживает эту область, адрес и её имя для агента.</summary>
/// <param name="Url">Адрес MCP демона (общий для всех областей); null — демон не работает или эта область у него не открыта.</param>
/// <param name="Workspace">Имя этой области у демона — значение аргумента <c>workspace</c>; null — область ему не разрешена.</param>
/// <param name="WorkspaceState">Состояние области у демона; null — не разрешена или демон не работает.</param>
/// <param name="Error">Почему не запустился демон или не открылась область.</param>
public sealed record WorkspaceMcp(
    DaemonState Daemon,
    int? Port,
    string? Url,
    string? Workspace,
    WorkspaceState? WorkspaceState,
    string? Error);

/// <summary>Десктоп: что с MCP рабочей области. Реализацию даёт хост; без неё интерфейс считает, что демон не работает.</summary>
public interface IDaemonMcp
{
    Task<WorkspaceMcp> Find(WorkspaceLocation location, CancellationToken ct = default);
}

/// <summary>Десктоп спрашивает у демона, обслуживает ли он MCP рабочей области (<see cref="IDaemonMcp"/>).</summary>
public sealed class DaemonMcpLocator(DesktopDaemon daemon) : IDaemonMcp
{
    public async Task<WorkspaceMcp> Find(WorkspaceLocation location, CancellationToken ct = default)
    {
        var view = await daemon.GetView(ct);
        if (view.Status is not { } status)
            return new WorkspaceMcp(view.State, null, null, null, null, view.Error);

        var workspace = status.Workspaces.FirstOrDefault(x => SameWorkspace(x, location));
        return workspace == null
            ? new WorkspaceMcp(DaemonState.Running, status.Port, null, null, null, null)
            : new WorkspaceMcp(DaemonState.Running, status.Port, workspace.State == WorkspaceState.Open && workspace.Key != null ? status.McpUrl : null,
                workspace.Key, workspace.State, workspace.Error);
    }

    private static bool SameWorkspace(WorkspaceStatus status, WorkspaceLocation location) =>
        status.Kind == location.Kind && string.Equals(status.Path, location.Path, StringComparison.Ordinal);
}
