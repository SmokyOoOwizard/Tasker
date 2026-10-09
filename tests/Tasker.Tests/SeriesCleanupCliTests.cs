using System.Text.Json.Nodes;
using Tasker.Cli;
using Tasker.Daemon.Services;
using Xunit;

namespace Tasker.Tests;

/// <summary><c>tasker cleanup</c> на настоящем git: слияния веток дают дубликаты номеров, недействительные ссылки и конфликты файлов серий.</summary>
public class SeriesCleanupCliTests : IDisposable
{
    private readonly SeriesRepo _r = new();

    public void Dispose() => _r.Dispose();

    private static Task<CliResult> Ok(Task<CliResult> run) => SeriesRepo.Ok(run);

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task<DateTimeOffset> CreatedAt(Guid id) =>
        (await Ok(_r.P("task", "get", id.ToString(), "--json"))).Json["createdAt"]!.GetValue<DateTimeOffset>();

    // ---- дубликаты номеров ----

    [InProcess]
    [Fact]
    public async Task After_a_merge_both_tasks_are_visible_and_marked_as_a_conflict()
    {
        await _r.Seed();
        var (earlier, later) = await _r.MergeDuplicateNumbers();

        var get = await Ok(_r.P("task", "get", "TSK-2"));
        Assert.Contains("From a", get.Out);
        Assert.Contains("From b", get.Out);
        Assert.Contains($"conflict: TSK-2 is also used by {later}", get.Out);
        Assert.Contains($"conflict: TSK-2 is also used by {earlier}", get.Out);

        var list = (await Ok(_r.P("task", "list", "--series", "TSK"))).Data;
        Assert.Equal(3, Lines(list).Length);
        Assert.Equal(2, Lines(list).Count(x => x.Contains("TSK-2")));
    }

    [InProcess]
    [Fact]
    public async Task Sync_reports_duplicate_numbers_and_writes_nothing()
    {
        await _r.Seed();
        var (earlier, later) = await _r.MergeDuplicateNumbers();
        var before = _r.Snapshot();

        var sync = await Ok(_r.Cli("sync"));

        Assert.Contains("Series problems in project 'Demo':", sync.Out);
        Assert.Contains("TSK-2: tasks", sync.Out);
        Assert.Contains(earlier.ToString(), sync.Out);
        Assert.Contains(later.ToString(), sync.Out);
        Assert.Contains("tasker cleanup --resolve-conflicts", sync.Out);
        Assert.Equal(before, _r.Snapshot());

        var json = (await Ok(_r.Cli("sync", "--json"))).Json["series"]!.AsArray();
        var project = Assert.Single(json)!;
        Assert.Equal("Demo", project["projectName"]!.GetValue<string>());
        var conflict = Assert.Single(project["numberConflicts"]!.AsArray())!;
        Assert.Equal("TSK-2", conflict["reference"]!.GetValue<string>());
        Assert.Equal(2, conflict["taskIds"]!.AsArray().Count);
        Assert.Empty(project["prefixConflicts"]!.AsArray());
        Assert.Equal(0, project["tasksWithInvalidSeries"]!.GetValue<int>());
        Assert.Equal(before, _r.Snapshot());
    }

    [InProcess]
    [Fact]
    public async Task Quiet_sync_prints_only_problems()
    {
        await _r.Seed();
        var clean = await Ok(_r.Cli("sync", "--quiet"));
        Assert.Empty(clean.Out);
        Assert.Empty(clean.Err);

        await _r.MergeDuplicateNumbers();
        var quiet = await Ok(_r.Cli("sync", "--quiet"));
        Assert.DoesNotContain("Synced", quiet.Out);
        Assert.Contains("TSK-2: tasks", quiet.Out);
    }

    [InProcess]
    [Fact]
    public async Task Default_cleanup_does_not_touch_numbers()
    {
        await _r.Seed();
        await _r.MergeDuplicateNumbers();
        var before = _r.Snapshot();

        var result = await Ok(_r.Cli("cleanup"));

        Assert.Equal(before, _r.Snapshot());
        Assert.Contains("TSK-2: tasks", result.Out);
        Assert.Contains("--resolve-conflicts", result.Out);
        Assert.Contains("tasker series renumber-task", result.Out);
    }

    [InProcess]
    [Fact]
    public async Task Resolve_conflicts_renumbers_and_the_earliest_task_keeps_its_number()
    {
        await _r.Seed();
        var (earlier, later) = await _r.MergeDuplicateNumbers();
        Assert.True(await CreatedAt(earlier) < await CreatedAt(later));

        var result = await Ok(_r.Cli("cleanup", "--resolve-conflicts"));

        Assert.Contains("duplicate number TSK-2 changed to TSK-3", result.Out);
        Assert.Contains("1 change(s) made", result.Out);
        Assert.Equal(earlier, (await Ok(_r.P("task", "get", "TSK-2", "--json"))).Json["id"]!.GetValue<Guid>());
        Assert.Equal(later, (await Ok(_r.P("task", "get", "TSK-3", "--json"))).Json["id"]!.GetValue<Guid>());
        Assert.Contains("number: 3", await File.ReadAllTextAsync(_r.FileOf("tasks", later)));

        // Повторный запуск ничего не меняет.
        var before = _r.Snapshot();
        var again = await Ok(_r.Cli("cleanup", "--resolve-conflicts"));
        Assert.Contains("Nothing to clean up", again.Out);
        Assert.Equal(before, _r.Snapshot());
        Assert.Equal(0, (await _r.Cli("cleanup", "--resolve-conflicts", "--check")).Code);
        Assert.DoesNotContain("Series problems", (await Ok(_r.Cli("sync"))).Out);
    }

    [InProcess]
    [Fact]
    public async Task Dry_run_shows_what_the_real_run_then_does_and_writes_nothing()
    {
        await _r.Seed();
        // Три задачи с одним номером: новые номера, выданные в одном запуске, не должны совпасть.
        foreach (var branch in new[] { "a", "b", "c" })
            await _r.Branch(branch, async () => await _r.NewTask($"From {branch}", "TSK"));
        _r.Merge("a", "b", "c");
        var before = _r.Snapshot();

        var dry = await Ok(_r.Cli("cleanup", "--resolve-conflicts", "--dry-run"));
        Assert.Equal(before, _r.Snapshot());
        var dryJson = (await Ok(_r.Cli("cleanup", "--resolve-conflicts", "--dry-run", "--json"))).Json;
        Assert.Equal(before, _r.Snapshot());

        var real = await Ok(_r.Cli("cleanup", "--resolve-conflicts"));
        var realJson = (await Ok(_r.Cli("sync", "--json"))).Json["series"]!.AsArray();
        Assert.Empty(realJson);

        var changeLines = (string text) => Lines(text).Where(x => x.Contains("changed to")).ToArray();
        Assert.Equal(2, changeLines(dry.Out).Length);
        Assert.Equal(changeLines(dry.Out), changeLines(real.Out));
        Assert.Contains("changed to TSK-3", dry.Out);
        Assert.Contains("changed to TSK-4", dry.Out);
        Assert.Contains("would be made", dry.Out);
        Assert.True(dryJson["dryRun"]!.GetValue<bool>());
        Assert.Equal(2, dryJson["changeCount"]!.GetValue<int>());
    }

    [InProcess]
    [Fact]
    public async Task Check_exit_codes()
    {
        await _r.Seed();
        var clean = await _r.Cli("cleanup", "--check");
        Assert.Equal(0, clean.Code);
        Assert.Contains("Nothing to clean up", clean.Out);

        await _r.MergeDuplicateNumbers();
        var before = _r.Snapshot();
        // Без --resolve-conflicts дубликат остаётся и требует внимания; с ним чистка что-то изменила бы.
        Assert.Equal(2, (await _r.Cli("cleanup", "--check")).Code);
        var resolve = await _r.Cli("cleanup", "--check", "--resolve-conflicts", "--json");
        Assert.Equal(2, resolve.Code);
        Assert.True(resolve.Json["needsAttention"]!.GetValue<bool>());
        Assert.Equal(before, _r.Snapshot());

        await Ok(_r.Cli("cleanup", "--resolve-conflicts"));
        Assert.Equal(0, (await _r.Cli("cleanup", "--check")).Code);
    }

    // ---- недействительные ссылки ----

    private async Task MergeDeletedSeriesAndNewTask()
    {
        await _r.Branch("a", async () => await Ok(_r.P("series", "delete", "TSK")));
        await _r.Branch("b", async () => await _r.NewTask("Late", "TSK"));
        _r.Merge("a", "b");
    }

    [InProcess]
    [Fact]
    public async Task A_series_deleted_in_one_branch_leaves_an_invalid_reference_that_sync_reports_and_cleanup_removes()
    {
        await _r.Seed();
        await MergeDeletedSeriesAndNewTask();
        var late = Guid.Parse((await Ok(_r.P("task", "list", "--json"))).Json["data"]!.AsArray().Single(x => x!["title"]!.GetValue<string>() == "Late")!["id"]!.GetValue<string>());
        var before = _r.Snapshot();

        var sync = await Ok(_r.Cli("sync"));
        Assert.Contains("1 task(s) refer to a series that does not exist", sync.Out);
        Assert.Contains("tasker cleanup", sync.Out);
        Assert.Equal(1, (await Ok(_r.Cli("sync", "--json"))).Json["series"]![0]!["tasksWithInvalidSeries"]!.GetValue<int>());
        Assert.Equal(before, _r.Snapshot());
        Assert.Contains("(invalid series", (await Ok(_r.P("task", "get", late.ToString()))).Out);
        Assert.Contains("series:", await File.ReadAllTextAsync(_r.FileOf("tasks", late)));

        // Проверка и пробный запуск ничего не пишут.
        Assert.Equal(2, (await _r.Cli("cleanup", "--check")).Code);
        var dry = await Ok(_r.Cli("cleanup", "--dry-run"));
        Assert.Contains("Task 'Late': removed invalid series reference (was #2)", dry.Out);
        Assert.Equal(before, _r.Snapshot());

        var real = await Ok(_r.Cli("cleanup"));
        Assert.Contains("Task 'Late': removed invalid series reference (was #2)", real.Out);
        Assert.DoesNotContain("series:", await File.ReadAllTextAsync(_r.FileOf("tasks", late)));
        Assert.DoesNotContain("(invalid series", (await Ok(_r.P("task", "get", late.ToString()))).Out);
        Assert.Empty((await Ok(_r.Cli("sync", "--json"))).Json["series"]!.AsArray());
        Assert.Equal(0, (await _r.Cli("cleanup", "--check")).Code);
    }

    [InProcess]
    [Fact]
    public async Task Cleanup_json_has_a_stable_shape()
    {
        await _r.Seed();
        await MergeDeletedSeriesAndNewTask();

        var json = (await Ok(_r.Cli("cleanup", "--json"))).Json;

        Assert.False(json["dryRun"]!.GetValue<bool>());
        Assert.False(json["check"]!.GetValue<bool>());
        Assert.False(json["resolveConflicts"]!.GetValue<bool>());
        Assert.True(json["needsAttention"]!.GetValue<bool>());
        Assert.Equal(1, json["changeCount"]!.GetValue<int>());
        var project = Assert.Single(json["projects"]!.AsArray())!;
        Assert.Equal("Demo", project["project"]!.GetValue<string>());
        Assert.NotNull(project["projectId"]);
        var change = Assert.Single(project["changes"]!.AsArray())!;
        Assert.Equal("removedInvalidSeries", change["kind"]!.GetValue<string>());
        Assert.Equal("Late", change["taskTitle"]!.GetValue<string>());
        Assert.Equal(2, change["oldNumber"]!.GetValue<int>());
        Assert.Empty(project["remainingNumberConflicts"]!.AsArray());
        Assert.Empty(project["prefixConflicts"]!.AsArray());
        Assert.False(project["skipped"]!.GetValue<bool>());
        Assert.Null(project["skipReason"]);
    }

    [InProcess]
    [Fact]
    public async Task Cleanup_can_be_limited_to_one_project()
    {
        await _r.Seed();
        await MergeDeletedSeriesAndNewTask();
        await Ok(_r.Cli("project", "create", "Other"));

        var all = (await Ok(_r.Cli("cleanup", "--dry-run", "--json"))).Json["projects"]!.AsArray();
        Assert.Equal(2, all.Count);
        var only = (await Ok(_r.Cli("cleanup", "--dry-run", "--json", "--project", "Other"))).Json["projects"]!.AsArray();
        Assert.Equal("Other", Assert.Single(only)!["project"]!.GetValue<string>());
        Assert.Contains("Nothing to clean up", (await Ok(_r.Cli("cleanup", "--project", "Other"))).Out);
        Assert.Contains("No project 'Nope'", (await _r.Cli("cleanup", "--project", "Nope")).Err);
    }

    // ---- нечитаемые файлы серий ----

    [InProcess]
    [Fact]
    public async Task An_unreadable_series_file_makes_cleanup_skip_and_leaves_tasks_untouched()
    {
        await _r.Seed();
        var seriesFile = _r.FileOf("series", _r.SeriesId("TSK"));
        await File.WriteAllTextAsync(seriesFile, "<<<<<<< HEAD\nname: A\n=======\nname: B\n>>>>>>> b\n");
        var before = _r.Snapshot();

        var sync = await Ok(_r.Cli("sync"));
        Assert.Contains("1 series file(s) cannot be read", sync.Out);
        Assert.Equal(1, (await Ok(_r.Cli("sync", "--json"))).Json["series"]![0]!["unreadableSeriesFiles"]!.GetValue<int>());

        var cleanup = await _r.Cli("cleanup");
        Assert.Equal(1, cleanup.Code);
        Assert.Contains("skipped: Some series files cannot be read", cleanup.Out);
        Assert.Contains("skipped", cleanup.Err);
        Assert.Equal(before, _r.Snapshot());

        var check = await _r.Cli("cleanup", "--check");
        Assert.Equal(2, check.Code);
        Assert.Contains("skipped", check.Out);
        var dry = await _r.Cli("cleanup", "--dry-run", "--json");
        Assert.Equal(0, dry.Code);
        Assert.True(dry.Json["projects"]![0]!["skipped"]!.GetValue<bool>());
        Assert.Contains("cannot be read", dry.Json["projects"]![0]!["skipReason"]!.GetValue<string>());
        Assert.Equal(before, _r.Snapshot());
    }

    // ---- конфликт префиксов ----

    private async Task MergeSamePrefix()
    {
        await _r.Seed(withSeries: false);
        await _r.Branch("a", async () => await Ok(_r.P("series", "create", "First", "--prefix", "DUP")));
        await _r.Branch("b", async () => await Ok(_r.P("series", "create", "Second", "--prefix", "DUP")));
        _r.Merge("a", "b");
    }

    [InProcess]
    [Fact]
    public async Task Two_series_with_the_same_prefix_require_a_rename()
    {
        await MergeSamePrefix();
        var before = _r.Snapshot();

        var sync = await Ok(_r.Cli("sync"));
        Assert.Contains("series rename required: series prefix 'DUP' is used by several series", sync.Out);
        Assert.Contains("tasker series update <id> --prefix", sync.Out);
        var conflict = (await Ok(_r.Cli("sync", "--json"))).Json["series"]![0]!["prefixConflicts"]![0]!;
        Assert.Equal("DUP", conflict["prefix"]!.GetValue<string>());
        Assert.Equal(2, conflict["seriesIds"]!.AsArray().Count);
        Assert.Contains("series rename required", (await Ok(_r.Cli("sync", "--quiet"))).Out);

        var cleanup = await Ok(_r.Cli("cleanup", "--resolve-conflicts"));
        Assert.Contains("series prefix 'DUP' is used by several series", cleanup.Out);
        Assert.Contains("rename with 'tasker series update <id> --prefix ...'", cleanup.Out);
        Assert.Equal(2, (await _r.Cli("cleanup", "--check")).Code);
        Assert.Equal(before, _r.Snapshot());

        // Переименование по id решает конфликт.
        var ids = conflict["seriesIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        await Ok(_r.P("series", "update", ids[0], "--prefix", "ONE"));
        Assert.Equal(0, (await _r.Cli("cleanup", "--check")).Code);
        Assert.Empty((await Ok(_r.Cli("sync", "--json"))).Json["series"]!.AsArray());
    }

    // ---- git в процессе ----

    private async Task ConflictedMerge()
    {
        await _r.Seed();
        var file = _r.FileOf("series", _r.SeriesId("TSK"));
        await _r.Branch("a", async () => await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("name: Tasks", "name: FromA")));
        await _r.Branch("b", async () => await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("name: Tasks", "name: FromB")));
        _r.Merge("a");
        Assert.Throws<InvalidOperationException>(() => _r.Merge("b"));
    }

    [InProcess]
    [Fact]
    public async Task Cleanup_refuses_while_a_merge_is_in_progress_but_check_and_dry_run_work()
    {
        await ConflictedMerge();
        var before = _r.Snapshot();

        var refused = await _r.Cli("cleanup");
        Assert.Equal(1, refused.Code);
        Assert.Contains("A git merge is in progress", refused.Err);
        Assert.Equal(before, _r.Snapshot());
        Assert.Equal(1, (await _r.Cli("cleanup", "--resolve-conflicts")).Code);

        Assert.Equal(2, (await _r.Cli("cleanup", "--check")).Code);
        Assert.Equal(0, (await _r.Cli("cleanup", "--dry-run")).Code);
        Assert.Equal(before, _r.Snapshot());

        _r.Repo.Git("merge", "--abort");
        Assert.Equal(0, (await _r.Cli("cleanup")).Code);
    }

    [InProcess]
    [Fact]
    public async Task Cleanup_refuses_while_a_rebase_is_in_progress()
    {
        await _r.Seed();
        var file = _r.FileOf("series", _r.SeriesId("TSK"));
        await _r.Branch("a", async () => await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("name: Tasks", "name: FromA")));
        _r.Repo.Git("checkout", "-q", "-b", "b", "main");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("name: Tasks", "name: FromB"));
        _r.Repo.Commit("b");
        Assert.Throws<InvalidOperationException>(() => _r.Repo.Git("rebase", "a"));

        var refused = await _r.Cli("cleanup");
        Assert.Equal(1, refused.Code);
        Assert.Contains("A git rebase is in progress", refused.Err);
        Assert.Equal(2, (await _r.Cli("cleanup", "--check")).Code);
    }

    [InProcess]
    [Fact]
    public async Task Git_operations_are_detected_and_a_missing_git_or_repository_lets_cleanup_go_on()
    {
        var git = new GitOperations(new SystemProcessRunner());
        await _r.Seed();
        Assert.Null(await git.InProgress(_r.Folder));

        var file = _r.FileOf("series", _r.SeriesId("TSK"));
        await _r.Branch("a", async () => await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("name: Tasks", "name: FromA")));
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("name: Tasks", "name: FromMain"));
        _r.Repo.Commit("main");
        Assert.Throws<InvalidOperationException>(() => _r.Repo.Git("cherry-pick", "a"));
        Assert.Equal("cherry-pick", await git.InProgress(_r.Folder));
        _r.Repo.Git("cherry-pick", "--abort");
        Assert.Throws<InvalidOperationException>(() => _r.Repo.Git("revert", "--no-edit", "HEAD~1"));
        Assert.Equal("revert", await git.InProgress(_r.Folder));
        _r.Repo.Git("revert", "--abort");
        Assert.Null(await git.InProgress(_r.Folder));

        // Папка вне репозитория и отсутствующий git — не мешают.
        var plain = Directory.CreateTempSubdirectory("tasker-nogit-").FullName;
        try
        {
            Assert.Null(await git.InProgress(plain));
        }
        finally
        {
            Directory.Delete(plain, true);
        }
        Assert.Null(await new GitOperations(new FakeRunner(127)).InProgress(_r.Folder));
    }

    private sealed class FakeRunner(int code) : IProcessRunner
    {
        public Task<ProcessResult> Run(string file, params string[] arguments) => Task.FromResult(new ProcessResult(code, "", "no git"));
    }

    [Fact]
    public async Task Cleanup_works_in_a_folder_that_is_not_a_git_repository()
    {
        using var ws = TestWorkspace.Create("files", asProcess: true);
        await Ok(ws.Run("project", "create", "Demo"));

        var result = await ws.Run("cleanup");

        Assert.Equal(0, result.Code);
        Assert.Contains("Nothing to clean up", result.Out);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Cleanup_of_a_healthy_workspace_changes_nothing_on_both_storages(string storage)
    {
        using var ws = TestWorkspace.Create(storage, asProcess: true);
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.InProject("Demo", "status", "create", "Todo"));
        await Ok(ws.InProject("Demo", "status-set", "create", "Flow", "--status", "Todo"));
        await Ok(ws.InProject("Demo", "task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(ws.InProject("Demo", "series", "create", "Tasks", "--prefix", "TSK"));
        await Ok(ws.InProject("Demo", "task", "create", "T", "--type", "Bug", "--series", "TSK"));

        foreach (var flags in new[] { Array.Empty<string>(), ["--resolve-conflicts"], ["--dry-run"], ["--check"] })
        {
            var result = await ws.Run(["cleanup", .. flags]);
            Assert.Equal(0, result.Code);
            Assert.Contains("Nothing to clean up", result.Out);
        }

        var json = (await Ok(ws.Run("cleanup", "--json"))).Json;
        Assert.False(json["needsAttention"]!.GetValue<bool>());
        Assert.Equal(0, json["changeCount"]!.GetValue<int>());
    }
}
