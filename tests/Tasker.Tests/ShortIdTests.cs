using System.Text.Json.Nodes;
using Autofac;
using Microsoft.Data.Sqlite;
using Tasker.Cli;
using Tasker.Core;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.TaskSeries;
using Tasker.Core.Tasks;
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Tests.SeriesCore;
using Xunit;

namespace Tasker.Tests;

/// <summary>Короткий id (первые 8 символов Guid): разбор, показ в списках консоли, поиск в обоих хранилищах, команды и MCP.</summary>
public class ShortIdTests
{
    // ---- разбор ----

    [Fact]
    public void The_short_id_is_the_first_eight_characters_of_the_guid()
    {
        Assert.Equal("70053344", ShortId.Of(Guid.Parse("70053344-2907-4f88-b86c-40a39484c33d")));
    }

    [Theory]
    [InlineData("70053344", "70053344")]
    [InlineData("  70053344 ", "70053344")]
    [InlineData("7005334429074F88", "70053344-2907-4f88")]
    [InlineData("70053344-2907", null)] // дефисы не принимаются
    [InlineData("7005334", null)] // короче 8
    [InlineData("1234567", null)]
    [InlineData("7005334g", null)] // не hex
    [InlineData("70053344290744f88b86c40a39484c33d0", null)] // длиннее Guid
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_prefix_needs_eight_or_more_hex_characters_and_is_case_insensitive(string? text, string? key)
    {
        Assert.Equal(key, ShortId.TryKey(text));
    }

    [Fact]
    public void A_prefix_matches_by_the_start_of_the_guid_including_the_part_after_the_first_dash()
    {
        var id = Guid.Parse("70053344-2907-4f88-b86c-40a39484c33d");

        Assert.True(ShortId.Matches(id, ShortId.TryKey("70053344")!));
        Assert.True(ShortId.Matches(id, ShortId.TryKey("700533442907")!));
        Assert.True(ShortId.Matches(id, ShortId.TryKey("70053344290")!));
        Assert.False(ShortId.Matches(id, ShortId.TryKey("70053345")!));
        Assert.False(ShortId.Matches(id, ShortId.TryKey("70053344291")!));
    }

    [Fact]
    public void The_task_reference_takes_a_prefix_only_when_asked_to_and_never_mixes_it_up_with_a_series_reference()
    {
        Assert.Null(TaskReference.TryParse("70053344"));
        Assert.Throws<TaskerValidationException>(() => TaskReference.Parse("70053344"));

        var parsed = TaskReference.TryParse("70053344", allowIdPrefix: true);
        Assert.Equal("70053344", parsed!.Value.IdKey);
        Assert.Null(parsed.Value.Prefix);

        var series = TaskReference.TryParse("ABCDEF12-5", allowIdPrefix: true);
        Assert.Equal("ABCDEF12", series!.Value.Prefix);
        Assert.Null(series.Value.IdKey);

        Assert.Null(TaskReference.TryParse("7005334", allowIdPrefix: true));
        Assert.NotNull(TaskReference.TryParse(Guid.NewGuid().ToString(), allowIdPrefix: true)!.Value.Id);
    }

    // ---- ссылки на сущности: имя сильнее префикса, неоднозначность ----

    private sealed record Item(Guid Id, string Name);

    [Fact]
    public void Entities_are_found_by_a_prefix_of_the_id_unless_a_name_matches_exactly()
    {
        var a = new Item(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "Alpha");
        var b = new Item(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), "Beta");
        var named = new Item(Guid.Parse("cccccccc-0000-0000-0000-000000000003"), "aaaaaaaa");
        Item[] items = [a, b];

        Assert.Same(a, Refs.FindItem(items, "aaaaaaaa", x => x.Id, x => x.Name, "thing"));
        Assert.Same(a, Refs.FindItem(items, "AAAAAAAA", x => x.Id, x => x.Name, "thing"));
        Assert.Same(a, Refs.FindItem(items, "aaaaaaaa0000", x => x.Id, x => x.Name, "thing"));
        Assert.Same(b, Refs.FindItem(items, "Beta", x => x.Id, x => x.Name, "thing"));

        // Точное имя из 8 hex-символов сильнее префикса.
        Assert.Same(named, Refs.FindItem([a, b, named], "aaaaaaaa", x => x.Id, x => x.Name, "thing"));
    }

    [Fact]
    public void A_short_or_unknown_prefix_is_not_found_and_an_ambiguous_one_lists_the_full_ids()
    {
        var a = new Item(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "Alpha");
        var b = new Item(Guid.Parse("aaaaaaaa-1111-0000-0000-000000000002"), "Beta");
        Item[] items = [a, b];

        Assert.Equal("No thing 'aaaaaaa'", Assert.Throws<CliException>(() => Refs.FindItem(items, "aaaaaaa", x => x.Id, x => x.Name, "thing")).Message);
        Assert.Equal("No thing 'dddddddd'", Assert.Throws<CliException>(() => Refs.FindItem(items, "dddddddd", x => x.Id, x => x.Name, "thing")).Message);

        var ambiguous = Assert.Throws<CliException>(() => Refs.FindItem(items, "aaaaaaaa", x => x.Id, x => x.Name, "thing"));
        Assert.Contains("Several things start with 'aaaaaaaa'", ambiguous.Message);
        Assert.Contains(a.Id.ToString(), ambiguous.Message);
        Assert.Contains(b.Id.ToString(), ambiguous.Message);

        // Дальше префикс различает.
        Assert.Same(b, Refs.FindItem(items, "aaaaaaaa1", x => x.Id, x => x.Name, "thing"));
        Assert.Same(a, Refs.FindItem(items, "aaaaaaaa0", x => x.Id, x => x.Name, "thing"));
    }

    // ---- хранилища ----

    private static TaskItem NewTask(Guid projectId, string id, Guid typeId, Guid statusId, DateTimeOffset createdAt) => new()
    {
        Id = Guid.Parse(id),
        ProjectId = projectId,
        Title = "Task " + id[..13],
        TypeId = typeId,
        StatusId = statusId,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        Version = ""
    };

    private static async Task CheckFindByIdPrefix(ITaskStorage tasks, Guid projectId, Guid typeId, Guid statusId)
    {
        var now = DateTimeOffset.UtcNow;
        var first = NewTask(projectId, "abcdef12-0000-4000-8000-000000000001", typeId, statusId, now);
        var second = NewTask(projectId, "abcdef12-9999-4000-8000-000000000002", typeId, statusId, now.AddSeconds(1));
        var other = NewTask(projectId, "abcdef13-0000-4000-8000-000000000003", typeId, statusId, now.AddSeconds(2));
        foreach (var task in new[] { first, second, other })
            await tasks.Add(task);

        Assert.Equal([first.Id, second.Id], (await tasks.FindByIdPrefix(projectId, "abcdef12")).Select(x => x.Id));
        Assert.Equal([second.Id], (await tasks.FindByIdPrefix(projectId, "abcdef12-9999")).Select(x => x.Id));
        Assert.Equal([other.Id], (await tasks.FindByIdPrefix(projectId, "abcdef13")).Select(x => x.Id));
        Assert.Equal([first.Id], (await tasks.FindByIdPrefix(projectId, "abcdef12-0000-4000-8000-000000000001")).Select(x => x.Id));
        Assert.Empty(await tasks.FindByIdPrefix(projectId, "abcdef14"));
        // Другой проект ту же задачу не видит.
        Assert.Empty(await tasks.FindByIdPrefix(Guid.NewGuid(), "abcdef12"));
    }

    [Fact]
    public async Task The_files_storage_finds_tasks_by_the_id_prefix_in_the_index()
    {
        using var h = new FilesHarness();

        await CheckFindByIdPrefix(h.Tasks, h.ProjectId, Guid.NewGuid(), Guid.NewGuid());
    }

    [Fact]
    public async Task The_db_storage_finds_tasks_by_the_id_prefix_with_like()
    {
        var root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var builder = new ContainerBuilder();
        builder.RegisterModule(new DbStorageModule(new DbConfigs { SqliteFile = Path.Combine(root, "tasker.db") }));
        await using var container = builder.Build();
        try
        {
            await container.Resolve<IStorageLifecycle>().Start(default);
            await using var scope = container.BeginLifetimeScope();
            var (project, status, set, type) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await scope.Resolve<IProjectStorage>().Add(new Project { Id = project, Name = "P", CreatedAt = DateTimeOffset.UtcNow, Version = "" });
            await scope.Resolve<IStatusStorage>().Add(new Status { Id = status, ProjectId = project, Name = "Open", Color = "#000000", Version = "" });
            await scope.Resolve<IStatusSetStorage>().Add(new StatusSet { Id = set, ProjectId = project, Name = "S", StatusIds = [status], Version = "" });
            await scope.Resolve<ITaskTypeStorage>().Add(new TaskType { Id = type, ProjectId = project, Name = "T", StatusSetId = set, Version = "" });

            await CheckFindByIdPrefix(scope.Resolve<ITaskStorage>(), project, type, status);
        }
        finally
        {
            await container.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- консоль ----

    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    private static Task<CliResult> P(TestWorkspace ws, params string[] args) => ws.InProject("Demo", args);

    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(P(ws, "status", "create", "Todo"));
        await Ok(P(ws, "status", "create", "Done"));
        await Ok(P(ws, "status-set", "create", "Flow", "--status", "Todo", "Done"));
        await Ok(P(ws, "task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(P(ws, "series", "create", "Tasks", "--prefix", "TSK"));
    }

    private static async Task<Guid> New(TestWorkspace ws, string title, params string[] series) =>
        (await Ok(P(ws, ["task", "create", title, "--type", "Bug", .. series.SelectMany(x => new[] { "--series", x }), "--json"]))).Json["id"]!.GetValue<Guid>();

    private static string[] Lines(CliResult r) => r.Data.Trim().Split('\n');

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Lists_show_the_short_id_where_there_is_no_series_reference_and_the_column_stays_narrow(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await New(ws, "One", "TSK");
        var loose = await New(ws, "Loose");
        var id8 = ShortId.Of(loose);

        Assert.Equal(["TSK-1     Todo  Bug  One", $"{id8}  Todo  Bug  Loose"], Lines(await Ok(P(ws, "task", "list"))));

        // В --json и в task get — полный id.
        Assert.Equal(loose, (await Ok(P(ws, "task", "list", "--json"))).Json["data"]![1]!["id"]!.GetValue<Guid>());
        Assert.Contains($"id:           {loose}", (await Ok(P(ws, "task", "get", id8))).Out);

        // Списки сущностей: короткий id вместо полного.
        var type = (await Ok(P(ws, "task-type", "list", "--json"))).Json["data"]![0]!["id"]!.GetValue<Guid>();
        Assert.Equal($"{ShortId.Of(type)}  Bug", (await Ok(P(ws, "task-type", "list"))).Data.Trim());
        var project = (await Ok(ws.Run("project", "list", "--json"))).Json["data"]![0]!["id"]!.GetValue<Guid>();
        Assert.Equal($"{ShortId.Of(project)}  Demo", (await Ok(ws.Run("project", "list"))).Data.Trim());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Links_and_board_lists_show_the_short_id_of_a_task_without_a_series(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await New(ws, "One", "TSK");
        var loose = await New(ws, "Loose");
        var id8 = ShortId.Of(loose);

        await Ok(P(ws, "task", "link", "TSK-1", "blocks", id8));

        Assert.Equal([$"blocks  {id8}  Todo  Loose"], Lines(await Ok(P(ws, "task", "links", "TSK-1"))));
        Assert.Equal(["is blocked by  TSK-1  Todo  One"], Lines(await Ok(P(ws, "task", "links", id8))));
        Assert.Contains($"  blocks  {id8}  Todo  Loose", (await Ok(P(ws, "task", "get", "TSK-1"))).Out);
        Assert.Contains("  is blocked by  TSK-1  Todo  One", (await Ok(P(ws, "task", "get", id8))).Out);

        Assert.Contains("Unlinked", (await Ok(P(ws, "task", "unlink", id8.ToUpperInvariant(), "is blocked by", "TSK-1"))).Out);
        Assert.Empty((await Ok(P(ws, "task", "links", "TSK-1"))).Data.Trim());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Commands_take_a_prefix_of_the_task_id_for_a_task_with_and_without_a_series(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var withSeries = await New(ws, "One", "TSK");
        var loose = await New(ws, "Loose");

        // 8 символов, больше, другой регистр, весь Guid.
        foreach (var reference in new[] { ShortId.Of(withSeries), withSeries.ToString("N")[..12], ShortId.Of(withSeries).ToUpperInvariant(), withSeries.ToString("N"), withSeries.ToString() })
            Assert.Contains("title:        One", (await Ok(P(ws, "task", "get", reference))).Out);
        Assert.Contains("series:       TSK-1", (await Ok(P(ws, "task", "get", ShortId.Of(withSeries)))).Out);

        var updated = await Ok(P(ws, "task", "update", ShortId.Of(loose).ToUpperInvariant(), "--status", "Done"));
        Assert.Contains($"Updated task 'Loose' {loose}", updated.Out);
        Assert.Equal(["TSK-1     Todo  Bug  One", $"{ShortId.Of(loose)}  Done  Bug  Loose"], Lines(await Ok(P(ws, "task", "list"))));

        var deleted = await Ok(P(ws, "task", "delete", ShortId.Of(loose)));
        Assert.Contains($"Deleted task 'Loose' {loose}", deleted.Out);
        Assert.Equal(["TSK-1  Todo  Bug  One"], Lines(await Ok(P(ws, "task", "list"))));

        // Сущности — по префиксу, имя при этом по-прежнему работает.
        var type = (await Ok(P(ws, "task-type", "list", "--json"))).Json["data"]![0]!["id"]!.GetValue<Guid>();
        Assert.Contains("name:", (await Ok(P(ws, "task-type", "get", ShortId.Of(type)))).Out);
        Assert.Contains("name:", (await Ok(P(ws, "task-type", "get", "Bug"))).Out);
        var series = (await Ok(P(ws, "series", "list", "--json"))).Json["data"]![0]!["id"]!.GetValue<Guid>();
        Assert.Contains("prefix:       TSK", (await Ok(P(ws, "series", "get", ShortId.Of(series)))).Out);
        Assert.Equal($"{ShortId.Of(series)}  TSK  Tasks", (await Ok(P(ws, "series", "list"))).Data.Trim());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task A_prefix_shorter_than_eight_characters_or_unknown_is_an_error(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var loose = await New(ws, "Loose");

        var tooShort = await P(ws, "task", "get", ShortId.Of(loose)[..7]);
        Assert.Equal(1, tooShort.Code);
        Assert.Contains("Task is given by id", tooShort.Err);

        var unknown = await P(ws, "task", "get", loose.ToString("N")[..7].Replace("a", "b") + "0000");
        Assert.Equal(1, unknown.Code);

        var entity = await P(ws, "task-type", "get", "12345678");
        Assert.Equal(1, entity.Code);
        Assert.Contains("No task type '12345678'", entity.Err);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task An_entity_named_with_eight_hex_characters_is_found_by_the_name_before_the_prefix(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var series = (await Ok(P(ws, "series", "list", "--json"))).Json["data"]![0]!["id"]!.GetValue<Guid>();
        var hex = ShortId.Of(series);

        // Статус, названный по короткому id серии, — это другой статус; а префикс статуса без такого имени работает.
        await Ok(P(ws, "status", "create", hex));
        var named = (await Ok(P(ws, "status", "get", hex, "--json"))).Json;
        Assert.Equal(hex, named["name"]!.GetValue<string>());

        var todo = (await Ok(P(ws, "status", "list", "--json"))).Json["data"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == "Todo")!["id"]!.GetValue<Guid>();
        Assert.Equal("Todo", (await Ok(P(ws, "status", "get", ShortId.Of(todo), "--json"))).Json["name"]!.GetValue<string>());
    }

    // ---- ядро: неоднозначность у задач ----

    [Fact]
    public async Task An_ambiguous_task_prefix_is_an_error_with_the_full_ids_and_a_longer_prefix_resolves_it()
    {
        var env = new SeriesEnv();
        var shared = Guid.Parse("deadbeef-0000-0000-0000-000000000001");
        var twin = Guid.Parse("deadbeef-1111-0000-0000-000000000002");
        env.Seed("first", id: shared);
        env.Seed("second", id: twin);

        var error = await Assert.ThrowsAsync<TaskerValidationException>(() => env.TaskSvc.Resolve(env.Project, "deadbeef", allowIdPrefix: true));
        Assert.Contains("Several tasks start with 'deadbeef'", error.Message);
        Assert.Contains(shared.ToString(), error.Message);
        Assert.Contains(twin.ToString(), error.Message);

        Assert.Equal([twin], (await env.TaskSvc.Resolve(env.Project, "deadbeef1", allowIdPrefix: true)).Select(x => x.Id));
        Assert.Equal(shared, await env.TaskSvc.ResolveId(env.Project, "DEADBEEF0"));
        Assert.Null(await env.TaskSvc.ResolveId(env.Project, "cafebabe"));
        // Без allowIdPrefix (REST) префикс — не id.
        await Assert.ThrowsAsync<TaskerValidationException>(() => env.TaskSvc.Resolve(env.Project, "deadbeef"));
        await Assert.ThrowsAsync<TaskerValidationException>(() => env.TaskSvc.ResolveId(env.Project, "deadbee"));
    }

    // ---- MCP ----

    [Fact]
    public async Task Mcp_task_tools_take_the_short_id_and_rest_style_full_ids_still_work()
    {
        using var daemon = new DaemonFixture();
        var folder = await daemon.Workspace("a", "Alpha");
        var status = await daemon.Start();
        var key = status.Workspaces.Single().Key!;
        var projectId = JsonNode.Parse(await daemon.CallTool(key, "list_projects"))!["data"]![0]!["id"]!.GetValue<string>();

        async Task<JsonNode> Call(string tool, object args)
        {
            var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject();
            node["projectId"] ??= projectId;
            var (isError, text) = await daemon.CallToolResult(key, tool, node);
            Assert.False(isError, $"{tool} failed: {text}");
            return JsonNode.Parse(text)!;
        }

        async Task<string> Fail(string tool, object args)
        {
            var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject();
            node["projectId"] ??= projectId;
            var (isError, text) = await daemon.CallToolResult(key, tool, node);
            Assert.True(isError, $"{tool} unexpectedly succeeded: {text}");
            return text;
        }

        var todo = await Call("create_status", new { name = "Todo", color = "#112233" });
        var set = await Call("create_status_set", new { name = "Set", statusIds = new[] { todo["id"]!.GetValue<string>() } });
        var type = await Call("create_task_type", new { name = "Task", statusSetId = set["id"]!.GetValue<string>() });
        var typeId = type["id"]!.GetValue<string>();
        var series = await Call("create_series", new { name = "Tasks", prefix = "TSK" });

        var one = await Call("create_task", new { title = "one", typeId, seriesIds = new[] { series["id"]!.GetValue<string>() } });
        var two = await Call("create_task", new { title = "two", typeId });
        var oneId = one["id"]!.GetValue<string>();
        var twoId = two["id"]!.GetValue<string>();

        Assert.Equal(oneId, (await Call("get_task", new { taskId = oneId[..8] }))["id"]!.GetValue<string>());
        Assert.Equal(oneId, (await Call("get_task", new { taskId = oneId[..8].ToUpperInvariant() }))["id"]!.GetValue<string>());
        Assert.Equal(oneId, (await Call("get_task", new { taskId = oneId }))["id"]!.GetValue<string>());

        var shortError = await Fail("get_task", new { taskId = oneId[..7] });
        Assert.StartsWith("[invalid]", shortError);
        Assert.StartsWith("[not_found]", await Fail("get_task", new { taskId = "12345678" }));

        var links = await Call("link_tasks", new { taskId = oneId[..8], link = "blocks", otherTaskId = twoId[..8] });
        Assert.Equal(1, links["links"]!.AsArray().Count);
        Assert.Equal(1, (await Call("get_task_links", new { taskId = twoId[..8] }))["links"]!.AsArray().Count);
        Assert.Equal(0, (await Call("unlink_tasks", new { taskId = oneId[..8], link = "blocks", otherTaskId = twoId[..8] }))["links"]!.AsArray().Count);

        var found = await Call("find_tasks_by_reference", new { reference = twoId[..8] });
        Assert.Equal(twoId, found["tasks"]![0]!["id"]!.GetValue<string>());

        var updated = await Call("update_task", new { taskId = twoId[..8], version = two["version"]!.GetValue<string>(), title = "two!" });
        Assert.Equal("two!", updated["title"]!.GetValue<string>());
        Assert.Contains("deleted", (await daemon.CallToolResult(key, "delete_task", JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(new { projectId, taskId = twoId[..8], version = updated["version"]!.GetValue<string>() }))!)).Text);
        Assert.NotNull(folder);
    }
}
