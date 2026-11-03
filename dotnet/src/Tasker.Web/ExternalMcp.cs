using Tasker.Storage.Files.Workspaces;

namespace Tasker.Web;

/// <summary>MCP рабочей области, которую обслуживает не этот процесс, а демон MCP.</summary>
/// <param name="DaemonRunning">Демон работает.</param>
/// <param name="Url">Адрес MCP демона (общий для всех областей); null — демон работает, но эта область ему не разрешена или не открылась.</param>
/// <param name="Workspace">Имя этой области у демона — значение аргумента <c>workspace</c>; null — как у <paramref name="Url"/>.</param>
public sealed record ExternalMcp(bool DaemonRunning, string? Url, string? Workspace = null);

/// <summary>
/// Десктоп: где искать MCP рабочей области, если его держит демон. Реализацию даёт хост (она знает про демон);
/// без неё интерфейс показывает только собственный MCP десктопа.
/// </summary>
public interface IExternalMcp
{
    Task<ExternalMcp> Find(WorkspaceLocation location, CancellationToken ct = default);
}
