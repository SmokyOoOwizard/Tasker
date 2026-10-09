using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Tasker.Cli;
using Tasker.Stress;
using Tasker.Core.Workspace;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Параллельная работа многих клиентов в одной области: стенд <c>tools/Tasker.Stress</c> с малыми числами (чтобы тест не флачил)
/// и точечные проверки найденных стендом проблем. Большой прогон — вручную: <c>dotnet run --project tools/Tasker.Stress</c>
/// или тот же тест с <c>TASKER_STRESS=1</c> (10 клиентов, все сценарии, оба хранилища).
/// </summary>
[InProcess]
public class ParallelWorkTests
{
    private static async Task Run(string client, string storage, int clients, int ops, params string[] scenarios)
    {
        var options = new StressOptions { Clients = clients, Ops = ops, Client = client, Storage = storage, Scenarios = [.. scenarios], Scale = [2] };
        var report = await StressRunner.Run(options);
        Assert.True(report.Passed, report.ToText());
    }

    [Fact]
    public Task Several_console_clients_and_agents_in_one_project_keep_the_files_and_the_index_consistent() =>
        Run("mix", "files", 4, 2, "create", "edit-same", "edit-different", "locks", "cascade-wait", "links", "delete-while-edit");

    [Fact]
    public Task Several_console_clients_in_a_sqlite_workspace_keep_the_data_consistent() =>
        Run("cli", "sqlite", 3, 2, "create", "edit-same", "links");

    [Fact]
    public Task Killed_processes_leave_no_broken_files_and_no_stuck_locks() =>
        Run("mix", "files", 3, 2, "kill");

    [Fact]
    public Task The_daemon_replaced_on_the_fly_under_load_loses_no_call_and_no_write() =>
        Run("mcp", "files", 3, 2, "upgrade");

    /// <summary>Большой прогон стенда: 10 клиентов, все сценарии; только с TASKER_STRESS=1 (долго, грузит машину).</summary>
    [Theory]
    [InlineData("cli", "files")]
    [InlineData("mcp", "files")]
    [InlineData("mix", "files")]
    [InlineData("cli", "sqlite")]
    [InlineData("mix", "sqlite")]
    public async Task Stand_with_ten_clients(string client, string storage)
    {
        if (Environment.GetEnvironmentVariable("TASKER_STRESS") is not "1")
            return;

        var options = new StressOptions { Clients = 10, Ops = 5, Client = client, Storage = storage };
        var report = await StressRunner.Run(options);
        Assert.True(report.Passed, report.ToText());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Links_added_to_one_task_by_many_clients_are_all_kept(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await ws.Run("project", "create", "Demo");
        await ws.Run("status", "create", "Todo", "-p", "Demo");
        await ws.Run("status-set", "create", "Flow", "--status", "Todo", "-p", "Demo");
        await ws.Run("task-type", "create", "Bug", "--status-set", "Flow", "-p", "Demo");

        var hub = (await ws.Run("task", "create", "hub", "--type", "Bug", "-p", "Demo")).Id.ToString();
        var targets = new List<string>();
        for (var i = 0; i < 12; i++)
            targets.Add((await ws.Run("task", "create", $"target {i}", "--type", "Bug", "-p", "Demo")).Id.ToString());

        // Версию клиенты не передают: связи коммутативны, гонку за версию задачи-источника переживает сама операция.
        var results = await Task.WhenAll(targets.Select(t => Task.Run(() => ws.Run("task", "link", hub, "blocks", t, "-p", "Demo"))));

        Assert.All(results, r => Assert.True(r.Code == 0, r.Err));
        var links = (await ws.Run("task", "links", hub, "-p", "Demo", "--json")).Json["links"]!.AsArray();
        Assert.Equal(12, links.Count);
    }

    [Fact]
    public async Task A_late_writer_does_not_resurrect_the_folder_of_a_deleted_project()
    {
        using var ws = TestWorkspace.Create("files");
        var project = (await ws.Run("project", "create", "Doomed")).Id;
        await ws.Run("status", "create", "Todo", "-p", "Doomed");
        await ws.Run("status-set", "create", "Flow", "--status", "Todo", "-p", "Doomed");
        var type = (await ws.Run("task-type", "create", "Bug", "--status-set", "Flow", "-p", "Doomed")).Id;
        var status = (await ws.Run("status", "list", "-p", "Doomed", "--json")).Json["data"]![0]!["id"]!.GetValue<string>();

        // Агент проверил проект и тип, его запись «зависла» — а проект удалили.
        await using var session = await Session.Open(new WorkspaceSettings(ws.Root, null), CancellationToken.None);
        var storage = session.Get<Tasker.Core.Tasks.ITaskStorage>();
        Assert.True((await ws.Run("project", "delete", "Doomed", "--yes")).Code == 0);

        var late = new Tasker.Core.Tasks.TaskItem
        {
            Id = Guid.NewGuid(), ProjectId = project, Title = "late", TypeId = type, StatusId = Guid.Parse(status),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = ""
        };
        await Assert.ThrowsAsync<Tasker.Core.TaskerValidationException>(() => storage.Add(late));

        Assert.False(Directory.Exists(Path.Combine(ws.Root, ".tasker", "projects", project.ToString())), "the folder of the deleted project must stay deleted");
    }

    /// <summary>
    /// Десктоп (REST) как ещё один клиент и события SSE: две вкладки слушают поток <c>/events</c>, пока REST и процессы консоли создают задачи
    /// в одной области. Каждая созданная задача должна дойти до обеих вкладок (событием о ней или общим «unknown» — «перечитать всё»),
    /// без обрывов потока; номера серии уникальны и сплошные.
    /// </summary>
    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Events_reach_every_subscriber_while_rest_and_console_clients_write(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var series = await api.NewSeries("TSK");
        var location = storage == "sqlite" ? new[] { "--sqlite", Path.Combine(api.Root, "tasker.db") } : ["--workspace", api.Root];

        var tabs = new[] { await EventTab.Open(api), await EventTab.Open(api) };
        try
        {
            var created = new ConcurrentDictionary<string, long>();
            var restWriters = Enumerable.Range(0, 3).Select(w => Task.Run(async () =>
            {
                for (var i = 0; i < 3; i++)
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    created[(await api.NewTask($"rest {w}/{i}", series))["id"]!.GetValue<string>()] = started;
                }
            }));
            // Консоль пишет в ту же папку отдельными процессами; в SQLite снаружи за изменениями не следят (события — только за запросами REST).
            var consoleWriters = storage == "sqlite" ? Enumerable.Empty<Task>() : Enumerable.Range(0, 3).Select(w => Task.Run(async () =>
            {
                for (var i = 0; i < 2; i++)
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    var result = await TaskerProcess.Run(["task", "create", $"console {w}/{i}", "--type", api.TypeId.ToString(), "--series", "TSK", "--json", "-p", api.ProjectId.ToString(), .. location]);
                    Assert.True(result.Code == 0, result.Err);
                    created[result.Json["id"]!.GetValue<string>()] = started;
                }
            }));
            await Task.WhenAll(restWriters.Concat(consoleWriters));

            var expected = storage == "sqlite" ? 9 : 15;
            Assert.Equal(expected, created.Count);
            foreach (var tab in tabs)
                await tab.SeesAll(created);

            // Список и номера серии согласованы.
            var list = (await api.Get(api.P("/tasks?limit=100"))).Body!["data"]!.AsArray();
            Assert.Equal(expected, list.Count);
            Assert.Equal(Enumerable.Range(1, expected), list.Select(x => x!["seriesNumbers"]![0]!["number"]!.GetValue<int>()).Order());
            Assert.All(tabs, x => Assert.False(x.Ended, "the event stream must stay open"));
        }
        finally
        {
            foreach (var tab in tabs)
                tab.Dispose();
        }
    }

    /// <summary>
    /// Вкладка: читает поток событий в фоне. Событие о задаче называет её id; событие «задачи вообще» (без id: изменилась папка целиком)
    /// и «unknown» (перечитать всё) покрывают любые задачи, созданные до него, — вкладка в ответ перечитывает список.
    /// </summary>
    private sealed class EventTab : IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<string, byte> _ids = new();
        private readonly ConcurrentQueue<long> _coverAll = new();
        private readonly ConcurrentQueue<string> _raw = new();
        private volatile bool _ended;

        public bool Ended => _ended;

        public static async Task<EventTab> Open(ApiHost api)
        {
            var tab = new EventTab();
            var response = await tab._http.GetAsync(api.BaseUrl + "/events", HttpCompletionOption.ResponseHeadersRead);
            var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            Assert.StartsWith(": connected", await reader.ReadLineAsync());
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await reader.ReadLineAsync(tab._stop.Token) is { } line)
                    {
                        if (!line.StartsWith("data:"))
                            continue;
                        tab._raw.Enqueue(line);
                        var change = JsonNode.Parse(line["data:".Length..])!;
                        var entity = change["entity"]!.GetValue<string>();
                        if (entity == "task" && change["id"] is { } id)
                            tab._ids[id.GetValue<string>()] = 0;
                        else if (entity is "task" or "unknown")
                            tab._coverAll.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
                    }

                    tab._ended = !tab._stop.IsCancellationRequested;
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException)
                {
                }
            });
            return tab;
        }

        /// <summary>Ждёт до 20 секунд: о каждой задаче есть событие (с её id или общее, пришедшее после её создания)..</summary>
        public async Task SeesAll(IReadOnlyDictionary<string, long> created)
        {
            for (var i = 0; i < 200; i++)
            {
                var latest = _coverAll.DefaultIfEmpty(0).Max();
                if (created.All(x => _ids.ContainsKey(x.Key) || latest >= x.Value))
                    return;
                await Task.Delay(100);
            }

            var latestCover = _coverAll.DefaultIfEmpty(0).Max();
            var missing = created.Where(x => !_ids.ContainsKey(x.Key) && latestCover < x.Value).Select(x => x.Key).ToArray();
            Assert.Fail($"a tab missed events: {missing.Length} of {created.Count} tasks never reported: {string.Join(',', missing)}; received: {string.Join(' ', _raw)}");
        }

        public void Dispose()
        {
            _stop.Cancel();
            _http.Dispose();
        }
    }
}
