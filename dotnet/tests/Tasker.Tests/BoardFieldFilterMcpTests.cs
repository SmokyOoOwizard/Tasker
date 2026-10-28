using System.Text.Json.Nodes;
using Xunit;

namespace Tasker.Tests;

/// <summary>Условия колонки доски по полям через настоящий демон MCP: create_board / update_board / get_board / move_task / update_enum.</summary>
public class BoardFieldFilterMcpTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();
    private static string Version(JsonNode node) => node["version"]!.GetValue<string>();

    [Fact]
    public async Task Column_conditions_are_written_read_enforced_on_move_and_cascaded_through_mcp()
    {
        await _daemon.Workspace("a", "Alpha");
        var status = await _daemon.Start();
        var key = status.Workspaces.Single().Key!;
        var projectId = JsonNode.Parse(await _daemon.CallTool(key, "list_projects"))!["data"]![0]!["id"]!.GetValue<string>();

        async Task<JsonNode> Call(string tool, object args)
        {
            var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject();
            node["projectId"] ??= projectId;
            var (isError, text) = await _daemon.CallToolResult(key, tool, node);
            Assert.False(isError, $"{tool} failed: {text}");
            return JsonNode.Parse(text)!;
        }

        async Task<string> Fail(string tool, object args)
        {
            var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject();
            node["projectId"] ??= projectId;
            var (isError, text) = await _daemon.CallToolResult(key, tool, node);
            Assert.True(isError, $"{tool} unexpectedly succeeded: {text}");
            return text;
        }

        var open = await Call("create_status", new { name = "Open", color = "#112233" });
        var closed = await Call("create_status", new { name = "Closed", color = "#445566" });
        var set = await Call("create_status_set", new { name = "Flow", statusIds = new[] { Id(open), Id(closed) } });
        var levels = await Call("create_enum", new { name = "LevelValues", values = new[] { "Low", "High" } });
        var level = await Call("create_field", new { name = "Level", type = "enum", enumId = Id(levels) });
        var type = await Call("create_task_type", new { name = "Bug", statusSetId = Id(set), fields = new[] { new { fieldId = Id(level), required = false } } });

        async Task<JsonNode> NewTask(string title, string value) => await Call("create_task", new
        {
            title, typeId = Id(type), fields = new { values = new[] { new { fieldId = Id(level), values = new[] { value } } } }
        });
        var high = await NewTask("high", "High");
        var low = await NewTask("low", "Low");

        var drop = new Dictionary<string, string> { [Id(set)] = Id(open) };
        var board = await Call("create_board", new
        {
            name = "Main", statusSetIds = new[] { Id(set) },
            columns = new object[]
            {
                new { name = "Critical", statusIds = new[] { Id(open) }, dropStatuses = drop, fieldFilters = new[] { "Level=High" } },
                new { name = "Rest", statusIds = new[] { Id(open) }, dropStatuses = drop, fieldFilters = new[] { "Level!=High" } }
            }
        });
        Assert.Equal(Id(level), board["columns"]![0]!["fieldConditions"]![0]!["fieldId"]!.GetValue<string>());

        // Два столбца с общим статусом без исключающих условий — [invalid].
        Assert.StartsWith("[invalid]", await Fail("create_board", new
        {
            name = "Bad", statusSetIds = new[] { Id(set) },
            columns = new object[]
            {
                new { name = "A", statusIds = new[] { Id(open) }, dropStatuses = drop },
                new { name = "B", statusIds = new[] { Id(open) }, dropStatuses = drop }
            }
        }));

        // get_board: у колонки условия текстом, задачи — по статусу и условиям.
        var view = await Call("get_board", new { boardId = Id(board) });
        Assert.Equal(["Level=High"], view["columns"]![0]!["fieldFilters"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Equal(["high"], view["columns"]![0]!["tasks"]!["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray());
        Assert.Equal(["low"], view["columns"]![1]!["tasks"]!["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray());
        // Условие просмотра добавляется к условиям колонки.
        var viewed = await Call("get_board", new { boardId = Id(board), field = new[] { "Level=Low" } });
        Assert.Equal(0, viewed["columns"]![0]!["tasks"]!["totalCount"]!.GetValue<int>());
        Assert.Equal(1, viewed["columns"]![1]!["tasks"]!["totalCount"]!.GetValue<int>());

        // move_task: подходящая задача переносится, неподходящая — нет.
        var critical = board["columns"]![0]!["id"]!.GetValue<string>();
        Assert.Contains("does not match the field conditions", await Fail("move_task", new { boardId = Id(board), columnId = critical, taskId = Id(low), version = Version(low) }));
        Assert.Equal(Id(open), (await Call("move_task", new { boardId = Id(board), columnId = critical, taskId = Id(high), version = Version(high) }))["statusId"]!.GetValue<string>());

        // update_board без fieldFilters у колонок с id сохраняет их.
        var current = await Call("get_board", new { boardId = Id(board) });
        var kept = await Call("update_board", new
        {
            boardId = Id(board), version = Version(current["board"]!),
            columns = current["board"]!["columns"]!.AsArray().Select(c => new
            {
                id = c!["id"]!.GetValue<string>(), name = c["name"]!.GetValue<string>(), statusIds = new[] { Id(open) }, dropStatuses = drop
            }).ToArray()
        });
        Assert.Single(kept["columns"]![0]!["fieldConditions"]!.AsArray());
        Assert.Single(kept["columns"]![1]!["fieldConditions"]!.AsArray());

        // update_enum: значение из условий колонок — только с выбором; «убрать» оставило бы колонки с общим статусом без условий.
        var enumNow = await Call("get_enum", new { enumeration = Id(levels) });
        var lowId = enumNow["values"]![0]!["id"]!.GetValue<string>();
        var onlyLow = new[] { new { id = lowId, name = "Low" } };
        Assert.StartsWith("[in_use]", await Fail("update_enum", new { enumId = Id(levels), version = Version(enumNow), values = onlyLow }));
        Assert.Contains("would show the same tasks",
            await Fail("update_enum", new { enumId = Id(levels), version = Version(enumNow), values = onlyLow, removedValues = "clear" }));
        var reassigned = await Call("update_enum", new { enumId = Id(levels), version = Version(enumNow), values = onlyLow, removedValues = "reassign", reassignTo = lowId });
        Assert.Equal(2, reassigned["affectedColumns"]!.GetValue<int>());
        Assert.Equal(lowId, (await Call("list_boards", new { }))["data"]![0]!["columns"]![0]!["fieldConditions"]![0]!["value"]!.GetValue<string>());
    }
}
