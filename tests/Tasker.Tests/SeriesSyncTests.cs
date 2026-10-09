using System.Text.Json.Nodes;
using Tasker.Core;
using Tasker.Core.Dto;
using Tasker.Core.Projects;
using Tasker.Core.TaskSeries;
using Tasker.Daemon;
using Tasker.Tests.SeriesCore;
using Xunit;

namespace Tasker.Tests;

/// <summary>Сверка серий: <c>tasker sync</c> напрямую и через демон, сообщения в логе демона.</summary>
[InProcess]
public class SeriesSyncTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();
    private readonly SeriesRepo _r;

    public SeriesSyncTests() => _r = new SeriesRepo(isolate: false);

    public void Dispose()
    {
        _r.Dispose();
        _daemon.Dispose();
    }

    private static Task<CliResult> Ok(Task<CliResult> run) => SeriesRepo.Ok(run);

    private string LogText()
    {
        var directory = Path.Combine(_daemon.Home.Path, "logs");
        if (!Directory.Exists(directory))
            return "";
        return string.Join('\n', Directory.GetFiles(directory, "mcp-*.log").Select(file =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));
    }

    private async Task<string> WaitForLog(string text)
    {
        for (var i = 0; i < 100; i++)
        {
            if (LogText() is var log && log.Contains(text))
                return log;
            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException($"The daemon log has no '{text}':\n{LogText()}");
    }

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    private async Task Serve() => Assert.Equal(0, (await _daemon.Mcp("workspace", "add", _r.Folder)).Code);

    [Fact]
    public async Task The_daemon_path_of_sync_returns_the_same_series_report_as_the_direct_one()
    {
        await _r.Seed();
        await _r.MergeDuplicateNumbers();

        var direct = (await Ok(_r.Cli("sync", "--json"))).Json;
        Assert.Equal("direct", direct["via"]!.GetValue<string>());
        await Serve();
        await _daemon.Start();
        var served = (await Ok(_r.Cli("sync", "--json"))).Json;

        Assert.Equal("daemon", served["via"]!.GetValue<string>());
        Assert.Single(served["series"]!.AsArray());
        Assert.Equal(direct["series"]!.ToJsonString(), served["series"]!.ToJsonString());
        var text = (await Ok(_r.Cli("sync"))).Out;
        Assert.Contains("(via daemon)", text);
        Assert.Contains("TSK-2: tasks", text);
    }

    [Fact]
    public async Task The_daemon_logs_an_error_about_prefixes_and_says_rename_required_in_sync_without_repeating_itself()
    {
        await _r.Seed(withSeries: false);
        await _r.Branch("a", async () => await Ok(_r.P("series", "create", "First", "--prefix", "DUP")));
        await _r.Branch("b", async () => await Ok(_r.P("series", "create", "Second", "--prefix", "DUP")));
        _r.Merge("a", "b");
        await Serve();
        await _daemon.Start();

        // После открытия области — Error в логе.
        var log = await WaitForLog("series rename required");
        var line = log.Split('\n').First(x => x.Contains("series rename required"));
        Assert.Contains("[ERR]", line);
        Assert.Contains("series prefix 'DUP' is used by several series", line);
        Assert.Contains("project 'Demo'", line);

        var sync = await Ok(_r.Cli("sync"));
        Assert.Contains("(via daemon)", sync.Out);
        Assert.Contains("series rename required", sync.Out);
        Assert.Contains("--prefix", sync.Out);
        await Ok(_r.Cli("sync"));
        await Ok(_r.Cli("sync", "--quiet"));

        // Состояние не менялось: то же сообщение повторно не пишется.
        Assert.Equal(1, Count(LogText(), "series rename required"));

        // Исправили — при новой проблеме сообщение появится снова.
        var ids = (await Ok(_r.Cli("sync", "--json"))).Json["series"]![0]!["prefixConflicts"]![0]!["seriesIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        await Ok(_r.P("series", "update", ids[0], "--prefix", "ONE"));
        Assert.Empty((await Ok(_r.Cli("sync", "--json"))).Json["series"]!.AsArray());
        // Снова конфликт (как после очередного слияния) — правим файл, потому что команда такой префикс не выдаст.
        var file = _r.FileOf("series", Guid.Parse(ids[0]));
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("prefix: ONE", "prefix: DUP"));
        await Ok(_r.Cli("sync"));
        Assert.Equal(2, Count(LogText(), "series rename required"));
    }

    [Fact]
    public async Task The_daemon_logs_a_warning_for_duplicate_numbers_and_invalid_references_once_per_state()
    {
        await _r.Seed();
        await _r.MergeDuplicateNumbers();
        await Serve();
        await _daemon.Start();

        var log = await WaitForLog("series need attention");
        var line = log.Split('\n').First(x => x.Contains("series need attention"));
        Assert.Contains("[WRN]", line);
        Assert.Contains("TSK-2", line);
        Assert.DoesNotContain("rename required", log);

        await Ok(_r.Cli("sync"));
        await Ok(_r.Cli("sync"));
        Assert.Equal(1, Count(LogText(), "series need attention"));

        // Чистка меняет состояние: ошибок и предупреждений больше нет, повторного предупреждения нет.
        await Ok(_r.Cli("cleanup", "--resolve-conflicts"));
        Assert.Empty((await Ok(_r.Cli("sync", "--json"))).Json["series"]!.AsArray());
        Assert.Equal(1, Count(LogText(), "series need attention"));
    }

    [Fact]
    public async Task The_daemon_logs_a_warning_for_invalid_links_once_per_state_and_keeps_series_messages_separate()
    {
        await _r.Seed();
        await _r.MergeDanglingTargetLink();
        await Serve();
        await _daemon.Start();

        var log = await WaitForLog("links need attention");
        var line = log.Split('\n').First(x => x.Contains("links need attention"));
        Assert.Contains("[WRN]", line);
        Assert.Contains("project 'Demo'", line);
        Assert.Contains("link to a task or link type that does not exist", line);
        Assert.DoesNotContain("series need attention", log); // про серии сказать нечего

        await Ok(_r.Cli("sync"));
        await Ok(_r.Cli("sync"));
        Assert.Equal(1, Count(LogText(), "links need attention"));

        // Чистка меняет состояние: связей-сирот нет, повторного предупреждения нет.
        await Ok(_r.Cli("cleanup"));
        Assert.Empty((await Ok(_r.Cli("sync", "--json"))).Json["series"]!.AsArray());
        Assert.Equal(1, Count(LogText(), "links need attention"));
    }

    [Fact]
    public async Task The_daemon_logs_a_link_cycle_once_per_state_with_the_unlink_hint()
    {
        await _r.Seed();
        await Ok(_r.P("task", "create", "Other", "--type", "Bug", "--series", "TSK"));
        await Ok(_r.P("task", "link", "TSK-1", "relates to", "TSK-2"));
        await Ok(_r.P("task", "unlink", "TSK-1", "relates to", "TSK-2"));
        _r.Repo.Commit("link types");
        await _r.Branch("one", async () => await Ok(_r.P("task", "link", "TSK-1", "blocks", "TSK-2")));
        await _r.Branch("two", async () => await Ok(_r.P("task", "link", "TSK-2", "blocks", "TSK-1")));
        _r.Merge("one", "two");
        await Serve();
        await _daemon.Start();

        var log = await WaitForLog("links need attention");
        var line = log.Split('\n').First(x => x.Contains("links need attention"));
        Assert.Contains("[WRN]", line);
        Assert.Contains("project 'Demo'", line);
        Assert.Contains("Link cycle: TSK-1 → TSK-2 → TSK-1 (link type Blocks)", line);
        Assert.Contains("task unlink", line);

        await Ok(_r.Cli("sync"));
        await Ok(_r.Cli("sync"));
        Assert.Equal(1, Count(LogText(), "links need attention"));
    }

    [Fact]
    public async Task Writing_through_the_cli_while_the_daemon_serves_the_folder_gives_consecutive_numbers()
    {
        await _r.Seed();
        await Serve();
        var status = await _daemon.Start();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            TaskerProcess.Run("task", "create", $"Task {i}", "--type", "Bug", "--series", "TSK", "-p", "Demo", "-w", _r.Folder)));
        Assert.All(results, x => Assert.True(x.Code == 0, x.Err));

        var numbers = (await Ok(_r.P("task", "list", "--series", "TSK", "--json"))).Json["data"]!.AsArray()
            .Select(x => x!["seriesNumbers"]![0]!["number"]!.GetValue<int>()).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, 5), numbers);
        Assert.Contains("Demo", await _daemon.CallTool(status.Workspaces[0].Key!, "list_projects"));

        var sync = (await Ok(_r.Cli("sync", "--json"))).Json;
        Assert.Equal("daemon", sync["via"]!.GetValue<string>());
        Assert.Empty(sync["series"]!.AsArray());
        Assert.Equal(0, (await _r.Cli("cleanup", "--check")).Code);
    }

    [Fact]
    public async Task Quiet_sync_through_the_daemon_prints_nothing_when_all_is_well()
    {
        await _r.Seed();
        await Serve();
        await _daemon.Start();

        var result = await Ok(_r.Cli("sync", "--quiet"));

        Assert.Empty(result.Out);
        Assert.Empty(result.Err);
    }

    // ---- сбой проверки не роняет сверку ----

    private sealed class OneProject(Guid id) : IProjectStorage
    {
        public Task<Project?> GetById(Guid projectId, CancellationToken ct = default) => Task.FromResult<Project?>(null);

        public Task<ListDto<Project>> GetRange(Guid[]? ids, Page page, CancellationToken ct = default) => Task.FromResult(new ListDto<Project>
        {
            TotalCount = 1,
            Offset = 0,
            Limit = page.Limit,
            Data = [new Project { Id = id, Name = "Broken", CreatedAt = DateTimeOffset.UnixEpoch, Version = "v" }]
        });

        public Task<string> Add(Project project, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> Update(Project project, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> Delete(Guid projectId, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class BrokenSeries : ISeriesStorage
    {
        public Task<Series?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => throw new IOException("disk gone");
        public Task<Series[]> GetAll(Guid projectId, CancellationToken ct = default) => throw new IOException("disk gone");
        public Task<ListDto<Series>> GetRange(Guid projectId, Page page, CancellationToken ct = default) => throw new IOException("disk gone");
        public Task<string> Add(Series series, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> Update(Series series, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => throw new IOException("disk gone");
    }

    [Fact]
    public async Task A_failing_health_check_is_reported_as_text_and_does_not_throw()
    {
        var report = await SeriesReports.Compute(new OneProject(Guid.NewGuid()), new InMemoryTaskStorage(), new BrokenSeries(), new InMemoryLinkTypeStorage());

        var project = Assert.Single(report);
        Assert.Equal("Broken", project.ProjectName);
        Assert.Contains("disk gone", project.Error);
        Assert.Contains("disk gone", string.Concat(SeriesReports.Describe(project)));
    }

    [Fact]
    public void An_old_sync_json_without_series_still_reads()
    {
        var result = System.Text.Json.JsonSerializer.Deserialize<SyncResult>(
            """{"path":"/w","problemCount":0,"problems":[]}""", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Empty(result.Series);
    }
}
