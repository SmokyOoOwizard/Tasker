using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Несколько значений в фильтрах списка задач (TSK-105): <c>--status A --status B</c> и <c>--status A B</c> консоли, <c>?statusId=A&amp;statusId=B</c> REST,
/// <c>statusIds</c> MCP — то же для типа и серии. Значения одного параметра — ИЛИ, разные параметры (статус, тип, серия, поля) — И.
/// Обе реализации хранилища (папка с индексом и SQLite), одно значение, неизвестное имя, статус не из набора типа, сочетание с полями, сортировкой и страницами, автодополнение.
/// </summary>
public class TaskMultiFilterTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    public static IEnumerable<object[]> Storages() => TestWorkspace.Storages();

    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    private static Task<CliResult> P(TestWorkspace ws, params string[] args) => ws.InProject("Demo", args);

    /// <summary>
    /// Статусы Todo, Doing, Done, Blocked; наборы Flow (Todo, Doing, Done) и Alt (Done, Blocked); типы Bug и Story на Flow, Chore на Alt; серии TSK и PRJ; поле Estimate.
    /// Задачи: 1 banana Bug Todo TSK 8; 2 Apple Story Doing TSK 3; 3 cherry Bug Done TSK 21; 4 Äpfel Chore Done TSK 3; 5 дыня Story Todo TSK+PRJ;
    /// 6 Яблоко Bug Doing PRJ; 7 арбуз Chore Blocked PRJ.
    /// </summary>
    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        foreach (var status in new[] { "Todo", "Doing", "Done", "Blocked" })
            await Ok(P(ws, "status", "create", status));
        await Ok(P(ws, "status-set", "create", "Flow", "--status", "Todo", "Doing", "Done"));
        await Ok(P(ws, "status-set", "create", "Alt", "--status", "Done", "Blocked"));
        await Ok(P(ws, "task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(P(ws, "task-type", "create", "Story", "--status-set", "Flow"));
        await Ok(P(ws, "task-type", "create", "Chore", "--status-set", "Alt"));
        await Ok(P(ws, "series", "create", "Tasks", "--prefix", "TSK"));
        await Ok(P(ws, "series", "create", "Proj", "--prefix", "PRJ"));
        await Ok(P(ws, "field", "create", "Estimate", "--type", "int"));

        await New(ws, "banana", "Bug", "Todo", ["TSK"], "Estimate=8");
        await New(ws, "Apple", "Story", "Doing", ["TSK"], "Estimate=3");
        await New(ws, "cherry", "Bug", "Done", ["TSK"], "Estimate=21");
        await New(ws, "Äpfel", "Chore", "Done", ["TSK"], "Estimate=3");
        await New(ws, "дыня", "Story", "Todo", ["TSK", "PRJ"]);
        await New(ws, "Яблоко", "Bug", "Doing", ["PRJ"]);
        await New(ws, "арбуз", "Chore", "Blocked", ["PRJ"]);
    }

    private static Task New(TestWorkspace ws, string title, string type, string status, string[] series, params string[] fields) =>
        Ok(P(ws, ["task", "create", title, "--type", type, "--status", status, .. series.SelectMany(x => new[] { "--series", x }), .. fields.SelectMany(x => new[] { "--field", x })]));

    private static string[] Titles(CliResult result) =>
        result.Json["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray();

    private static async Task<string[]> List(TestWorkspace ws, params string[] args) =>
        Titles(await Ok(P(ws, ["task", "list", "--json", "--all", .. args])));

    // ---- консоль: ИЛИ внутри параметра ----

    [Theory, MemberData(nameof(Storages))]
    public async Task Several_statuses_are_alternatives_by_repeating_the_option_or_listing_values(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        string[] expected = ["banana", "Apple", "дыня", "Яблоко"];
        Assert.Equal(expected, await List(ws, "--status", "Todo", "--status", "Doing"));
        Assert.Equal(expected, await List(ws, "--status", "Todo", "Doing"));
        Assert.Equal(expected, await List(ws, "--status", "todo", "Doing", "--status", "TODO"));
        // Порядок значений на результат не влияет, повторы не удваивают задачи.
        Assert.Equal(expected, await List(ws, "--status", "Doing", "--status", "Todo", "--status", "Doing"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task One_value_works_as_before_and_no_filter_lists_everything(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["banana", "дыня"], await List(ws, "--status", "Todo"));
        Assert.Equal(["banana", "cherry", "Яблоко"], await List(ws, "--type", "Bug"));
        Assert.Equal(["дыня", "Яблоко", "арбуз"], await List(ws, "--series", "PRJ"));
        Assert.Equal(7, (await List(ws)).Length);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Several_types_and_several_series_are_alternatives_too(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["banana", "cherry", "Äpfel", "Яблоко", "арбуз"], await List(ws, "--type", "Bug", "--type", "Chore"));
        Assert.Equal(["banana", "cherry", "Äpfel", "Яблоко", "арбуз"], await List(ws, "--type", "Bug", "Chore"));
        // Задача из обеих серий (дыня) показывается один раз.
        Assert.Equal(7, (await List(ws, "--series", "TSK", "--series", "PRJ")).Length);
        Assert.Equal(7, (await List(ws, "--series", "TSK", "PRJ")).Length);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Values_may_be_ids_and_short_ids_mixed_with_names(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var todo = (await Ok(P(ws, "status", "get", "Todo", "--json"))).Json["id"]!.GetValue<Guid>();
        var blocked = (await Ok(P(ws, "status", "get", "Blocked", "--json"))).Json["id"]!.GetValue<Guid>();
        var chore = (await Ok(P(ws, "task-type", "get", "Chore", "--json"))).Json["id"]!.GetValue<Guid>();

        Assert.Equal(["banana", "дыня", "арбуз"], await List(ws, "--status", todo.ToString(), blocked.ToString("N")[..8]));
        Assert.Equal(["banana", "дыня", "арбуз"], await List(ws, "--status", "Todo", "--status", blocked.ToString("N")[..8]));
        Assert.Equal(["Äpfel", "арбуз"], await List(ws, "--type", chore.ToString("N")[..8]));
    }

    // ---- консоль: И между параметрами ----

    [Theory, MemberData(nameof(Storages))]
    public async Task Different_filters_must_all_hold(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // (Todo или Doing) И Bug.
        Assert.Equal(["banana", "Яблоко"], await List(ws, "--status", "Todo", "Doing", "--type", "Bug"));
        // (Todo или Doing) И (Bug или Story) И серия PRJ.
        Assert.Equal(["дыня", "Яблоко"], await List(ws, "--status", "Todo", "--status", "Doing", "--type", "Bug", "Story", "--series", "PRJ"));
        // Плюс условие по полю: Estimate>=5 — тоже по И.
        Assert.Equal(["banana"], await List(ws, "--status", "Todo", "Doing", "--type", "Bug", "Story", "--field", "Estimate>=5"));
        Assert.Equal(["banana", "Apple"], await List(ws, "--status", "Todo", "Doing", "--field", "Estimate:set"));
        Assert.Empty(await List(ws, "--status", "Blocked", "--type", "Bug", "Story"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task A_status_outside_the_set_of_the_type_matches_nothing_and_the_rest_still_match(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // Todo нет в наборе Alt типа Chore: ничего, но это не ошибка.
        Assert.Empty(await List(ws, "--type", "Chore", "--status", "Todo"));
        // Done есть в обоих наборах: подходят и Bug Done, и Chore Done.
        Assert.Equal(["cherry", "Äpfel"], await List(ws, "--status", "Done", "--type", "Bug", "Chore", "--field", "Estimate>=3"));
        Assert.Equal(["Äpfel", "арбуз"], await List(ws, "--type", "Chore", "--status", "Todo", "Done", "Blocked"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Works_with_sort_paging_and_total_count(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["Apple", "banana", "дыня", "Яблоко"], await List(ws, "--status", "Todo", "--status", "Doing", "--sort", "title"));
        Assert.Equal(["Яблоко", "дыня", "banana", "Apple"], await List(ws, "--status", "Todo", "Doing", "--sort", "-title"));

        var page = await Ok(P(ws, "task", "list", "--json", "--status", "Todo", "Doing", "--sort", "title", "--offset", "1", "--limit", "2"));
        Assert.Equal(4, page.Json["totalCount"]!.GetValue<int>());
        Assert.Equal(["banana", "дыня"], Titles(page));

        var preview = await Ok(P(ws, "task", "list", "--json", "--status", "Todo", "--status", "Done", "--description-length", "0", "--limit", "2"));
        Assert.Equal(4, preview.Json["totalCount"]!.GetValue<int>());
        Assert.Equal(2, Titles(preview).Length);

        // В тексте тоже только подходящие.
        var text = await Ok(P(ws, "task", "list", "--status", "Todo", "--status", "Blocked"));
        Assert.Contains("banana", text.Out);
        Assert.Contains("арбуз", text.Out);
        Assert.DoesNotContain("Apple", text.Out);
    }

    // ---- консоль: ошибки ----

    [Theory, MemberData(nameof(Storages))]
    public async Task An_unknown_value_is_an_error_listing_the_valid_ones(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var status = await P(ws, "task", "list", "--status", "Todo", "--status", "Nope");
        Assert.NotEqual(0, status.Code);
        Assert.Contains("No status 'Nope'", status.Err);
        Assert.Contains("Available: Blocked, Doing, Done, Todo", status.Err);
        Assert.Empty(status.Out.Trim());

        var type = await P(ws, "task", "list", "--type", "Bug", "Nope");
        Assert.NotEqual(0, type.Code);
        Assert.Contains("No task type 'Nope'", type.Err);
        Assert.Contains("Available: Bug, Chore, Story", type.Err);

        var series = await P(ws, "task", "list", "--series", "TSK", "--series", "NOPE");
        Assert.NotEqual(0, series.Code);
        Assert.Contains("No series 'NOPE'", series.Err);
        Assert.Contains("Available: PRJ, TSK", series.Err);
    }

    // ---- автодополнение ----

    private static async Task<string[]> Suggest(TestWorkspace ws, string line)
    {
        var text = $"{string.Join(' ', ws.Location.Select(x => $"\"{x}\""))} --project Demo {line}";
        var result = await TestWorkspace.Invoke([$"[suggest:{text.Length}]", text]);
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Err);
        return result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(x => !x.StartsWith("--")).Order(StringComparer.Ordinal).ToArray();
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Values_are_suggested_for_the_first_value_after_a_repeat_and_after_listing(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["Blocked", "Doing", "Done", "Todo"], await Suggest(ws, "task list --status "));
        Assert.Equal(["Blocked", "Doing", "Done", "Todo"], await Suggest(ws, "task list --status Todo --status "));
        Assert.Equal(["Blocked", "Doing", "Done", "Todo"], await Suggest(ws, "task list --status Todo Doing "));
        Assert.Equal(["Doing", "Done"], await Suggest(ws, "task list --status Todo --status Do"));

        Assert.Equal(["Bug", "Chore", "Story"], await Suggest(ws, "task list --type Bug --type "));
        Assert.Equal(["Bug", "Chore", "Story"], await Suggest(ws, "task list --type Bug "));
        Assert.Equal(["PRJ", "TSK"], await Suggest(ws, "task list --series TSK --series "));
        Assert.Equal(["PRJ", "TSK"], await Suggest(ws, "task list --series TSK "));

        // Статусы сужаются типами: одним — его набор, несколькими — наборы всех.
        Assert.Equal(["Blocked", "Done"], await Suggest(ws, "task list --type Chore --status "));
        Assert.Equal(["Doing", "Done", "Todo"], await Suggest(ws, "task list --type Bug --status "));
        Assert.Equal(["Blocked", "Doing", "Done", "Todo"], await Suggest(ws, "task list --type Bug --type Chore --status "));
        Assert.Equal(["Blocked", "Doing", "Done", "Todo"], await Suggest(ws, "task list --type Bug Chore --status "));
    }

    // ---- REST ----

    [Theory, MemberData(nameof(Storages))]
    public async Task Rest_takes_repeated_status_type_and_series_ids(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        async Task<Guid> Make(string path, object body) => (await api.Post(api.P(path), body)).Body!["id"]!.GetValue<Guid>();
        var open = await Make("/statuses", new { name = "Open", color = "#111111" });
        var closed = await Make("/statuses", new { name = "Closed", color = "#222222" });
        var hold = await Make("/statuses", new { name = "Hold", color = "#333333" });
        var flow = await Make("/status-sets", new { name = "Flow", statusIds = new[] { open, closed } });
        var alt = await Make("/status-sets", new { name = "Alt", statusIds = new[] { hold, closed } });
        var bug = await Make("/task-types", new { name = "Bug", statusSetId = flow });
        var chore = await Make("/task-types", new { name = "Chore", statusSetId = alt });
        var a = await Make("/series", new { name = "A", prefix = "AAA" });
        var b = await Make("/series", new { name = "B", prefix = "BBB" });
        await Make("/tasks", new { title = "one", typeId = bug, statusId = open, seriesIds = new[] { a } });
        await Make("/tasks", new { title = "two", typeId = bug, statusId = closed, seriesIds = new[] { b } });
        await Make("/tasks", new { title = "three", typeId = chore, statusId = hold, seriesIds = new[] { a, b } });
        await Make("/tasks", new { title = "four", typeId = chore, statusId = closed });

        async Task<string[]> Titles(string query)
        {
            var (status, body) = await api.Get(api.P("/tasks" + query));
            Assert.Equal(HttpStatusCode.OK, status);
            return body!["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray();
        }

        Assert.Equal(["one", "two", "three", "four"], await Titles(""));
        Assert.Equal(["one"], await Titles($"?statusId={open}"));
        Assert.Equal(["one", "three"], await Titles($"?statusId={open}&statusId={hold}"));
        Assert.Equal(["one", "three"], await Titles($"?statusId={hold}&statusId={open}&statusId={open}"));
        Assert.Equal(["one", "two"], await Titles($"?typeId={bug}"));
        Assert.Equal(["one", "two", "three", "four"], await Titles($"?typeId={bug}&typeId={chore}"));
        Assert.Equal(["one", "three"], await Titles($"?seriesId={a}"));
        Assert.Equal(["one", "two", "three"], await Titles($"?seriesId={a}&seriesId={b}"));
        // И между параметрами: (Open или Hold) и серия B.
        Assert.Equal(["three"], await Titles($"?statusId={open}&statusId={hold}&seriesId={b}"));
        Assert.Equal(["two"], await Titles($"?statusId={open}&statusId={closed}&typeId={bug}&seriesId={b}"));
        // Статус не из набора типа — пусто; общий Closed есть у обоих типов.
        Assert.Empty(await Titles($"?typeId={bug}&statusId={hold}"));
        Assert.Equal(["two", "four"], await Titles($"?statusId={closed}&typeId={bug}&typeId={chore}"));
        // Страницы и totalCount считаются по отфильтрованному.
        var (_, page) = await api.Get(api.P($"/tasks?statusId={open}&statusId={closed}&sort=title&offset=1&limit=1"));
        Assert.Equal(3, page!["totalCount"]!.GetValue<int>());
        Assert.Equal(["one"], page["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray());
    }

    // ---- MCP ----

    [Fact]
    public async Task Mcp_list_tasks_takes_arrays_and_merges_them_with_the_single_ids()
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

        string Id(JsonNode node) => node["id"]!.GetValue<string>();
        var open = await Call("create_status", new { name = "Open", color = "#111111" });
        var closed = await Call("create_status", new { name = "Closed", color = "#222222" });
        var hold = await Call("create_status", new { name = "Hold", color = "#333333" });
        var flow = await Call("create_status_set", new { name = "Flow", statusIds = new[] { Id(open), Id(closed) } });
        var alt = await Call("create_status_set", new { name = "Alt", statusIds = new[] { Id(hold), Id(closed) } });
        var bug = await Call("create_task_type", new { name = "Bug", statusSetId = Id(flow) });
        var chore = await Call("create_task_type", new { name = "Chore", statusSetId = Id(alt) });
        var a = await Call("create_series", new { name = "A", prefix = "AAA" });
        var b = await Call("create_series", new { name = "B", prefix = "BBB" });
        await Call("create_task", new { title = "one", typeId = Id(bug), statusId = Id(open), seriesIds = new[] { Id(a) } });
        await Call("create_task", new { title = "two", typeId = Id(bug), statusId = Id(closed), seriesIds = new[] { Id(b) } });
        await Call("create_task", new { title = "three", typeId = Id(chore), statusId = Id(hold), seriesIds = new[] { Id(a), Id(b) } });
        await Call("create_task", new { title = "four", typeId = Id(chore), statusId = Id(closed) });

        string[] Titles(JsonNode list) => list["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray();

        Assert.Equal(["one", "three"], Titles(await Call("list_tasks", new { statusIds = new[] { Id(open), Id(hold) } })));
        Assert.Equal(["one"], Titles(await Call("list_tasks", new { statusIds = new[] { Id(open) } })));
        Assert.Equal(["one"], Titles(await Call("list_tasks", new { statusId = Id(open) })));
        // Одиночный и массив объединяются.
        Assert.Equal(["one", "three"], Titles(await Call("list_tasks", new { statusId = Id(open), statusIds = new[] { Id(hold) } })));
        Assert.Equal(["one", "three"], Titles(await Call("list_tasks", new { statusId = Id(open), statusIds = new[] { Id(hold), Id(open) } })));
        Assert.Equal(["one", "two", "three", "four"], Titles(await Call("list_tasks", new { typeIds = new[] { Id(bug), Id(chore) } })));
        Assert.Equal(["one", "two", "three"], Titles(await Call("list_tasks", new { seriesIds = new[] { Id(a), Id(b) } })));
        // И между параметрами.
        Assert.Equal(["three"], Titles(await Call("list_tasks", new { statusIds = new[] { Id(open), Id(hold) }, seriesIds = new[] { Id(b) } })));
        Assert.Equal(["three"], Titles(await Call("list_tasks", new { statusIds = new[] { Id(open), Id(hold) }, typeIds = new[] { Id(chore) } })));
        Assert.Empty(Titles(await Call("list_tasks", new { statusIds = new[] { Id(hold) }, typeId = Id(bug) })));
        // Пустой массив не ограничивает; страницы и totalCount по отфильтрованному, вместе с sort.
        Assert.Equal(4, Titles(await Call("list_tasks", new { statusIds = Array.Empty<string>() })).Length);
        var page = await Call("list_tasks", new { statusIds = new[] { Id(open), Id(closed) }, sort = "title", offset = 1, limit = 1 });
        Assert.Equal(3, page["totalCount"]!.GetValue<int>());
        Assert.Equal(["one"], Titles(page));
    }
}
