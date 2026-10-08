using System.Text.Json.Nodes;
using Xunit;

namespace Tasker.Tests;

/// <summary>Инструменты MCP для серий — через настоящий демон MCP; данные проверяются и командной строкой.</summary>
public class SeriesMcpTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    private sealed class Agent(DaemonFixture daemon, string key, string folder, Guid projectId, Guid typeId)
    {
        public string Folder { get; } = folder;
        public Guid ProjectId { get; } = projectId;
        public Guid TypeId { get; } = typeId;

        private object With(object? args)
        {
            var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args ?? new { }))!.AsObject();
            node["projectId"] ??= ProjectId.ToString();
            return node;
        }

        public async Task<JsonNode> Call(string tool, object? args = null)
        {
            var (isError, text) = await daemon.CallToolResult(key, tool, With(args));
            Assert.False(isError, $"{tool} failed: {text}");
            return JsonNode.Parse(text)!;
        }

        /// <summary>Инструмент должен вернуть ошибку; возвращает её текст, начиная с кода <c>[invalid]</c> и т. п.</summary>
        public async Task<string> Fail(string tool, object? args = null)
        {
            var (isError, text) = await daemon.CallToolResult(key, tool, With(args));
            Assert.True(isError, $"{tool} unexpectedly succeeded: {text}");
            return text;
        }

        public Task<(bool IsError, string Text)> Raw(string tool, object args) => daemon.CallToolResult(key, tool, args);

        public Task<JsonNode> NewSeries(string prefix, string? name = null) => Call("create_series", new { name = name ?? prefix + " series", prefix });

        public Task<JsonNode> NewTask(string title, params JsonNode[] series) =>
            Call("create_task", series.Length == 0
                ? new { title, typeId = TypeId }
                : (object)new { title, typeId = TypeId, seriesIds = series.Select(x => x["id"]!.GetValue<string>()).ToArray() });
    }

    private async Task<Agent> Start()
    {
        var folder = await _daemon.Workspace("a", "Alpha");
        var status = await _daemon.Start();
        var key = status.Workspaces.Single().Key!;
        var projectId = Guid.Parse(JsonNode.Parse(await _daemon.CallTool(key, "list_projects"))!["data"]![0]!["id"]!.GetValue<string>());

        async Task<JsonNode> Create(string tool, object args)
        {
            var (isError, text) = await _daemon.CallToolResult(key, tool, args);
            Assert.False(isError, text);
            return JsonNode.Parse(text)!;
        }

        var todo = await Create("create_status", new { projectId, name = "Todo", color = "#112233" });
        var set = await Create("create_status_set", new { projectId, name = "Set", statusIds = new[] { todo["id"]!.GetValue<string>() } });
        var type = await Create("create_task_type", new { projectId, name = "Task", statusSetId = set["id"]!.GetValue<string>() });
        return new Agent(_daemon, key, folder, projectId, Guid.Parse(type["id"]!.GetValue<string>()));
    }

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();
    private static string Version(JsonNode node) => node["version"]!.GetValue<string>();

    private static string[] Numbers(JsonNode task) =>
        task["seriesNumbers"]!.AsArray().Select(x => $"{x!["seriesId"]}:{x["number"]}").ToArray();

    [Fact]
    public async Task The_series_tools_are_listed_with_the_right_markers_and_a_guid_array_parameter()
    {
        var agent = await Start();
        var key = (await _daemon.Status())!.Workspaces.Single().Key!;

        var tools = (await _daemon.Rpc("tools/list"))["tools"]!.AsArray().ToDictionary(x => x!["name"]!.GetValue<string>(), x => x!);

        foreach (var name in new[] { "list_series", "get_series", "create_series", "update_series", "delete_series", "series_health",
                     "add_task_to_series", "remove_task_from_series", "renumber_task", "find_tasks_by_reference" })
            Assert.True(tools.ContainsKey(name), $"tool {name} is missing");

        foreach (var name in new[] { "list_series", "get_series", "series_health", "find_tasks_by_reference" })
            Assert.True(tools[name]["annotations"]!["readOnlyHint"]!.GetValue<bool>(), name);
        Assert.True(tools["delete_series"]["annotations"]!["destructiveHint"]!.GetValue<bool>());
        Assert.Contains("removed", tools["delete_series"]["description"]!.GetValue<string>());

        // Guid[] приходит от агента, а не из DI: параметр есть в схеме, он массив и не обязателен.
        var seriesIds = tools["create_task"]["inputSchema"]!["properties"]!["seriesIds"]!;
        Assert.Contains("array", seriesIds.ToJsonString());
        Assert.DoesNotContain("seriesIds", tools["create_task"]["inputSchema"]!["required"]!.ToJsonString());
        Assert.NotNull(tools["list_tasks"]["inputSchema"]!["properties"]!["seriesId"]);
        Assert.NotNull(agent);
    }

    [Fact]
    public async Task Series_crud_numbering_references_and_cascade_through_mcp_are_visible_in_the_cli()
    {
        var agent = await Start();

        var tsk = await agent.NewSeries("TSK", "Tasks");
        var bug = await agent.NewSeries("BUG");
        Assert.Equal("TSK", tsk["prefix"]!.GetValue<string>());
        Assert.Equal("BUG", (await agent.Call("get_series", new { series = "BUG" }))["prefix"]!.GetValue<string>());
        Assert.Equal(Id(tsk), Id(await agent.Call("get_series", new { series = Id(tsk) })));

        var list = await agent.Call("list_series");
        Assert.Equal(2, list["totalCount"]!.GetValue<int>());
        Assert.Equal(["BUG", "TSK"], list["data"]!.AsArray().Select(x => x!["prefix"]!.GetValue<string>()).ToArray());

        // Guid[] в create_task: номера сразу по нескольким сериям.
        var first = await agent.NewTask("first", tsk);
        var second = await agent.NewTask("second", tsk, bug);
        var plain = await agent.NewTask("plain");
        Assert.Equal([$"{Id(tsk)}:1"], Numbers(first));
        Assert.Equal([$"{Id(tsk)}:2", $"{Id(bug)}:1"], Numbers(second));
        Assert.Empty(plain["seriesNumbers"]!.AsArray());

        // list_tasks по серии.
        var titles = (await agent.Call("list_tasks", new { seriesId = Id(bug) }))["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray();
        Assert.Equal(["second"], titles);
        Assert.Equal(3, (await agent.Call("list_tasks"))["totalCount"]!.GetValue<int>());

        // Добавить в серию по префиксу, повторно — без изменений.
        var added = await agent.Call("add_task_to_series", new { series = "BUG", taskId = Id(plain), version = Version(plain) });
        Assert.Equal([$"{Id(bug)}:2"], Numbers(added));
        var again = await agent.Call("add_task_to_series", new { series = Id(bug), taskId = Id(plain), version = Version(added) });
        Assert.Equal([$"{Id(bug)}:2"], Numbers(again));

        // Поиск по ссылке: PREFIX-number и Guid; нет совпадений — пустой список; префикс с учётом регистра.
        Assert.Equal([Id(second)], (await agent.Call("find_tasks_by_reference", new { reference = "TSK-2" }))["tasks"]!.AsArray().Select(x => Id(x!)).ToArray());
        Assert.Equal([Id(first)], (await agent.Call("find_tasks_by_reference", new { reference = Id(first) }))["tasks"]!.AsArray().Select(x => Id(x!)).ToArray());
        Assert.Empty((await agent.Call("find_tasks_by_reference", new { reference = "TSK-77" }))["tasks"]!.AsArray());
        Assert.Empty((await agent.Call("find_tasks_by_reference", new { reference = "tsk-1" }))["tasks"]!.AsArray());

        // Перенумерация: на свободный номер, на следующий свободный.
        var moved = await agent.Call("renumber_task", new { series = "TSK", taskId = Id(first), version = Version(first), to = 10 });
        Assert.Equal([$"{Id(tsk)}:10"], Numbers(moved));
        var next = await agent.Call("renumber_task", new { series = "TSK", taskId = Id(second), version = Version(second) });
        Assert.Equal([$"{Id(tsk)}:11", $"{Id(bug)}:1"], Numbers(next));

        // Убрать из серии.
        var removed = await agent.Call("remove_task_from_series", new { series = "BUG", taskId = Id(next), version = Version(next) });
        Assert.Equal([$"{Id(tsk)}:11"], Numbers(removed));

        // Переименование серии: номера остаются, ссылка по новому префиксу.
        var renamed = await agent.Call("update_series", new { seriesId = Id(tsk), version = Version(tsk), prefix = "NEW", name = "Newer" });
        Assert.Equal("NEW", renamed["prefix"]!.GetValue<string>());
        Assert.Equal("Newer", renamed["name"]!.GetValue<string>());
        Assert.Equal([Id(removed)], (await agent.Call("find_tasks_by_reference", new { reference = "NEW-11" }))["tasks"]!.AsArray().Select(x => Id(x!)).ToArray());

        var health = await agent.Call("series_health");
        Assert.Empty(health["numberConflicts"]!.AsArray());
        Assert.Empty(health["prefixConflicts"]!.AsArray());
        Assert.Equal(0, health["tasksWithInvalidSeries"]!.GetValue<int>());

        // Командная строка видит то же самое, что записал MCP.
        var cli = await TaskerProcess.Run("task", "list", "--json", "--project", agent.ProjectId.ToString(), "-w", agent.Folder);
        Assert.Equal(0, cli.Code);
        var byTitle = cli.Json["data"]!.AsArray().ToDictionary(x => x!["title"]!.GetValue<string>(), x => x!);
        Assert.Equal([$"{Id(tsk)}:10"], Numbers(byTitle["first"]));
        Assert.Equal([$"{Id(tsk)}:11"], Numbers(byTitle["second"]));
        Assert.Equal([$"{Id(bug)}:2"], Numbers(byTitle["plain"]));
        Assert.True(File.Exists(FileFinder.In(Path.Combine(agent.Folder, ".tasker", "projects", agent.ProjectId.ToString("D"), "series"), Guid.Parse(Id(tsk)))));

        // Удаление серии с задачами: номера убираются, задачи остаются.
        var (deleteFailed, deleted) = await agent.Raw("delete_series", new { projectId = agent.ProjectId, seriesId = Id(bug), version = Version(bug) });
        Assert.False(deleteFailed);
        Assert.Equal("deleted", deleted);
        Assert.Empty((await agent.Call("get_task", new { taskId = Id(plain) }))["seriesNumbers"]!.AsArray());
        Assert.Equal(3, (await agent.Call("list_tasks"))["totalCount"]!.GetValue<int>());
        Assert.Equal(1, (await agent.Call("list_series"))["totalCount"]!.GetValue<int>());
        Assert.Contains("not_found", await agent.Fail("get_series", new { series = "BUG" }));
    }

    [Fact]
    public async Task Errors_carry_codes_an_agent_can_react_to()
    {
        var agent = await Start();
        var tsk = await agent.NewSeries("TSK");
        var one = await agent.NewTask("one", tsk);
        var two = await agent.NewTask("two", tsk);
        var unknown = Guid.NewGuid().ToString();

        // [invalid]: префикс, имя, ссылка.
        foreach (var prefix in new[] { "", "TSK-1", "with space", new string('A', 21) })
        {
            var text = await agent.Fail("create_series", new { name = "X", prefix });
            Assert.True(text.StartsWith("[invalid]"), $"prefix '{prefix}': {text}");
        }
        Assert.StartsWith("[invalid]", await agent.Fail("create_series", new { name = " ", prefix = "OK" }));
        Assert.StartsWith("[invalid]", await agent.Fail("update_series", new { seriesId = Id(tsk), version = Version(tsk), prefix = "bad-one" }));
        Assert.StartsWith("[invalid]", await agent.Fail("find_tasks_by_reference", new { reference = "not a reference" }));
        Assert.StartsWith("[invalid]", await agent.Fail("create_task", new { title = "x", typeId = agent.TypeId, seriesIds = new[] { unknown } }));

        // [in_use]: занятый префикс, занятый номер.
        Assert.StartsWith("[in_use]", await agent.Fail("create_series", new { name = "Again", prefix = "TSK" }));
        var other = await agent.NewSeries("BUG");
        Assert.StartsWith("[in_use]", await agent.Fail("update_series", new { seriesId = Id(other), version = Version(other), prefix = "TSK" }));
        Assert.StartsWith("[in_use]", await agent.Fail("renumber_task", new { series = "TSK", taskId = Id(two), version = Version(two), to = 1 }));
        Assert.StartsWith("[in_use]", await agent.Fail("renumber_task", new { series = "TSK", taskId = Id(two), version = Version(two), to = 0 }));

        // [modified]: устаревшая версия у серии и у задачи.
        await agent.Call("update_series", new { seriesId = Id(tsk), version = Version(tsk), name = "Changed" });
        Assert.StartsWith("[modified]", await agent.Fail("update_series", new { seriesId = Id(tsk), version = Version(tsk), name = "Stale" }));
        Assert.StartsWith("[modified]", await agent.Fail("delete_series", new { seriesId = Id(tsk), version = Version(tsk) }));
        Assert.StartsWith("[modified]", await agent.Fail("add_task_to_series", new { series = "BUG", taskId = Id(one), version = "stale" }));
        Assert.StartsWith("[modified]", await agent.Fail("remove_task_from_series", new { series = "TSK", taskId = Id(one), version = "stale" }));
        Assert.StartsWith("[modified]", await agent.Fail("renumber_task", new { series = "TSK", taskId = Id(one), version = "stale" }));

        // [not_found]: серия, задача, задача не в серии, проект.
        Assert.StartsWith("[not_found]", await agent.Fail("get_series", new { series = "NOPE" }));
        Assert.StartsWith("[not_found]", await agent.Fail("get_series", new { series = unknown }));
        Assert.StartsWith("[not_found]", await agent.Fail("update_series", new { seriesId = unknown, version = "v" }));
        Assert.StartsWith("[not_found]", await agent.Fail("delete_series", new { seriesId = unknown, version = "v" }));
        Assert.StartsWith("[not_found]", await agent.Fail("add_task_to_series", new { series = "NOPE", taskId = Id(one), version = Version(one) }));
        Assert.StartsWith("[not_found]", await agent.Fail("add_task_to_series", new { series = "TSK", taskId = unknown, version = "v" }));
        Assert.StartsWith("[not_found]", await agent.Fail("remove_task_from_series", new { series = "TSK", taskId = unknown, version = "v" }));
        Assert.StartsWith("[not_found]", await agent.Fail("renumber_task", new { series = "BUG", taskId = Id(one), version = Version(one) }));
        Assert.StartsWith("[not_found]", await agent.Fail("list_series", new { projectId = unknown }));
        Assert.StartsWith("[not_found]", await agent.Fail("series_health", new { projectId = unknown }));
        Assert.StartsWith("[not_found]", await agent.Fail("find_tasks_by_reference", new { projectId = unknown, reference = "TSK-1" }));
    }
}
