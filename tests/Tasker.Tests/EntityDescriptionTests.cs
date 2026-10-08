using System.Net;
using System.Text.Json.Nodes;
using Tasker.Storage.Files.Storages;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Описание у статусов и типов задач (TSK-104): REST и консоль на обеих реализациях хранилища, формат файлов (ключ только у непустого
/// описания, старый файл без описания читается), MCP через настоящий демон.
/// </summary>
public class EntityDescriptionTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    public static IEnumerable<object[]> Storages() => TestWorkspace.Storages();

    private static string Long(int length) => string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + i % 26)));

    private const string Multiline = "Задачу взял исполнитель \U0001F600\nКод пишется.\n\n  Закрывается в «Готово» после слияния.";

    private static string Str(JsonNode node, string name) => node[name]!.GetValue<string>();

    // ---- REST (папка и SQLite) ----

    [Theory, MemberData(nameof(Storages))]
    public async Task Rest_status_description_is_optional_unicode_multiline_and_can_be_changed_and_cleared(string storage)
    {
        await using var api = await ApiHost.Start(storage);

        // Клиент без поля description по-прежнему работает: в ответе пустая строка.
        var plain = (await api.Post(api.P("/statuses"), new { name = "Plain", color = "#112233" })).Body!;
        Assert.Equal("", Str(plain, "description"));

        var created = (await api.Post(api.P("/statuses"), new { name = "Doing", color = "#445566", description = Multiline })).Body!;
        var id = Str(created, "id");
        Assert.Equal(Multiline, Str(created, "description"));
        Assert.Equal(Multiline, Str((await api.Get(api.P($"/statuses/{id}"))).Body!, "description"));

        // Правка названия описание не трогает.
        var renamed = (await api.Patch(api.P($"/statuses/{id}"), new { name = "Doing 2", version = Str(created, "version") })).Body!;
        Assert.Equal("Doing 2", Str(renamed, "name"));
        Assert.Equal(Multiline, Str(renamed, "description"));

        // Новое описание.
        var changed = (await api.Patch(api.P($"/statuses/{id}"), new { description = "Новое", version = Str(renamed, "version") })).Body!;
        Assert.Equal("Новое", Str(changed, "description"));
        Assert.Equal("Doing 2", Str(changed, "name"));

        // Пустая строка очищает; из одних пробелов — то же.
        var cleared = (await api.Patch(api.P($"/statuses/{id}"), new { description = "", version = Str(changed, "version") })).Body!;
        Assert.Equal("", Str(cleared, "description"));
        var again = (await api.Patch(api.P($"/statuses/{id}"), new { description = "x", version = Str(cleared, "version") })).Body!;
        var blank = (await api.Patch(api.P($"/statuses/{id}"), new { description = "  \n ", version = Str(again, "version") })).Body!;
        Assert.Equal("", Str(blank, "description"));
        Assert.Equal("", Str((await api.Get(api.P($"/statuses/{id}"))).Body!, "description"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Rest_task_type_description_is_optional_unicode_multiline_and_can_be_changed_and_cleared(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var type = (await api.Get(api.P($"/task-types/{api.TypeId}"))).Body!;
        Assert.Equal("", Str(type, "description")); // создан без описания
        var setId = Str(type, "statusSetId");

        var created = (await api.Post(api.P("/task-types"), new { name = "Bug", statusSetId = setId, description = Multiline })).Body!;
        var id = Str(created, "id");
        Assert.Equal(Multiline, Str(created, "description"));
        Assert.Equal(Multiline, Str((await api.Get(api.P($"/task-types/{id}"))).Body!, "description"));

        var renamed = (await api.Patch(api.P($"/task-types/{id}"), new { name = "Defect", version = Str(created, "version") })).Body!;
        Assert.Equal("Defect", Str(renamed, "name"));
        Assert.Equal(Multiline, Str(renamed, "description"));

        var changed = (await api.Patch(api.P($"/task-types/{id}"), new { description = "R&D", version = Str(renamed, "version") })).Body!;
        Assert.Equal("R&D", Str(changed, "description"));

        var cleared = (await api.Patch(api.P($"/task-types/{id}"), new { description = "", version = Str(changed, "version") })).Body!;
        Assert.Equal("", Str(cleared, "description"));
        Assert.Equal("", Str((await api.Get(api.P($"/task-types/{id}"))).Body!, "description"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Rest_description_has_no_length_limit_like_the_task_description(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var text = Long(70_000);

        var status = (await api.Post(api.P("/statuses"), new { name = "Big", color = "#112233", description = text })).Body!;
        var type = (await api.Post(api.P("/task-types"), new { name = "Big", statusSetId = Str((await api.Get(api.P($"/task-types/{api.TypeId}"))).Body!, "statusSetId"), description = text })).Body!;

        Assert.Equal(text, Str((await api.Get(api.P($"/statuses/{Str(status, "id")}"))).Body!, "description"));
        Assert.Equal(text, Str((await api.Get(api.P($"/task-types/{Str(type, "id")}"))).Body!, "description"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Rest_lists_are_full_by_default_and_take_descriptionLength(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var setId = Str((await api.Get(api.P($"/task-types/{api.TypeId}"))).Body!, "statusSetId");
        var text = Long(500);
        await api.Post(api.P("/statuses"), new { name = "Zeta", color = "#112233", description = text });
        await api.Post(api.P("/statuses"), new { name = "Emoji", color = "#112233", description = "ab\U0001F600cd" });
        await api.Post(api.P("/task-types"), new { name = "Zeta", statusSetId = setId, description = text });

        foreach (var (path, name) in new[] { ("/statuses", "Zeta"), ("/task-types", "Zeta") })
        {
            JsonNode Item(JsonNode list) => list["data"]!.AsArray().Single(x => Str(x!, "name") == name)!;

            var full = Item((await api.Get(api.P(path))).Body!);
            Assert.Equal(text, Str(full, "description"));
            Assert.False(full["descriptionTruncated"]!.GetValue<bool>());
            Assert.Equal(500, full["descriptionLength"]!.GetValue<int>());

            Assert.Equal(text, Str(Item((await api.Get(api.P(path + "?descriptionLength=-1"))).Body!), "description"));

            var cut = Item((await api.Get(api.P(path + "?descriptionLength=10"))).Body!);
            Assert.Equal(text[..10], Str(cut, "description"));
            Assert.True(cut["descriptionTruncated"]!.GetValue<bool>());
            Assert.Equal(500, cut["descriptionLength"]!.GetValue<int>());

            Assert.False(Item((await api.Get(api.P(path + "?descriptionLength=500"))).Body!)["descriptionTruncated"]!.GetValue<bool>());
            Assert.True(Item((await api.Get(api.P(path + "?descriptionLength=499"))).Body!)["descriptionTruncated"]!.GetValue<bool>());

            var none = Item((await api.Get(api.P(path + "?descriptionLength=0"))).Body!);
            Assert.Equal("", Str(none, "description"));
            Assert.Equal(500, none["descriptionLength"]!.GetValue<int>());

            Assert.Equal(HttpStatusCode.BadRequest, (await api.Get(api.P(path + "?descriptionLength=-2"))).Status);

            // Одна сущность — всегда с полным описанием.
            var one = (await api.Get(api.P($"{path}/{Str(Item((await api.Get(api.P(path))).Body!), "id")}"))).Body!;
            Assert.Equal(text, Str(one, "description"));
        }

        // Эмодзи на границе не режется.
        var emoji = (await api.Get(api.P("/statuses?descriptionLength=3"))).Body!["data"]!.AsArray().Single(x => Str(x!, "name") == "Emoji")!;
        Assert.Equal("ab\U0001F600", Str(emoji, "description"));
    }

    // ---- файлы ----

    private static string[] YamlFilesIn(TestWorkspace ws, string folder) =>
        Directory.GetFiles(Path.Combine(ws.Root, ".tasker"), "*.yaml", SearchOption.AllDirectories)
            .Where(x => Path.GetFileName(Path.GetDirectoryName(x)) == folder)
            .ToArray();

    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    [Fact]
    public async Task The_file_has_the_description_key_only_while_the_description_is_not_empty()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("status", "create", "Todo"));
        await Ok(ws.Run("status-set", "create", "Flow", "--status", "Todo"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Flow"));

        Assert.DoesNotContain("description", File.ReadAllText(YamlFilesIn(ws, "statuses").Single()));
        Assert.DoesNotContain("description", File.ReadAllText(YamlFilesIn(ws, "task-types").Single()));

        await Ok(ws.Run("status", "update", "Todo", "-d", Multiline));
        await Ok(ws.Run("task-type", "update", "Bug", "-d", "Дефект \U0001F41B"));
        var status = File.ReadAllText(YamlFilesIn(ws, "statuses").Single());
        Assert.Contains("description:", status);
        Assert.StartsWith($"formatVersion: {FormatVersions.Current}\n", status);
        Assert.Contains("Дефект", File.ReadAllText(YamlFilesIn(ws, "task-types").Single()));
        Assert.Equal("Дефект \U0001F41B", Str((await Ok(ws.Run("task-type", "get", "Bug", "--json"))).Json, "description"));

        await Ok(ws.Run("status", "update", "Todo", "-d", ""));
        await Ok(ws.Run("task-type", "update", "Bug", "-d", ""));
        Assert.DoesNotContain("description", File.ReadAllText(YamlFilesIn(ws, "statuses").Single()));
        Assert.DoesNotContain("description", File.ReadAllText(YamlFilesIn(ws, "task-types").Single()));
    }

    [Fact]
    public void The_step_from_7_to_8_only_changes_the_version()
    {
        var old = "formatVersion: 7\nid: 1\nname: Todo\ncolor: '#112233'\n";
        Assert.Equal("formatVersion: 9\nid: 1\nname: Todo\ncolor: '#112233'\n", FormatVersions.Upgrade(old, "x.yaml"));
        Assert.Equal("formatVersion: 9\nid: 1\nname: Todo\ncolor: '#112233'\n", FormatVersions.Upgrade("formatVersion: 8\nid: 1\nname: Todo\ncolor: '#112233'\n", "x.yaml"));
    }

    [Fact]
    public void The_format_version_is_9_and_the_step_from_8_only_changes_the_version()
    {
        Assert.Equal(9, FormatVersions.Current);

        var old = "formatVersion: 8\nid: 1\nname: Needs\noutwardName: needs\ninwardName: is needed by\nallowCycles: true\n";
        Assert.Equal(old.Replace("formatVersion: 8", "formatVersion: 9"), FormatVersions.Upgrade(old, "x.yaml"));
    }

    [Fact]
    public async Task An_old_file_without_a_description_is_read_with_an_empty_one_and_gets_the_key_when_edited()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("status", "create", "Todo"));
        await Ok(ws.Run("status-set", "create", "Flow", "--status", "Todo"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Flow"));

        // Файлы в формате 7: без описания и с прежней версией.
        foreach (var file in YamlFilesIn(ws, "statuses").Concat(YamlFilesIn(ws, "task-types")))
            File.WriteAllText(file, File.ReadAllText(file).Replace($"formatVersion: {FormatVersions.Current}", "formatVersion: 7"));
        var before = YamlFilesIn(ws, "statuses").Select(File.ReadAllText).ToArray();

        var status = (await Ok(ws.Run("status", "get", "Todo", "--json"))).Json;
        Assert.Equal("", Str(status, "description"));
        Assert.Equal("", Str((await Ok(ws.Run("task-type", "get", "Bug", "--json"))).Json, "description"));
        Assert.Equal(before, YamlFilesIn(ws, "statuses").Select(File.ReadAllText).ToArray()); // чтение файл не меняет

        await Ok(ws.Run("status", "update", "Todo", "-d", "Теперь с описанием"));
        var updated = File.ReadAllText(YamlFilesIn(ws, "statuses").Single());
        Assert.StartsWith($"formatVersion: {FormatVersions.Current}\n", updated);
        Assert.Contains("Теперь с описанием", updated);
    }

    // ---- консоль (папка и SQLite) ----

    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("status", "create", "Todo", "-d", "Первая строка\nвторая строка \U0001F600"));
        await Ok(ws.Run("status", "create", "Plain"));
        await Ok(ws.Run("status-set", "create", "Flow", "--status", "Todo", "Plain"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Flow", "--description", "Дефект в выпущенном поведении"));
        await Ok(ws.Run("task-type", "create", "Plain", "--status-set", "Flow"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Console_get_shows_the_description_and_text_lists_do_not(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var status = (await Ok(ws.Run("status", "get", "Todo"))).Out;
        Assert.Contains("Первая строка\nвторая строка \U0001F600", status);
        Assert.DoesNotContain("description", (await Ok(ws.Run("status", "get", "Plain"))).Out);
        var type = (await Ok(ws.Run("task-type", "get", "Bug"))).Out;
        Assert.Contains("Дефект в выпущенном поведении", type);
        Assert.Contains("status set:", type);

        // В текстовых списках описаний нет.
        var statuses = (await Ok(ws.Run("status", "list"))).Out;
        Assert.DoesNotContain("Первая", statuses);
        Assert.Contains("Todo", statuses);
        Assert.DoesNotContain("Дефект", (await Ok(ws.Run("task-type", "list", "--description-length", "3"))).Out);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Console_update_changes_and_clears_the_description(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // -d — единственная правка.
        await Ok(ws.Run("status", "update", "Plain", "-d", "Теперь описан"));
        await Ok(ws.Run("task-type", "update", "Plain", "-d", "Тоже описан"));
        Assert.Equal("Теперь описан", Str((await Ok(ws.Run("status", "get", "Plain", "--json"))).Json, "description"));
        Assert.Equal("Тоже описан", Str((await Ok(ws.Run("task-type", "get", "Plain", "--json"))).Json, "description"));

        // Другая правка описание не трогает.
        await Ok(ws.Run("status", "update", "Plain", "--color", "#AABBCC"));
        await Ok(ws.Run("task-type", "update", "Plain", "--name", "Plain2"));
        Assert.Equal("Теперь описан", Str((await Ok(ws.Run("status", "get", "Plain", "--json"))).Json, "description"));
        Assert.Equal("Тоже описан", Str((await Ok(ws.Run("task-type", "get", "Plain2", "--json"))).Json, "description"));

        // Пустое значение очищает.
        await Ok(ws.Run("status", "update", "Plain", "-d", ""));
        await Ok(ws.Run("task-type", "update", "Plain2", "-d", ""));
        Assert.Equal("", Str((await Ok(ws.Run("status", "get", "Plain", "--json"))).Json, "description"));
        Assert.Equal("", Str((await Ok(ws.Run("task-type", "get", "Plain2", "--json"))).Json, "description"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Console_json_lists_are_full_by_default_and_take_description_length(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Ok(ws.Run("project", "create", "Demo"));
        var text = Long(300) + "\U0001F600";
        await Ok(ws.Run("status", "create", "Todo", "-d", text));
        await Ok(ws.Run("status-set", "create", "Flow", "--status", "Todo"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Flow", "-d", text));

        foreach (var group in new[] { "status", "task-type" })
        {
            var full = (await Ok(ws.Run(group, "list", "--json"))).Json["data"]![0]!;
            Assert.Equal(text, Str(full, "description"));
            Assert.False(full["descriptionTruncated"]!.GetValue<bool>());
            Assert.Equal(301, full["descriptionLength"]!.GetValue<int>());

            var cut = (await Ok(ws.Run(group, "list", "--json", "--description-length", "12"))).Json["data"]![0]!;
            Assert.Equal(Long(12), Str(cut, "description"));
            Assert.True(cut["descriptionTruncated"]!.GetValue<bool>());
            Assert.Equal(301, cut["descriptionLength"]!.GetValue<int>());

            Assert.Equal("", Str((await Ok(ws.Run(group, "list", "--json", "--description-length", "0"))).Json["data"]![0]!, "description"));
            Assert.Equal(Long(300), Str((await Ok(ws.Run(group, "list", "--json", "--description-length", "300"))).Json["data"]![0]!, "description"));
            Assert.NotEqual(0, (await ws.Run(group, "list", "--json", "--description-length", "-5")).Code);
        }
    }

    // ---- MCP (настоящий демон) ----

    private async Task<(string Key, Guid ProjectId)> StartMcp()
    {
        await _daemon.Workspace("a", "Alpha");
        var status = await _daemon.Start();
        var key = status.Workspaces.Single().Key!;
        var projectId = Guid.Parse(JsonNode.Parse(await _daemon.CallTool(key, "list_projects"))!["data"]![0]!["id"]!.GetValue<string>());
        return (key, projectId);
    }

    private async Task<JsonNode> Call(string key, Guid projectId, string tool, object args)
    {
        var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject();
        node["projectId"] = projectId.ToString();
        var (isError, text) = await _daemon.CallToolResult(key, tool, node);
        Assert.False(isError, $"{tool} failed: {text}");
        return JsonNode.Parse(text)!;
    }

    [Fact]
    public async Task Mcp_creates_updates_and_lists_descriptions_with_a_200_character_preview_by_default()
    {
        var (key, projectId) = await StartMcp();

        var tools = (await _daemon.Rpc("tools/list"))["tools"]!.AsArray().ToDictionary(x => Str(x!, "name"), x => x!);
        foreach (var tool in new[] { "create_status", "update_status", "create_task_type", "update_task_type" })
            Assert.NotNull(tools[tool]["inputSchema"]!["properties"]!["description"]);
        foreach (var tool in new[] { "list_statuses", "list_task_types" })
        {
            Assert.NotNull(tools[tool]["inputSchema"]!["properties"]!["descriptionLength"]);
            Assert.DoesNotContain("descriptionLength", tools[tool]["inputSchema"]!["required"]?.ToJsonString() ?? "");
            Assert.Contains("descriptionLength -1", Str(tools[tool], "description"));
        }

        var text = Long(2700);
        var status = await Call(key, projectId, "create_status", new { name = "Todo", color = "#112233", description = text });
        Assert.Equal(text, Str(status, "description"));
        var plain = await Call(key, projectId, "create_status", new { name = "Plain", color = "#112233" });
        Assert.Equal("", Str(plain, "description"));
        var set = await Call(key, projectId, "create_status_set", new { name = "Set", statusIds = new[] { Str(status, "id"), Str(plain, "id") } });
        var type = await Call(key, projectId, "create_task_type", new { name = "Bug", statusSetId = Str(set, "id"), description = text });
        Assert.Equal(text, Str(type, "description"));

        foreach (var (list, name) in new[] { ("list_statuses", "Todo"), ("list_task_types", "Bug") })
        {
            JsonNode Item(JsonNode result) => result["data"]!.AsArray().Single(x => Str(x!, "name") == name)!;

            var preview = Item(await Call(key, projectId, list, new { }));
            Assert.Equal(text[..200], Str(preview, "description"));
            Assert.True(preview["descriptionTruncated"]!.GetValue<bool>());
            Assert.Equal(2700, preview["descriptionLength"]!.GetValue<int>());

            Assert.Equal(text, Str(Item(await Call(key, projectId, list, new { descriptionLength = -1 })), "description"));
            Assert.Equal(text[..7], Str(Item(await Call(key, projectId, list, new { descriptionLength = 7 })), "description"));
            var none = Item(await Call(key, projectId, list, new { descriptionLength = 0 }));
            Assert.Equal("", Str(none, "description"));
            Assert.Equal(2700, none["descriptionLength"]!.GetValue<int>());

            var (isError, _) = await _daemon.CallToolResult(key, list, JsonNode.Parse($"{{\"projectId\":\"{projectId}\",\"descriptionLength\":-2}}")!);
            Assert.True(isError);
        }

        // Правка: описание меняется отдельно, другие правки его не трогают, пустая строка очищает.
        var changed = await Call(key, projectId, "update_status", new { statusId = Str(status, "id"), version = Str(status, "version"), description = "Коротко" });
        Assert.Equal("Коротко", Str(changed, "description"));
        var renamed = await Call(key, projectId, "update_status", new { statusId = Str(status, "id"), version = Str(changed, "version"), name = "Todo2" });
        Assert.Equal("Коротко", Str(renamed, "description"));
        var cleared = await Call(key, projectId, "update_status", new { statusId = Str(status, "id"), version = Str(renamed, "version"), description = "" });
        Assert.Equal("", Str(cleared, "description"));

        var typeChanged = (await Call(key, projectId, "update_task_type", new { taskTypeId = Str(type, "id"), version = Str(type, "version"), description = "R&D" }));
        Assert.Equal("R&D", Str(typeChanged, "description"));
        var typeCleared = (await Call(key, projectId, "update_task_type", new { taskTypeId = Str(type, "id"), version = Str(typeChanged, "version"), description = "" }));
        Assert.Equal("", Str(typeCleared, "description"));
    }
}
