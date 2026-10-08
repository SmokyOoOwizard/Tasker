using Tasker.Daemon;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Web;

/// <summary>Десктоп спрашивает у работающего демона, где MCP рабочей области (<see cref="IExternalMcp"/>).</summary>
public sealed class DaemonMcpLocator : IExternalMcp
{
    public async Task<ExternalMcp> Find(WorkspaceLocation location, CancellationToken ct = default)
    {
        if (await DaemonClient.GetStatus(ct) is not { } status)
            return new ExternalMcp(false, null);

        var workspace = status.Workspaces.FirstOrDefault(x => x.State == WorkspaceState.Open && x.Key != null && SameWorkspace(x, location));
        return workspace == null ? new ExternalMcp(true, null) : new ExternalMcp(true, status.McpUrl, workspace.Key);
    }

    private static bool SameWorkspace(WorkspaceStatus status, WorkspaceLocation location) =>
        status.Kind == location.Kind && string.Equals(status.Path, location.Path, StringComparison.Ordinal);
}
