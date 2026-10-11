using Xunit;

namespace Tasker.Tests;

/// <summary>get, update и delete команды tasker — на папке с файлами и на SQLite.</summary>
public class CliChangeTests
{
    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    /// <summary>Проект Demo: статусы Todo, Doing, Done; набор Flow; тип Bug; доска Main; задача «Fix login».</summary>
    private static async Task<Guid> Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.InProject("Demo", "status", "create", "Todo"));
        await Ok(ws.InProject("Demo", "status", "create", "Doing"));
        await Ok(ws.InProject("Demo", "status", "create", "Done"));
        await Ok(ws.InProject("Demo", "status-set", "create", "Flow", "--status", "Todo", "Doing", "Done"));
        await Ok(ws.InProject("Demo", "task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(ws.InProject("Demo", "board", "create", "Main", "--status-set", "Flow",
            "--column", "Todo=Todo", "--column", "In work=Doing", "--column", "Finished=Done"));
        return (await Ok(ws.InProject("Demo", "task", "create", "Fix login", "--type", "Bug", "-d", "details"))).Id;
    }

    private static async Task<string> Get(TestWorkspace ws, params string[] args) =>
        (await Ok(ws.InProject("Demo", [.. args, "--json"]))).Out;

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Get_shows_every_kind_of_entity(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        var taskId = await Seed(ws);
        await Ok(ws.Run("user", "create", "Ivan"));
        await Ok(ws.Run("agent", "create", "bot"));

        var project = await Ok(ws.Run("project", "get", "Demo"));
        Assert.Contains("name:", project.Out);
        Assert.Contains("Demo", project.Out);
        Assert.Contains("version:", project.Out);

        Assert.Contains("#808080", (await Ok(ws.InProject("Demo", "status", "get", "Todo"))).Out);

        var set = (await Ok(ws.InProject("Demo", "status-set", "get", "Flow"))).Out;
        Assert.Contains("Todo (", set);
        Assert.Contains("Done (", set);

        Assert.Contains("Flow (", (await Ok(ws.InProject("Demo", "task-type", "get", "Bug"))).Out);

        var board = (await Ok(ws.InProject("Demo", "board", "get", "Main"))).Out;
        Assert.Contains("In work: Doing (", board);
        Assert.Contains("Flow (", board);

        var task = (await Ok(ws.InProject("Demo", "task", "get", taskId.ToString()))).Out;
        Assert.Contains("Fix login", task);
        Assert.Contains("Bug (", task);
        Assert.Contains("Todo (", task);
        Assert.EndsWith("details\n", task.ReplaceLineEndings("\n"));

        Assert.Contains("Ivan", (await Ok(ws.Run("user", "get", "Ivan"))).Out);
        Assert.Contains("bot", (await Ok(ws.Run("agent", "get", "bot"))).Out);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Get_json_matches_the_entity_and_accepts_id(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var id = (await Ok(ws.InProject("Demo", "status", "list", "--json"))).Json["data"]![0]!["id"]!.GetValue<Guid>();

        var byId = (await Ok(ws.InProject("Demo", "status", "get", id.ToString(), "--json"))).Json;
        var byName = (await Ok(ws.InProject("Demo", "status", "get", byId["name"]!.GetValue<string>(), "--json"))).Json;

        Assert.Equal(id, byId["id"]!.GetValue<Guid>());
        Assert.Equal(byId["version"]!.GetValue<string>(), byName["version"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Update_changes_every_kind_of_entity(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        var taskId = await Seed(ws);
        await Ok(ws.Run("user", "create", "Ivan"));
        await Ok(ws.Run("agent", "create", "bot"));

        Assert.StartsWith("Updated project 'Renamed'", (await Ok(ws.Run("project", "update", "Demo", "--name", "Renamed"))).Out);

        // Дальше проект уже называется Renamed.
        Task<CliResult> P(params string[] a) => ws.InProject("Renamed", a);

        await Ok(P("status", "update", "Todo", "--name", "Backlog", "--color", "#112233"));
        var status = (await Ok(P("status", "get", "Backlog", "--json"))).Json;
        Assert.Equal("#112233", status["color"]!.GetValue<string>());

        await Ok(P("status-set", "update", "Flow", "--name", "Flow2", "--status", "Backlog", "Doing", "Done"));
        Assert.Contains("Flow2", (await Ok(P("status-set", "list"))).Out);

        await Ok(P("task-type", "update", "Bug", "--name", "Defect"));
        Assert.Contains("Defect", (await Ok(P("task-type", "list"))).Out);

        await Ok(P("board", "update", "Main", "--name", "Sprint"));
        Assert.Contains("Sprint", (await Ok(P("board", "list"))).Out);

        await Ok(P("task", "update", taskId.ToString(), "--title", "Fix logout", "--status", "Doing"));
        var task = (await Ok(P("task", "get", taskId.ToString(), "--json"))).Json;
        Assert.Equal("Fix logout", task["title"]!.GetValue<string>());
        Assert.Equal("details", task["description"]!.GetValue<string>());

        await Ok(ws.Run("user", "update", "Ivan", "--name", "Ivan2"));
        Assert.Contains("Ivan2", (await Ok(ws.Run("user", "list"))).Out);
        await Ok(ws.Run("agent", "update", "bot", "--name", "bot2"));
        Assert.Contains("bot2", (await Ok(ws.Run("agent", "list"))).Out);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Update_changes_only_the_given_fields(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        await Ok(ws.InProject("Demo", "status", "update", "Done", "--color", "#00FF00"));

        var status = (await Ok(ws.InProject("Demo", "status", "get", "Done", "--json"))).Json;
        Assert.Equal("Done", status["name"]!.GetValue<string>());
        Assert.Equal("#00FF00", status["color"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Renaming_a_set_or_board_keeps_its_statuses_and_columns(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        await Ok(ws.InProject("Demo", "status-set", "update", "Flow", "--name", "Flow2"));
        await Ok(ws.InProject("Demo", "board", "update", "Main", "--name", "Sprint"));

        Assert.Equal(3, (await Ok(ws.InProject("Demo", "status-set", "get", "Flow2", "--json"))).Json["statusIds"]!.AsArray().Count);
        var board = (await Ok(ws.InProject("Demo", "board", "get", "Sprint", "--json"))).Json;
        Assert.Single(board["statusSetIds"]!.AsArray());
        Assert.Equal(3, board["columns"]!.AsArray().Count);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Task_update_can_clear_the_description_and_change_type(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        var taskId = await Seed(ws);
        await Ok(ws.InProject("Demo", "task-type", "create", "Story", "--status-set", "Flow"));

        await Ok(ws.InProject("Demo", "task", "update", taskId.ToString(), "-d", ""));
        await Ok(ws.InProject("Demo", "task", "update", taskId.ToString(), "--type", "Story"));

        var task = (await Ok(ws.InProject("Demo", "task", "get", taskId.ToString(), "--json"))).Json;
        Assert.Null(task["description"]);
        Assert.Contains("Story (", (await Ok(ws.InProject("Demo", "task", "get", taskId.ToString()))).Out);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Board_update_keeps_columns_with_the_same_name(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var before = (await Get(ws, "board", "get", "Main"));

        await Ok(ws.InProject("Demo", "board", "update", "Main", "--column", "Todo=Todo", "--column", "Active=Doing,Done"));

        var after = System.Text.Json.Nodes.JsonNode.Parse(await Get(ws, "board", "get", "Main"))!;
        var columns = after["columns"]!.AsArray();
        Assert.Equal(["Todo", "Active"], columns.Select(x => x!["name"]!.GetValue<string>()));
        Assert.Equal(System.Text.Json.Nodes.JsonNode.Parse(before)!["columns"]![0]!["id"]!.GetValue<Guid>(), columns[0]!["id"]!.GetValue<Guid>());
        Assert.Equal(2, columns[1]!["statusIds"]!.AsArray().Count);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Delete_removes_every_kind_of_entity(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        var taskId = await Seed(ws);
        await Ok(ws.Run("user", "create", "Ivan"));
        await Ok(ws.Run("agent", "create", "bot"));

        Assert.StartsWith("Deleted task 'Fix login'", (await Ok(ws.InProject("Demo", "task", "delete", taskId.ToString()))).Out);
        Assert.Equal(0, (await ws.InProject("Demo", "task", "list", "--json")).Json["totalCount"]!.GetValue<int>());

        await Ok(ws.InProject("Demo", "board", "delete", "Main"));
        await Ok(ws.InProject("Demo", "task-type", "delete", "Bug"));
        await Ok(ws.InProject("Demo", "status-set", "delete", "Flow"));
        foreach (var status in new[] { "Todo", "Doing", "Done" })
            await Ok(ws.InProject("Demo", "status", "delete", status));
        await Ok(ws.Run("user", "delete", "Ivan"));
        await Ok(ws.Run("agent", "delete", "bot"));

        Assert.Equal(0, (await ws.InProject("Demo", "status", "list", "--json")).Json["totalCount"]!.GetValue<int>());
        Assert.Empty((await Ok(ws.Run("user", "list"))).Data);
        Assert.Empty((await Ok(ws.Run("agent", "list"))).Data);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Deleting_used_entities_is_refused(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var status = await ws.InProject("Demo", "status", "delete", "Todo");
        Assert.Equal(1, status.Code);
        Assert.StartsWith("In use:", status.Err);

        Assert.Equal(1, (await ws.InProject("Demo", "status-set", "delete", "Flow")).Code);
        Assert.Equal(1, (await ws.InProject("Demo", "task-type", "delete", "Bug")).Code);

        // Ничего не удалилось.
        Assert.Contains("Todo", (await Ok(ws.InProject("Demo", "status", "list"))).Out);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Project_delete_needs_confirmation_and_removes_everything(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(ws.Run("project", "create", "Other"));

        var refused = await ws.Run("project", "delete", "Demo");
        Assert.Equal(1, refused.Code);
        Assert.Contains("--yes", refused.Err);
        Assert.Contains("1 task", refused.Err);
        Assert.Contains("Demo", (await Ok(ws.Run("project", "list"))).Out);

        Assert.StartsWith("Deleted project 'Demo'", (await Ok(ws.Run("project", "delete", "Demo", "--yes"))).Out);

        var left = (await Ok(ws.Run("project", "list", "--json"))).Json["data"]!.AsArray();
        Assert.Equal(["Other"], left.Select(x => x!["name"]!.GetValue<string>()));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Expected_version_protects_against_a_stale_change(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var old = (await Ok(ws.InProject("Demo", "status", "get", "Todo", "--json"))).Json["version"]!.GetValue<string>();
        await Ok(ws.InProject("Demo", "status", "update", "Todo", "--name", "Backlog"));

        var stale = await ws.InProject("Demo", "status", "update", "Backlog", "--name", "Lost", "--expected-version", old);
        Assert.Equal(1, stale.Code);
        Assert.StartsWith("Modified by someone else:", stale.Err);
        Assert.Contains("Backlog", (await Ok(ws.InProject("Demo", "status", "list"))).Out);

        var staleDelete = await ws.InProject("Demo", "status", "delete", "Backlog", "--expected-version", old);
        Assert.Equal(1, staleDelete.Code);

        var current = (await Ok(ws.InProject("Demo", "status", "get", "Backlog", "--json"))).Json["version"]!.GetValue<string>();
        await Ok(ws.InProject("Demo", "status", "update", "Backlog", "--name", "Kept", "--expected-version", current));
        Assert.Contains("Kept", (await Ok(ws.InProject("Demo", "status", "list"))).Out);
    }

    public static IEnumerable<object[]> Failures() =>
    [
        [new[] { "project", "update", "Demo" }, "Nothing to change"],
        [new[] { "status", "update", "Todo", "-p", "Demo" }, "Nothing to change: pass at least one of --name, --color"],
        [new[] { "task", "update", "00000000-0000-0000-0000-000000000001", "-p", "Demo" }, "Nothing to change"],
        [new[] { "task", "get", "Fix login", "-p", "Demo" }, "Task is given by id"],
        [new[] { "task", "get", "00000000-0000-0000-0000-000000000001", "-p", "Demo" }, "No task"],
        [new[] { "task", "delete", "00000000-0000-0000-0000-000000000001", "-p", "Demo" }, "No task"],
        [new[] { "status", "get", "Nope", "-p", "Demo" }, "No status 'Nope'"],
        [new[] { "board", "delete", "Nope", "-p", "Demo" }, "No board 'Nope'"],
        [new[] { "project", "get", "Nope" }, "No project 'Nope'"],
        [new[] { "status", "get", "Todo" }, "Project is required"],
        [new[] { "status", "update", "Todo", "--color", "red", "-p", "Demo" }, "#RRGGBB"],
        [new[] { "task", "update", "%TASK%", "--status", "Nope", "-p", "Demo" }, "No status 'Nope'"],
        [new[] { "user", "get", "Nobody" }, "No user 'Nobody'"],
        [new[] { "agent", "get", "Ivan" }, "No agent 'Ivan'"],
        [new[] { "user", "delete", "bot" }, "No user 'bot'"],
        [new[] { "board", "update", "Main", "--column", "broken", "-p", "Demo" }, "expected \"Name=status,status\""],
    ];

    [Theory, MemberData(nameof(Failures))]
    public async Task Failing_command_reports_error_and_exits_with_1(string[] args, string message)
    {
        using var ws = TestWorkspace.Create("files");
        var taskId = await Seed(ws);
        await Ok(ws.Run("project", "create", "Other")); // с одним проектом --project необязателен
        await Ok(ws.Run("user", "create", "Ivan"));
        await Ok(ws.Run("agent", "create", "bot"));

        var result = await ws.Run(args.Select(x => x == "%TASK%" ? taskId.ToString() : x).ToArray());

        Assert.Equal(1, result.Code);
        Assert.Contains(message, result.Err);
        Assert.Empty(result.Out);
    }

    [Fact]
    public async Task Users_and_agents_are_not_mixed_up()
    {
        using var ws = TestWorkspace.Create("files");
        var user = (await Ok(ws.Run("user", "create", "Ivan"))).Id;
        await Ok(ws.Run("agent", "create", "bot"));

        Assert.Equal(1, (await ws.Run("agent", "delete", user.ToString())).Code);
        Assert.Contains("Ivan", (await Ok(ws.Run("user", "list"))).Out);
    }

    [Fact]
    public async Task Change_is_visible_to_the_next_command_through_the_files()
    {
        using var ws = TestWorkspace.Create("files");
        var id = (await Ok(ws.Run("project", "create", "Demo"))).Id;
        await Ok(ws.Run("project", "update", "Demo", "--name", "Changed"));

        var yaml = File.ReadAllText(Path.Combine(ws.Root, ".tasker", "projects", id.ToString(), "project.yaml"));
        Assert.Contains("Changed", yaml);

        await Ok(ws.Run("project", "delete", "Changed", "--yes"));
        Assert.False(File.Exists(Path.Combine(ws.Root, ".tasker", "projects", id.ToString(), "project.yaml")));
    }
}
