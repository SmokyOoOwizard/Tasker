using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Autofac;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Storage.Files.Workspaces;
using Tasker.Web;
using Tasker.Web.Workspaces;
using Xunit;

namespace Tasker.Tests;

/// <summary>Десктопный хост в процессе теста: HTTP API одной рабочей области (папка или SQLite) во временной папке.</summary>
internal sealed class ApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly WorkspaceLease _lease;
    private readonly IsolatedHome _home = new();
    private readonly HttpClient _http = new();

    private ApiHost(WebApplication app, WorkspaceLease lease, string root, string baseUrl)
    {
        _app = app;
        _lease = lease;
        Root = root;
        BaseUrl = baseUrl;
    }

    public string Root { get; }

    /// <summary>Аренда области, открытой хостом: из неё демон берёт сервисы для отчёта о сериях.</summary>
    public WorkspaceLease Lease => _lease;

    /// <summary>Адрес API области без завершающего слеша: <c>http://127.0.0.1:port/w/key/api</c>.</summary>
    public string BaseUrl { get; }

    public Guid ProjectId { get; private set; }
    public Guid TypeId { get; private set; }

    public static async Task<ApiHost> Start(string storage)
    {
        var root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var builder = TaskerWebApp.CreateBuilder<Tasker.Daemon.Host.DaemonModule>([], TaskerMode.Local);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapTasker();
        await app.StartAsync();

        var location = storage == "sqlite" ? WorkspaceLocation.Sqlite(Path.Combine(root, "tasker.db")) : WorkspaceLocation.Files(root);
        var lease = await app.Services.GetRequiredService<WorkspaceRegistry>().Open(location);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var host = new ApiHost(app, lease, root, $"{address}{lease.BasePath}/api");
        await host.CreateProject();
        return host;
    }

    /// <summary>Проект со статусом, набором статусов и типом задачи.</summary>
    public async Task<(Guid ProjectId, Guid TypeId)> CreateProject(string name = "P")
    {
        var project = (await Send(HttpMethod.Post, "/projects", new { name })).Body!;
        var projectId = Guid.Parse(project["id"]!.GetValue<string>());
        var status = (await Send(HttpMethod.Post, $"/projects/{projectId}/statuses", new { name = "Todo", color = "#112233" })).Body!;
        var set = (await Send(HttpMethod.Post, $"/projects/{projectId}/status-sets", new { name = "Set", statusIds = new[] { status["id"]!.GetValue<string>() } })).Body!;
        var type = (await Send(HttpMethod.Post, $"/projects/{projectId}/task-types", new { name = "Task", statusSetId = set["id"]!.GetValue<string>() })).Body!;
        var typeId = Guid.Parse(type["id"]!.GetValue<string>());

        if (ProjectId == Guid.Empty)
        {
            ProjectId = projectId;
            TypeId = typeId;
        }
        return (projectId, typeId);
    }

    public string P(string path = "") => $"/projects/{ProjectId}{path}";

    public async Task<(HttpStatusCode Status, JsonNode? Body)> Send(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        if (body != null)
            request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    public Task<(HttpStatusCode Status, JsonNode? Body)> Get(string path) => Send(HttpMethod.Get, path);
    public Task<(HttpStatusCode Status, JsonNode? Body)> Post(string path, object? body = null) => Send(HttpMethod.Post, path, body ?? new { });
    public Task<(HttpStatusCode Status, JsonNode? Body)> Patch(string path, object body) => Send(HttpMethod.Patch, path, body);
    public Task<(HttpStatusCode Status, JsonNode? Body)> Delete(string path) => Send(HttpMethod.Delete, path);

    public async Task<JsonNode> NewSeries(string prefix, string? name = null, Guid? project = null)
    {
        var (status, body) = await Post($"/projects/{project ?? ProjectId}/series", new { name = name ?? prefix + " series", prefix });
        Assert.Equal(HttpStatusCode.Created, status);
        return body!;
    }

    public async Task<JsonNode> NewTask(string title, params JsonNode[] series)
    {
        var (status, body) = await Post(P("/tasks"), new { title, typeId = TypeId, seriesIds = series.Select(x => x["id"]!.GetValue<string>()).ToArray() });
        Assert.Equal(HttpStatusCode.Created, status);
        return body!;
    }

    public async Task<JsonNode> GetTask(JsonNode task) => (await Get(P($"/tasks/{task["id"]}"))).Body!;

    /// <summary>Файл задачи: называется по заголовку, поэтому ищется по id.</summary>
    public string TaskFile(JsonNode task) =>
        new Tasker.Storage.Files.TaskerDirectory(Root).Project(ProjectId).FindTaskFile(Guid.Parse(task["id"]!.GetValue<string>()))
        ?? throw new FileNotFoundException("No file of the task " + task["id"]);

    /// <summary>Пары «префикс: номер» задачи в порядке хранения.</summary>
    public static string[] Numbers(JsonNode task, params JsonNode[] series) =>
        task["seriesNumbers"]!.AsArray()
            .Select(n => $"{series.First(s => s["id"]!.GetValue<string>() == n!["seriesId"]!.GetValue<string>())["prefix"]!.GetValue<string>()}:{n!["number"]}")
            .ToArray();

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _lease.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _home.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>HTTP API серий и номеров задач: десктопный хост, папка и SQLite.</summary>
public class SeriesApiTests
{
    public static IEnumerable<object[]> Storages() => TestWorkspace.Storages();

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();
    private static string Version(JsonNode node) => node["version"]!.GetValue<string>();

    [Theory, MemberData(nameof(Storages))]
    public async Task Create_get_list_in_prefix_order_with_paging(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        await api.NewSeries("TSK");
        await api.NewSeries("tsk");
        await api.NewSeries("A1");

        var list = (await api.Get(api.P("/series"))).Body!;
        var prefixes = list["data"]!.AsArray().Select(x => x!["prefix"]!.GetValue<string>()).ToArray();
        Assert.Equal(3, list["totalCount"]!.GetValue<int>());
        Assert.Equal(["A1", "TSK", "tsk"], prefixes);
        Assert.Equal(0, list["offset"]!.GetValue<int>());

        var page = (await api.Get(api.P("/series?offset=1&limit=1"))).Body!;
        Assert.Equal(3, page["totalCount"]!.GetValue<int>());
        Assert.Equal(["TSK"], page["data"]!.AsArray().Select(x => x!["prefix"]!.GetValue<string>()).ToArray());

        var first = list["data"]![0]!;
        var (status, one) = await api.Get(api.P($"/series/{Id(first)}"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("A1", one!["prefix"]!.GetValue<string>());
        Assert.Equal("A1 series", one["name"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Duplicate_prefix_is_409_but_a_different_case_is_a_different_series(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        await api.NewSeries("TSK");
        await api.NewSeries("tsk");

        var (status, body) = await api.Post(api.P("/series"), new { name = "Again", prefix = "TSK" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("in_use", body!["code"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Invalid_prefix_or_name_is_400(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var ok = await api.NewSeries("OK");

        foreach (var prefix in new[] { "", "TSK-1", "with space", "Ünï", new string('A', 21) })
        {
            var (status, body) = await api.Post(api.P("/series"), new { name = "X", prefix });
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.NotNull(body!["error"]);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post(api.P("/series"), new { name = " ", prefix = "NAME" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Patch(api.P($"/series/{Id(ok)}"), new { prefix = "bad-one", version = Version(ok) })).Status);
        Assert.Equal(new string('B', 20), (await api.NewSeries(new string('B', 20)))["prefix"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Patch_changes_name_and_renames_the_prefix(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var series = await api.NewSeries("TSK", "Tasks");
        var other = await api.NewSeries("BUG");
        var task = await api.NewTask("t", series);

        var (status, renamed) = await api.Patch(api.P($"/series/{Id(series)}"), new { prefix = "NEW", version = Version(series) });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("NEW", renamed!["prefix"]!.GetValue<string>());
        Assert.Equal("Tasks", renamed["name"]!.GetValue<string>());
        Assert.NotEqual(Version(series), Version(renamed));

        // Номер остался, ссылка теперь по новому префиксу.
        Assert.Single((await api.GetTask(task))["seriesNumbers"]!.AsArray());
        Assert.Single((await api.Get(api.P("/tasks/resolve?ref=NEW-1"))).Body!.AsArray());
        Assert.Empty((await api.Get(api.P("/tasks/resolve?ref=TSK-1"))).Body!.AsArray());

        var (_, named) = await api.Patch(api.P($"/series/{Id(series)}"), new { name = "Renamed", version = Version(renamed) });
        Assert.Equal("Renamed", named!["name"]!.GetValue<string>());
        Assert.Equal("NEW", named["prefix"]!.GetValue<string>());

        // Занятый префикс — 409, версия и содержимое не тронуты.
        var (conflict, body) = await api.Patch(api.P($"/series/{Id(series)}"), new { prefix = "BUG", version = Version(named) });
        Assert.Equal(HttpStatusCode.Conflict, conflict);
        Assert.Equal("in_use", body!["code"]!.GetValue<string>());
        Assert.Equal("NEW", (await api.Get(api.P($"/series/{Id(series)}"))).Body!["prefix"]!.GetValue<string>());
        Assert.NotNull(other);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Stale_version_is_409_modified_for_patch_and_delete(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var series = await api.NewSeries("TSK");
        await api.Patch(api.P($"/series/{Id(series)}"), new { name = "Changed", version = Version(series) });

        var patch = await api.Patch(api.P($"/series/{Id(series)}"), new { name = "Again", version = Version(series) });
        var delete = await api.Delete(api.P($"/series/{Id(series)}?version={Uri.EscapeDataString(Version(series))}"));

        Assert.Equal(HttpStatusCode.Conflict, patch.Status);
        Assert.Equal("modified", patch.Body!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, delete.Status);
        Assert.Equal("modified", delete.Body!["code"]!.GetValue<string>());
        Assert.Equal("Changed", (await api.Get(api.P($"/series/{Id(series)}"))).Body!["name"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Unknown_series_is_404_and_health_is_not_taken_for_an_id(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var unknown = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await api.Get(api.P($"/series/{unknown}"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Patch(api.P($"/series/{unknown}"), new { name = "x", version = "v" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Delete(api.P($"/series/{unknown}?version=v"))).Status);

        var (status, health) = await api.Get(api.P("/series/health"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(health!["numberConflicts"]!.AsArray());
        Assert.Empty(health["prefixConflicts"]!.AsArray());
        Assert.Equal(0, health["tasksWithInvalidSeries"]!.GetValue<int>());
        Assert.Equal(0, health["unreadableSeriesFiles"]!.GetValue<int>());
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Tasks_get_numbers_one_two_three_and_several_series_on_one_task(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var tsk = await api.NewSeries("TSK");
        var bug = await api.NewSeries("BUG");

        var first = await api.NewTask("first", tsk);
        var second = await api.NewTask("second", tsk);
        var third = await api.NewTask("third", tsk, bug);
        var plain = await api.NewTask("plain");

        Assert.Equal(["TSK:1"], ApiHost.Numbers(first, tsk, bug));
        Assert.Equal(["TSK:2"], ApiHost.Numbers(second, tsk, bug));
        Assert.Equal(["TSK:3", "BUG:1"], ApiHost.Numbers(third, tsk, bug));
        Assert.Empty(plain["seriesNumbers"]!.AsArray());

        // Форма JSON: camelCase, как у остальных полей.
        var pair = third["seriesNumbers"]![0]!.AsObject();
        Assert.Equal(["seriesId", "number"], pair.Select(x => x.Key).ToArray());

        // Добавление в серию даёт следующий номер, повторное — без изменений.
        var added = await api.Post(api.P($"/tasks/{Id(plain)}/series"), new { seriesId = Id(bug), version = Version(plain) });
        Assert.Equal(HttpStatusCode.OK, added.Status);
        Assert.Equal(["BUG:2"], ApiHost.Numbers(added.Body!, tsk, bug));
        var again = await api.Post(api.P($"/tasks/{Id(plain)}/series"), new { seriesId = Id(bug), version = Version(added.Body!) });
        Assert.Equal(["BUG:2"], ApiHost.Numbers(again.Body!, tsk, bug));

        // Дубликат в seriesIds при создании и неизвестная серия — 400.
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post(api.P("/tasks"), new { title = "x", typeId = api.TypeId, seriesIds = new[] { Guid.NewGuid() } })).Status);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Add_to_series_returns_404_for_an_unknown_task_or_series_and_409_for_a_stale_version(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var tsk = await api.NewSeries("TSK");
        var task = await api.NewTask("t");

        Assert.Equal(HttpStatusCode.NotFound, (await api.Post(api.P($"/tasks/{Guid.NewGuid()}/series"), new { seriesId = Id(tsk), version = Version(task) })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post(api.P($"/tasks/{Id(task)}/series"), new { seriesId = Guid.NewGuid(), version = Version(task) })).Status);

        var stale = await api.Post(api.P($"/tasks/{Id(task)}/series"), new { seriesId = Id(tsk), version = "stale" });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("modified", stale.Body!["code"]!.GetValue<string>());
        Assert.Empty((await api.GetTask(task))["seriesNumbers"]!.AsArray());
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Remove_from_series_frees_the_number(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var tsk = await api.NewSeries("TSK");
        var bug = await api.NewSeries("BUG");
        var one = await api.NewTask("one", tsk);
        var two = await api.NewTask("two", tsk, bug);

        var removed = await api.Delete(api.P($"/tasks/{Id(two)}/series/{Id(tsk)}?version={Uri.EscapeDataString(Version(two))}"));

        Assert.Equal(HttpStatusCode.OK, removed.Status);
        Assert.Equal(["BUG:1"], ApiHost.Numbers(removed.Body!, tsk, bug));
        // Освободившийся максимальный номер выдаётся заново.
        Assert.Equal(["TSK:2"], ApiHost.Numbers(await api.NewTask("three", tsk), tsk, bug));

        // Не в серии — задача как есть; версия задачи проверяется всё равно.
        var noop = await api.Delete(api.P($"/tasks/{Id(two)}/series/{Id(tsk)}?version={Uri.EscapeDataString(Version(removed.Body!))}"));
        Assert.Equal(HttpStatusCode.OK, noop.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await api.Delete(api.P($"/tasks/{Id(one)}/series/{Id(tsk)}?version=stale"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Delete(api.P($"/tasks/{Guid.NewGuid()}/series/{Id(tsk)}?version=v"))).Status);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Renumber_to_a_free_number_to_a_taken_number_and_to_the_next_free(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var tsk = await api.NewSeries("TSK");
        var bug = await api.NewSeries("BUG");
        var one = await api.NewTask("one", tsk);
        var two = await api.NewTask("two", tsk);

        var taken = await api.Post(api.P($"/tasks/{Id(two)}/series/{Id(tsk)}/renumber"), new { to = 1, version = Version(two) });
        Assert.Equal(HttpStatusCode.Conflict, taken.Status);
        Assert.Contains("taken", taken.Body!["error"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await api.Post(api.P($"/tasks/{Id(two)}/series/{Id(tsk)}/renumber"), new { to = 0, version = Version(two) })).Status);

        var free = await api.Post(api.P($"/tasks/{Id(two)}/series/{Id(tsk)}/renumber"), new { to = 10, version = Version(two) });
        Assert.Equal(HttpStatusCode.OK, free.Status);
        Assert.Equal(["TSK:10"], ApiHost.Numbers(free.Body!, tsk, bug));

        // Без to — следующий свободный (максимум + 1).
        var next = await api.Post(api.P($"/tasks/{Id(one)}/series/{Id(tsk)}/renumber"), new { version = Version(one) });
        Assert.Equal(["TSK:11"], ApiHost.Numbers(next.Body!, tsk, bug));

        Assert.Equal(HttpStatusCode.Conflict, (await api.Post(api.P($"/tasks/{Id(one)}/series/{Id(tsk)}/renumber"), new { to = 20, version = "stale" })).Status);
        // Задача не в серии, неизвестная серия, неизвестная задача — 404.
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post(api.P($"/tasks/{Id(one)}/series/{Id(bug)}/renumber"), new { version = Version(next.Body!) })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post(api.P($"/tasks/{Id(one)}/series/{Guid.NewGuid()}/renumber"), new { version = Version(next.Body!) })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post(api.P($"/tasks/{Guid.NewGuid()}/series/{Id(tsk)}/renumber"), new { version = "v" })).Status);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Deleting_a_series_with_tasks_removes_their_numbers(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var tsk = await api.NewSeries("TSK");
        var bug = await api.NewSeries("BUG");
        var a = await api.NewTask("a", tsk);
        var b = await api.NewTask("b", tsk, bug);
        var c = await api.NewTask("c", bug);

        var deleted = await api.Delete(api.P($"/series/{Id(tsk)}?version={Uri.EscapeDataString(Version(tsk))}"));

        Assert.Equal(HttpStatusCode.NoContent, deleted.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get(api.P($"/series/{Id(tsk)}"))).Status);
        Assert.Empty((await api.GetTask(a))["seriesNumbers"]!.AsArray());
        Assert.Equal(["BUG:1"], ApiHost.Numbers(await api.GetTask(b), tsk, bug));
        Assert.Equal(["BUG:2"], ApiHost.Numbers(await api.GetTask(c), tsk, bug));
        // Задачи остались.
        Assert.Equal(3, (await api.Get(api.P("/tasks"))).Body!["totalCount"]!.GetValue<int>());
        Assert.Equal(0, (await api.Get(api.P("/series/health"))).Body!["tasksWithInvalidSeries"]!.GetValue<int>());
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Tasks_list_filters_by_series(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var tsk = await api.NewSeries("TSK");
        var bug = await api.NewSeries("BUG");
        await api.NewTask("in tsk", tsk);
        await api.NewTask("in both", tsk, bug);
        await api.NewTask("in bug", bug);
        await api.NewTask("plain");

        string[] Titles(JsonNode body) => body["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).Order().ToArray();

        var byTsk = (await api.Get(api.P($"/tasks?seriesId={Id(tsk)}"))).Body!;
        Assert.Equal(["in both", "in tsk"], Titles(byTsk));
        Assert.Equal(2, byTsk["totalCount"]!.GetValue<int>());
        Assert.Equal(["in both", "in bug"], Titles((await api.Get(api.P($"/tasks?seriesId={Id(bug)}"))).Body!));
        Assert.Empty((await api.Get(api.P($"/tasks?seriesId={Guid.NewGuid()}"))).Body!["data"]!.AsArray());
        Assert.Equal(4, (await api.Get(api.P("/tasks"))).Body!["totalCount"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Get(api.P("/tasks?seriesId=nope"))).Status);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Resolve_finds_tasks_by_id_and_by_reference(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var tsk = await api.NewSeries("TSK");
        await api.NewSeries("tsk");
        var one = await api.NewTask("one", tsk);
        var two = await api.NewTask("two", tsk);

        var byRef = (await api.Get(api.P("/tasks/resolve?ref=TSK-2"))).Body!.AsArray();
        Assert.Equal([Id(two)], byRef.Select(x => Id(x!)).ToArray());

        var byId = await api.Get(api.P($"/tasks/resolve?ref={Id(one)}"));
        Assert.Equal(HttpStatusCode.OK, byId.Status);
        Assert.Equal([Id(one)], byId.Body!.AsArray().Select(x => Id(x!)).ToArray());

        // Регистр префикса важен: серия tsk есть, но в ней нет задач; серии Tsk нет вовсе.
        Assert.Empty((await api.Get(api.P("/tasks/resolve?ref=tsk-1"))).Body!.AsArray());
        Assert.Empty((await api.Get(api.P("/tasks/resolve?ref=Tsk-1"))).Body!.AsArray());
        Assert.Empty((await api.Get(api.P("/tasks/resolve?ref=TSK-99"))).Body!.AsArray());
        Assert.Empty((await api.Get(api.P($"/tasks/resolve?ref={Guid.NewGuid()}"))).Body!.AsArray());

        foreach (var bad in new[] { "hello", "TSK-", "TSK-0", "TSK-x", "-5", "TSK-1-2" })
            Assert.Equal(HttpStatusCode.BadRequest, (await api.Get(api.P($"/tasks/resolve?ref={bad}"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Get(api.P("/tasks/resolve"))).Status);

        // /tasks/{id} по-прежнему работает рядом с /tasks/resolve.
        Assert.Equal(HttpStatusCode.OK, (await api.Get(api.P($"/tasks/{Id(one)}"))).Status);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Lease_resolves_the_services_of_the_open_workspace_and_the_daemon_report_uses_them(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var tsk = await api.NewSeries("TSK");
        await api.NewTask("one", tsk);

        // Тот же контейнер, что обслуживает API: хранилище и индекс не создаются заново.
        Assert.Same(api.Lease.Resolve<Tasker.Core.TaskSeries.ISeriesStorage>(), api.Lease.Resolve<Tasker.Core.TaskSeries.ISeriesStorage>());
        Assert.Equal(1, (await api.Lease.Resolve<Tasker.Core.TaskSeries.ISeriesStorage>().GetAll(api.ProjectId)).Length);

        Assert.Empty(await Tasker.Daemon.Host.WorkspaceSync.ComputeSeries(api.Lease, default));
    }

    [Fact]
    public async Task The_daemon_series_report_sees_a_duplicated_number_through_the_lease()
    {
        await using var api = await ApiHost.Start("files");
        var tsk = await api.NewSeries("TSK");
        var original = await api.NewTask("original", tsk);
        await DuplicateTaskFile(api, original, x => x);

        var report = await Eventually<Tasker.Daemon.ProjectSeriesHealth[]>(async () =>
        {
            var found = await Tasker.Daemon.Host.WorkspaceSync.ComputeSeries(api.Lease, default);
            return found.Length == 1 ? found : null;
        });
        Assert.Null(report[0].Error);
        Assert.Equal("TSK-1", Assert.Single(report[0].NumberConflicts).Reference);
    }

    [Fact]
    public async Task Resolve_on_a_released_lease_throws()
    {
        var api = await ApiHost.Start("files");
        var lease = api.Lease;
        await lease.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => lease.Resolve<Tasker.Core.TaskSeries.ISeriesStorage>());
        await api.DisposeAsync();
    }

    [Fact]
    public async Task Resolve_returns_all_tasks_when_a_number_is_duplicated_and_health_reports_it()
    {
        await using var api = await ApiHost.Start("files");
        var tsk = await api.NewSeries("TSK");
        var original = await api.NewTask("original", tsk);
        var clone = await DuplicateTaskFile(api, original, x => x);

        // Индекс папки подхватит файл, как после слияния веток git.
        var found = await Eventually<JsonArray>(async () =>
        {
            var array = (await api.Get(api.P("/tasks/resolve?ref=TSK-1"))).Body!.AsArray();
            return array.Count == 2 ? array : null;
        });
        Assert.Equal(new[] { Id(original), clone }.Order().ToArray(), found.Select(x => Id(x!)).Order().ToArray());

        var health = (await api.Get(api.P("/series/health"))).Body!;
        var conflict = Assert.Single(health["numberConflicts"]!.AsArray())!;
        Assert.Equal(1, conflict["number"]!.GetValue<int>());
        Assert.Equal(Id(tsk), conflict["seriesId"]!.GetValue<string>());
        Assert.Equal(2, conflict["taskIds"]!.AsArray().Count);
        Assert.Equal(0, health["tasksWithInvalidSeries"]!.GetValue<int>());
    }

    [Fact]
    public async Task Health_reports_invalid_references_and_duplicated_prefixes()
    {
        await using var api = await ApiHost.Start("files");
        var tsk = await api.NewSeries("TSK");
        var task = await api.NewTask("t", tsk);
        var missing = Guid.NewGuid().ToString("D");
        await DuplicateTaskFile(api, task, text => text.Replace(Id(tsk), missing));

        // Вторая серия с тем же префиксом — как после слияния веток: файл серии копируется под новым id.
        var seriesFile = FileFinder.In(Path.Combine(api.Root, ".tasker", "projects", api.ProjectId.ToString("D"), "series"), Guid.Parse(Id(tsk)));
        var twin = Guid.NewGuid().ToString("D");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(seriesFile)!, twin + ".yaml"), File.ReadAllText(seriesFile).Replace(Id(tsk), twin));

        var health = await Eventually<JsonNode>(async () =>
        {
            var body = (await api.Get(api.P("/series/health"))).Body!;
            return body["tasksWithInvalidSeries"]!.GetValue<int>() == 1 && body["prefixConflicts"]!.AsArray().Count == 1 ? body : null;
        });

        var prefix = health["prefixConflicts"]![0]!;
        Assert.Equal("TSK", prefix["prefix"]!.GetValue<string>());
        Assert.Equal(2, prefix["seriesIds"]!.AsArray().Count);
        Assert.Empty(health["numberConflicts"]!.AsArray());
        Assert.Equal(0, health["unreadableSeriesFiles"]!.GetValue<int>());
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Series_of_another_project_are_not_visible_or_usable(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var (otherProject, otherType) = await api.CreateProject("Other");
        var foreign = await api.NewSeries("OTH", project: otherProject);
        var foreignTask = (await api.Post($"/projects/{otherProject}/tasks", new { title = "f", typeId = otherType, seriesIds = new[] { Id(foreign) } })).Body!;
        var mine = await api.NewSeries("MINE");
        var task = await api.NewTask("mine");

        Assert.Equal(HttpStatusCode.NotFound, (await api.Get(api.P($"/series/{Id(foreign)}"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Patch(api.P($"/series/{Id(foreign)}"), new { name = "x", version = Version(foreign) })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Delete(api.P($"/series/{Id(foreign)}?version={Uri.EscapeDataString(Version(foreign))}"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post(api.P($"/tasks/{Id(task)}/series"), new { seriesId = Id(foreign), version = Version(task) })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post(api.P($"/tasks/{Id(foreignTask)}/series"), new { seriesId = Id(mine), version = Version(foreignTask) })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Delete(api.P($"/tasks/{Id(foreignTask)}/series/{Id(foreign)}?version={Version(foreignTask)}"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post(api.P($"/tasks/{Id(foreignTask)}/series/{Id(foreign)}/renumber"), new { version = Version(foreignTask) })).Status);
        Assert.Empty((await api.Get(api.P("/tasks/resolve?ref=OTH-1"))).Body!.AsArray());
        Assert.Empty((await api.Get(api.P($"/tasks/resolve?ref={Id(foreignTask)}"))).Body!.AsArray());
        Assert.Equal(["MINE"], (await api.Get(api.P("/series"))).Body!["data"]!.AsArray().Select(x => x!["prefix"]!.GetValue<string>()).ToArray());

        // Чужие серии и задачи остались нетронутыми; несуществующий проект — 404 на всех маршрутах.
        Assert.Single((await api.Get($"/projects/{otherProject}/tasks/{Id(foreignTask)}")).Body!["seriesNumbers"]!.AsArray());
        var nobody = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/projects/{nobody}/series")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/projects/{nobody}/series/health")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post($"/projects/{nobody}/series", new { name = "x", prefix = "X" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/projects/{nobody}/tasks/resolve?ref=X-1")).Status);
    }

    [Fact]
    public async Task Series_files_produce_series_events_on_the_events_stream()
    {
        await using var api = await ApiHost.Start("files");
        var events = await Subscribe(api);

        var series = await api.NewSeries("TSK");

        var change = await events.Next(x => x["entity"]!.GetValue<string>() == "series");
        Assert.Equal(api.ProjectId.ToString("D"), change["projectId"]!.GetValue<string>());
        Assert.Equal(Id(series), change["id"]!.GetValue<string>());

        // Правка и удаление серии — тоже события серии.
        await api.Patch(api.P($"/series/{Id(series)}"), new { name = "Renamed", version = Version(series) });
        Assert.Equal(Id(series), (await events.Next(x => x["entity"]!.GetValue<string>() == "series"))["id"]!.GetValue<string>());
        events.Dispose();
    }

    [Fact]
    public async Task Sqlite_workspace_publishes_an_unknown_event_after_a_series_change()
    {
        await using var api = await ApiHost.Start("sqlite");
        var events = await Subscribe(api);

        await api.NewSeries("TSK");

        Assert.NotNull(await events.Next(x => x["entity"]!.GetValue<string>() == "unknown"));
        events.Dispose();
    }

    // ---- вспомогательное ----

    /// <summary>Копирует файл задачи под новым id (как после слияния веток, где обе задачи получили один номер); text — правка содержимого.</summary>
    private static Task<string> DuplicateTaskFile(ApiHost api, JsonNode task, Func<string, string> edit)
    {
        var id = Guid.NewGuid().ToString("D");
        var text = edit(File.ReadAllText(api.TaskFile(task))).Replace(task["id"]!.GetValue<string>(), id);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(api.TaskFile(task))!, id + ".yaml"), text);
        return Task.FromResult(id);
    }

    private static async Task<T> Eventually<T>(Func<Task<T?>> attempt) where T : class
    {
        for (var i = 0; i < 100; i++)
        {
            if (await attempt() is { } value)
                return value;
            await Task.Delay(150);
        }
        throw new Xunit.Sdk.XunitException("the condition did not become true in 15 seconds");
    }

    private sealed class EventStream(HttpClient http, HttpResponseMessage response, StreamReader reader) : IDisposable
    {
        public async Task<JsonNode> Next(Func<JsonNode, bool> match)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var line = await reader.ReadLineAsync(timeout.Token) ?? throw new Xunit.Sdk.XunitException("the event stream ended");
                if (line.StartsWith("data:") && JsonNode.Parse(line["data:".Length..])! is { } change && match(change))
                    return change;
            }
        }

        public void Dispose()
        {
            reader.Dispose();
            response.Dispose();
            http.Dispose();
        }
    }

    private static async Task<EventStream> Subscribe(ApiHost api)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var response = await http.GetAsync(api.BaseUrl + "/events", HttpCompletionOption.ResponseHeadersRead);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.StartsWith(": connected", await reader.ReadLineAsync());
        return new EventStream(http, response, reader);
    }
}
