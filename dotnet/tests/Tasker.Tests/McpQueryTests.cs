using System.Text.Json.Nodes;
using Xunit;

namespace Tasker.Tests;

public class McpQueryTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    [Fact]
    public async Task Mcp_list_tasks_and_get_board_take_query()
    {
        await _daemon.Workspace("a", "Alpha");
        var status = await _daemon.Start();
        var key = status.Workspaces.Single().Key!;
        var projectId = JsonNode.Parse(await _daemon.CallTool(key, "list_projects"))!["data"]![0]!["id"]!.GetValue<string>();

        async Task<(bool IsError, string Text)> Raw(string tool, object args)
        {
            var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject();
            node["projectId"] ??= projectId;
            return await _daemon.CallToolResult(key, tool, node);
        }

        async Task<JsonNode> Call(string tool, object args)
        {
            var (isError, text) = await Raw(tool, args);
            Assert.False(isError, $"{tool} failed: {text}");
            return JsonNode.Parse(text)!;
        }

        string Id(JsonNode node) => node["id"]!.GetValue<string>();
        var open = await Call("create_status", new { name = "Open", color = "#112233" });
        var closed = await Call("create_status", new { name = "Closed", color = "#445566" });
        var set = await Call("create_status_set", new { name = "Flow", statusIds = new[] { Id(open), Id(closed) } });
        var type = await Call("create_task_type", new { name = "Bug", statusSetId = Id(set) });
        var estimate = await Call("create_field", new { name = "Estimate", type = "int" });
        async Task Make(string title, string statusId, int points) => await Call("create_task", new
        {
            title, typeId = Id(type), statusId,
            fields = new { values = new[] { new { fieldId = Id(estimate), values = new[] { points.ToString() } } } }
        });
        await Make("one", Id(closed), 5);
        await Make("two", Id(open), 1);
        await Make("three", Id(open), 20);

        string[] Titles(JsonNode list) => list["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray();

        Assert.Equal(["two", "three"], Titles(await Call("list_tasks", new { query = ".Status == \"Open\"" })));
        Assert.Equal(["three", "two"], Titles(await Call("list_tasks", new { query = ".Status == \"Open\" | sort -.Field.Estimate" })));
        Assert.Equal(["one", "three"], Titles(await Call("list_tasks", new { query = ".Field.Estimate > 3" })));
        Assert.Equal(["three"], Titles(await Call("list_tasks", new { query = ".Status == \"Open\"", field = new[] { "Estimate>3" } })));
        Assert.Equal(["one", "three", "two"], Titles(await Call("list_tasks", new { query = "| sort .Title", sort = "-title" })));
        Assert.Equal(["one", "two", "three"], Titles(await Call("list_tasks", new { query = "" })));

        var (bad, text) = await Raw("list_tasks", new { query = ".Status == " });
        Assert.True(bad);
        Assert.StartsWith("[invalid] query.", text);
        Assert.Contains("position", text);
        var (unknown, unknownText) = await Raw("list_tasks", new { query = ".Status == \"Nope\"" });
        Assert.True(unknown);
        Assert.StartsWith("[invalid] query.", unknownText);

        var board = await Call("create_board", new
        {
            name = "Main", statusSetIds = new[] { Id(set) },
            columns = new object[]
            {
                new { name = "All", statusIds = new[] { Id(open), Id(closed) }, dropStatuses = new Dictionary<string, string> { [Id(set)] = Id(open) } }
            }
        });
        var view = await Call("get_board", new { boardId = Id(board), query = ".Field.Estimate > 3 | sort -.Title" });
        Assert.Equal(["three", "one"], Titles(view["columns"]![0]!["tasks"]!));
        var (boardBad, boardText) = await Raw("get_board", new { boardId = Id(board), query = ".Status ==" });
        Assert.True(boardBad);
        Assert.StartsWith("[invalid] query.", boardText);
    }
}
