using System.Diagnostics;
using Autofac;
using Microsoft.Data.Sqlite;
using Tasker.Core.Dto;
using Tasker.Core.TaskSeries;
using Tasker.Core.Tasks;
using Tasker.Storage.Files;
using Tasker.Storage.Files.Index;
using Tasker.Storage.Files.Storages;
using Xunit;

namespace Tasker.Tests;

/// <summary>Номера задач в индексе файлового хранилища, <see cref="IWriteScope"/> и порядок блокировок.</summary>
public class SeriesIndexTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    private readonly FilesHarness _h = new();
    private readonly Guid _s1 = Guid.NewGuid();
    private readonly Guid _s2 = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Dispose() => _h.Dispose();

    private static TaskSeriesNumber N(Guid series, int number) => new(series, number);

    [Fact]
    public async Task GetMaxNumber_is_zero_without_tasks_and_counts_dangling_series()
    {
        Assert.Equal(0, await _h.Tasks.GetMaxNumber(_h.ProjectId, _s1));

        // Серий в хранилище нет вовсе: ссылки недействительные, но номер они занимают.
        await _h.Tasks.Add(_h.NewTask(numbers: [N(_s1, 3), N(_s2, 9)]));
        await _h.Tasks.Add(_h.NewTask(numbers: [N(_s1, 5)]));
        await _h.Tasks.Add(_h.NewTask());

        Assert.Equal(5, await _h.Tasks.GetMaxNumber(_h.ProjectId, _s1));
        Assert.Equal(9, await _h.Tasks.GetMaxNumber(_h.ProjectId, _s2));
        Assert.Equal(0, await _h.Tasks.GetMaxNumber(_h.ProjectId, Guid.NewGuid()));
        Assert.Equal(0, await _h.Tasks.GetMaxNumber(Guid.NewGuid(), _s1));
    }

    [Fact]
    public async Task Numbers_follow_task_updates_and_deletes()
    {
        var task = _h.NewTask(numbers: [N(_s1, 4)]);
        var version = await _h.Tasks.Add(task);
        Assert.Equal(4, await _h.Tasks.GetMaxNumber(_h.ProjectId, _s1));

        version = (await _h.Tasks.Update(task with { SeriesNumbers = [N(_s1, 2), N(_s2, 1)] }, version))!;
        Assert.Equal(2, await _h.Tasks.GetMaxNumber(_h.ProjectId, _s1));
        Assert.Equal(1, await _h.Tasks.GetMaxNumber(_h.ProjectId, _s2));

        Assert.True(await _h.Tasks.Delete(_h.ProjectId, task.Id, version));
        Assert.Equal(0, await _h.Tasks.GetMaxNumber(_h.ProjectId, _s1));
        Assert.Empty(await _h.Tasks.FindByNumber(_h.ProjectId, _s2, 1));
    }

    [Fact]
    public async Task FindByNumber_returns_all_tasks_ordered_by_created_then_id()
    {
        var late = _h.NewTask("late", T0.AddDays(2), N(_s1, 1));
        var early = _h.NewTask("early", T0, N(_s1, 1));
        var tieA = _h.NewTask("tieA", T0.AddDays(1), N(_s1, 1));
        var tieB = _h.NewTask("tieB", T0.AddDays(1), N(_s1, 1));
        var other = _h.NewTask("other", T0, N(_s1, 2), N(_s2, 1));
        foreach (var task in new[] { late, tieA, other, early, tieB })
            await _h.Tasks.Add(task);

        var tie = new[] { tieA, tieB }.OrderBy(x => x.Id.ToString(), StringComparer.Ordinal).Select(x => x.Id);
        var found = await _h.Tasks.FindByNumber(_h.ProjectId, _s1, 1);
        Assert.Equal([early.Id, .. tie, late.Id], found.Select(x => x.Id));
        Assert.All(found, x => Assert.Equal(N(_s1, 1), Assert.Single(x.SeriesNumbers)));

        Assert.Equal(other.Id, Assert.Single(await _h.Tasks.FindByNumber(_h.ProjectId, _s2, 1)).Id);
        Assert.Empty(await _h.Tasks.FindByNumber(_h.ProjectId, _s2, 2));
        Assert.Empty(await _h.Tasks.FindByNumber(Guid.NewGuid(), _s1, 1));
    }

    [Fact]
    public async Task GetNumberConflicts_groups_tasks_sharing_a_number()
    {
        Assert.Empty(await _h.Tasks.GetNumberConflicts(_h.ProjectId));

        var first = _h.NewTask("first", T0.AddDays(1), N(_s1, 5));
        var second = _h.NewTask("second", T0, N(_s1, 5), N(_s2, 2));
        var third = _h.NewTask("third", T0.AddDays(2), N(_s1, 5));
        var pairA = _h.NewTask("pairA", T0, N(_s2, 2));
        var single = _h.NewTask("single", T0, N(_s1, 6));
        var noSeries = _h.NewTask("none");
        foreach (var task in new[] { first, second, third, pairA, single, noSeries })
            await _h.Tasks.Add(task);
        // Конфликты другого проекта не видны.
        var foreign = _h.NewTask("foreign", T0, N(_s1, 5)) with { ProjectId = Guid.NewGuid() };
        await _h.Tasks.Add(foreign);

        var conflicts = await _h.Tasks.GetNumberConflicts(_h.ProjectId);
        var expected = new[]
        {
            new NumberConflict(_s1, 5, [second.Id, first.Id, third.Id]),
            new NumberConflict(_s2, 2, new[] { second, pairA }.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id.ToString(), StringComparer.Ordinal).Select(x => x.Id).ToArray())
        }.OrderBy(x => x.SeriesId.ToString(), StringComparer.Ordinal).ThenBy(x => x.Number).ToArray();

        Assert.Equal(expected.Length, conflicts.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].SeriesId, conflicts[i].SeriesId);
            Assert.Equal(expected[i].Number, conflicts[i].Number);
            Assert.Equal(expected[i].TaskIds, conflicts[i].TaskIds);
        }
    }

    [Fact]
    public async Task GetNumberConflicts_ignores_a_task_that_repeats_the_same_number_itself()
    {
        var file = _h.NewTask("hand edited", T0, N(_s1, 1), N(_s1, 1));
        await _h.Tasks.Add(file);
        Assert.Empty(await _h.Tasks.GetNumberConflicts(_h.ProjectId));
    }

    [Fact]
    public async Task GetWithSeriesNotIn_finds_dangling_references_ordered_by_created_then_id()
    {
        var late = _h.NewTask("late", T0.AddDays(1), N(_s1, 1), N(_s2, 1));
        var early = _h.NewTask("early", T0, N(_s2, 4));
        var clean = _h.NewTask("clean", T0, N(_s1, 2));
        var none = _h.NewTask("none");
        foreach (var task in new[] { late, clean, none, early })
            await _h.Tasks.Add(task);

        Assert.Equal([early.Id, late.Id], (await _h.Tasks.GetWithSeriesNotIn(_h.ProjectId, [_s1])).Select(x => x.Id));
        Assert.Equal([clean.Id, late.Id], (await _h.Tasks.GetWithSeriesNotIn(_h.ProjectId, [_s2])).Select(x => x.Id));
        Assert.Equal(new[] { early.Id, late.Id, clean.Id }.Order(), (await _h.Tasks.GetWithSeriesNotIn(_h.ProjectId, [])).Select(x => x.Id).Order());
        Assert.Empty(await _h.Tasks.GetWithSeriesNotIn(_h.ProjectId, [_s1, _s2]));
    }

    [Fact]
    public async Task Tasks_with_unreadable_files_do_not_take_part_in_numbering()
    {
        await _h.Tasks.Add(_h.NewTask(numbers: [N(_s1, 2)]));

        // Тот же файл-задача с конфликтом слияния и номером 9, и файл с чужим id в имени.
        var broken = Guid.NewGuid();
        await File.WriteAllTextAsync(_h.Project.LegacyFile(EntityFolders.Tasks, broken),
            $"<<<<<<< HEAD\nid: {broken}\ntitle: x\nseries:\n- seriesId: {_s1}\n  number: 9\n=======\nid: {broken}\n>>>>>>> b\n");
        var wrongId = Guid.NewGuid();
        var other = _h.NewTask(createdAt: T0, numbers: N(_s1, 2)) with { Id = Guid.NewGuid() };
        await _h.Tasks.Add(other);
        await File.WriteAllTextAsync(_h.Project.LegacyFile(EntityFolders.Tasks, wrongId),
            (await File.ReadAllTextAsync(_h.Project.FindTaskFile(other.Id)!)));
        await _h.Container.Resolve<WorkspaceIndex>().Rescan();

        Assert.Equal(2, await _h.Tasks.GetMaxNumber(_h.ProjectId, _s1));
        Assert.Equal(2, (await _h.Tasks.FindByNumber(_h.ProjectId, _s1, 2)).Length);
        Assert.Empty(await _h.Tasks.FindByNumber(_h.ProjectId, _s1, 9));
        Assert.Single(await _h.Tasks.GetNumberConflicts(_h.ProjectId));
        Assert.Empty(await _h.Tasks.GetWithSeriesNotIn(_h.ProjectId, [_s1]));
        Assert.Equal(2, await _h.Tasks.Count(_h.ProjectId, new TaskFilter { SeriesIds = [_s1] }));

        // Файл починили: номер сразу учитывается.
        await File.WriteAllTextAsync(_h.Project.LegacyFile(EntityFolders.Tasks, broken),
            $"id: {broken}\ntitle: x\ntypeId: {Guid.NewGuid()}\nstatusId: {Guid.NewGuid()}\ncreatedAt: 2026-01-01T00:00:00.0000000+00:00\nupdatedAt: 2026-01-01T00:00:00.0000000+00:00\nseries:\n- seriesId: {_s1}\n  number: 9\n");
        await _h.Container.Resolve<WorkspaceIndex>().Rescan();
        Assert.Equal(9, await _h.Tasks.GetMaxNumber(_h.ProjectId, _s1));
    }

    [Fact]
    public async Task TaskFilter_SeriesIds_pages_and_counts()
    {
        var inS1 = Enumerable.Range(0, 7).Select(i => _h.NewTask("a" + i, T0.AddMinutes(i), N(_s1, i + 1))).ToList();
        var inBoth = _h.NewTask("both", T0.AddMinutes(100), N(_s1, 8), N(_s2, 1));
        var inS2 = _h.NewTask("b", T0.AddMinutes(50), N(_s2, 2));
        var without = _h.NewTask("c", T0);
        foreach (var task in inS1.Append(inBoth).Append(inS2).Append(without))
            await _h.Tasks.Add(task);

        var filter = new TaskFilter { SeriesIds = [_s1] };
        Assert.Equal(8, await _h.Tasks.Count(_h.ProjectId, filter));
        var page = await _h.Tasks.GetRange(_h.ProjectId, filter, new Page { Offset = 6, Limit = 5 });
        Assert.Equal(8, page.TotalCount);
        Assert.Equal([inS1[6].Id, inBoth.Id], page.Data.Select(x => x.Id));

        // Задача в двух сериях попадает в выборку один раз.
        var both = new TaskFilter { SeriesIds = [_s1, _s2] };
        Assert.Equal(9, await _h.Tasks.Count(_h.ProjectId, both));
        Assert.Equal(9, (await _h.Tasks.GetRange(_h.ProjectId, both, new Page { Limit = 50 })).Data.Length);

        Assert.Equal(0, await _h.Tasks.Count(_h.ProjectId, new TaskFilter { SeriesIds = [] }));
        Assert.Equal(0, await _h.Tasks.Count(_h.ProjectId, new TaskFilter { SeriesIds = [Guid.NewGuid()] }));
        Assert.Equal(10, await _h.Tasks.Count(_h.ProjectId, new TaskFilter()));
        Assert.Equal(1, await _h.Tasks.Count(_h.ProjectId, new TaskFilter { SeriesIds = [_s2], TypeIds = [inS2.TypeId] }));
    }

    [Fact]
    public async Task Exclusive_sees_task_files_that_arrived_behind_the_index()
    {
        // Индекс уже открыт и о новом файле не знает (наблюдатель не запущен), как после git pull.
        Assert.Equal(0, await _h.Tasks.GetMaxNumber(_h.ProjectId, _s1));

        var handmade = Guid.NewGuid();
        Directory.CreateDirectory(_h.Project.Tasks);
        await File.WriteAllTextAsync(_h.Project.LegacyFile(EntityFolders.Tasks, handmade),
            $"id: {handmade}\ntitle: pulled\ntypeId: {Guid.NewGuid()}\nstatusId: {Guid.NewGuid()}\ncreatedAt: 2026-01-01T00:00:00.0000000+00:00\nupdatedAt: 2026-01-01T00:00:00.0000000+00:00\nseries:\n- seriesId: {_s1}\n  number: 41\n");
        Assert.Equal(0, await _h.Tasks.GetMaxNumber(_h.ProjectId, _s1));

        var seen = await _h.Scope.Exclusive(_h.ProjectId, () => _h.Tasks.GetMaxNumber(_h.ProjectId, _s1));
        Assert.Equal(41, seen);
    }

    [Fact]
    public async Task Exclusive_can_be_nested_and_writes_inside_do_not_deadlock()
    {
        var result = await _h.Scope.Exclusive(_h.ProjectId, async () =>
        {
            var task = _h.NewTask(numbers: N(_s1, 1));
            var version = await _h.Tasks.Add(task);
            return await _h.Scope.Exclusive(_h.ProjectId, async () =>
            {
                await _h.Series.Add(_h.NewSeries("X"));
                return await _h.Tasks.Update(task with { Title = "changed" }, version) != null;
            });
        }).WaitAsync(Limit);

        Assert.True(result);
    }

    [Fact]
    public async Task Exclusive_gives_unique_consecutive_numbers_under_parallelism_from_two_containers()
    {
        var second = _h.Open();
        var scopes = new[] { _h.Scope, second.Resolve<IWriteScope>() };
        var storages = new[] { _h.Tasks, second.Resolve<ITaskStorage>() };
        const int perContainer = 40;

        var work = Enumerable.Range(0, perContainer * 2).Select(i => Task.Run(async () =>
        {
            var c = i % 2;
            await scopes[c].Exclusive(_h.ProjectId, async () =>
            {
                var next = await storages[c].GetMaxNumber(_h.ProjectId, _s1) + 1;
                await storages[c].Add(_h.NewTask("t" + i, numbers: N(_s1, next)));
                return next;
            });
        })).ToArray();
        await Task.WhenAll(work).WaitAsync(Limit);

        var numbers = (await _h.Tasks.GetRange(_h.ProjectId, null, new Page { Limit = 200 })).Data
            .Select(x => Assert.Single(x.SeriesNumbers).Number).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, perContainer * 2), numbers);
        Assert.Empty(await _h.Tasks.GetNumberConflicts(_h.ProjectId));
    }

    [Fact]
    public async Task Exclusive_waits_for_another_process_holding_the_write_lock()
    {
        if (OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(_h.Directory_.Cache);
        using var holder = TaskerProcess.StartHolder(_h.Directory_.WriteLock);
        await holder.WaitUntilHeld();

        var entered = false;
        var run = _h.Scope.Exclusive(_h.ProjectId, () =>
        {
            entered = true;
            return Task.FromResult(1);
        });
        await Task.Delay(700);
        Assert.False(entered);
        Assert.False(run.IsCompleted);

        // Запись файла тоже стоит на той же блокировке.
        var write = _h.Tasks.Add(_h.NewTask());
        await Task.Delay(300);
        Assert.False(write.IsCompleted);

        holder.Release();
        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(20)));
        await write.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(entered);
    }

    [Fact]
    public void Write_lock_path_is_defined_once()
    {
        Assert.Equal(Path.Combine(_h.Directory_.Root, ".cache", "write.lock"), _h.Directory_.WriteLock);
        Assert.Equal(_h.Directory_.WriteLock, TaskerDirectory.WriteLockOf(_h.Directory_.Root));
    }

    [Fact]
    public async Task Exclusive_and_plain_writes_together_do_not_deadlock()
    {
        // Записи хранилищ берут блокировку записи, затем очередь файла; Exclusive держит блокировку записи
        // и внутри пишет те же файлы. Порядок один — иначе эти два вида операций взаимно блокируются.
        var second = _h.Open();
        var tasks = new List<TaskItem>();
        var versions = new Dictionary<Guid, string>();
        for (var i = 0; i < 4; i++)
        {
            var task = _h.NewTask("t" + i);
            tasks.Add(task);
            versions[task.Id] = await _h.Tasks.Add(task);
        }

        var stop = Stopwatch.StartNew();
        var operations = 0;

        async Task PlainWorker(ITaskStorage storage, int seed)
        {
            var random = new Random(seed);
            while (stop.Elapsed < TimeSpan.FromSeconds(4))
            {
                var task = tasks[random.Next(tasks.Count)];
                var current = await storage.GetById(_h.ProjectId, task.Id);
                await storage.Update(current! with { Title = "p" + random.Next() }, current.Version);
                Interlocked.Increment(ref operations);
            }
        }

        async Task ScopedWorker(IWriteScope scope, ITaskStorage storage, int seed)
        {
            var random = new Random(seed);
            while (stop.Elapsed < TimeSpan.FromSeconds(4))
            {
                var task = tasks[random.Next(tasks.Count)];
                await scope.Exclusive(_h.ProjectId, async () =>
                {
                    var current = await storage.GetById(_h.ProjectId, task.Id);
                    var next = await storage.GetMaxNumber(_h.ProjectId, _s1) + 1;
                    await storage.Update(current! with { Title = "e" + next, SeriesNumbers = [N(_s1, next)] }, current.Version);
                    return 0;
                });
                Interlocked.Increment(ref operations);
            }
        }

        var workers = new List<Task>();
        for (var n = 0; n < 3; n++)
        {
            var i = n;
            workers.Add(Task.Run(() => PlainWorker(_h.Tasks, i)));
            workers.Add(Task.Run(() => ScopedWorker(_h.Scope, _h.Tasks, 100 + i)));
            workers.Add(Task.Run(() => PlainWorker(second.Resolve<ITaskStorage>(), 200 + i)));
            workers.Add(Task.Run(() => ScopedWorker(second.Resolve<IWriteScope>(), second.Resolve<ITaskStorage>(), 300 + i)));
        }

        await Task.WhenAll(workers).WaitAsync(Limit);
        Assert.True(operations > 20, $"only {operations} operations");
    }

    [Fact]
    public async Task An_index_of_an_older_schema_is_rebuilt()
    {
        await _h.Tasks.Add(_h.NewTask(numbers: [N(_s1, 3), N(_s2, 8)]));
        await _h.Series.Add(_h.NewSeries("OLD"));
        var indexFile = _h.Directory_.IndexFile;
        _h.CloseContainers();

        // Индекс прежней версии: другая схема, без таблицы номеров.
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            File.Delete(indexFile + suffix);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = indexFile, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE files (path TEXT PRIMARY KEY, junk TEXT); INSERT INTO files VALUES ('x', 'y'); PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }

        _h.Open();
        Assert.Equal(3, await _h.Tasks.GetMaxNumber(_h.ProjectId, _s1));
        Assert.Equal(8, await _h.Tasks.GetMaxNumber(_h.ProjectId, _s2));
        Assert.Equal("OLD", Assert.Single(await _h.Series.GetAll(_h.ProjectId)).Prefix);
    }
}
