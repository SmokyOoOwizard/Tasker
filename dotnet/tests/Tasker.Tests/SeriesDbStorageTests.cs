using Autofac;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tasker.Core;
using Tasker.Core.Dto;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.TaskSeries;
using Tasker.Core.Tasks;
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
using Xunit;

namespace Tasker.Tests;

/// <summary>Серии и номера задач в хранилище БД (SQLite-файл): через интерфейсы хранилищ, без сервисов Core.</summary>
public sealed class SeriesDbStorageTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private IContainer _container = null!;
    private Guid _projectId;
    private Guid _typeId;
    private Guid _statusId;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var builder = new ContainerBuilder();
        builder.RegisterModule(new DbStorageModule(new DbConfigs { SqliteFile = Path.Combine(_root, "tasker.db") }));
        _container = builder.Build();
        await _container.Resolve<IStorageLifecycle>().Start(default);

        await using var scope = _container.BeginLifetimeScope();
        (_projectId, _typeId, _statusId) = await CreateProject(scope, "P");
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static async Task<(Guid Project, Guid Type, Guid Status)> CreateProject(ILifetimeScope scope, string name)
    {
        var project = Guid.NewGuid();
        var status = Guid.NewGuid();
        var set = Guid.NewGuid();
        var type = Guid.NewGuid();
        await scope.Resolve<IProjectStorage>().Add(new Project { Id = project, Name = name, CreatedAt = DateTimeOffset.UtcNow, Version = "" });
        await scope.Resolve<IStatusStorage>().Add(new Status { Id = status, ProjectId = project, Name = "Open", Color = "#000000", Version = "" });
        await scope.Resolve<IStatusSetStorage>().Add(new StatusSet { Id = set, ProjectId = project, Name = "S", StatusIds = [status], Version = "" });
        await scope.Resolve<ITaskTypeStorage>().Add(new TaskType { Id = type, ProjectId = project, Name = "T", StatusSetId = set, Version = "" });
        return (project, type, status);
    }

    private static Series NewSeries(Guid projectId, string prefix, string name = "Name") =>
        new() { Id = Guid.NewGuid(), ProjectId = projectId, Name = name, Prefix = prefix, Version = "" };

    private TaskItem NewTask(Guid? projectId = null, DateTimeOffset? createdAt = null, params TaskSeriesNumber[] numbers) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId ?? _projectId,
        Title = "Task",
        TypeId = _typeId,
        StatusId = _statusId,
        SeriesNumbers = numbers,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        UpdatedAt = createdAt ?? DateTimeOffset.UtcNow,
        Version = ""
    };

    private static readonly Page All = new(0, 100);

    // ---- серии ----

    [Fact]
    public async Task Series_crud_and_version_conflicts()
    {
        await using var scope = _container.BeginLifetimeScope();
        var storage = scope.Resolve<ISeriesStorage>();
        var series = NewSeries(_projectId, "TSK", "Tasks");

        var v1 = await storage.Add(series);
        Assert.Equal(series with { Version = v1 }, await storage.GetById(_projectId, series.Id));
        Assert.Null(await storage.GetById(Guid.NewGuid(), series.Id));

        var v2 = await storage.Update(series with { Name = "New", Prefix = "NEW" }, v1);
        Assert.NotNull(v2);
        Assert.NotEqual(v1, v2);
        Assert.Null(await storage.Update(series with { Name = "Stale" }, v1));
        Assert.Null(await storage.Update(series with { Name = "Junk" }, "junk"));

        var read = await storage.GetById(_projectId, series.Id);
        Assert.Equal("New", read!.Name);
        Assert.Equal("NEW", read.Prefix);
        Assert.Equal(v2, read.Version);

        Assert.False(await storage.Delete(_projectId, series.Id, v1));
        Assert.False(await storage.Delete(_projectId, series.Id, "junk"));
        Assert.True(await storage.Delete(_projectId, series.Id, v2!));
        Assert.Null(await storage.GetById(_projectId, series.Id));
        Assert.Equal(0, await storage.CountUnreadable(_projectId));
    }

    [Fact]
    public async Task Series_are_ordered_by_ordinal_prefix_then_id_and_paged()
    {
        await using var scope = _container.BeginLifetimeScope();
        var storage = scope.Resolve<ISeriesStorage>();
        foreach (var prefix in new[] { "tsk", "B", "TSK", "a", "A1", "Z" })
            await storage.Add(NewSeries(_projectId, prefix));

        // Ordinal: заглавные раньше строчных, цифры раньше букв.
        string[] expected = ["A1", "B", "TSK", "Z", "a", "tsk"];
        Assert.Equal(expected, (await storage.GetAll(_projectId)).Select(x => x.Prefix));

        var page = await storage.GetRange(_projectId, new Page(2, 3));
        Assert.Equal(6, page.TotalCount);
        Assert.Equal(expected[2..5], page.Data.Select(x => x.Prefix));
    }

    [Fact]
    public async Task Series_prefix_is_case_sensitive_and_unique_per_project()
    {
        await using var scope = _container.BeginLifetimeScope();
        var storage = scope.Resolve<ISeriesStorage>();
        await storage.Add(NewSeries(_projectId, "TSK"));
        await storage.Add(NewSeries(_projectId, "tsk"));
        Assert.Equal(2, (await storage.GetAll(_projectId)).Length);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => storage.Add(NewSeries(_projectId, "TSK")));
        Assert.True(IsUnique(error));

        // В другом проекте тот же префикс допустим.
        var (other, _, _) = await CreateProject(scope, "Other");
        await storage.Add(NewSeries(other, "TSK"));
    }

    [Fact]
    public async Task Series_are_isolated_by_project_and_deleted_with_the_project()
    {
        await using var scope = _container.BeginLifetimeScope();
        var storage = scope.Resolve<ISeriesStorage>();
        var (other, _, _) = await CreateProject(scope, "Other");
        await storage.Add(NewSeries(_projectId, "A"));
        await storage.Add(NewSeries(other, "B"));

        Assert.Equal(["A"], (await storage.GetAll(_projectId)).Select(x => x.Prefix));

        var projects = scope.Resolve<IProjectStorage>();
        var project = (await projects.GetById(other))!;
        Assert.True(await projects.Delete(other, project.Version));
        Assert.Empty(await storage.GetAll(other));
        Assert.Single(await storage.GetAll(_projectId));
    }

    // ---- номера задач ----

    [Fact]
    public async Task Task_round_trips_several_numbers_and_update_replaces_them()
    {
        await using var scope = _container.BeginLifetimeScope();
        var tasks = scope.Resolve<ITaskStorage>();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var task = NewTask(numbers: [new(a, 1), new(b, 7)]);
        var version = await tasks.Add(task);

        var read = (await tasks.GetById(_projectId, task.Id))!;
        Assert.Equal(new[] { a, b }.OrderBy(x => x), read.SeriesNumbers.Select(x => x.SeriesId));
        Assert.Contains(new TaskSeriesNumber(a, 1), read.SeriesNumbers);
        Assert.Contains(new TaskSeriesNumber(b, 7), read.SeriesNumbers);

        var list = await tasks.GetRange(_projectId, null, All);
        Assert.Equal(2, list.Data.Single().SeriesNumbers.Count);

        // Замена набора: b снят, a получил другой номер, добавлена c.
        var next = await tasks.Update(read with { SeriesNumbers = [new(a, 2), new(c, 1)] }, version);
        Assert.NotNull(next);
        var updated = (await tasks.GetById(_projectId, task.Id))!;
        Assert.Equal(2, updated.SeriesNumbers.Count);
        Assert.Contains(new TaskSeriesNumber(a, 2), updated.SeriesNumbers);
        Assert.Contains(new TaskSeriesNumber(c, 1), updated.SeriesNumbers);

        // Ещё раз (те же ключи строк) и с пустым набором.
        var again = await tasks.Update(updated with { SeriesNumbers = [new(a, 2)] }, next!);
        Assert.NotNull(again);
        Assert.Single((await tasks.GetById(_projectId, task.Id))!.SeriesNumbers);
        var cleared = await tasks.Update(updated with { SeriesNumbers = [] }, again!);
        Assert.NotNull(cleared);
        Assert.Empty((await tasks.GetById(_projectId, task.Id))!.SeriesNumbers);
    }

    [Fact]
    public async Task Stale_task_update_leaves_numbers_untouched()
    {
        await using var scope = _container.BeginLifetimeScope();
        var tasks = scope.Resolve<ITaskStorage>();
        var series = Guid.NewGuid();
        var task = NewTask(numbers: [new(series, 1)]);
        await tasks.Add(task);

        Assert.Null(await tasks.Update(task with { SeriesNumbers = [new(series, 9)] }, "77"));
        Assert.Equal(1, (await tasks.GetById(_projectId, task.Id))!.SeriesNumbers.Single().Number);
    }

    [Fact]
    public async Task Duplicate_number_violates_the_unique_index()
    {
        await using var scope = _container.BeginLifetimeScope();
        var tasks = scope.Resolve<ITaskStorage>();
        var series = Guid.NewGuid();
        await tasks.Add(NewTask(numbers: [new(series, 1)]));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => tasks.Add(NewTask(numbers: [new(series, 1)])));
        Assert.True(IsUnique(error));

        // Update на занятый номер — тоже; строка задачи при этом не меняется (транзакция откатилась).
        var other = NewTask(numbers: [new(series, 2)]);
        var version = await tasks.Add(other);
        error = await Assert.ThrowsAnyAsync<Exception>(() =>
            tasks.Update(other with { Title = "Changed", SeriesNumbers = [new(series, 1)] }, version));
        Assert.True(IsUnique(error));
        var read = (await tasks.GetById(_projectId, other.Id))!;
        Assert.Equal("Task", read.Title);
        Assert.Equal(2, read.SeriesNumbers.Single().Number);

        // Тот же номер в другой серии или другом проекте — можно.
        await tasks.Add(NewTask(numbers: [new(Guid.NewGuid(), 1)]));
        var (project, _, _) = await CreateProject(scope, "Other");
        await using var inner = _container.BeginLifetimeScope();
        var foreign = NewTask(project, numbers: [new(series, 1)]) with { TypeId = await TypeOf(inner, project), StatusId = await StatusOf(inner, project) };
        await inner.Resolve<ITaskStorage>().Add(foreign);
    }

    private static async Task<Guid> TypeOf(ILifetimeScope scope, Guid project) =>
        (await scope.Resolve<ITaskTypeStorage>().GetAll(project)).Single().Id;

    private static async Task<Guid> StatusOf(ILifetimeScope scope, Guid project) =>
        (await scope.Resolve<IStatusStorage>().GetAll(project)).Single().Id;

    [Fact]
    public async Task Deleting_a_task_removes_its_numbers()
    {
        await using var scope = _container.BeginLifetimeScope();
        var tasks = scope.Resolve<ITaskStorage>();
        var series = Guid.NewGuid();
        var task = NewTask(numbers: [new(series, 3)]);
        var version = await tasks.Add(task);

        Assert.True(await tasks.Delete(_projectId, task.Id, version));

        var context = scope.Resolve<AppDbContext>();
        Assert.Equal(0, await context.TaskSeriesNumbers.CountAsync());
        Assert.Equal(0, await tasks.GetMaxNumber(_projectId, series));
        // Номер свободен снова.
        await tasks.Add(NewTask(numbers: [new(series, 3)]));
    }

    [Fact]
    public async Task Deleting_a_series_leaves_dangling_numbers()
    {
        await using var scope = _container.BeginLifetimeScope();
        var seriesStorage = scope.Resolve<ISeriesStorage>();
        var tasks = scope.Resolve<ITaskStorage>();
        var series = NewSeries(_projectId, "TSK");
        var version = await seriesStorage.Add(series);
        var task = NewTask(numbers: [new(series.Id, 1)]);
        await tasks.Add(task);

        Assert.True(await seriesStorage.Delete(_projectId, series.Id, version));

        Assert.Equal([new TaskSeriesNumber(series.Id, 1)], (await tasks.GetById(_projectId, task.Id))!.SeriesNumbers);
        Assert.Equal(1, await tasks.GetMaxNumber(_projectId, series.Id));
        Assert.Equal([task.Id], (await tasks.GetWithSeriesNotIn(_projectId, [])).Select(x => x.Id));
    }

    [Fact]
    public async Task Deleting_the_project_removes_tasks_and_numbers()
    {
        await using var scope = _container.BeginLifetimeScope();
        var (project, type, status) = await CreateProject(scope, "Doomed");
        var tasks = scope.Resolve<ITaskStorage>();
        await tasks.Add(NewTask(project, numbers: [new(Guid.NewGuid(), 1)]) with { TypeId = type, StatusId = status });

        var projects = scope.Resolve<IProjectStorage>();
        Assert.True(await projects.Delete(project, (await projects.GetById(project))!.Version));
        Assert.Equal(0, await scope.Resolve<AppDbContext>().TaskSeriesNumbers.CountAsync());
    }

    // ---- запросы по номерам ----

    [Fact]
    public async Task Numbering_queries()
    {
        await using var scope = _container.BeginLifetimeScope();
        var tasks = scope.Resolve<ITaskStorage>();
        Guid s1 = Guid.NewGuid(), s2 = Guid.NewGuid(), gone = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddDays(-1);

        Assert.Equal(0, await tasks.GetMaxNumber(_projectId, s1));

        var a = NewTask(createdAt: t0, numbers: [new(s1, 1)]);
        var b = NewTask(createdAt: t0.AddMinutes(1), numbers: [new(s1, 5), new(s2, 2)]);
        var c = NewTask(createdAt: t0.AddMinutes(2), numbers: [new(gone, 9)]);
        var d = NewTask(createdAt: t0.AddMinutes(3));
        foreach (var task in new[] { d, c, b, a })
            await tasks.Add(task);

        Assert.Equal(5, await tasks.GetMaxNumber(_projectId, s1));
        Assert.Equal(2, await tasks.GetMaxNumber(_projectId, s2));
        Assert.Equal(9, await tasks.GetMaxNumber(_projectId, gone));
        Assert.Equal(0, await tasks.GetMaxNumber(Guid.NewGuid(), s1));

        Assert.Equal([b.Id], (await tasks.FindByNumber(_projectId, s1, 5)).Select(x => x.Id));
        Assert.Equal(2, (await tasks.FindByNumber(_projectId, s1, 5))[0].SeriesNumbers.Count);
        Assert.Empty(await tasks.FindByNumber(_projectId, s1, 2));
        Assert.Empty(await tasks.FindByNumber(_projectId, s2, 1));

        Assert.Empty(await tasks.GetNumberConflicts(_projectId));

        // Пустой набор известных серий: недействительны все ссылки.
        Assert.Equal([a.Id, b.Id, c.Id], (await tasks.GetWithSeriesNotIn(_projectId, [])).Select(x => x.Id));
        Assert.Equal([c.Id], (await tasks.GetWithSeriesNotIn(_projectId, [s1, s2])).Select(x => x.Id));
        Assert.Equal([b.Id, c.Id], (await tasks.GetWithSeriesNotIn(_projectId, [s1])).Select(x => x.Id));
        Assert.Empty(await tasks.GetWithSeriesNotIn(_projectId, [s1, s2, gone]));
        Assert.Empty(await tasks.GetWithSeriesNotIn(Guid.NewGuid(), []));
    }

    [Fact]
    public async Task Number_conflicts_are_found_in_a_database_without_the_unique_index()
    {
        await using var scope = _container.BeginLifetimeScope();
        var tasks = scope.Resolve<ITaskStorage>();
        var context = scope.Resolve<AppDbContext>();
        await context.Database.ExecuteSqlRawAsync("DROP INDEX IX_task_series_numbers_ProjectId_SeriesId_Number");

        Guid s1 = Guid.NewGuid(), s2 = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddDays(-1);
        var late = NewTask(createdAt: t0.AddMinutes(5), numbers: [new(s1, 4)]);
        var early = NewTask(createdAt: t0, numbers: [new(s1, 4)]);
        var third = NewTask(createdAt: t0.AddMinutes(9), numbers: [new(s1, 4), new(s2, 1)]);
        var other = NewTask(createdAt: t0, numbers: [new(s2, 1), new(s1, 3)]);
        var single = NewTask(numbers: [new(s2, 2)]);
        foreach (var task in new[] { late, early, third, other, single })
            await tasks.Add(task);

        var conflicts = await tasks.GetNumberConflicts(_projectId);
        var ordered = new[] { s1, s2 }.Order().ToArray();
        Assert.Equal(2, conflicts.Length);
        Assert.Equal((s1 == ordered[0]) ? new[] { (s1, 4), (s2, 1) } : new[] { (s2, 1), (s1, 4) },
            conflicts.Select(x => (x.SeriesId, x.Number)));
        Assert.Equal([early.Id, late.Id, third.Id], conflicts.Single(x => x.SeriesId == s1).TaskIds);
        Assert.Equal([other.Id, third.Id], conflicts.Single(x => x.SeriesId == s2).TaskIds);
        Assert.Equal([early.Id, late.Id, third.Id], (await tasks.FindByNumber(_projectId, s1, 4)).Select(x => x.Id));
    }

    [Fact]
    public async Task Filter_by_series_pages_and_counts()
    {
        await using var scope = _container.BeginLifetimeScope();
        var tasks = scope.Resolve<ITaskStorage>();
        Guid s1 = Guid.NewGuid(), s2 = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddDays(-1);
        var created = new List<TaskItem>();
        for (var i = 0; i < 5; i++)
            created.Add(NewTask(createdAt: t0.AddMinutes(i), numbers: i % 2 == 0 ? [new(s1, i + 1)] : [new(s2, i + 1)]));
        created.Add(NewTask(createdAt: t0.AddMinutes(9), numbers: [new(s1, 10), new(s2, 10)]));
        created.Add(NewTask(createdAt: t0.AddMinutes(10)));
        foreach (var task in created)
            await tasks.Add(task);

        var filter = new TaskFilter { SeriesIds = [s1] };
        Assert.Equal(4, await tasks.Count(_projectId, filter));
        var page = await tasks.GetRange(_projectId, filter, new Page(1, 2));
        Assert.Equal(4, page.TotalCount);
        Assert.Equal([created[2].Id, created[4].Id], page.Data.Select(x => x.Id));

        Assert.Equal(6, await tasks.Count(_projectId, new TaskFilter { SeriesIds = [s1, s2] }));
        Assert.Equal(0, await tasks.Count(_projectId, new TaskFilter { SeriesIds = [] }));
        Assert.Equal(7, await tasks.Count(_projectId, new TaskFilter()));
        Assert.Equal(1, await tasks.Count(_projectId, new TaskFilter { SeriesIds = [s1], StatusIds = [_statusId], TypeIds = [_typeId] }) - 3);
    }

    // ---- IWriteScope ----

    [Fact]
    public async Task Exclusive_gives_unique_consecutive_numbers_under_parallel_load()
    {
        var series = Guid.NewGuid();
        const int count = 20;

        var numbers = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
        {
            await using var scope = _container.BeginLifetimeScope();
            var tasks = scope.Resolve<ITaskStorage>();
            return await scope.Resolve<IWriteScope>().Exclusive(_projectId, async () =>
            {
                var number = await tasks.GetMaxNumber(_projectId, series) + 1;
                await tasks.Add(NewTask(numbers: [new(series, number)]));
                return number;
            });
        })));

        Assert.Equal(Enumerable.Range(1, count), numbers.Order());
        await using var check = _container.BeginLifetimeScope();
        Assert.Equal(count, await check.Resolve<ITaskStorage>().GetMaxNumber(_projectId, series));
        Assert.Empty(await check.Resolve<ITaskStorage>().GetNumberConflicts(_projectId));
    }

    [Fact]
    public async Task Exclusive_retries_after_a_unique_violation_and_rolls_back_the_failed_attempt()
    {
        await using var scope = _container.BeginLifetimeScope();
        var tasks = scope.Resolve<ITaskStorage>();
        var series = Guid.NewGuid();
        var attempts = 0;
        var survivor = NewTask(numbers: [new(series, 1)]);

        var result = await scope.Resolve<IWriteScope>().Exclusive(_projectId, async () =>
        {
            attempts++;
            await tasks.Add(survivor);
            if (attempts == 1)
                await tasks.Add(NewTask(numbers: [new(series, 1)])); // нарушает уникальный индекс
            return attempts;
        });

        Assert.Equal(2, result);
        Assert.Equal([survivor.Id], (await tasks.GetRange(_projectId, null, All)).Data.Select(x => x.Id));
        Assert.Equal(1, await tasks.GetMaxNumber(_projectId, series));
    }

    [Fact]
    public async Task Exclusive_rethrows_after_bounded_attempts_and_other_errors_are_not_retried()
    {
        await using var scope = _container.BeginLifetimeScope();
        var tasks = scope.Resolve<ITaskStorage>();
        var writes = scope.Resolve<IWriteScope>();
        var series = Guid.NewGuid();
        await tasks.Add(NewTask(numbers: [new(series, 1)]));

        var attempts = 0;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => writes.Exclusive<int>(_projectId, async () =>
        {
            attempts++;
            await tasks.Add(NewTask(numbers: [new(series, 1)]));
            return 0;
        }));
        Assert.True(IsUnique(error));
        Assert.Equal(5, attempts);

        attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => writes.Exclusive<int>(_projectId, () =>
        {
            attempts++;
            throw new InvalidOperationException("boom");
        }));
        Assert.Equal(1, attempts);

        // После сбоев контекст и соединение в порядке.
        Assert.Equal(1, (await tasks.GetRange(_projectId, null, All)).TotalCount);
        Assert.Equal(7, await writes.Exclusive(_projectId, () => Task.FromResult(7)));
    }

    [Fact]
    public async Task Exclusive_can_be_nested_and_runs_storages_in_one_transaction()
    {
        await using var scope = _container.BeginLifetimeScope();
        var tasks = scope.Resolve<ITaskStorage>();
        var writes = scope.Resolve<IWriteScope>();
        var series = Guid.NewGuid();
        var context = scope.Resolve<AppDbContext>();

        var outerAttempts = 0;
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => writes.Exclusive<int>(_projectId, async () =>
        {
            outerAttempts++;
            Assert.NotNull(context.Database.CurrentTransaction);
            return await writes.Exclusive<int>(_projectId, async () =>
            {
                await tasks.Add(NewTask(numbers: [new(series, 1)]));
                throw new InvalidOperationException("boom");
            });
        }));
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, outerAttempts);
        // Внешняя транзакция откатила и внутренние записи.
        Assert.Equal(0, (await tasks.GetRange(_projectId, null, All)).TotalCount);

        var number = await writes.Exclusive<int>(_projectId, () => writes.Exclusive<int>(_projectId, async () =>
        {
            var next = await tasks.GetMaxNumber(_projectId, series) + 1;
            await tasks.Add(NewTask(numbers: [new(series, next)]));
            return next;
        }));
        Assert.Equal(1, number);
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(1, await tasks.GetMaxNumber(_projectId, series));
    }

    private static bool IsUnique(Exception e)
    {
        for (var current = e; current != null; current = current.InnerException)
            if (current is SqliteException { SqliteExtendedErrorCode: 2067 })
                return true;
        return false;
    }
}
