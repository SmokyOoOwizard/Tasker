using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Блокировка на время правки снаружи: REST десктопа, команды консоли и MCP. Держатели разные
/// (интерфейс — <c>local</c>, консоль — <c>cli</c>, агент — свой пользователь), поэтому блокировка одного останавливает другого.
/// </summary>
public class EditLockInterfaceTests
{
    // Настройки (имя для whoami) — в каталоге данных Tasker. Подмена TASKER_HOME живёт в AsyncLocal и не выходит из async-метода,
    // поэтому изолировать каталог нужно в теле самого теста, до первого await; ApiHost.Start для этого не годится.
    private static IsolatedHome Home() => new();

    private static string[] Location(ApiHost api, string storage) =>
        storage == "sqlite" ? ["--sqlite", Path.Combine(api.Root, "tasker.db")] : ["--workspace", api.Root];

    private static Task<CliResult> Cli(ApiHost api, string storage, params string[] args) =>
        TestWorkspace.Invoke([.. args, "--project", api.ProjectId.ToString(), .. Location(api, storage)]);

    private static async Task<JsonNode> NewTask(ApiHost api, string title = "Fix login") =>
        await api.NewTask(title);

    // ---- REST ----

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Rest_lock_can_be_taken_shown_renewed_and_released(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var task = await NewTask(api);
        var path = api.P($"/tasks/{task["id"]}/lock");

        Assert.Equal(HttpStatusCode.NotFound, (await api.Get(path)).Status);

        var (status, taken) = await api.Post(path);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("task", taken!["entity"]!.GetValue<string>());
        Assert.Equal(task["id"]!.GetValue<string>(), taken["id"]!.GetValue<string>());
        Assert.True(taken["mine"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(taken["holder"]!.GetValue<string>()));

        var renewed = (await api.Post(path)).Body!;
        Assert.Equal(taken["acquiredAt"]!.GetValue<DateTimeOffset>(), renewed["acquiredAt"]!.GetValue<DateTimeOffset>());
        Assert.True(renewed["expiresAt"]!.GetValue<DateTimeOffset>() >= taken["expiresAt"]!.GetValue<DateTimeOffset>());

        Assert.Equal(taken["holder"]!.GetValue<string>(), (await api.Get(path)).Body!["holder"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NoContent, (await api.Delete(path)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get(path)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Delete(path)).Status);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Every_project_entity_kind_can_be_locked_and_an_unknown_one_is_404(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var series = await api.NewSeries("TSK");
        string Child(string folder) => (api.Get(api.P($"/{folder}")).Result.Body!["data"]![0]!["id"]!.GetValue<string>());

        var paths = new Dictionary<string, string>
        {
            ["project"] = api.P("/lock"),
            ["task"] = api.P($"/tasks/{(await NewTask(api))["id"]}/lock"),
            ["taskType"] = api.P($"/task-types/{Child("task-types")}/lock"),
            ["status"] = api.P($"/statuses/{Child("statuses")}/lock"),
            ["statusSet"] = api.P($"/status-sets/{Child("status-sets")}/lock"),
            ["series"] = api.P($"/series/{series["id"]}/lock"),
        };

        foreach (var (entity, path) in paths)
        {
            var taken = await api.Post(path);
            Assert.True(taken.Status == HttpStatusCode.OK, $"{entity}: {taken.Status}");
            Assert.Equal(entity, taken.Body!["entity"]!.GetValue<string>());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await api.Post(api.P($"/tasks/{Guid.NewGuid()}/lock"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post($"/projects/{Guid.NewGuid()}/tasks/{Guid.NewGuid()}/lock")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post($"/projects/{Guid.NewGuid()}/lock")).Status);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Project_lock_list_shows_active_locks_of_this_project_only(string storage)
    {
        using var home = Home();
        await using var api = await ApiHost.Start(storage);
        var (otherProject, _) = await api.CreateProject("Other");
        var task = await NewTask(api);
        var series = await api.NewSeries("TSK");
        string FirstId(string folder) => api.Get(api.P($"/{folder}")).Result.Body!["data"]![0]!["id"]!.GetValue<string>();

        Assert.Empty((await api.Get(api.P("/locks"))).Body!["data"]!.AsArray());

        await api.Post(api.P($"/tasks/{task["id"]}/lock"));
        await api.Post(api.P("/lock"));
        await api.Post(api.P($"/statuses/{FirstId("statuses")}/lock"));
        // Консоль держит серию: в списке она чужая.
        Assert.Equal(0, (await Cli(api, storage, "lock", "acquire", "series", "TSK")).Code);
        // Блокировка в другом проекте в этот список не попадает.
        var foreignTask = (await api.Post($"/projects/{otherProject}/tasks", new { title = "Foreign", typeId = (await api.Get($"/projects/{otherProject}/task-types")).Body!["data"]![0]!["id"]!.GetValue<string>() })).Body!;
        await api.Post($"/projects/{otherProject}/tasks/{foreignTask["id"]}/lock");

        var page = (await api.Get(api.P("/locks"))).Body!;
        var locks = page["data"]!.AsArray();
        Assert.Equal(4, page["totalCount"]!.GetValue<int>());
        Assert.Equal(["task", "project", "status", "series"], locks.Select(x => x!["entity"]!.GetValue<string>()).ToArray());
        Assert.Equal([true, true, true, false], locks.Select(x => x!["mine"]!.GetValue<bool>()).ToArray());
        Assert.EndsWith("(console)", locks[3]!["holder"]!.GetValue<string>());
        Assert.Equal(api.ProjectId.ToString(), locks[1]!["id"]!.GetValue<string>());

        await api.Delete(api.P($"/tasks/{task["id"]}/lock"));
        Assert.Equal(3, (await api.Get(api.P("/locks"))).Body!["totalCount"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/projects/{Guid.NewGuid()}/locks")).Status);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Console_lock_stops_rest_changes_and_names_the_holder(string storage)
    {
        using var home = Home();
        await using var api = await ApiHost.Start(storage);
        var task = await NewTask(api);
        var id = task["id"]!.GetValue<string>();
        Assert.Equal(0, (await TestWorkspace.Invoke(["whoami", "Ivan"])).Code);

        var locked = await Cli(api, storage, "lock", "acquire", "task", id);
        Assert.True(locked.Code == 0, locked.Err);

        var patch = await api.Patch(api.P($"/tasks/{id}"), new { title = "Other", version = task["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.Conflict, patch.Status);
        Assert.Equal("locked", patch.Body!["code"]!.GetValue<string>());
        Assert.Equal("Ivan (console)", patch.Body["heldBy"]!["name"]!.GetValue<string>());
        Assert.Contains("Ivan (console)", patch.Body["error"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.Conflict, (await api.Delete(api.P($"/tasks/{id}?version={task["version"]!.GetValue<string>()}"))).Status);

        // Взять чужую блокировку нельзя, увидеть — можно, и она не «моя».
        var take = await api.Post(api.P($"/tasks/{id}/lock"));
        Assert.Equal(HttpStatusCode.Conflict, take.Status);
        Assert.Equal("locked", take.Body!["code"]!.GetValue<string>());
        var shown = (await api.Get(api.P($"/tasks/{id}/lock"))).Body!;
        Assert.False(shown["mine"]!.GetValue<bool>());
        Assert.Equal("Ivan (console)", shown["holder"]!.GetValue<string>());

        // Чужую блокировку REST снять не может: DELETE ничего не делает.
        await api.Delete(api.P($"/tasks/{id}/lock"));
        Assert.Equal(HttpStatusCode.Conflict, (await api.Patch(api.P($"/tasks/{id}"), new { title = "Other", version = task["version"]!.GetValue<string>() })).Status);

        Assert.Equal(0, (await Cli(api, storage, "lock", "release", "task", id)).Code);

        var updated = await api.Patch(api.P($"/tasks/{id}"), new { title = "Other", version = task["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        Assert.Equal("Other", updated.Body!["title"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Rest_lock_stops_console_changes(string storage)
    {
        using var home = Home();
        await using var api = await ApiHost.Start(storage);
        var id = (await NewTask(api))["id"]!.GetValue<string>();
        await api.Post(api.P($"/tasks/{id}/lock"));

        var blocked = await Cli(api, storage, "task", "update", id, "--title", "From console");
        Assert.Equal(1, blocked.Code);
        Assert.StartsWith("Locked: ", blocked.Err);

        var show = await Cli(api, storage, "lock", "show", "task", id, "--json");
        Assert.False(show.Json["lock"]!["mine"]!.GetValue<bool>());

        // Заняли интерфейс — консоль не заняла бы то же самое.
        var take = await Cli(api, storage, "lock", "acquire", "task", id);
        Assert.Equal(1, take.Code);
        Assert.StartsWith("Locked: ", take.Err);

        await api.Delete(api.P($"/tasks/{id}/lock"));
        var allowed = await Cli(api, storage, "task", "update", id, "--title", "From console");
        Assert.True(allowed.Code == 0, allowed.Err);
    }

    [Fact]
    public async Task Taking_and_releasing_a_lock_publish_a_lock_event_but_renewing_does_not()
    {
        await using var api = await ApiHost.Start("files");
        var id = (await NewTask(api))["id"]!.GetValue<string>();
        using var events = await Events.Subscribe(api);

        await api.Post(api.P($"/tasks/{id}/lock"));
        var taken = await events.Next(x => x["entity"]!.GetValue<string>() == "lock");
        Assert.Equal(id, taken["id"]!.GetValue<string>());

        // Продление — тот же держатель, блокировка уже видна: события нет. Дальше сразу снятие.
        await api.Post(api.P($"/tasks/{id}/lock"));
        await api.Delete(api.P($"/tasks/{id}/lock"));
        var released = await events.Next(x => x["entity"]!.GetValue<string>() == "lock");
        Assert.Equal(id, released["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Sqlite_lock_requests_do_not_trigger_the_catch_all_unknown_event()
    {
        await using var api = await ApiHost.Start("sqlite");
        var id = (await NewTask(api))["id"]!.GetValue<string>();
        using var events = await Events.Subscribe(api);

        // Создание задачи ещё отдаёт свой unknown (с задержкой склейки событий): дожидаемся его и забываем.
        await events.Next(x => x["entity"]!.GetValue<string>() == "unknown");

        await api.Post(api.P($"/tasks/{id}/lock"));
        await api.Delete(api.P($"/tasks/{id}/lock"));
        var seen = await events.Collect(TimeSpan.FromSeconds(1));
        Assert.Equal(["lock"], seen.Select(x => x["entity"]!.GetValue<string>()).Distinct().ToArray());

        // Правка данных, наоборот, по-прежнему даёт unknown.
        await api.NewSeries("TSK");
        Assert.Equal("unknown", (await events.Next(x => true))["entity"]!.GetValue<string>());
    }

    // ---- консоль ----

    [Fact]
    public async Task Whoami_shows_sets_and_clears_the_name()
    {
        using var home = Home();
        await using var api = await ApiHost.Start("files");

        Assert.Equal(Environment.UserName, (await TestWorkspace.Invoke(["whoami"])).Out.Trim());
        Assert.Equal("Your name is Ivan", (await TestWorkspace.Invoke(["whoami", "  Ivan  "])).Out.Trim());
        Assert.Equal("Ivan", (await TestWorkspace.Invoke(["whoami"])).Out.Trim());
        Assert.Equal("Ivan", (await TestWorkspace.Invoke(["whoami", "--json"])).Json["name"]!.GetValue<string>());

        Assert.Equal(1, (await TestWorkspace.Invoke(["whoami", "A", "--clear"])).Code);
        Assert.Equal(1, (await TestWorkspace.Invoke(["whoami", new string('x', 101)])).Code);

        Assert.Equal(0, (await TestWorkspace.Invoke(["whoami", "--clear"])).Code);
        Assert.Equal(Environment.UserName, (await TestWorkspace.Invoke(["whoami"])).Out.Trim());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Lock_commands_find_entities_by_name_and_reference_and_reject_unknown_ones(string storage)
    {
        using var home = Home();
        await using var api = await ApiHost.Start(storage);
        var series = await api.NewSeries("TSK");
        var task = await api.NewTask("Fix login", series);

        foreach (var args in new[]
        {
            new[] { "project", "P" },
            new[] { "task", "TSK-1" },
            new[] { "task", task["id"]!.GetValue<string>() },
            new[] { "taskType", "Task" },
            new[] { "task-type", "Task" },
            new[] { "status", "Todo" },
            new[] { "statusSet", "Set" },
            new[] { "series", "TSK" },
        })
        {
            var result = await Cli(api, storage, ["lock", "acquire", .. args]);
            Assert.True(result.Code == 0, $"{string.Join(' ', args)}: {result.Err}");
            Assert.StartsWith($"locked {args[0].Replace("-", "").ToLowerInvariant()} ", result.Out.ToLowerInvariant());
        }

        Assert.Equal(1, (await Cli(api, storage, "lock", "acquire", "task", "TSK-99")).Code);
        Assert.Equal(1, (await Cli(api, storage, "lock", "acquire", "widget", "x")).Code);
        Assert.Equal(1, (await Cli(api, storage, "lock", "acquire", "user", Guid.NewGuid().ToString())).Code);

        // Снять то, чего не держишь, — не ошибка.
        var released = await Cli(api, storage, "lock", "release", "task", "TSK-1");
        Assert.Equal(0, released.Code);
        var again = await Cli(api, storage, "lock", "release", "task", "TSK-1");
        Assert.Equal(0, again.Code);
        Assert.Contains("not locked by you", again.Out);
    }

    // ---- MCP ----

    [Fact]
    public async Task Mcp_agent_can_lock_and_others_are_stopped_until_it_unlocks()
    {
        using var daemon = new DaemonFixture();
        var folder = await daemon.Workspace("a", "Alpha");
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
        var task = await Call("create_task", new { projectId, title = "Fix login", typeId = type["id"]!.GetValue<string>() });
        var id = task["id"]!.GetValue<string>();

        Assert.Null((await Call("get_lock", new { projectId, entity = "task", entityId = id }))["lock"]);

        var taken = await Call("lock_entity", new { projectId, entity = "task", entityId = id });
        Assert.True(taken["mine"]!.GetValue<bool>());
        Assert.Equal("task", taken["entity"]!.GetValue<string>());
        var holder = taken["holder"]!.GetValue<string>();
        Assert.True((await Call("get_lock", new { projectId, entity = "task", entityId = id }))["lock"]!["mine"]!.GetValue<bool>());

        // Консоль — другой держатель: правка отклонена, и в сообщении названа сущность и агент.
        var blocked = await TestWorkspace.Invoke(["task", "update", id, "--title", "From console", "--project", projectId.ToString(), "--workspace", folder]);
        Assert.Equal(1, blocked.Code);
        Assert.StartsWith("Locked: ", blocked.Err);
        Assert.Contains(holder, blocked.Err);

        var unlocked = await daemon.CallToolResult(key, "unlock_entity", new { projectId, entity = "task", entityId = id });
        Assert.False(unlocked.IsError, unlocked.Text);
        Assert.Contains("unlocked", unlocked.Text);

        var allowed = await TestWorkspace.Invoke(["task", "update", id, "--title", "From console", "--project", projectId.ToString(), "--workspace", folder]);
        Assert.True(allowed.Code == 0, allowed.Err);

        // Ошибки запроса: вид сущности, id и проект.
        Assert.StartsWith("[invalid]", await Fail("lock_entity", new { projectId, entity = "widget", entityId = id }));
        Assert.StartsWith("[invalid]", await Fail("lock_entity", new { projectId, entity = "task" }));
        Assert.StartsWith("[not_found]", await Fail("lock_entity", new { projectId, entity = "task", entityId = Guid.NewGuid() }));
        Assert.StartsWith("[not_found]", await Fail("lock_entity", new { projectId = Guid.NewGuid(), entity = "project" }));
        Assert.Equal("project", (await Call("lock_entity", new { projectId, entity = "project" }))["entity"]!.GetValue<string>());
    }

    // ---- события ----

    private sealed class Events : IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
        private readonly System.Threading.Channels.Channel<JsonNode> _queue = System.Threading.Channels.Channel.CreateUnbounded<JsonNode>();
        private readonly CancellationTokenSource _stop = new();
        private HttpResponseMessage _response = null!;
        private StreamReader _reader = null!;

        // События читает фоновая задача, а тест ждёт их из очереди: отмена чтения прямо из сетевого потока ломает сам поток.
        public static async Task<Events> Subscribe(ApiHost api)
        {
            var events = new Events();
            events._response = await events._http.GetAsync(api.BaseUrl + "/events", HttpCompletionOption.ResponseHeadersRead);
            events._reader = new StreamReader(await events._response.Content.ReadAsStreamAsync());
            Assert.StartsWith(": connected", await events._reader.ReadLineAsync());
            _ = Task.Run(events.Pump);
            return events;
        }

        private async Task Pump()
        {
            try
            {
                while (await _reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    if (line.StartsWith("data:"))
                        _queue.Writer.TryWrite(JsonNode.Parse(line["data:".Length..])!);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException)
            {
                // Тест закончился.
            }
            finally
            {
                _queue.Writer.TryComplete();
            }
        }

        public async Task<JsonNode> Next(Func<JsonNode, bool> match)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                if (!await _queue.Reader.WaitToReadAsync(timeout.Token))
                    throw new Xunit.Sdk.XunitException("the event stream ended");
                if (_queue.Reader.TryRead(out var change) && match(change))
                    return change;
            }
        }

        /// <summary>Всё, что пришло за <paramref name="window"/>.</summary>
        public async Task<List<JsonNode>> Collect(TimeSpan window)
        {
            var found = new List<JsonNode>();
            using var timeout = new CancellationTokenSource(window);
            try
            {
                while (await _queue.Reader.WaitToReadAsync(timeout.Token))
                {
                    while (_queue.Reader.TryRead(out var change))
                        found.Add(change);
                }
            }
            catch (OperationCanceledException)
            {
            }
            return found;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _reader.Dispose();
            _response.Dispose();
            _http.Dispose();
            _stop.Dispose();
        }
    }
}
