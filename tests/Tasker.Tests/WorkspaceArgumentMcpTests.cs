using System.Text.Json.Nodes;
using Tasker.Daemon;
using Tasker.Storage.Files.Workspaces;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Общий MCP: один адрес <c>/mcp</c> на все рабочие области, область — необязательный аргумент <c>workspace</c> каждого инструмента
/// (<c>WorkspaceArgument</c>). Через настоящий демон MCP.
/// </summary>
public class WorkspaceArgumentMcpTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    private async Task<DaemonStatus> StartWithTwo()
    {
        await _daemon.Workspace("a", "Alpha");
        await _daemon.Workspace("b", "Beta");
        return await _daemon.Start();
    }

    private static string KeyOf(DaemonStatus status, string folder) =>
        status.Workspaces.Single(x => x.Path.EndsWith("/" + folder)).Key!;

    private async Task<JsonNode> Call(string? workspace, string tool, object? args = null)
    {
        var (isError, text) = await _daemon.CallToolResult(workspace, tool, args);
        Assert.False(isError, $"{tool}: {text}");
        return JsonNode.Parse(text)!;
    }

    private async Task<string> Fail(string? workspace, string tool, object? args = null)
    {
        var (isError, text) = await _daemon.CallToolResult(workspace, tool, args);
        Assert.True(isError, $"{tool} unexpectedly succeeded: {text}");
        return text;
    }

    // ---- выбор области аргументом ----

    [Fact]
    public async Task Two_workspaces_in_one_daemon_give_different_data_by_the_argument()
    {
        var status = await StartWithTwo();
        var a = KeyOf(status, "a");
        var b = KeyOf(status, "b");

        Assert.Equal(["Alpha"], DaemonFixture.ProjectNames((await Call(a, "list_projects")).ToJsonString()));
        Assert.Equal(["Beta"], DaemonFixture.ProjectNames((await Call(b, "list_projects")).ToJsonString()));

        // Запись тоже идёт в выбранную область, а инструменты внутри проекта работают в ней с её единственным проектом.
        await Call(b, "create_status", new { name = "Todo", color = "#112233" });
        Assert.Equal(1, (await Call(b, "list_statuses"))["totalCount"]!.GetValue<int>());
        Assert.Equal(0, (await Call(a, "list_statuses"))["totalCount"]!.GetValue<int>());
        await Call(a, "create_project", new { name = "Gamma" });
        Assert.Equal(["Alpha", "Gamma"], DaemonFixture.ProjectNames((await Call(a, "list_projects")).ToJsonString()));
        Assert.Equal(["Beta"], DaemonFixture.ProjectNames((await Call(b, "list_projects")).ToJsonString()));

        // Каждая область изменилась в своей папке.
        Assert.Contains("Gamma", (await TaskerProcess.Run("project", "list", "-w", status.Workspaces.Single(x => x.Key == a).Path)).Out);
        Assert.DoesNotContain("Gamma", (await TaskerProcess.Run("project", "list", "-w", status.Workspaces.Single(x => x.Key == b).Path)).Out);
    }

    [Fact]
    public async Task The_workspace_can_be_named_by_path_and_the_name_ignores_case()
    {
        var status = await StartWithTwo();
        var path = status.Workspaces.Single(x => x.Path.EndsWith("/b")).Path;

        Assert.Equal(["Beta"], DaemonFixture.ProjectNames((await Call(path, "list_projects")).ToJsonString()));
        Assert.Equal(["Alpha"], DaemonFixture.ProjectNames((await Call(KeyOf(status, "a").ToUpperInvariant(), "list_projects")).ToJsonString()));
    }

    [Fact]
    public async Task Folders_with_the_same_name_get_a_suffix_and_stay_distinct()
    {
        await _daemon.Workspace("x/same", "First");
        await _daemon.Workspace("y/same", "Second");
        var status = await _daemon.Start();

        var keys = status.Workspaces.Select(x => x.Key!).Order().ToArray();
        Assert.Equal(["same", "same-2"], keys);
        var names = keys.Select(async key => DaemonFixture.ProjectNames((await Call(key, "list_projects")).ToJsonString()).Single());
        Assert.Equal(["First", "Second"], (await Task.WhenAll(names)).Order().ToArray());
    }

    // ---- одна область: аргумент не нужен ----

    [Fact]
    public async Task With_one_workspace_the_argument_can_be_omitted()
    {
        await _daemon.Workspace("a", "Alpha");
        var key = (await _daemon.Start()).Workspaces.Single().Key!;

        Assert.Equal(["Alpha"], DaemonFixture.ProjectNames((await Call(null, "list_projects")).ToJsonString()));
        // Вместе с проектом по умолчанию: ни область, ни проект указывать не нужно.
        await Call(null, "create_status", new { name = "Todo", color = "#112233" });
        Assert.Equal(1, (await Call(null, "list_statuses"))["totalCount"]!.GetValue<int>());
        // С аргументом — тот же результат; пустое значение и null — «не указан».
        Assert.Equal(1, (await Call(key, "list_statuses"))["totalCount"]!.GetValue<int>());
        Assert.Equal(1, (await Call("", "list_statuses"))["totalCount"]!.GetValue<int>());
    }

    // ---- несколько областей: аргумент обязателен ----

    [Fact]
    public async Task With_several_workspaces_the_argument_is_required_and_the_error_lists_them()
    {
        var status = await StartWithTwo();

        foreach (var tool in new[] { "list_projects", "list_statuses", "whoami" })
        {
            var error = await Fail(null, tool);
            Assert.StartsWith("[invalid] workspace is required", error);
            Assert.Contains("2 workspaces", error);
            Assert.Contains(KeyOf(status, "a"), error);
            Assert.Contains(KeyOf(status, "b"), error);
            Assert.Contains("list_workspaces", error);
        }
    }

    [Fact]
    public async Task Without_workspaces_the_error_says_how_to_allow_one()
    {
        await _daemon.Start();

        var error = await Fail(null, "list_projects");

        Assert.StartsWith("[invalid] workspace is required", error);
        Assert.Contains("tasker mcp workspace add", error);
        Assert.Empty((await Call(null, "list_workspaces"))["workspaces"]!.AsArray());
    }

    // ---- только разрешённые области ----

    [Fact]
    public async Task A_folder_that_is_not_allowed_cannot_be_opened_by_name_or_path_and_looks_like_an_unknown_one()
    {
        var forbidden = await _daemon.Workspace("secret", "Secret", allow: false);
        var status = await StartWithTwo();

        var byPath = await Fail(forbidden, "list_projects");
        var byName = await Fail("secret", "list_projects");
        var unknown = await Fail("nothing-like-it", "list_projects");

        Assert.StartsWith("[not_found] Workspace '", byPath);
        Assert.Equal(byName.Replace("secret", "X"), unknown.Replace("nothing-like-it", "X"));
        Assert.Equal(byPath.Replace(forbidden, "X"), unknown.Replace("nothing-like-it", "X"));
        Assert.DoesNotContain("Secret", byPath + byName + unknown);

        // И не только чтение: создать проект в чужой папке тоже нельзя.
        Assert.StartsWith("[not_found]", await Fail(forbidden, "create_project", new { name = "Evil" }));
        Assert.DoesNotContain("Evil", (await TaskerProcess.Run("project", "list", "-w", forbidden)).Out);

        // Ни имя «чужой» папки, ни её путь не попадают в список.
        var listed = (await Call(null, "list_workspaces")).ToJsonString();
        Assert.DoesNotContain("Secret", listed);
        Assert.DoesNotContain(forbidden, listed);
        Assert.Equal(2, status.Workspaces.Length);
    }

    [Fact]
    public async Task A_workspace_removed_from_the_settings_stops_being_available()
    {
        var status = await StartWithTwo();
        var a = KeyOf(status, "a");
        var b = status.Workspaces.Single(x => x.Path.EndsWith("/b"));
        Assert.Equal(["Beta"], DaemonFixture.ProjectNames((await Call(b.Key, "list_projects")).ToJsonString()));

        await _daemon.Mcp("workspace", "remove", b.Path);
        for (var i = 0; i < 150 && (await _daemon.Status())!.Workspaces.Length != 1; i++)
            await Task.Delay(100);

        Assert.StartsWith("[not_found]", await Fail(b.Key, "list_projects"));
        Assert.StartsWith("[not_found]", await Fail(b.Path, "list_projects"));
        // Осталась одна — аргумент снова необязателен.
        Assert.Equal(["Alpha"], DaemonFixture.ProjectNames((await Call(null, "list_projects")).ToJsonString()));
        Assert.Equal(["Alpha"], DaemonFixture.ProjectNames((await Call(a, "list_projects")).ToJsonString()));
    }

    // ---- list_workspaces ----

    [Fact]
    public async Task List_workspaces_shows_name_path_status_and_project_count()
    {
        await _daemon.Workspace("c", "Gamma");
        var status = await StartWithTwo();
        await Call(KeyOf(status, "a"), "create_project", new { name = "Alpha2" });

        var listed = (await Call(null, "list_workspaces"))["workspaces"]!.AsArray().Select(x => x!).ToArray();

        Assert.Equal(3, listed.Length);
        var a = listed.Single(x => x["name"]!.GetValue<string>() == "a");
        Assert.Equal("open", a["status"]!.GetValue<string>());
        Assert.Equal(WorkspaceLocation.Files(Path.Combine(_daemon.Root, "a")).Path, a["path"]!.GetValue<string>());
        Assert.Equal(2, a["projects"]!.GetValue<int>());
        Assert.Equal(1, listed.Single(x => x["name"]!.GetValue<string>() == "b")["projects"]!.GetValue<int>());
        Assert.Equal(["a", "b", "c"], listed.Select(x => x["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task List_workspaces_reports_a_workspace_that_did_not_open_and_the_others_keep_working()
    {
        var gone = await _daemon.Workspace("gone", "Gone");
        await _daemon.Workspace("kept", "Kept");
        Directory.Delete(gone, recursive: true);
        await _daemon.Start();

        var listed = (await Call(null, "list_workspaces"))["workspaces"]!.AsArray().Select(x => x!).ToArray();

        var failed = listed.Single(x => x["name"]!.GetValue<string>() == "gone");
        Assert.Equal("failed", failed["status"]!.GetValue<string>());
        Assert.Contains("does not exist", failed["error"]!.GetValue<string>());
        Assert.Null(failed["projects"]);
        Assert.Equal("open", listed.Single(x => x["name"]!.GetValue<string>() == "kept")["status"]!.GetValue<string>());

        // Работать в неоткрывшейся области нельзя, и агент узнаёт почему; в открытой — можно.
        var error = await Fail("gone", "list_projects");
        Assert.StartsWith("[failed] Workspace 'gone' could not be opened: ", error);
        Assert.Contains("does not exist", error);
        Assert.Equal(["Kept"], DaemonFixture.ProjectNames((await Call("kept", "list_projects")).ToJsonString()));
        // Разрешено две области, одна из них не открыта: без аргумента неясно, какая нужна.
        Assert.StartsWith("[invalid] workspace is required", await Fail(null, "list_projects"));
    }

    // ---- схема ----

    [Fact]
    public async Task Every_tool_except_list_workspaces_has_an_optional_workspace_argument()
    {
        await StartWithTwo();

        var tools = (await _daemon.Rpc("tools/list"))["tools"]!.AsArray().ToDictionary(x => x!["name"]!.GetValue<string>(), x => x!);

        Assert.True(tools.Count > 40, $"tools: {tools.Count}");
        foreach (var (name, tool) in tools)
        {
            var schema = tool["inputSchema"]!;
            var properties = schema["properties"]!.AsObject();
            var required = schema["required"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? [];
            if (name == "list_workspaces")
            {
                Assert.False(properties.ContainsKey("workspace"));
                continue;
            }

            Assert.True(properties.ContainsKey("workspace"), $"{name} has no workspace argument");
            Assert.Equal("string", properties["workspace"]!["type"]!.GetValue<string>());
            Assert.DoesNotContain("workspace", required);
        }

        // Остальные обязательные параметры остались обязательными, описание не дублируется при повторном запросе.
        Assert.Equal(["name", "color"], tools["create_status"]["inputSchema"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        var again = (await _daemon.Rpc("tools/list"))["tools"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == "list_projects")!;
        Assert.Contains("list_workspaces", again["inputSchema"]!["properties"]!["workspace"]!["description"]!.GetValue<string>());
        Assert.Equal(1, again["inputSchema"]!["properties"]!["workspace"]!["description"]!.GetValue<string>().Split("Optional if exactly one workspace").Length - 1);
    }

    // ---- адреса ----

    [Fact]
    public async Task The_old_per_workspace_address_is_gone_and_the_shared_one_is_the_only_mcp()
    {
        var status = await StartWithTwo();
        var key = KeyOf(status, "a");

        Assert.Equal(404, await _daemon.PostStatus($"http://127.0.0.1:{_daemon.Port}/w/{key}/mcp"));
        Assert.Equal(404, await _daemon.PostStatus($"http://127.0.0.1:{_daemon.Port}/w/nothing/mcp"));
        Assert.Equal(200, await _daemon.PostStatus(_daemon.McpUrl));
        Assert.Equal(_daemon.McpUrl, status.McpUrl);
    }

    // ---- формат ошибок ----

    [Fact]
    public async Task Workspace_errors_start_with_the_code_in_brackets_without_the_sdk_prefix()
    {
        var status = await StartWithTwo();

        var errors = new[]
        {
            await Fail(null, "list_projects"),
            await Fail("nothing", "list_projects"),
            await Fail(KeyOf(status, "a"), "list_statuses", new { projectId = Guid.NewGuid() })
        };

        Assert.StartsWith("[invalid] ", errors[0]);
        Assert.StartsWith("[not_found] ", errors[1]);
        Assert.StartsWith("[not_found] ", errors[2]);
        Assert.All(errors, x => Assert.DoesNotContain("An error occurred", x));

        // Значение не строкой — тоже [invalid], а не падение.
        var (isError, text) = await _daemon.CallToolResult(null, "list_projects", new { workspace = 5 });
        Assert.True(isError);
        Assert.StartsWith("[invalid] workspace must be a string", text);
    }

    // ---- агент в области ----

    [Fact]
    public async Task The_agent_header_is_checked_against_the_chosen_workspace()
    {
        var status = await StartWithTwo();
        var a = KeyOf(status, "a");
        var b = KeyOf(status, "b");

        // Локальный агент области a: его id действует в a и не действует в b (у каждой области свои пользователи).
        var agentA = (await Call(a, "whoami"))["id"]!.GetValue<string>();
        var asAgent = new Dictionary<string, string> { ["X-Tasker-Agent"] = agentA };

        var (isError, text) = await _daemon.CallToolResult(a, "whoami", headers: asAgent);
        Assert.False(isError, text);
        Assert.Equal(agentA, JsonNode.Parse(text)!["id"]!.GetValue<string>());

        (isError, text) = await _daemon.CallToolResult(b, "whoami", headers: asAgent);
        Assert.True(isError);
        Assert.StartsWith("[forbidden] X-Tasker-Agent", text);

        // Без заголовка в b работает локальный агент b — другой пользователь.
        Assert.NotEqual(agentA, (await Call(b, "whoami"))["id"]!.GetValue<string>());
    }
}
