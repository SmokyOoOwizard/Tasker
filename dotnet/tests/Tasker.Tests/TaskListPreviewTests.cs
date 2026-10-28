using System.Net;
using System.Text.Json.Nodes;
using Tasker.Core.Tasks;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Краткий режим списков задач (TSK-89): параметр <c>descriptionLength</c> — усечение описаний в записях списка.
/// Ядро (усечение по символам Unicode), REST, консоль (обе реализации хранилища) и MCP через настоящий демон.
/// </summary>
public class TaskListPreviewTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    public static IEnumerable<object[]> Storages() => TestWorkspace.Storages();

    // ---- усечение: ядро ----

    [Theory]
    [InlineData("hello world", 5, "hello", 11)]
    [InlineData("hello", 5, "hello", 5)] // граница: N равно длине — не усечено
    [InlineData("hello", 4, "hell", 5)]
    [InlineData("hello", 100, "hello", 5)]
    [InlineData("hello", 0, "", 5)]
    [InlineData("hello", -1, "hello", 5)]
    [InlineData("", 10, "", 0)]
    public void Cut_takes_the_first_characters_without_an_ellipsis(string text, int length, string expected, int total)
    {
        var (cut, full) = DescriptionPreview.Cut(text, length);
        Assert.Equal(expected, cut);
        Assert.Equal(total, full);
    }

    [Fact]
    public void Cut_of_a_missing_description_is_empty_with_zero_length()
    {
        var (text, length) = DescriptionPreview.Cut(null, 5);
        Assert.Null(text);
        Assert.Equal(0, length);
    }

    [Theory]
    [InlineData("ab\U0001F600cd", 3, "ab\U0001F600", 5)] // эмодзи — один символ (две единицы UTF-16), не режется
    [InlineData("ab\U0001F600cd", 2, "ab", 5)]
    [InlineData("\U0001F600\U0001F600", 1, "\U0001F600", 2)]
    [InlineData("éé", 1, "", 4)] // «é» из e + комбинируемый акцент — одна графема: не рвём, отбрасываем целиком
    [InlineData("éé", 2, "é", 4)]
    [InlineData("éé", 3, "é", 4)]
    [InlineData("a\U0001F468‍\U0001F469‍\U0001F467b", 2, "a", 7)] // семья из ZWJ-последовательности не рвётся
    [InlineData("a\U0001F468‍\U0001F469‍\U0001F467b", 6, "a\U0001F468‍\U0001F469‍\U0001F467", 7)]
    [InlineData("Привет, мир", 6, "Привет", 11)]
    public void Cut_never_splits_a_surrogate_pair_or_a_combining_sequence(string text, int length, string expected, int total)
    {
        var (cut, full) = DescriptionPreview.Cut(text, length);
        Assert.Equal(expected, cut);
        Assert.Equal(total, full);
        Assert.True(cut!.Length == 0 || !char.IsHighSurrogate(cut[^1]));
    }

    [Fact]
    public void Check_accepts_minus_one_zero_and_positive_and_rejects_less()
    {
        Assert.Equal(-1, DescriptionPreview.Check(null));
        Assert.Equal(200, DescriptionPreview.Check(null, 200));
        Assert.Equal(0, DescriptionPreview.Check(0));
        Assert.Equal(7, DescriptionPreview.Check(7));
        Assert.Equal(-1, DescriptionPreview.Check(-1));
        Assert.Throws<Tasker.Core.TaskerValidationException>(() => DescriptionPreview.Check(-2));
    }

    // ---- REST (папка и SQLite) ----

    private static string Long(int length) => string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + i % 26)));

    private static async Task<JsonNode> Created(ApiHost api, string title, string? description) =>
        (await api.Post(api.P("/tasks"), new { title, typeId = api.TypeId, description })).Body!;

    [Theory, MemberData(nameof(Storages))]
    public async Task Rest_list_is_full_by_default_and_takes_descriptionLength(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var text = Long(500);
        var emoji = "ab\U0001F600cd";
        await Created(api, "Long", text);
        await Created(api, "Emoji", emoji);
        await Created(api, "Empty", null);

        JsonNode Item(JsonNode list, string title) => list["data"]!.AsArray().Single(x => x!["title"]!.GetValue<string>() == title)!;

        // Без параметра — как раньше: полный текст (плюс новые поля).
        var full = (await api.Get(api.P("/tasks"))).Body!;
        Assert.Equal(3, full["totalCount"]!.GetValue<int>());
        Assert.Equal(text, Item(full, "Long")["description"]!.GetValue<string>());
        Assert.False(Item(full, "Long")["descriptionTruncated"]!.GetValue<bool>());
        Assert.Equal(500, Item(full, "Long")["descriptionLength"]!.GetValue<int>());
        Assert.Equal(0, Item(full, "Long")["linksCount"]!.GetValue<int>());

        // -1 — то же самое.
        Assert.Equal(text, Item((await api.Get(api.P("/tasks?descriptionLength=-1"))).Body!, "Long")["description"]!.GetValue<string>());

        var cut = (await api.Get(api.P("/tasks?descriptionLength=10"))).Body!;
        Assert.Equal(text[..10], Item(cut, "Long")["description"]!.GetValue<string>());
        Assert.True(Item(cut, "Long")["descriptionTruncated"]!.GetValue<bool>());
        Assert.Equal(500, Item(cut, "Long")["descriptionLength"]!.GetValue<int>());
        Assert.Equal(3, cut["totalCount"]!.GetValue<int>()); // totalCount и страницы не меняются

        // Граница: N равно длине и на единицу меньше.
        Assert.False(Item((await api.Get(api.P("/tasks?descriptionLength=500"))).Body!, "Long")["descriptionTruncated"]!.GetValue<bool>());
        Assert.True(Item((await api.Get(api.P("/tasks?descriptionLength=499"))).Body!, "Long")["descriptionTruncated"]!.GetValue<bool>());

        // 0 — без описания, но длина известна.
        var none = (await api.Get(api.P("/tasks?descriptionLength=0"))).Body!;
        Assert.Equal("", Item(none, "Long")["description"]!.GetValue<string>());
        Assert.True(Item(none, "Long")["descriptionTruncated"]!.GetValue<bool>());
        Assert.Equal(500, Item(none, "Long")["descriptionLength"]!.GetValue<int>());

        // Эмодзи на границе не режется.
        Assert.Equal("ab\U0001F600", Item((await api.Get(api.P("/tasks?descriptionLength=3"))).Body!, "Emoji")["description"]!.GetValue<string>());
        Assert.Equal("ab", Item((await api.Get(api.P("/tasks?descriptionLength=2"))).Body!, "Emoji")["description"]!.GetValue<string>());

        // Пустое описание: не усечено, длина 0.
        var empty = Item(none, "Empty");
        Assert.False(empty["descriptionTruncated"]!.GetValue<bool>());
        Assert.Equal(0, empty["descriptionLength"]!.GetValue<int>());
        Assert.True(string.IsNullOrEmpty(empty["description"]?.GetValue<string>()));

        // Меньше -1 — ошибка запроса.
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Get(api.P("/tasks?descriptionLength=-2"))).Status);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Rest_single_task_is_always_full_and_linksCount_counts_both_directions(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var text = Long(400);
        var a = await Created(api, "A", text);
        var b = await Created(api, "B", "b");
        var c = await Created(api, "C", "c");
        var blocks = (await api.Get(api.P("/link-types"))).Body!["data"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "Blocks")!["id"]!.GetValue<string>();
        string Id(JsonNode n) => n["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.OK, (await api.Post(api.P($"/tasks/{Id(a)}/links"), new { typeId = blocks, targetId = Id(b) })).Status);
        Assert.Equal(HttpStatusCode.OK, (await api.Post(api.P($"/tasks/{Id(c)}/links"), new { typeId = blocks, targetId = Id(b) })).Status);

        var list = (await api.Get(api.P("/tasks?descriptionLength=5"))).Body!;
        int Links(string title) => list["data"]!.AsArray().Single(x => x!["title"]!.GetValue<string>() == title)!["linksCount"]!.GetValue<int>();
        Assert.Equal(1, Links("A")); // исходящая
        Assert.Equal(2, Links("B")); // две входящие
        Assert.Equal(1, Links("C"));

        Assert.Equal(text, (await api.Get(api.P($"/tasks/{Id(a)}"))).Body!["description"]!.GetValue<string>());
        Assert.Equal(text, (await api.Get(api.P($"/tasks/resolve?ref={Id(a)}"))).Body!.AsArray()[0]!["description"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Rest_column_tasks_take_descriptionLength(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var type = (await api.Get(api.P($"/task-types/{api.TypeId}"))).Body!;
        var setId = type["statusSetId"]!.GetValue<string>();
        var statusId = (await api.Get(api.P("/statuses"))).Body!["data"]![0]!["id"]!.GetValue<string>();
        var board = (await api.Post(api.P("/boards"), new
        {
            name = "Main", statusSetIds = new[] { setId },
            columns = new[] { new { name = "All", statusIds = new[] { statusId }, dropStatuses = new Dictionary<string, string> { [setId] = statusId } } }
        })).Body!;
        var path = api.P($"/boards/{board["id"]!.GetValue<string>()}/columns/{board["columns"]![0]!["id"]!.GetValue<string>()}/tasks");
        var text = Long(300);
        await Created(api, "One", text);

        var fullItem = (await api.Get(path)).Body!["data"]![0]!;
        Assert.Equal(text, fullItem["description"]!.GetValue<string>());
        Assert.False(fullItem["descriptionTruncated"]!.GetValue<bool>());

        var cutItem = (await api.Get(path + "?descriptionLength=20")).Body!["data"]![0]!;
        Assert.Equal(text[..20], cutItem["description"]!.GetValue<string>());
        Assert.True(cutItem["descriptionTruncated"]!.GetValue<bool>());
        Assert.Equal(300, cutItem["descriptionLength"]!.GetValue<int>());
    }

    // ---- консоль (папка и SQLite) ----

    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    private static async Task Seed(TestWorkspace ws, string description)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("status", "create", "Todo"));
        await Ok(ws.Run("status-set", "create", "Flow", "--status", "Todo"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(ws.Run("series", "create", "Tasks", "--prefix", "TSK"));
        await Ok(ws.Run("board", "create", "Main", "--status-set", "Flow", "--column", "Todo=Todo"));
        await Ok(ws.Run("task", "create", "First", "--type", "Bug", "--series", "TSK", "--description", description));
        await Ok(ws.Run("task", "create", "Second", "--type", "Bug", "--series", "TSK"));
        await Ok(ws.Run("task", "link", "TSK-1", "blocks", "TSK-2"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Console_json_lists_are_full_by_default_and_take_description_length(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        var text = Long(300) + "\U0001F600";
        await Seed(ws, text);

        var item = (await Ok(ws.Run("task", "list", "--json"))).Json["data"]![0]!;
        Assert.Equal(text, item["description"]!.GetValue<string>());
        Assert.False(item["descriptionTruncated"]!.GetValue<bool>());
        Assert.Equal(301, item["descriptionLength"]!.GetValue<int>());
        Assert.Equal(1, item["linksCount"]!.GetValue<int>());

        var cut = (await Ok(ws.Run("task", "list", "--json", "--description-length", "12"))).Json["data"]!.AsArray();
        Assert.Equal(Long(12), cut[0]!["description"]!.GetValue<string>());
        Assert.True(cut[0]!["descriptionTruncated"]!.GetValue<bool>());
        Assert.Equal(301, cut[0]!["descriptionLength"]!.GetValue<int>());
        Assert.Equal(1, cut[1]!["linksCount"]!.GetValue<int>()); // входящая связь

        var none = (await Ok(ws.Run("task", "list", "--json", "--description-length", "0"))).Json["data"]![0]!;
        Assert.Equal("", none["description"]!.GetValue<string>());

        // Граница на эмодзи: 300 букв + эмодзи; N=300 — без эмодзи, N=301 — полностью.
        Assert.Equal(Long(300), (await Ok(ws.Run("task", "list", "--json", "--description-length", "300"))).Json["data"]![0]!["description"]!.GetValue<string>());
        Assert.Equal(text, (await Ok(ws.Run("task", "list", "--json", "--description-length", "301"))).Json["data"]![0]!["description"]!.GetValue<string>());

        var bad = await ws.Run("task", "list", "--json", "--description-length", "-5");
        Assert.NotEqual(0, bad.Code);

        // Одна задача — всегда полная.
        Assert.Equal(text, (await Ok(ws.Run("task", "get", "TSK-1", "--json"))).Json["description"]!.GetValue<string>());
        // Текстовый вывод не меняется.
        Assert.Equal(["TSK-1  Todo  Bug  First", "TSK-2  Todo  Bug  Second"],
            (await Ok(ws.Run("task", "list", "--description-length", "3"))).Data.TrimEnd('\n').Split('\n'));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Console_board_json_takes_description_length(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        var text = Long(100);
        await Seed(ws, text);

        var fullTask = (await Ok(ws.Run("board", "tasks", "Main", "Todo", "--json"))).Json["data"]![0]!;
        Assert.Equal(text, fullTask["description"]!.GetValue<string>());
        var cutTask = (await Ok(ws.Run("board", "tasks", "Main", "Todo", "--json", "--description-length", "8"))).Json["data"]![0]!;
        Assert.Equal(text[..8], cutTask["description"]!.GetValue<string>());
        Assert.True(cutTask["descriptionTruncated"]!.GetValue<bool>());

        var fullShow = (await Ok(ws.Run("board", "show", "Main", "--json"))).Json["columns"]![0]!["tasks"]![0]!;
        Assert.Equal(text, fullShow["description"]!.GetValue<string>());
        var cutShow = (await Ok(ws.Run("board", "show", "Main", "--json", "--description-length", "0", "--all"))).Json["columns"]![0]!["tasks"]![0]!;
        Assert.Equal("", cutShow["description"]!.GetValue<string>());
        Assert.Equal(100, cutShow["descriptionLength"]!.GetValue<int>());
    }

    // ---- MCP (настоящий демон) ----

    private async Task<(string Key, Guid ProjectId, string TypeId, string SetId, string StatusId)> StartMcp()
    {
        await _daemon.Workspace("a", "Alpha");
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
        return (key, projectId, type["id"]!.GetValue<string>(), set["id"]!.GetValue<string>(), todo["id"]!.GetValue<string>());
    }

    private async Task<JsonNode> Call(string key, Guid projectId, string tool, object args)
    {
        var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject();
        node["projectId"] = projectId.ToString();
        var (isError, text) = await _daemon.CallToolResult(key, tool, node);
        Assert.False(isError, $"{tool} failed: {text}");
        return JsonNode.Parse(text)!;
    }

    private async Task<string> Raw(string key, Guid projectId, string tool, object args)
    {
        var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject();
        node["projectId"] = projectId.ToString();
        var (isError, text) = await _daemon.CallToolResult(key, tool, node);
        Assert.False(isError, $"{tool} failed: {text}");
        return text;
    }

    [Fact]
    public async Task Mcp_lists_default_to_a_200_character_preview_and_are_much_smaller()
    {
        var (key, projectId, typeId, setId, statusId) = await StartMcp();
        var series = await Call(key, projectId, "create_series", new { name = "Tasks", prefix = "TSK" });
        var text = Long(2700);
        for (var i = 0; i < 50; i++)
            await Call(key, projectId, "create_task", new { title = $"Task {i:00}", typeId, description = text, seriesIds = new[] { series["id"]!.GetValue<string>() } });

        // Схемы: параметр есть, необязателен, а описание инструмента говорит, как получить полный текст.
        var tools = (await _daemon.Rpc("tools/list"))["tools"]!.AsArray().ToDictionary(x => x!["name"]!.GetValue<string>(), x => x!);
        foreach (var tool in new[] { "list_tasks", "find_tasks_by_reference", "get_board" })
        {
            Assert.NotNull(tools[tool]["inputSchema"]!["properties"]!["descriptionLength"]);
            Assert.DoesNotContain("descriptionLength", tools[tool]["inputSchema"]!["required"]?.ToJsonString() ?? "");
            Assert.Contains("get_task", tools[tool]["description"]!.GetValue<string>());
            Assert.Contains("descriptionLength -1", tools[tool]["description"]!.GetValue<string>());
        }

        // По умолчанию 200 символов, длина и признак усечения.
        var shortRaw = await Raw(key, projectId, "list_tasks", new { });
        var list = JsonNode.Parse(shortRaw)!;
        Assert.Equal(50, list["totalCount"]!.GetValue<int>());
        var first = list["data"]![0]!;
        Assert.Equal(text[..200], first["description"]!.GetValue<string>());
        Assert.True(first["descriptionTruncated"]!.GetValue<bool>());
        Assert.Equal(2700, first["descriptionLength"]!.GetValue<int>());
        Assert.Equal(0, first["linksCount"]!.GetValue<int>());

        // Полный текст — -1; размер ответа с предпросмотром резко меньше.
        var fullRaw = await Raw(key, projectId, "list_tasks", new { descriptionLength = -1 });
        Assert.Equal(text, JsonNode.Parse(fullRaw)!["data"]![0]!["description"]!.GetValue<string>());
        Assert.True(shortRaw.Length * 4 < fullRaw.Length, $"short {shortRaw.Length}, full {fullRaw.Length}");

        var none = JsonNode.Parse(await Raw(key, projectId, "list_tasks", new { descriptionLength = 0 }))!["data"]![0]!;
        Assert.True(string.IsNullOrEmpty(none["description"]?.GetValue<string>()));
        Assert.Equal(2700, none["descriptionLength"]!.GetValue<int>());
        Assert.Equal(text[..7], JsonNode.Parse(await Raw(key, projectId, "list_tasks", new { descriptionLength = 7, limit = 1 }))!["data"]![0]!["description"]!.GetValue<string>());

        // Одна задача — полная.
        var taskId = first["id"]!.GetValue<string>();
        Assert.Equal(text, (await Call(key, projectId, "get_task", new { taskId }))["description"]!.GetValue<string>());

        // find_tasks_by_reference.
        var found = (await Call(key, projectId, "find_tasks_by_reference", new { reference = "TSK-1" }))["tasks"]![0]!;
        Assert.Equal(text[..200], found["description"]!.GetValue<string>());
        Assert.True(found["descriptionTruncated"]!.GetValue<bool>());
        var foundFull = (await Call(key, projectId, "find_tasks_by_reference", new { reference = "TSK-1", descriptionLength = -1 }))["tasks"]![0]!;
        Assert.Equal(text, foundFull["description"]!.GetValue<string>());

        // get_board.
        var board = await Call(key, projectId, "create_board", new
        {
            name = "Main", statusSetIds = new[] { setId },
            columns = new[] { new { name = "All", statusIds = new[] { statusId }, dropStatuses = new Dictionary<string, string> { [setId] = statusId } } }
        });
        var boardId = board["id"]!.GetValue<string>();
        var column = (await Call(key, projectId, "get_board", new { boardId, tasksPerColumn = 3 }))["columns"]![0]!;
        Assert.Equal(50, column["tasks"]!["totalCount"]!.GetValue<int>());
        Assert.Equal(text[..200], column["tasks"]!["data"]![0]!["description"]!.GetValue<string>());
        var boardFull = (await Call(key, projectId, "get_board", new { boardId, tasksPerColumn = 1, descriptionLength = -1 }))["columns"]![0]!;
        Assert.Equal(text, boardFull["tasks"]!["data"]![0]!["description"]!.GetValue<string>());
    }
}
