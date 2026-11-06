using Tasker.Daemon;
using Tasker.Daemon.Host;
using Tasker.Global;
using Tasker.Storage.Files.Workspaces;
using Xunit;

namespace Tasker.Tests;

public class HostDaemonMcpTests
{
    [Fact]
    public async Task Allowed_workspace_reports_url_name_and_state()
    {
        var state = new DaemonRuntimeState(5733, 5733);
        var location = WorkspaceLocation.Files(Path.Combine(Path.GetTempPath(), "mcp-host-a", "Demo"));
        state.SetWorkspaces([new WorkspaceStatus(WorkspaceKind.Files, location.Path, WorkspaceState.Open, "Demo", null)]);

        var mcp = await new HostDaemonMcp(state, 5733).Find(location);

        Assert.Equal(DaemonState.Running, mcp.Daemon);
        Assert.Equal(5733, mcp.Port);
        Assert.Equal("http://127.0.0.1:5733/mcp", mcp.Url);
        Assert.Equal("Demo", mcp.Workspace);
        Assert.Equal(WorkspaceState.Open, mcp.WorkspaceState);
        Assert.Null(mcp.Error);
    }

    [Fact]
    public async Task Failed_workspace_reports_the_error()
    {
        var state = new DaemonRuntimeState(5733, 5733);
        var location = WorkspaceLocation.Files(Path.Combine(Path.GetTempPath(), "mcp-host-b", "Gone"));
        state.SetWorkspaces([new WorkspaceStatus(WorkspaceKind.Files, location.Path, WorkspaceState.Failed, null, "folder is missing")]);

        var mcp = await new HostDaemonMcp(state, 5733).Find(location);

        Assert.Equal(WorkspaceState.Failed, mcp.WorkspaceState);
        Assert.Equal("Gone", mcp.Workspace);
        Assert.Equal("folder is missing", mcp.Error);
    }

    [Fact]
    public async Task Workspace_not_allowed_in_settings_has_no_name_or_state()
    {
        var state = new DaemonRuntimeState(5733, 5733);

        var mcp = await new HostDaemonMcp(state, 5733).Find(WorkspaceLocation.Files(Path.Combine(Path.GetTempPath(), "mcp-host-c")));

        Assert.Equal(DaemonState.Running, mcp.Daemon);
        Assert.Null(mcp.Workspace);
        Assert.Null(mcp.WorkspaceState);
    }
}
