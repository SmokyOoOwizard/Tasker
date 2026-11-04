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

/// <summary>Что с MCP рабочей области. Реализацию даёт хост; без неё интерфейс считает, что демон не работает.</summary>
public interface IDaemonMcp
{
    Task<WorkspaceMcp> Find(WorkspaceLocation location, CancellationToken ct = default);
}
