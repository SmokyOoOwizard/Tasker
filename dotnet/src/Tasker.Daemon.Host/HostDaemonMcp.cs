using Tasker.Daemon;
using Tasker.Mcp;
using Tasker.Storage.Files.Workspaces;
using Tasker.Web;

namespace Tasker.Daemon.Host;

/// <summary>Состояние MCP для рабочей области, которую обслуживает сам демон: он знает свой порт и список областей.</summary>
internal sealed class HostDaemonMcp(DaemonRuntimeState state, int port) : IDaemonMcp
{
    public Task<WorkspaceMcp> Find(WorkspaceLocation location, CancellationToken ct = default)
    {
        var url = McpRegistration.LocalUrl(port);
        var found = state.Snapshot().Workspaces.FirstOrDefault(x => x.Kind == location.Kind && string.Equals(x.Path, location.Path, StringComparison.Ordinal));
        if (found == null)
            return Task.FromResult(new WorkspaceMcp(Tasker.Daemon.DaemonState.Running, port, url, null, null, null));

        var name = found.Key ?? (Path.GetFileName(found.Path.TrimEnd('/', '\\')) is { Length: > 0 } folder ? folder : found.Path);
        return Task.FromResult(new WorkspaceMcp(Tasker.Daemon.DaemonState.Running, port, url, name, found.State, found.Error));
    }
}
