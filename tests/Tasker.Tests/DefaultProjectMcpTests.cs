using System.Text.Json.Nodes;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// <c>projectId</c> в инструментах MCP необязателен, если в области ровно один проект: его подставляет фильтр вызова
/// (<c>DefaultProject</c>), а схема инструментов объявляет параметр необязательным. Через настоящий демон MCP.
/// </summary>
public class DefaultProjectMcpTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    private async Task<string> StartWithOneProject()
    {
        await _daemon.Workspace("a", "Alpha");
        return (await _daemon.Start()).Workspaces.Single().Key!;
    }

    private async Task<JsonNode> Call(string key, string tool, object? args = null)
    {
        var (isError, text) = await _daemon.CallToolResult(key, tool, args);
        Assert.False(isError, $"{tool}: {text}");
        return JsonNode.Parse(text)!;
    }

    private async Task<string> Fail(string key, string tool, object? args = null)
    {
        var (isError, text) = await _daemon.CallToolResult(key, tool, args);
        Assert.True(isError, $"{tool} unexpectedly succeeded: {text}");
        return text;
    }

    [Fact]
    public async Task Error_text_starts_with_code_without_sdk_prefix()
    {
        var key = await StartWithOneProject();

        // Ошибка инструмента (McpException): isError, код в начале, без «An error occurred invoking».
        var (isError, text) = await _daemon.CallToolResult(key, "get_task_links", new { taskId = Guid.NewGuid() });
        Assert.True(isError);
        Assert.StartsWith("[not_found] ", text);
        Assert.DoesNotContain("An error occurred", text);

        // Ошибка проверки Core ([invalid]) и ошибка фильтра проекта по умолчанию.
        (isError, text) = await _daemon.CallToolResult(key, "create_status", new { name = "", color = "#112233" });
        Assert.True(isError);
        Assert.StartsWith("[invalid] ", text);
        Assert.DoesNotContain("An error occurred", text);

        (isError, text) = await _daemon.CallToolResult(key, "list_statuses", new { projectId = Guid.NewGuid() });
        Assert.True(isError);
        Assert.StartsWith("[not_found] ", text);
    }

    private async Task<string> ProjectId(string key) =>
        (await Call(key, "list_projects"))["data"]![0]!["id"]!.GetValue<string>();

    // ---- схема ----

    [Fact]
    public async Task Tools_of_a_project_declare_project_id_optional_and_other_tools_are_unchanged()
    {
        var key = await StartWithOneProject();

        var tools = (await _daemon.Rpc("tools/list"))["tools"]!.AsArray().ToDictionary(x => x!["name"]!.GetValue<string>(), x => x!);

        var scoped = tools.Values.Where(x => x["inputSchema"]!["properties"]!.AsObject().ContainsKey("projectId")).ToArray();
        Assert.True(scoped.Length > 30, $"project tools: {scoped.Length}");
        foreach (var tool in scoped)
        {
            var schema = tool["inputSchema"]!;
            var required = schema["required"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? [];
            Assert.DoesNotContain("projectId", required);
            Assert.Contains("Optional if the workspace has exactly one project", schema["properties"]!["projectId"]!["description"]!.GetValue<string>());
        }

        // Остальные обязательные параметры остались обязательными.
        Assert.Equal(["name", "color"], tools["create_status"]["inputSchema"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Equal(["statusId", "version"], tools["delete_status"]["inputSchema"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());

        // Инструменты без projectId — как были.
        Assert.False(tools["list_projects"]["inputSchema"]!["properties"]!.AsObject().ContainsKey("projectId"));
        Assert.Equal(["name"], tools["create_project"]["inputSchema"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.DoesNotContain("exactly one project", tools["create_project"].ToJsonString());

        // Повторный запрос списка не дописывает примечание второй раз.
        var again = (await _daemon.Rpc("tools/list"))["tools"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == "list_statuses")!;
        Assert.Equal(2, again["inputSchema"]!["properties"]!["projectId"]!["description"]!.GetValue<string>().Split("Optional if the workspace has exactly one project").Length);
    }

    // ---- один проект ----

    [Fact]
    public async Task With_one_project_every_project_tool_works_without_project_id()
    {
        var key = await StartWithOneProject();
        var projectId = await ProjectId(key);

        var todo = await Call(key, "create_status", new { name = "Todo", color = "#112233" });
        var done = await Call(key, "create_status", new { name = "Done", color = "#445566" });
        var set = await Call(key, "create_status_set", new { name = "Flow", statusIds = new[] { todo["id"]!.GetValue<string>(), done["id"]!.GetValue<string>() } });
        var type = await Call(key, "create_task_type", new { name = "Bug", statusSetId = set["id"]!.GetValue<string>() });
        var a = await Call(key, "create_task", new { title = "Fix login", typeId = type["id"]!.GetValue<string>() });
        var b = await Call(key, "create_task", new { title = "Release", typeId = type["id"]!.GetValue<string>() });

        // Всё создано в единственном проекте.
        Assert.Equal(a["projectId"]!.GetValue<string>(), projectId);
        Assert.Equal(2, (await Call(key, "list_statuses"))["totalCount"]!.GetValue<int>());
        Assert.Equal(2, (await Call(key, "list_tasks"))["totalCount"]!.GetValue<int>());

        // Без projectId и с ним — один и тот же результат.
        Assert.Equal((await Call(key, "list_tasks")).ToJsonString(), (await Call(key, "list_tasks", new { projectId })).ToJsonString());

        // Инструменты с другими обязательными параметрами и разных групп: связи, блокировка, серии, правка.
        var links = await Call(key, "link_tasks", new { taskId = a["id"]!.GetValue<string>(), link = "blocks", otherTaskId = b["id"]!.GetValue<string>() });
        Assert.Equal("Release", links["links"]![0]!["task"]!["title"]!.GetValue<string>());
        Assert.Equal("project", (await Call(key, "lock_entity", new { entity = "project" }))["entity"]!.GetValue<string>());
        var series = await Call(key, "create_series", new { name = "Tasks", prefix = "TSK" });
        Assert.Equal("TSK", series["prefix"]!.GetValue<string>());
        var renamed = await Call(key, "update_status", new { statusId = todo["id"]!.GetValue<string>(), version = todo["version"]!.GetValue<string>(), name = "To do" });
        Assert.Equal("To do", renamed["name"]!.GetValue<string>());
        Assert.StartsWith("[not_found]", await Fail(key, "get_task_links", new { taskId = Guid.NewGuid() })); // обычные ошибки инструмента сохраняются
    }

    [Fact]
    public async Task An_explicit_project_id_wins_and_null_means_not_given()
    {
        var key = await StartWithOneProject();
        var projectId = await ProjectId(key);

        Assert.Equal(0, (await Call(key, "list_statuses", new { projectId }))["totalCount"]!.GetValue<int>());
        Assert.Equal(0, (await Call(key, "list_statuses", new { projectId = (string?)null }))["totalCount"]!.GetValue<int>());

        // Указанный, но чужой или несуществующий проект — по-прежнему not_found, а не «возьмём единственный».
        Assert.StartsWith("[not_found]", await Fail(key, "list_statuses", new { projectId = Guid.NewGuid() }));
    }

    // ---- несколько проектов и ни одного ----

    [Fact]
    public async Task With_several_projects_project_id_is_required_and_the_error_says_how_many()
    {
        var key = await StartWithOneProject();
        var alpha = await ProjectId(key);
        var beta = (await Call(key, "create_project", new { name = "Beta" }))["id"]!.GetValue<string>();

        var error = await Fail(key, "list_statuses");
        Assert.StartsWith("[invalid] projectId is required", error);
        Assert.Contains("2 projects", error);
        Assert.Contains("list_projects", error);
        Assert.StartsWith("[invalid] projectId is required", await Fail(key, "create_status", new { name = "Todo", color = "#112233" }));
        Assert.StartsWith("[invalid] projectId is required", await Fail(key, "list_tasks", new { projectId = (string?)null }));

        // С projectId — как раньше; инструменты без него от числа проектов не зависят.
        await Call(key, "create_status", new { projectId = beta, name = "Todo", color = "#112233" });
        Assert.Equal(1, (await Call(key, "list_statuses", new { projectId = beta }))["totalCount"]!.GetValue<int>());
        Assert.Equal(0, (await Call(key, "list_statuses", new { projectId = alpha }))["totalCount"]!.GetValue<int>());
        Assert.Equal(2, (await Call(key, "list_projects"))["totalCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Without_projects_the_error_says_to_create_one_and_the_default_works_right_after()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_daemon.Root, "empty")).FullName;
        Assert.Equal(0, (await _daemon.Mcp("workspace", "add", folder)).Code);
        var key = (await _daemon.Start()).Workspaces.Single().Key!;

        var error = await Fail(key, "list_statuses");
        Assert.StartsWith("[invalid] projectId is required", error);
        Assert.Contains("no projects", error);
        Assert.Contains("create_project", error);

        await Call(key, "create_project", new { name = "First" });

        Assert.Equal(0, (await Call(key, "list_statuses"))["totalCount"]!.GetValue<int>());
    }
}
