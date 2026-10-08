using Tasker.Cli;
using Tasker.Core.IO;
using Tasker.Core.Workspace;
using Xunit;

namespace Tasker.Tests;

/// <summary>Короткие блокировки и работа нескольких процессов с одной папкой (демон + десктоп + командная строка).</summary>
public class ConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));

    public ConcurrencyTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string LockPath => Path.Combine(_root, "test.lock");

    [Fact]
    public async Task FileLock_is_exclusive_and_gives_way_after_release()
    {
        var first = await FileLock.Acquire(LockPath);

        var second = FileLock.Acquire(LockPath);
        await Task.Delay(200);
        Assert.False(second.IsCompleted);

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task FileLock_times_out_when_the_lock_is_not_released()
    {
        using var held = await FileLock.Acquire(LockPath);

        var error = await Assert.ThrowsAsync<TimeoutException>(() => FileLock.Acquire(LockPath, timeout: TimeSpan.FromMilliseconds(200)));
        Assert.Contains("is busy", error.Message);

        // Неудачная попытка не оставляет за собой захваченного: после освобождения блокировка берётся.
        held.Dispose();
        (await FileLock.Acquire(LockPath, timeout: TimeSpan.FromSeconds(1))).Dispose();
    }

    [Fact]
    public async Task FileLock_is_held_by_another_process_too()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Держит настоящий другой процесс: он берёт блокировку и не отпускает, пока не закроют его stdin.
        using var holder = TaskerProcess.StartHolder(LockPath);
        await holder.WaitUntilHeld();

        await Assert.ThrowsAsync<TimeoutException>(() => FileLock.Acquire(LockPath, timeout: TimeSpan.FromMilliseconds(300)));

        holder.Release();
        (await FileLock.Acquire(LockPath, timeout: TimeSpan.FromSeconds(10))).Dispose();
    }

    [Fact]
    public async Task FileLock_run_does_not_wait_for_itself_when_nested()
    {
        var result = await FileLock.Run(LockPath, () => FileLock.Run(LockPath, () => Task.FromResult(42)))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(42, result);
        (await FileLock.Acquire(LockPath, timeout: TimeSpan.FromSeconds(1))).Dispose();
    }

    [Fact]
    public async Task FileLock_run_serializes_read_modify_write()
    {
        var counter = Path.Combine(_root, "counter.txt");
        await File.WriteAllTextAsync(counter, "0");

        await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() => FileLock.Run(LockPath, async () =>
        {
            var value = int.Parse(await File.ReadAllTextAsync(counter));
            await Task.Delay(1);
            await File.WriteAllTextAsync(counter, (value + 1).ToString());
        }))));

        Assert.Equal("30", await File.ReadAllTextAsync(counter));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Parallel_processes_all_write_to_one_workspace(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await ws.Run("project", "create", "Demo");
        await ws.Run("status", "create", "Todo", "-p", "Demo");
        await ws.Run("status-set", "create", "Flow", "--status", "Todo", "-p", "Demo");
        await ws.Run("task-type", "create", "Bug", "--status-set", "Flow", "-p", "Demo");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            ws.RunProcess("task", "create", $"Task {i}", "--type", "Bug", "-p", "Demo")));

        Assert.All(results, r => Assert.True(r.Code == 0, r.Err));
        var list = (await ws.Run("task", "list", "-p", "Demo", "--limit", "50", "--json")).Json;
        Assert.Equal(8, list["totalCount"]!.GetValue<int>());
        Assert.Equal(8, list["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).Distinct().Count());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Of_two_processes_changing_the_same_version_only_one_wins(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await ws.Run("project", "create", "Demo");
        await ws.Run("status", "create", "Todo", "-p", "Demo");
        var status = (await ws.Run("status", "get", "Todo", "-p", "Demo", "--json")).Json;
        var id = status["id"]!.GetValue<string>();
        var version = status["version"]!.GetValue<string>();

        var results = await Task.WhenAll(
            ws.RunProcess("status", "update", id, "--name", "First", "--expected-version", version, "-p", "Demo"),
            ws.RunProcess("status", "update", id, "--name", "Second", "--expected-version", version, "-p", "Demo"));

        Assert.Single(results, r => r.Code == 0);
        var loser = Assert.Single(results, r => r.Code == 1);
        Assert.Contains("Modified by someone else", loser.Err);
    }

    [Fact]
    public async Task Idle_workspace_holds_no_locks()
    {
        using var ws = TestWorkspace.Create("files");
        await ws.Run("project", "create", "Demo");
        await ws.Run("project", "list");

        var cache = Path.Combine(ws.Root, ".tasker", ".cache");
        (await FileLock.Acquire(Path.Combine(cache, "index.lock"), timeout: TimeSpan.FromMilliseconds(300))).Dispose();
        (await FileLock.Acquire(Path.Combine(cache, "write.lock"), timeout: TimeSpan.FromMilliseconds(300))).Dispose();
    }

    [Fact]
    public async Task Reading_waits_for_the_index_lock_and_goes_on_after_release()
    {
        using var ws = TestWorkspace.Create("files");
        await ws.Run("project", "create", "Demo");
        var indexLock = Path.Combine(ws.Root, ".tasker", ".cache", "index.lock");

        var held = await FileLock.Acquire(indexLock);
        using var child = TaskerProcess.Start(["project", "list", .. ws.Location]);
        var output = child.StandardOutput.ReadToEndAsync();

        await Task.Delay(2500);
        Assert.False(child.HasExited, "the command must wait while the index lock is held");

        held.Dispose();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, child.ExitCode);
        Assert.Contains("Demo", await output);
    }

    [Fact]
    public async Task Writing_waits_for_the_write_lock()
    {
        using var ws = TestWorkspace.Create("files");
        await ws.Run("project", "create", "Demo");
        var writeLock = Path.Combine(ws.Root, ".tasker", ".cache", "write.lock");

        var held = await FileLock.Acquire(writeLock);
        using var child = TaskerProcess.Start(["project", "create", "Other", .. ws.Location]);
        var output = child.StandardOutput.ReadToEndAsync();

        await Task.Delay(2500);
        Assert.False(child.HasExited, "the write must wait while the write lock is held");

        held.Dispose();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, child.ExitCode);
        Assert.Contains("Other", (await ws.Run("project", "list")).Out);
    }

    [Fact]
    public async Task Two_processes_open_the_same_folder_and_see_each_others_changes()
    {
        using var ws = TestWorkspace.Create("files");
        await ws.Run("project", "create", "First");

        // Первый процесс (как десктоп или демон) держит папку открытой: индекс и наблюдатель за файлами.
        await using var opened = await Session.Open(new WorkspaceSettings(ws.Root, null), CancellationToken.None, watch: true);
        var changes = new List<string>();
        opened.Get<IWorkspaceIndex>().Changed += paths =>
        {
            lock (changes)
                changes.AddRange(paths.Select(x => x.Path));
        };

        // Второй пишет в ту же папку — и ему не мешают, и первый об этом узнаёт.
        var created = await ws.RunProcess("project", "create", "Second");
        Assert.Equal(0, created.Code);

        await Eventually(() =>
        {
            lock (changes)
                return changes.Any(x => x.EndsWith("project.yaml") && x.Contains(created.Id.ToString()));
        });

        var projects = await opened.Get<Tasker.Core.Projects.ProjectService>().GetRange(new Tasker.Core.Dto.Page(0, 50));
        Assert.Equal(["First", "Second"], projects.Data.Select(x => x.Name).Order());
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(100);

        Assert.True(condition(), "the change did not arrive in 10 seconds");
    }
}
