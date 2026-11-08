using System.Text.Json.Nodes;
using Xunit;

namespace Tasker.Tests;

public class BoardColumnQueryMcpTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();
    private static string Version(JsonNode node) => node["version"]!.GetValue<string>();
    private static string[] Titles(JsonNode column) => column["tasks"]!["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task Column_queries_are_written_read_enforced_on_move_and_a_broken_one_is_reported_in_its_column()
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

        async Task<string> Fail(string tool, object args)
        {
            var (isError, text) = await Raw(tool, args);
            Assert.True(isError, $"{tool} unexpectedly succeeded: {text}");
            return text;
        }

        var open = await Call("create_status", new { name = "Open", color = "#112233" });
        var closed = await Call("create_status", new { name = "Closed", color = "#445566" });
        var set = await Call("create_status_set", new { name = "Flow", statusIds = new[] { Id(open), Id(closed) } });
        var level = await Call("create_field", new { name = "Level", type = "int" });
        var type = await Call("create_task_type", new { name = "Bug", statusSetId = Id(set), fields = new[] { new { fieldId = Id(level), required = false } } });

        async Task<JsonNode> NewTask(string title, int value, string? statusId = null) => await Call("create_task", new
        {
            title, typeId = Id(type), statusId, fields = new { values = new[] { new { fieldId = Id(level), values = new[] { value.ToString() } } } }
        });
        await NewTask("one", 1);
        await NewTask("five", 5);
        await NewTask("nine", 9);
        var low = await NewTask("low", 0, Id(closed));

        var openDrop = new Dictionary<string, string> { [Id(set)] = Id(open) };
        var closedDrop = new Dictionary<string, string> { [Id(set)] = Id(closed) };
        var board = await Call("create_board", new
        {
            name = "Main", statusSetIds = new[] { Id(set) },
            columns = new object[]
            {
                new { name = "Big", statusIds = new[] { Id(open) }, dropStatuses = openDrop, query = ".Field.Level >= 5 | sort -.Field.Level" },
                new { name = "Small", statusIds = new[] { Id(open) }, dropStatuses = openDrop, query = ".Field.Level < 5" },
                new { name = "Closed", statusIds = new[] { Id(closed) }, dropStatuses = closedDrop }
            }
        });
        Assert.Equal(".Field.Level >= 5 | sort -.Field.Level", board["columns"]![0]!["query"]!.GetValue<string>());

        Assert.StartsWith("[invalid]", await Fail("create_board", new
        {
            name = "Bad", statusSetIds = new[] { Id(set) },
            columns = new object[]
            {
                new { name = "A", statusIds = new[] { Id(open) }, dropStatuses = openDrop, query = ".Field.Level >= 5" },
                new { name = "B", statusIds = new[] { Id(open) }, dropStatuses = openDrop, query = ".Field.Level >= 3" }
            }
        }));

        var view = await Call("get_board", new { boardId = Id(board) });
        Assert.Equal(".Field.Level < 5", view["columns"]![1]!["query"]!.GetValue<string>());
        Assert.Equal(["nine", "five"], Titles(view["columns"]![0]!));
        Assert.Equal(["one"], Titles(view["columns"]![1]!));
        Assert.Equal(["five", "nine"], Titles((await Call("get_board", new { boardId = Id(board), sort = "Level" }))["columns"]![0]!));

        var big = Id(board["columns"]![0]!);
        Assert.Contains("does not match the query of column 'Big'", await Fail("move_task", new { boardId = Id(board), columnId = big, taskId = Id(low), version = Version(low) }));

        var current = await Call("get_board", new { boardId = Id(board) });
        var kept = await Call("update_board", new
        {
            boardId = Id(board), version = Version(current["board"]!),
            columns = current["board"]!["columns"]!.AsArray().Select(c => new
            {
                id = Id(c!), name = c!["name"]!.GetValue<string>(), statusIds = new[] { c["statusIds"]![0]!.GetValue<string>() },
                dropStatuses = new Dictionary<string, string> { [Id(set)] = c["statusIds"]![0]!.GetValue<string>() }
            }).ToArray()
        });
        Assert.Equal(".Field.Level < 5", kept["columns"]![1]!["query"]!.GetValue<string>());

        var field = await Call("get_field", new { field = Id(level) });
        await Call("update_field", new { fieldId = Id(level), version = Version(field), name = "Size" });
        var broken = await Call("get_board", new { boardId = Id(board) });
        Assert.Null(broken["columns"]![0]!["tasks"]);
        Assert.Contains("The query of column 'Big'", broken["columns"]![0]!["error"]!.GetValue<string>());
        Assert.Single(Titles(broken["columns"]![2]!));
        Assert.Contains("The query of column 'Big'", await Fail("move_task", new { boardId = Id(board), columnId = big, taskId = Id(low), version = Version(low) }));
    }
}
