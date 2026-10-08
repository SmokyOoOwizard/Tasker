using Xunit;

namespace Tasker.Tests;

/// <summary>Сквозные проверки команды tasker на настоящем хранилище: и папка с файлами, и SQLite.</summary>
public class CliTests
{
    /// <summary>Проект с набором Flow (Todo → Doing → Done), типом Bug и доской.</summary>
    private static async Task<Guid> Seed(TestWorkspace ws)
    {
        Assert.Equal(0, (await ws.Run("project", "create", "Demo")).Code);
        await Ok(ws.InProject("Demo", "status", "create", "Todo", "--color", "#ff0000"));
        await Ok(ws.InProject("Demo", "status", "create", "Doing"));
        await Ok(ws.InProject("Demo", "status", "create", "Done", "--color", "#00AA00"));
        await Ok(ws.InProject("Demo", "status-set", "create", "Flow", "--status", "Todo", "Doing", "Done"));
        await Ok(ws.InProject("Demo", "task-type", "create", "Bug", "--status-set", "Flow"));
        return (await ws.Run("project", "list", "--json")).Json["data"]![0]!["id"]!.GetValue<Guid>();
    }

    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Creates_everything_and_lists_it(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        var projectId = await Seed(ws);

        var board = await Ok(ws.InProject("Demo", "board", "create", "Main", "--status-set", "Flow",
            "--column", "Todo=Todo", "--column", "In work=Doing", "--column", "Finished=Done"));
        Assert.StartsWith("Created board 'Main' ", board.Out);

        var first = await Ok(ws.InProject("Demo", "task", "create", "Fix login", "--type", "Bug", "-d", "details"));
        var second = await Ok(ws.InProject("Demo", "task", "create", "Second", "--type", "Bug", "--status", "Doing"));

        var tasks = (await Ok(ws.InProject("Demo", "task", "list", "--json"))).Json;
        Assert.Equal(2, tasks["totalCount"]!.GetValue<int>());
        var byTitle = tasks["data"]!.AsArray().ToDictionary(x => x!["title"]!.GetValue<string>(), x => x!);

        Assert.Equal(first.Id, byTitle["Fix login"]["id"]!.GetValue<Guid>());
        Assert.Equal("details", byTitle["Fix login"]["description"]!.GetValue<string>());
        Assert.Equal(projectId, byTitle["Fix login"]["projectId"]!.GetValue<Guid>());
        Assert.Equal(second.Id, byTitle["Second"]["id"]!.GetValue<Guid>());

        // Без --status задача получает первый статус набора.
        var statuses = (await Ok(ws.InProject("Demo", "status", "list", "--json"))).Json["data"]!.AsArray();
        string StatusId(string name) => statuses.Single(x => x!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<string>();
        Assert.Equal(StatusId("Todo"), byTitle["Fix login"]["statusId"]!.GetValue<string>());
        Assert.Equal(StatusId("Doing"), byTitle["Second"]["statusId"]!.GetValue<string>());

        Assert.Contains("Main", (await Ok(ws.InProject("Demo", "board", "list"))).Out);
        Assert.Contains("Bug", (await Ok(ws.InProject("Demo", "task-type", "list"))).Out);
        Assert.Contains("Flow  (3 statuses)", (await Ok(ws.InProject("Demo", "status-set", "list"))).Out);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Status_color_is_stored_in_upper_case_and_defaults_to_gray(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var statuses = (await Ok(ws.InProject("Demo", "status", "list", "--json"))).Json["data"]!.AsArray();
        string Color(string name) => statuses.Single(x => x!["name"]!.GetValue<string>() == name)!["color"]!.GetValue<string>();
        Assert.Equal("#FF0000", Color("Todo"));
        Assert.Equal("#808080", Color("Doing"));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Board_columns_get_drop_rule_from_first_status_of_each_set(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(ws.InProject("Demo", "status", "create", "Review"));
        await Ok(ws.InProject("Demo", "status-set", "create", "Short", "--status", "Todo", "Review"));

        await Ok(ws.InProject("Demo", "board", "create", "Both", "--status-set", "Flow", "Short",
            "--column", "Open=Doing,Todo", "--column", "Closed=Done,Review"));

        var board = (await Ok(ws.InProject("Demo", "board", "list", "--json"))).Json["data"]![0]!;
        var setIds = (await Ok(ws.InProject("Demo", "status-set", "list", "--json"))).Json["data"]!.AsArray()
            .ToDictionary(x => x!["name"]!.GetValue<string>(), x => x!["id"]!.GetValue<string>());
        var statusIds = (await Ok(ws.InProject("Demo", "status", "list", "--json"))).Json["data"]!.AsArray()
            .ToDictionary(x => x!["name"]!.GetValue<string>(), x => x!["id"]!.GetValue<string>());

        var open = board["columns"]![0]!;
        Assert.Equal("Open", open["name"]!.GetValue<string>());
        Assert.Equal(2, open["statusIds"]!.AsArray().Count);
        // «Doing» указан первым и есть в Flow; в Short первый статус колонки из этого набора — «Todo».
        Assert.Equal(statusIds["Doing"], open["dropStatuses"]![setIds["Flow"]]!.GetValue<string>());
        Assert.Equal(statusIds["Todo"], open["dropStatuses"]![setIds["Short"]]!.GetValue<string>());

        var closed = board["columns"]![1]!;
        Assert.Equal(statusIds["Done"], closed["dropStatuses"]![setIds["Flow"]]!.GetValue<string>());
        Assert.Equal(statusIds["Review"], closed["dropStatuses"]![setIds["Short"]]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Task_list_filters_by_type_and_status(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(ws.InProject("Demo", "task-type", "create", "Story", "--status-set", "Flow"));
        await Ok(ws.InProject("Demo", "task", "create", "Bug todo", "--type", "Bug"));
        await Ok(ws.InProject("Demo", "task", "create", "Bug doing", "--type", "Bug", "--status", "Doing"));
        await Ok(ws.InProject("Demo", "task", "create", "Story doing", "--type", "Story", "--status", "Doing"));

        static string[] Titles(CliResult r) =>
            r.Json["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).Order().ToArray();

        Assert.Equal(["Bug doing", "Bug todo"], Titles(await Ok(ws.InProject("Demo", "task", "list", "--type", "Bug", "--json"))));
        Assert.Equal(["Bug doing", "Story doing"], Titles(await Ok(ws.InProject("Demo", "task", "list", "--status", "Doing", "--json"))));
        Assert.Equal(["Bug doing"], Titles(await Ok(ws.InProject("Demo", "task", "list", "--type", "Bug", "--status", "Doing", "--json"))));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Users_and_agents_are_listed_separately(string storage)
    {
        using var ws = TestWorkspace.Create(storage);

        Assert.StartsWith("Created user 'Ivan' ", (await Ok(ws.Run("user", "create", "Ivan"))).Out);
        Assert.StartsWith("Created agent 'bot' ", (await Ok(ws.Run("agent", "create", "bot"))).Out);

        var users = (await Ok(ws.Run("user", "list"))).Out;
        Assert.Contains("Ivan", users);
        Assert.DoesNotContain("bot", users);

        var agents = (await Ok(ws.Run("agent", "list"))).Out;
        Assert.Contains("bot", agents);
        Assert.DoesNotContain("Ivan", agents);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Lists_are_paged(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        foreach (var name in new[] { "A", "B", "C" })
            await Ok(ws.Run("project", "create", name));

        var page = (await Ok(ws.Run("project", "list", "--limit", "2", "--json"))).Json;
        Assert.Equal(3, page["totalCount"]!.GetValue<int>());
        Assert.Equal(2, page["data"]!.AsArray().Count);

        var rest = (await Ok(ws.Run("project", "list", "--offset", "2", "--json"))).Json;
        Assert.Single(rest["data"]!.AsArray());
        Assert.Equal(2, rest["offset"]!.GetValue<int>());

        var text = await Ok(ws.Run("project", "list", "--limit", "2"));
        Assert.Equal("Found 3, shown 1-2 (use --offset/--limit)", text.Found);
        Assert.Equal(2, text.Data.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Empty(text.Err);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task References_accept_id_and_name_ignoring_case(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        var projectId = await Seed(ws);
        var setId = (await Ok(ws.InProject("Demo", "status-set", "list", "--json"))).Json["data"]![0]!["id"]!.GetValue<string>();

        Assert.Equal(0, (await ws.InProject(projectId.ToString(), "task-type", "create", "ByIds", "--status-set", setId)).Code);
        Assert.Equal(0, (await ws.InProject("dEmO", "task-type", "create", "ByName", "--status-set", "fLoW")).Code);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Ambiguous_name_asks_for_id(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(ws.InProject("Demo", "status-set", "create", "Other", "--status", "Todo"));
        await Ok(ws.InProject("Demo", "status", "create", "Todo"));

        var result = await ws.InProject("Demo", "status-set", "create", "Third", "--status", "Todo");
        Assert.Equal(1, result.Code);
        Assert.Contains("Several statuss are named 'Todo'", result.Err);
        Assert.Empty(result.Out);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Data_survives_between_commands(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Ok(ws.Run("project", "create", "Kept"));

        Assert.Contains("Kept", (await Ok(ws.Run("project", "list"))).Out);
    }

    public static IEnumerable<object[]> Failures() =>
    [
        // Ошибки, которые находит сам CLI.
        [new[] { "task", "create", "T", "--type", "Bug" }, "Project is required"],
        [new[] { "task", "create", "T", "--type", "Nope", "-p", "Demo" }, "No task type 'Nope'"],
        [new[] { "task", "list", "-p", "Missing" }, "No project 'Missing'"],
        [new[] { "task", "create", "T", "--type", "Bug", "--status", "Nope", "-p", "Demo" }, "No status 'Nope'"],
        [new[] { "board", "create", "B", "--status-set", "Flow", "--column", "no-equals", "-p", "Demo" }, "expected \"Name=status,status\""],
        [new[] { "board", "create", "B", "--status-set", "Nope", "--column", "A=Todo", "-p", "Demo" }, "No status set 'Nope'"],
        // Ошибки, которые находит Core.
        [new[] { "project", "create", " " }, "Project name is required"],
        [new[] { "status", "create", "X", "--color", "red", "-p", "Demo" }, "#RRGGBB"],
        [new[] { "task", "create", " ", "--type", "Bug", "-p", "Demo" }, "Title is required"],
        [new[] { "status-set", "create", "Empty", "--status", "Todo", "Todo", "-p", "Demo" }, "duplicates"],
    ];

    [Theory, MemberData(nameof(Failures))]
    public async Task Failing_command_reports_error_and_exits_with_1(string[] args, string message)
    {
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        await Ok(ws.Run("project", "create", "Other")); // с одним проектом --project необязателен

        var result = await ws.Run(args);

        Assert.Equal(1, result.Code);
        Assert.Contains(message, result.Err);
        Assert.Empty(result.Out);
    }

    [Fact]
    public async Task Failed_create_leaves_nothing_behind()
    {
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);

        await ws.InProject("Demo", "task", "create", "T", "--type", "Nope");

        Assert.Equal(0, (await Ok(ws.InProject("Demo", "task", "list", "--json"))).Json["totalCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Workspace_and_sqlite_together_are_rejected()
    {
        using var ws = TestWorkspace.Create("files");

        var result = await TestWorkspace.Invoke(["project", "list", "--workspace", ws.Root, "--sqlite", Path.Combine(ws.Root, "x.db")]);

        Assert.Equal(1, result.Code);
        Assert.Contains("not both", result.Err);
    }

    [Fact]
    public async Task Missing_folder_is_an_error_and_is_not_created()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tasker-tests-missing-" + Guid.NewGuid().ToString("N"));

        var result = await TestWorkspace.Invoke(["project", "list", "-w", folder]);

        Assert.Equal(1, result.Code);
        Assert.Contains("Folder not found", result.Err);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task Project_can_come_from_environment_variable()
    {
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);

        using var _ = Tasker.Global.AppEnvironment.Override("TASKER_PROJECT", "Demo");

        var result = await ws.Run("status", "list");
        Assert.Equal(0, result.Code);
        Assert.Contains("Todo", result.Out);

        // --project важнее переменной.
        var other = await ws.Run("status", "list", "--project", "Missing");
        Assert.Equal(1, other.Code);
    }

    [Fact]
    public async Task Default_workspace_is_the_current_folder()
    {
        using var ws = TestWorkspace.Create("files");

        // Текущая папка — свойство процесса, поэтому команды идут отдельными процессами tasker.
        Assert.Equal(0, (await TaskerProcess.RunIn(ws.Root, "project", "create", "Here")).Code);
        Assert.True(Directory.Exists(Path.Combine(ws.Root, ".tasker", "projects")));
        Assert.Contains("Here", (await TaskerProcess.RunIn(ws.Root, "project", "list")).Out);
    }

    [Fact]
    public async Task Files_are_written_to_the_tasker_folder()
    {
        using var ws = TestWorkspace.Create("files");
        var id = (await Ok(ws.Run("project", "create", "Demo"))).Id;

        Assert.True(File.Exists(Path.Combine(ws.Root, ".tasker", "projects", id.ToString(), "project.yaml")));
    }

    [Fact]
    public async Task Help_lists_all_commands()
    {
        var result = await TestWorkspace.Invoke(["--help"]);

        Assert.Equal(0, result.Code);
        foreach (var command in new[] { "project", "status", "status-set", "task-type", "board", "task", "user", "agent" })
            Assert.Contains(command, result.Out);
    }
}
