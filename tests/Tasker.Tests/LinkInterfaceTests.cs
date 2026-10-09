using System.Net;
using System.Text.Json.Nodes;
using Tasker.Global;
using Xunit;

namespace Tasker.Tests;

/// <summary>Связи между задачами снаружи: консоль, REST и MCP.</summary>
[InProcess]
public class LinkInterfaceTests
{
    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    /// <summary>Единственный проект (--project не нужен) с серией TSK и тремя задачами TSK-1..3 в статусе Todo.</summary>
    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("status", "create", "Todo"));
        await Ok(ws.Run("status-set", "create", "Flow", "--status", "Todo"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(ws.Run("series", "create", "Tasks", "--prefix", "TSK"));
        foreach (var title in new[] { "First", "Second", "Third" })
            await Ok(ws.Run("task", "create", title, "--type", "Bug", "--series", "TSK"));
    }

    private static string[] Lines(CliResult result) => result.Data.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // ---- консоль ----

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Link_types_default_to_the_jira_set_and_can_be_managed(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var list = await Ok(ws.Run("link-type", "list"));
        Assert.Equal(6, Lines(list).Length);
        Assert.Contains(Lines(list), x => System.Text.RegularExpressions.Regex.IsMatch(x, "Blocks +blocks / is blocked by"));
        Assert.Contains(Lines(list), x => System.Text.RegularExpressions.Regex.IsMatch(x, "Relates +relates to") && !x.Contains('/'));
        Assert.Equal(6, (await Ok(ws.Run("link-type", "list", "--json"))).Json["totalCount"]!.GetValue<int>());

        var created = await Ok(ws.Run("link-type", "create", "Depends", "--outward", "depends on", "--inward", "is a dependency of"));
        Assert.StartsWith("Created link type 'Depends' (depends on / is a dependency of) ", created.Out);
        Assert.Contains("is a dependency of", (await Ok(ws.Run("link-type", "get", "depends"))).Out);

        var updated = await Ok(ws.Run("link-type", "update", "Depends", "--inward", "is needed by"));
        Assert.Contains("(depends on / is needed by)", updated.Out);

        var duplicate = await ws.Run("link-type", "create", "blocks", "--outward", "x");
        Assert.Equal(1, duplicate.Code);
        Assert.Contains("already exists", duplicate.Err);

        await Ok(ws.Run("link-type", "delete", "Depends"));
        Assert.Equal(1, (await ws.Run("link-type", "get", "Depends")).Code);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Tasks_are_linked_and_each_side_sees_its_own_name(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var linked = await Ok(ws.Run("task", "link", "TSK-1", "blocks", "TSK-2"));
        Assert.Equal("Linked: TSK-1 blocks TSK-2", linked.Out.Trim());

        var from = Lines(await Ok(ws.Run("task", "links", "TSK-1")));
        Assert.Single(from);
        Assert.Matches(@"^blocks +TSK-2  Todo  Second$", from[0]);
        var to = Lines(await Ok(ws.Run("task", "links", "TSK-2")));
        Assert.Matches(@"^is blocked by +TSK-1  Todo  First$", to[0]);

        // task get показывает связи вместе с остальным.
        var get = (await Ok(ws.Run("task", "get", "TSK-2"))).Out;
        Assert.Contains("links:", get);
        Assert.Matches(@"is blocked by +TSK-1  Todo  First", get);
        Assert.DoesNotContain("links:", (await Ok(ws.Run("task", "get", "TSK-3"))).Out);

        // Повторное добавление ничего не меняет.
        await Ok(ws.Run("task", "link", "TSK-1", "Blocks", "TSK-2"));
        Assert.Single(Lines(await Ok(ws.Run("task", "links", "TSK-1"))));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Task_links_and_task_get_align_columns_of_references_of_different_length(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        for (var i = 4; i <= 10; i++)
            await Ok(ws.Run("task", "create", $"Задача {i}", "--type", "Bug", "--series", "TSK"));
        await Ok(ws.Run("task", "link", "TSK-1", "blocks", "TSK-2"));
        await Ok(ws.Run("task", "link", "TSK-1", "blocks", "TSK-10"));
        await Ok(ws.Run("task", "link", "TSK-9", "relates to", "TSK-1"));

        Assert.Equal(
            ["blocks      TSK-2   Todo  Second", "blocks      TSK-10  Todo  Задача 10", "relates to  TSK-9   Todo  Задача 9"],
            Lines(await Ok(ws.Run("task", "links", "TSK-1"))).OrderBy(x => x.StartsWith("relates") ? 1 : 0).ThenBy(x => x.Length).ToArray());

        var get = (await Ok(ws.Run("task", "get", "TSK-1"))).Out;
        Assert.Contains("  blocks      TSK-2   Todo  Second", get);
        Assert.Contains("  blocks      TSK-10  Todo  Задача 10", get);
        Assert.Contains("  relates to  TSK-9   Todo  Задача 9", get);
    }

    [Fact]
    public async Task Reading_never_writes_files_and_the_first_link_saves_the_default_types()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        string[] Files() => Directory.GetFiles(Path.Combine(ws.Root, ".tasker"), "*.yaml", SearchOption.AllDirectories).Order().ToArray();
        var before = Files();

        // Просмотр задач, связей и типов связей — только чтение: репозиторий остаётся как был.
        await Ok(ws.Run("task", "get", "TSK-1"));
        await Ok(ws.Run("task", "links", "TSK-1"));
        await Ok(ws.Run("link-type", "list"));
        await Ok(ws.Run("link-type", "get", "Blocks"));
        Assert.Equal(before, Files());
        Assert.DoesNotContain(Files(), x => x.Contains("link-types"));

        // Первая запись — связь — сохраняет шесть типов по умолчанию (и сам файл задачи получает links).
        await Ok(ws.Run("task", "link", "TSK-1", "blocks", "TSK-2"));
        Assert.Equal(6, Files().Count(x => x.Contains("link-types")));

        // Дальше они — обычные сохранённые типы: ничего не дублируется.
        await Ok(ws.Run("task", "link", "TSK-2", "relates to", "TSK-3"));
        Assert.Equal(6, Files().Count(x => x.Contains("link-types")));
        Assert.Equal(6, (await Ok(ws.Run("link-type", "list", "--json"))).Json["totalCount"]!.GetValue<int>());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task The_inward_phrase_links_in_the_other_direction(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // «TSK-2 is blocked by TSK-3» == «TSK-3 blocks TSK-2».
        var linked = await Ok(ws.Run("task", "link", "TSK-2", "is blocked by", "TSK-3"));
        Assert.Equal("Linked: TSK-2 is blocked by TSK-3", linked.Out.Trim());

        Assert.Matches(@"^blocks +TSK-2", Lines(await Ok(ws.Run("task", "links", "TSK-3")))[0]);
        Assert.Matches(@"^is blocked by +TSK-3", Lines(await Ok(ws.Run("task", "links", "TSK-2")))[0]);
        // Связь хранится в TSK-3: у TSK-2 в списке исходящих ничего нет.
        var json = (await Ok(ws.Run("task", "link", "TSK-2", "is blocked by", "TSK-3", "--json"))).Json;
        Assert.Equal("inward", json["direction"]!.GetValue<string>());
        Assert.Equal((await Ok(ws.Run("task", "get", "TSK-3", "--json"))).Json["id"]!.GetValue<string>(), json["source"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Symmetric_links_look_alike_and_are_removed_from_either_side(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        await Ok(ws.Run("task", "link", "TSK-1", "relates to", "TSK-2"));
        await Ok(ws.Run("task", "link", "TSK-2", "relates to", "TSK-1")); // уже есть с другой стороны

        Assert.Matches(@"^relates to +TSK-2", Lines(await Ok(ws.Run("task", "links", "TSK-1")))[0]);
        Assert.Matches(@"^relates to +TSK-1", Lines(await Ok(ws.Run("task", "links", "TSK-2")))[0]);
        Assert.Single(Lines(await Ok(ws.Run("task", "links", "TSK-2"))));

        var removed = await Ok(ws.Run("task", "unlink", "TSK-2", "relates to", "TSK-1"));
        Assert.Equal("Unlinked: TSK-2 relates to TSK-1", removed.Out.Trim());
        Assert.Empty((await Ok(ws.Run("task", "links", "TSK-1"))).Data.Trim());
        Assert.Empty((await Ok(ws.Run("task", "links", "TSK-2"))).Data.Trim());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Unlinking_and_deleting_tasks_clean_up_links(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(ws.Run("task", "link", "TSK-1", "blocks", "TSK-2"));
        await Ok(ws.Run("task", "link", "TSK-3", "duplicates", "TSK-2"));

        await Ok(ws.Run("task", "unlink", "TSK-1", "blocks", "TSK-2"));
        Assert.Empty((await Ok(ws.Run("task", "links", "TSK-1"))).Data.Trim());
        await Ok(ws.Run("task", "unlink", "TSK-1", "blocks", "TSK-2")); // нет связи — не ошибка

        // Удалили цель — у источника связь исчезла, и тип можно удалить.
        await Ok(ws.Run("task", "delete", "TSK-2"));
        Assert.Empty((await Ok(ws.Run("task", "links", "TSK-3"))).Data.Trim());
        await Ok(ws.Run("link-type", "delete", "Duplicate"));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task A_link_type_with_links_cannot_be_deleted(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(ws.Run("task", "link", "TSK-1", "blocks", "TSK-2"));

        var result = await ws.Run("link-type", "delete", "Blocks");

        Assert.Equal(1, result.Code);
        Assert.StartsWith("In use: ", result.Err);
        Assert.Contains("1 task(s)", result.Err);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Custom_link_types_work_by_either_of_their_names(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(ws.Run("link-type", "create", "Depends", "--outward", "depends on", "--inward", "is a dependency of"));

        await Ok(ws.Run("task", "link", "TSK-1", "depends on", "TSK-2"));
        await Ok(ws.Run("task", "link", "TSK-3", "is a dependency of", "TSK-1"));

        // «TSK-3 is a dependency of TSK-1» — то же, что «TSK-1 depends on TSK-3»: у TSK-1 две исходящие связи.
        var links = Lines(await Ok(ws.Run("task", "links", "TSK-1")));
        Assert.Equal(2, links.Length);
        Assert.Matches(@"^depends on +TSK-2", links[0]);
        Assert.Matches(@"^depends on +TSK-3", links[1]);
        // А обе зависимости видят её со своей стороны.
        Assert.Matches(@"^is a dependency of +TSK-1", Lines(await Ok(ws.Run("task", "links", "TSK-3")))[0]);
        Assert.Matches(@"^is a dependency of +TSK-1", Lines(await Ok(ws.Run("task", "links", "TSK-2")))[0]);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Wrong_links_are_reported_with_a_reason(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var self = await ws.Run("task", "link", "TSK-1", "blocks", "TSK-1");
        Assert.Equal(1, self.Code);
        Assert.Contains("itself", self.Err);

        var phrase = await ws.Run("task", "link", "TSK-1", "frobnicates", "TSK-2");
        Assert.Equal(1, phrase.Code);
        Assert.Contains("No link type or link name 'frobnicates'", phrase.Err);
        Assert.Contains("is blocked by", phrase.Err); // подсказка: что доступно

        var missing = await ws.Run("task", "link", "TSK-1", "blocks", "TSK-99");
        Assert.Equal(1, missing.Code);
        Assert.Contains("No task 'TSK-99'", missing.Err);

        Assert.Empty((await Ok(ws.Run("task", "links", "TSK-1"))).Data.Trim());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Task_get_json_shows_both_sides_like_the_text_and_task_links(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(ws.Run("task", "link", "TSK-1", "blocks", "TSK-2"));
        await Ok(ws.Run("task", "link", "TSK-2", "relates to", "TSK-3"));

        var json = (await Ok(ws.Run("task", "get", "TSK-2", "--json"))).Json;
        var views = json["linkViews"]!.AsArray();

        Assert.Equal(2, json["linkCount"]!.GetValue<int>());
        Assert.Equal(("Blocks", "inward", "is blocked by", "First"),
            (views[0]!["typeName"]!.GetValue<string>(), views[0]!["direction"]!.GetValue<string>(), views[0]!["name"]!.GetValue<string>(), views[0]!["task"]!["title"]!.GetValue<string>()));
        Assert.Equal(("outward", "relates to", "Third"), (views[1]!["direction"]!.GetValue<string>(), views[1]!["name"]!.GetValue<string>(), views[1]!["task"]!["title"]!.GetValue<string>()));
        // Те же представления, что у `task links --json`; сырые исходящие остаются в links (у TSK-2 одна: relates to TSK-3).
        Assert.Equal((await Ok(ws.Run("task", "links", "TSK-2", "--json"))).Json["links"]!.ToJsonString(), views.ToJsonString());
        Assert.Single(json["links"]!.AsArray());

        // Текст: один блок links с теми же двумя строками.
        var text = (await Ok(ws.Run("task", "get", "TSK-2"))).Out;
        Assert.Equal(1, text.Split("links:").Length - 1);
        Assert.Matches(@"is blocked by +TSK-1  Todo  First", text);
        Assert.Matches(@"relates to +TSK-3  Todo  Third", text);

        // Принимающая сторона TSK-1: своих хранимых связей нет, а видна исходящая.
        var first = (await Ok(ws.Run("task", "get", "TSK-1", "--json"))).Json;
        Assert.Equal("outward", Assert.Single(first["linkViews"]!.AsArray())!["direction"]!.GetValue<string>());
        // Симметричная связь у принимающей стороны — одна запись; в списке задач linkViews нет.
        var third = (await Ok(ws.Run("task", "get", "TSK-3", "--json"))).Json;
        Assert.Equal(1, third["linkCount"]!.GetValue<int>());
        Assert.Empty(third["links"]!.AsArray());
        Assert.All((await Ok(ws.Run("task", "list", "--json"))).Json["data"]!.AsArray(), x => Assert.Null(x!["linkViews"]));
    }

    [Fact]
    public async Task Links_in_json_are_the_same_views_the_api_returns()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        await Ok(ws.Run("task", "link", "TSK-1", "blocks", "TSK-2"));

        var links = (await Ok(ws.Run("task", "links", "TSK-2", "--json"))).Json["links"]!.AsArray();

        var link = Assert.Single(links)!;
        Assert.Equal("Blocks", link["typeName"]!.GetValue<string>());
        Assert.Equal("inward", link["direction"]!.GetValue<string>());
        Assert.Equal("is blocked by", link["name"]!.GetValue<string>());
        Assert.Equal("First", link["task"]!["title"]!.GetValue<string>());
        Assert.Equal(1, link["task"]!["seriesNumbers"]![0]!["number"]!.GetValue<int>());
    }

    // ---- REST ----

    private static async Task<JsonNode> Task(ApiHost api, string title) => await api.NewTask(title);

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Rest_link_types_crud_and_lazy_defaults(string storage)
    {
        await using var api = await ApiHost.Start(storage);

        var page = (await api.Get(api.P("/link-types"))).Body!;
        Assert.Equal(6, page["totalCount"]!.GetValue<int>());
        // Просмотр ничего не записал: типов в хранилище нет, а у показанных версия «default».
        Assert.All(page["data"]!.AsArray(), x => Assert.Equal("default", x!["version"]!.GetValue<string>()));
        Assert.Empty(Directory.Exists(Path.Combine(api.Root, ".tasker", "projects", api.ProjectId.ToString(), "link-types"))
            ? Directory.GetFiles(Path.Combine(api.Root, ".tasker", "projects", api.ProjectId.ToString(), "link-types"))
            : []);
        var blocks = page["data"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "Blocks")!;
        Assert.Equal("is blocked by", blocks["inwardName"]!.GetValue<string>());

        var (status, created) = await api.Post(api.P("/link-types"), new { name = "Depends", outwardName = "depends on", inwardName = "is a dependency of" });
        Assert.Equal(HttpStatusCode.Created, status);
        var id = created!["id"]!.GetValue<string>();
        Assert.Equal("depends on", (await api.Get(api.P($"/link-types/{id}"))).Body!["outwardName"]!.GetValue<string>());

        var patched = await api.Patch(api.P($"/link-types/{id}"), new { inwardName = "is needed by", version = created["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, patched.Status);
        Assert.Equal("is needed by", patched.Body!["inwardName"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await api.Patch(api.P($"/link-types/{id}"), new { name = "X", version = created["version"]!.GetValue<string>() })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Patch(api.P($"/link-types/{id}"), new { name = "X" })).Status); // версия обязательна

        Assert.Equal(HttpStatusCode.Conflict, (await api.Post(api.P("/link-types"), new { name = "depends", outwardName = "x" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get(api.P($"/link-types/{Guid.NewGuid()}"))).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await api.Delete(api.P($"/link-types/{id}?version={patched.Body["version"]!.GetValue<string>()}"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get(api.P($"/link-types/{id}"))).Status);

        // Блокировка на время правки есть и у типов связей.
        var lockPath = api.P($"/link-types/{blocks["id"]!.GetValue<string>()}/lock");
        Assert.Equal(HttpStatusCode.OK, (await api.Post(lockPath)).Status);
        Assert.Equal("linkType", (await api.Get(lockPath)).Body!["entity"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Rest_task_links_add_show_and_remove(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var blocks = (await api.Get(api.P("/link-types"))).Body!["data"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "Blocks")!["id"]!.GetValue<string>();
        var a = await Task(api, "Fix login");
        var b = await Task(api, "Release");
        var aId = a["id"]!.GetValue<string>();
        var bId = b["id"]!.GetValue<string>();

        var (status, updated) = await api.Post(api.P($"/tasks/{aId}/links"), new { typeId = blocks, targetId = bId });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(bId, updated!["links"]![0]!["targetId"]!.GetValue<string>());
        Assert.NotEqual(a["version"]!.GetValue<string>(), updated["version"]!.GetValue<string>());
        Assert.Equal((await api.GetTask(b))["version"]!.GetValue<string>(), b["version"]!.GetValue<string>()); // цель не менялась

        // Сама задача показывает связи с обеих сторон: у источника исходящая, у цели входящая (а хранимое links цели пусто).
        var gotA = (await api.Get(api.P($"/tasks/{aId}"))).Body!;
        Assert.Equal(("outward", "blocks", "Release", 1), (gotA["linkViews"]![0]!["direction"]!.GetValue<string>(), gotA["linkViews"]![0]!["name"]!.GetValue<string>(),
            gotA["linkViews"]![0]!["task"]!["title"]!.GetValue<string>(), gotA["linkCount"]!.GetValue<int>()));
        var gotB = (await api.Get(api.P($"/tasks/{bId}"))).Body!;
        Assert.Empty(gotB["links"]!.AsArray());
        Assert.Equal(("inward", "is blocked by", "Fix login", 1), (gotB["linkViews"]![0]!["direction"]!.GetValue<string>(), gotB["linkViews"]![0]!["name"]!.GetValue<string>(),
            gotB["linkViews"]![0]!["task"]!["title"]!.GetValue<string>(), gotB["linkCount"]!.GetValue<int>()));
        Assert.Equal("inward", (await api.Patch(api.P($"/tasks/{bId}"), new { description = "Ship it", version = gotB["version"]!.GetValue<string>() })).Body!["linkViews"]![0]!["direction"]!.GetValue<string>());
        // В списке входящие не считаются.
        Assert.All((await api.Get(api.P("/tasks"))).Body!["data"]!.AsArray(), x => Assert.Null(x!["linkViews"]));

        var fromA = (await api.Get(api.P($"/tasks/{aId}/links"))).Body!.AsArray();
        Assert.Equal(("blocks", "outward", "Release"), (fromA[0]!["name"]!.GetValue<string>(), fromA[0]!["direction"]!.GetValue<string>(), fromA[0]!["task"]!["title"]!.GetValue<string>()));
        var fromB = (await api.Get(api.P($"/tasks/{bId}/links"))).Body!.AsArray();
        Assert.Equal(("is blocked by", "inward", "Fix login"), (fromB[0]!["name"]!.GetValue<string>(), fromB[0]!["direction"]!.GetValue<string>(), fromB[0]!["task"]!["title"]!.GetValue<string>()));

        // Версия указана и устарела — 409 modified; указана верная — проходит.
        var stale = await api.Post(api.P($"/tasks/{aId}/links"), new { typeId = blocks, targetId = bId, version = a["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("modified", stale.Body!["code"]!.GetValue<string>());

        var removed = await api.Delete(api.P($"/tasks/{aId}/links?typeId={blocks}&targetId={bId}"));
        Assert.Equal(HttpStatusCode.OK, removed.Status);
        Assert.Empty(removed.Body!["links"]!.AsArray());
        Assert.Empty((await api.Get(api.P($"/tasks/{bId}/links"))).Body!.AsArray());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Rest_rejects_a_link_that_closes_a_cycle_and_the_flag_of_the_type_is_editable(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var types = (await api.Get(api.P("/link-types"))).Body!["data"]!.AsArray();
        var blocks = types.Single(x => x!["name"]!.GetValue<string>() == "Blocks")!;
        var blocksId = blocks["id"]!.GetValue<string>();
        Assert.False(blocks["allowCycles"]!.GetValue<bool>());
        Assert.All(types.Where(x => x!["name"]!.GetValue<string>() is not ("Blocks" or "Parent/Child")), x => Assert.True(x!["allowCycles"]!.GetValue<bool>()));
        Assert.False(types.Single(x => x!["name"]!.GetValue<string>() == "Parent/Child")!["allowCycles"]!.GetValue<bool>()); // иерархический тип циклов не допускает
        var aId = (await Task(api, "A"))["id"]!.GetValue<string>();
        var bId = (await Task(api, "B"))["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.OK, (await api.Post(api.P($"/tasks/{aId}/links"), new { typeId = blocksId, targetId = bId })).Status);
        var cycle = await api.Post(api.P($"/tasks/{bId}/links"), new { typeId = blocksId, targetId = aId });
        Assert.Equal(HttpStatusCode.BadRequest, cycle.Status);
        Assert.Contains("Cycle: ", cycle.Body!.ToJsonString());
        Assert.Empty((await api.Get(api.P($"/tasks/{bId}/links"))).Body!.AsArray().Where(x => x!["direction"]!.GetValue<string>() == "outward"));

        // Тип разрешили — связь проходит; версия свежая (типы по умолчанию записались вместе с первой связью).
        var fresh = (await api.Get(api.P($"/link-types/{blocksId}"))).Body!;
        var patched = await api.Patch(api.P($"/link-types/{blocksId}"), new { allowCycles = true, version = fresh["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, patched.Status);
        Assert.True(patched.Body!["allowCycles"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await api.Post(api.P($"/tasks/{bId}/links"), new { typeId = blocksId, targetId = aId })).Status);

        var (status, created) = await api.Post(api.P("/link-types"), new { name = "Parent", outwardName = "contains", inwardName = "is part of", allowCycles = false });
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.False(created!["allowCycles"]!.GetValue<bool>());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Rest_rejects_wrong_links_and_unknown_tasks(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var blocks = (await api.Get(api.P("/link-types"))).Body!["data"]![0]!["id"]!.GetValue<string>();
        var a = await Task(api, "A");
        var aId = a["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post(api.P($"/tasks/{aId}/links"), new { typeId = blocks, targetId = aId })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post(api.P($"/tasks/{aId}/links"), new { typeId = blocks, targetId = Guid.NewGuid() })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post(api.P($"/tasks/{aId}/links"), new { typeId = Guid.NewGuid(), targetId = aId })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post(api.P($"/tasks/{Guid.NewGuid()}/links"), new { typeId = blocks, targetId = aId })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get(api.P($"/tasks/{Guid.NewGuid()}/links"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/projects/{Guid.NewGuid()}/tasks/{aId}/links")).Status);

        // Задача из другого проекта целью быть не может.
        var (otherProject, otherType) = await api.CreateProject("Other");
        var foreign = (await api.Post($"/projects/{otherProject}/tasks", new { title = "Foreign", typeId = otherType })).Body!;
        var cross = await api.Post(api.P($"/tasks/{aId}/links"), new { typeId = blocks, targetId = foreign["id"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, cross.Status);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Rest_a_locked_task_gets_no_links_and_a_used_type_cannot_be_deleted(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var blocksBefore = (await api.Get(api.P("/link-types"))).Body!["data"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "Blocks")!;
        var a = await Task(api, "A");
        var b = await Task(api, "B");
        var aId = a["id"]!.GetValue<string>();
        var bId = b["id"]!.GetValue<string>();
        await api.Post(api.P($"/tasks/{aId}/links"), new { typeId = blocksBefore["id"]!.GetValue<string>(), targetId = bId });
        // Связь сохранила типы по умолчанию — версия Blocks теперь настоящая.
        var blocks = (await api.Get(api.P($"/link-types/{blocksBefore["id"]!.GetValue<string>()}"))).Body!;
        Assert.NotEqual("default", blocks["version"]!.GetValue<string>());

        var inUse = await api.Delete(api.P($"/link-types/{blocks["id"]}?version={blocks["version"]!.GetValue<string>()}"));
        Assert.Equal(HttpStatusCode.Conflict, inUse.Status);
        Assert.Equal("in_use", inUse.Body!["code"]!.GetValue<string>());

        // Консоль держит блокировку на задаче — связь через REST не добавить.
        var cli = storage == "sqlite" ? new[] { "--sqlite", Path.Combine(api.Root, "tasker.db") } : ["--workspace", api.Root];
        using var home = new IsolatedHome();
        var locked = await TestWorkspace.Invoke(["lock", "acquire", "task", bId, "--project", api.ProjectId.ToString(), .. cli]);
        Assert.Equal(0, locked.Code);
        var blocked = await api.Post(api.P($"/tasks/{bId}/links"), new { typeId = blocks["id"]!.GetValue<string>(), targetId = aId });
        Assert.Equal(HttpStatusCode.Conflict, blocked.Status);
        Assert.Equal("locked", blocked.Body!["code"]!.GetValue<string>());
    }

    // ---- MCP ----

    [Fact]
    public async Task Mcp_agent_manages_link_types_and_links_tasks_by_phrase()
    {
        using var daemon = new DaemonFixture();
        await daemon.Workspace("a", "Alpha");
        var key = (await daemon.Start()).Workspaces.Single().Key!;
        var projectId = Guid.Parse(JsonNode.Parse(await daemon.CallTool(key, "list_projects"))!["data"]![0]!["id"]!.GetValue<string>());

        async Task<JsonNode> Call(string tool, object args)
        {
            var (isError, text) = await daemon.CallToolResult(key, tool, args);
            Assert.False(isError, $"{tool}: {text}");
            return JsonNode.Parse(text)!;
        }

        async Task<string> Fail(string tool, object args)
        {
            var (isError, text) = await daemon.CallToolResult(key, tool, args);
            Assert.True(isError, $"{tool} unexpectedly succeeded: {text}");
            return text[text.IndexOf('[')..];
        }

        var todo = await Call("create_status", new { projectId, name = "Todo", color = "#112233" });
        var set = await Call("create_status_set", new { projectId, name = "Set", statusIds = new[] { todo["id"]!.GetValue<string>() } });
        var type = await Call("create_task_type", new { projectId, name = "Task", statusSetId = set["id"]!.GetValue<string>() });
        var typeId = type["id"]!.GetValue<string>();
        var a = (await Call("create_task", new { projectId, title = "Fix login", typeId }))["id"]!.GetValue<string>();
        var b = (await Call("create_task", new { projectId, title = "Release", typeId }))["id"]!.GetValue<string>();

        var types = await Call("list_link_types", new { projectId });
        Assert.Equal(6, types["totalCount"]!.GetValue<int>());

        var custom = await Call("create_link_type", new { projectId, name = "Depends", outwardName = "depends on", inwardName = "is a dependency of" });
        var updated = await Call("update_link_type", new { projectId, linkTypeId = custom["id"]!.GetValue<string>(), version = custom["version"]!.GetValue<string>(), inwardName = "is needed by" });
        Assert.Equal("is needed by", updated["inwardName"]!.GetValue<string>());

        // «A is blocked by B» и «A blocks B» — одна сторона и другая; ответ — связи задачи A.
        var first = await Call("link_tasks", new { projectId, taskId = a, link = "is blocked by", otherTaskId = b });
        Assert.Equal(("is blocked by", "Release"), (first["links"]![0]!["name"]!.GetValue<string>(), first["links"]![0]!["task"]!["title"]!.GetValue<string>()));
        // get_task: связи видны и у принимающей стороны — «A is blocked by B» хранится у B, а A видит входящую.
        var gotA = await Call("get_task", new { projectId, taskId = a });
        Assert.Empty(gotA["links"]!.AsArray());
        Assert.Equal(("inward", "is blocked by", "Release", 1), (gotA["linkViews"]![0]!["direction"]!.GetValue<string>(), gotA["linkViews"]![0]!["name"]!.GetValue<string>(),
            gotA["linkViews"]![0]!["task"]!["title"]!.GetValue<string>(), gotA["linkCount"]!.GetValue<int>()));
        var updatedA = await Call("update_task", new { projectId, taskId = a, version = gotA["version"]!.GetValue<string>(), description = "x" });
        Assert.Equal(1, updatedA["linkCount"]!.GetValue<int>());
        Assert.Equal("inward", updatedA["linkViews"]![0]!["direction"]!.GetValue<string>());
        Assert.All((await Call("list_tasks", new { projectId }))["data"]!.AsArray(), x => Assert.Null(x!["linkViews"]));
        var fromB = await Call("get_task_links", new { projectId, taskId = b });
        Assert.Equal(("blocks", "Fix login"), (fromB["links"]![0]!["name"]!.GetValue<string>(), fromB["links"]![0]!["task"]!["title"]!.GetValue<string>()));

        await Call("link_tasks", new { projectId, taskId = a, link = "depends on", otherTaskId = b });
        Assert.Equal(2, (await Call("get_task_links", new { projectId, taskId = a }))["links"]!.AsArray().Count);
        await Call("link_tasks", new { projectId, taskId = a, link = "depends on", otherTaskId = b }); // повтор
        Assert.Equal(2, (await Call("get_task_links", new { projectId, taskId = a }))["links"]!.AsArray().Count);

        Assert.StartsWith("[in_use]", await Fail("delete_link_type", new { projectId, linkTypeId = custom["id"]!.GetValue<string>(), version = updated["version"]!.GetValue<string>() }));

        var after = await Call("unlink_tasks", new { projectId, taskId = b, link = "blocks", otherTaskId = a });
        Assert.Equal("Depends", after["links"]![0]!["typeName"]!.GetValue<string>()); // осталась только «зависит»
        await Call("unlink_tasks", new { projectId, taskId = a, link = "depends on", otherTaskId = b });
        var gone = await daemon.CallToolResult(key, "delete_link_type", new { projectId, linkTypeId = custom["id"]!.GetValue<string>(), version = updated["version"]!.GetValue<string>() });
        Assert.False(gone.IsError, gone.Text);

        // Циклы (TSK-97): «Blocks» циклов не допускает, ошибка называет путь; признак типа правится и создаётся через MCP.
        var blocksType = types["data"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "Blocks")!;
        Assert.False(blocksType["allowCycles"]!.GetValue<bool>());
        await Call("link_tasks", new { projectId, taskId = a, link = "blocks", otherTaskId = b });
        var cycleError = await Fail("link_tasks", new { projectId, taskId = b, link = "blocks", otherTaskId = a });
        Assert.StartsWith("[invalid] Cycle: ", cycleError);
        Assert.Contains("→", cycleError);
        var cyclesOn = await Call("update_link_type", new
        {
            projectId, linkTypeId = blocksType["id"]!.GetValue<string>(),
            version = (await Call("list_link_types", new { projectId }))["data"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "Blocks")!["version"]!.GetValue<string>(),
            allowCycles = true
        });
        Assert.True(cyclesOn["allowCycles"]!.GetValue<bool>());
        await Call("link_tasks", new { projectId, taskId = b, link = "blocks", otherTaskId = a });
        var strict = await Call("create_link_type", new { projectId, name = "Parent", outwardName = "contains", inwardName = "is part of", allowCycles = false });
        Assert.False(strict["allowCycles"]!.GetValue<bool>());
        Assert.True(custom["allowCycles"]!.GetValue<bool>());

        Assert.StartsWith("[invalid]", await Fail("link_tasks", new { projectId, taskId = a, link = "frobnicates", otherTaskId = b }));
        Assert.StartsWith("[invalid]", await Fail("link_tasks", new { projectId, taskId = a, link = "blocks", otherTaskId = a }));
        Assert.StartsWith("[not_found]", await Fail("link_tasks", new { projectId, taskId = Guid.NewGuid(), link = "blocks", otherTaskId = b }));
        Assert.StartsWith("[not_found]", await Fail("get_task_links", new { projectId, taskId = Guid.NewGuid() }));
    }
}
