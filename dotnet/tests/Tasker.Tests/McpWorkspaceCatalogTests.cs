using Microsoft.Extensions.DependencyInjection;
using Tasker.Mcp;
using Tasker.Web.Workspaces;
using Xunit;

namespace Tasker.Tests;

public class McpWorkspaceCatalogTests
{
    [Fact]
    public async Task A_workspace_that_is_open_is_not_listed_again_as_opening()
    {
        await using var api = await ApiHost.Start("files");
        var path = api.Lease.Location.Path;
        var catalog = new McpWorkspaceCatalog(api.Services.GetRequiredService<WorkspaceRegistry>())
        {
            Unavailable = () => [new McpWorkspace("stale", path, McpWorkspaceStatus.Opening)]
        };

        var listed = catalog.List();

        var only = Assert.Single(listed);
        Assert.Equal(McpWorkspaceStatus.Open, only.Status);
        Assert.Equal(path, only.Path);
    }

    [Fact]
    public async Task A_workspace_that_is_not_open_is_still_listed()
    {
        await using var api = await ApiHost.Start("files");
        var catalog = new McpWorkspaceCatalog(api.Services.GetRequiredService<WorkspaceRegistry>())
        {
            Unavailable = () => [new McpWorkspace("later", "/somewhere/else", McpWorkspaceStatus.Opening)]
        };

        Assert.Equal(2, catalog.List().Count);
    }
}
