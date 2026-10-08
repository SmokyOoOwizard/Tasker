using Autofac;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core;
using Tasker.Core.Locks;
using Tasker.Core.Tasks;
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Storage.Files;
using Tasker.Tests.SeriesCore;
using Xunit;

namespace Tasker.Tests;

/// <summary>Блокировка на время правки в сервисах Core: кто может писать, пока запись занята.</summary>
public class EditLockServiceTests
{
    private static readonly EditHolder Ivan = FakeEditor.Ivan;
    private static readonly EditHolder Anna = FakeEditor.Anna;

    [Fact]
    public async Task Holder_can_acquire_and_renew_their_own_lock()
    {
        var env = new SeriesEnv();
        var id = Guid.NewGuid();

        var first = await env.Locks.Acquire(LockedEntity.Task, id, "Task 'A'");
        env.Clock.Now += TimeSpan.FromMinutes(1);
        var renewed = await env.Locks.Acquire(LockedEntity.Task, id, "Task 'A'");

        Assert.Equal(Ivan, first.Holder);
        Assert.Equal(first.AcquiredAt, renewed.AcquiredAt);
        Assert.Equal(env.Clock.Now + EditLockService.Duration, renewed.ExpiresAt);
    }

    [Fact]
    public async Task Another_holder_is_rejected_and_told_who_holds_the_lock()
    {
        var env = new SeriesEnv();
        var id = Guid.NewGuid();
        await env.Locks.Acquire(LockedEntity.Task, id, "Task 'A'");

        env.Editor.Holder = Anna;
        var error = await Assert.ThrowsAsync<TaskerLockedException>(() => env.Locks.Acquire(LockedEntity.Task, id, "Task 'A'"));

        Assert.Equal(ConflictCode.Locked, error.Code);
        Assert.Equal(Ivan, error.HeldBy.Holder);
        Assert.Equal("Task 'A' is being edited by Ivan", error.Message);
    }

    [Fact]
    public async Task Lock_expires_and_can_then_be_taken_by_another()
    {
        var env = new SeriesEnv();
        var id = Guid.NewGuid();
        await env.Locks.Acquire(LockedEntity.Task, id, "Task 'A'");

        env.Clock.Now += EditLockService.Duration + TimeSpan.FromSeconds(1);
        env.Editor.Holder = Anna;

        Assert.Null(await env.Locks.Get(LockedEntity.Task, id));
        Assert.Equal(Anna, (await env.Locks.Acquire(LockedEntity.Task, id, "Task 'A'")).Holder);
    }

    [Fact]
    public async Task Only_the_holder_can_release()
    {
        var env = new SeriesEnv();
        var id = Guid.NewGuid();
        await env.Locks.Acquire(LockedEntity.Task, id, "Task 'A'");

        env.Editor.Holder = Anna;
        Assert.False(await env.Locks.Release(LockedEntity.Task, id));
        Assert.NotNull(await env.Locks.Get(LockedEntity.Task, id));

        env.Editor.Holder = Ivan;
        Assert.True(await env.Locks.Release(LockedEntity.Task, id));
        Assert.Null(await env.Locks.Get(LockedEntity.Task, id));
    }

    [Fact]
    public async Task Locks_of_different_entities_do_not_affect_each_other()
    {
        var env = new SeriesEnv();
        var id = Guid.NewGuid();
        await env.Locks.Acquire(LockedEntity.Task, id, "Task");

        env.Editor.Holder = Anna;
        await env.Locks.Acquire(LockedEntity.Status, id, "Status");
        await env.Locks.EnsureWritable(LockedEntity.Board, id, "Board");
    }

    [Fact]
    public async Task Task_update_is_rejected_while_someone_else_edits_it_and_passes_otherwise()
    {
        var env = new SeriesEnv();
        var task = await env.Create("Fix login");
        await env.Locks.Acquire(LockedEntity.Task, task.Id, "Task");

        env.Editor.Holder = Anna;
        var error = await Assert.ThrowsAsync<TaskerLockedException>(() =>
            env.TaskSvc.Update(env.Project, task.Id, new UpdateTask("Other", null, null, null, task.Version)));
        Assert.Contains("Ivan", error.Message);
        Assert.Equal("Fix login", env.Get(task.Id).Title);

        // Свой вызов проходит под своей блокировкой, и она остаётся за держателем.
        env.Editor.Holder = Ivan;
        var updated = await env.TaskSvc.Update(env.Project, task.Id, new UpdateTask("Fixed", null, null, null, task.Version));
        Assert.Equal("Fixed", updated!.Title);
        Assert.Equal(Ivan, (await env.Locks.Get(LockedEntity.Task, task.Id))!.Holder);
    }

    [Fact]
    public async Task Free_entity_is_written_without_taking_a_lock()
    {
        var env = new SeriesEnv();
        var task = await env.Create("A");

        await env.TaskSvc.Update(env.Project, task.Id, new UpdateTask("B", null, null, null, task.Version));

        Assert.Null(await env.Locks.Get(LockedEntity.Task, task.Id));
    }

    [Fact]
    public async Task Expired_lock_does_not_block_writes()
    {
        var env = new SeriesEnv();
        var task = await env.Create("A");
        await env.Locks.Acquire(LockedEntity.Task, task.Id, "Task");

        env.Editor.Holder = Anna;
        env.Clock.Now += EditLockService.Duration + TimeSpan.FromSeconds(1);

        var updated = await env.TaskSvc.Update(env.Project, task.Id, new UpdateTask("B", null, null, null, task.Version));
        Assert.Equal("B", updated!.Title);
    }

    [Fact]
    public async Task Task_delete_is_rejected_for_others_and_removes_the_lock_for_the_holder()
    {
        var env = new SeriesEnv();
        var task = await env.Create("A");
        await env.Locks.Acquire(LockedEntity.Task, task.Id, "Task");

        env.Editor.Holder = Anna;
        await Assert.ThrowsAsync<TaskerLockedException>(() => env.TaskSvc.Delete(env.Project, task.Id, task.Version));
        Assert.NotNull(env.Tasks.All().SingleOrDefault(x => x.Id == task.Id));

        env.Editor.Holder = Ivan;
        Assert.True(await env.TaskSvc.Delete(env.Project, task.Id, task.Version));
        Assert.Null(await env.Locks.Get(LockedEntity.Task, task.Id));
    }

    [Fact]
    public async Task Series_operations_on_a_locked_task_are_rejected()
    {
        var env = new SeriesEnv();
        var series = env.AddSeries("TSK");
        var task = await env.Create("A", series.Id);
        await env.Locks.Acquire(LockedEntity.Task, task.Id, "Task");
        env.Editor.Holder = Anna;

        await Assert.ThrowsAsync<TaskerLockedException>(() => env.TaskSvc.RemoveFromSeries(env.Project, task.Id, series.Id, task.Version));
        await Assert.ThrowsAsync<TaskerLockedException>(() => env.TaskSvc.RemoveFromSeriesByNumber(env.Project, series.Id, 1));
        await Assert.ThrowsAsync<TaskerLockedException>(() => env.TaskSvc.Renumber(env.Project, task.Id, series.Id, 5, task.Version));
        Assert.Single(env.Get(task.Id).SeriesNumbers);
    }

    [Fact]
    public async Task Series_update_is_rejected_while_someone_else_edits_it()
    {
        var env = new SeriesEnv();
        var series = env.AddSeries("TSK");
        await env.Locks.Acquire(LockedEntity.Series, series.Id, "Series");

        env.Editor.Holder = Anna;
        await Assert.ThrowsAsync<TaskerLockedException>(() =>
            env.SeriesSvc.Update(env.Project, series.Id, new Tasker.Core.TaskSeries.UpdateSeries("New", null, series.Version)));
        await Assert.ThrowsAsync<TaskerLockedException>(() => env.SeriesSvc.Delete(env.Project, series.Id, series.Version));
    }
}

/// <summary>
/// Одно поведение для всех хранилищ блокировок: файлового и БД. <see cref="Run"/> выполняет вызов так, как его
/// выполнил бы отдельный запрос (БД — свой скоуп; файлы — свой контейнер, как другой процесс).
/// </summary>
public abstract class EditLockStorageContract : IAsyncLifetime
{
    protected static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    protected static readonly TimeSpan Span = TimeSpan.FromMinutes(2);
    protected static readonly EditHolder Ivan = FakeEditor.Ivan;
    protected static readonly EditHolder Anna = FakeEditor.Anna;

    public abstract Task InitializeAsync();
    public abstract Task DisposeAsync();

    protected abstract Task<T> Run<T>(Func<IEditLockStorage, Task<T>> action);

    private Task<EditLock> Acquire(Guid id, EditHolder holder, DateTimeOffset now, LockedEntity entity = LockedEntity.Task) =>
        Run(x => x.Acquire(entity, id, holder, now, now + Span));

    [Fact]
    public async Task Free_entity_can_be_acquired()
    {
        var id = Guid.NewGuid();

        var held = await Acquire(id, Ivan, Now);

        Assert.Equal(Ivan, held.Holder);
        Assert.Equal(Now, held.AcquiredAt);
        Assert.Equal(Now + Span, held.ExpiresAt);
        Assert.Equal(held, await Run(x => x.Get(LockedEntity.Task, id, Now)));
    }

    [Fact]
    public async Task Unknown_entity_has_no_lock()
    {
        Assert.Null(await Run(x => x.Get(LockedEntity.Task, Guid.NewGuid(), Now)));
    }

    [Fact]
    public async Task Held_lock_is_not_taken_by_another_holder()
    {
        var id = Guid.NewGuid();
        await Acquire(id, Ivan, Now);

        var held = await Acquire(id, Anna, Now.AddSeconds(30));

        Assert.Equal(Ivan, held.Holder);
        Assert.Equal(Now + Span, held.ExpiresAt);
    }

    [Fact]
    public async Task Own_lock_is_renewed_and_keeps_the_acquire_time()
    {
        var id = Guid.NewGuid();
        await Acquire(id, Ivan, Now);

        var renewed = await Acquire(id, Ivan, Now.AddSeconds(60));

        Assert.Equal(Now, renewed.AcquiredAt);
        Assert.Equal(Now.AddSeconds(60) + Span, renewed.ExpiresAt);
        Assert.Equal(renewed, await Run(x => x.Get(LockedEntity.Task, id, Now.AddSeconds(61))));
    }

    [Fact]
    public async Task Expired_lock_is_gone_and_can_be_taken_by_another_holder()
    {
        var id = Guid.NewGuid();
        await Acquire(id, Ivan, Now);
        var later = Now + Span;

        Assert.Null(await Run(x => x.Get(LockedEntity.Task, id, later)));

        var held = await Acquire(id, Anna, later);
        Assert.Equal(Anna, held.Holder);
        Assert.Equal(later, held.AcquiredAt);
        Assert.Equal(Anna, (await Run(x => x.Get(LockedEntity.Task, id, later)))!.Holder);
    }

    [Fact]
    public async Task Only_the_holder_can_release()
    {
        var id = Guid.NewGuid();
        await Acquire(id, Ivan, Now);

        Assert.False(await Run(x => x.Release(LockedEntity.Task, id, Anna.Key)));
        Assert.NotNull(await Run(x => x.Get(LockedEntity.Task, id, Now)));

        Assert.True(await Run(x => x.Release(LockedEntity.Task, id, Ivan.Key)));
        Assert.Null(await Run(x => x.Get(LockedEntity.Task, id, Now)));
        Assert.False(await Run(x => x.Release(LockedEntity.Task, id, Ivan.Key)));
    }

    [Fact]
    public async Task Remove_drops_the_lock_whoever_holds_it()
    {
        var id = Guid.NewGuid();
        await Acquire(id, Ivan, Now);

        await Run<int>(async x =>
        {
            await x.Remove(LockedEntity.Task, id);
            return 0;
        });

        Assert.Null(await Run(x => x.Get(LockedEntity.Task, id, Now)));
    }

    [Fact]
    public async Task Removing_or_releasing_a_lock_that_never_existed_is_harmless()
    {
        var id = Guid.NewGuid();

        await Run<int>(async x =>
        {
            await x.Remove(LockedEntity.Task, id);
            return 0;
        });

        Assert.False(await Run(x => x.Release(LockedEntity.Task, id, Ivan.Key)));
    }

    [Fact]
    public async Task Same_id_of_different_entity_kinds_are_independent_locks()
    {
        var id = Guid.NewGuid();
        await Acquire(id, Ivan, Now, LockedEntity.Task);

        var held = await Acquire(id, Anna, Now, LockedEntity.Status);

        Assert.Equal(Anna, held.Holder);
        Assert.Equal(Ivan, (await Run(x => x.Get(LockedEntity.Task, id, Now)))!.Holder);
    }

    [Fact]
    public async Task Locks_of_a_project_are_listed_oldest_first_without_expired_released_and_foreign_ones()
    {
        var project = Guid.NewGuid();
        var other = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var expired = Guid.NewGuid();
        var released = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var unscoped = Guid.NewGuid();

        await Run(x => x.Acquire(LockedEntity.Task, first, Ivan, Now, Now + Span, project));
        await Run(x => x.Acquire(LockedEntity.Status, second, Anna, Now.AddSeconds(10), Now.AddSeconds(10) + Span, project));
        await Run(x => x.Acquire(LockedEntity.Board, expired, Ivan, Now.AddMinutes(-10), Now.AddMinutes(-8), project));
        await Run(x => x.Acquire(LockedEntity.Series, released, Ivan, Now, Now + Span, project));
        await Run(x => x.Release(LockedEntity.Series, released, Ivan.Key));
        await Run(x => x.Acquire(LockedEntity.Task, foreign, Ivan, Now, Now + Span, other));
        await Run(x => x.Acquire(LockedEntity.Task, unscoped, Ivan, Now, Now + Span));

        var found = await Run(x => x.GetByProject(project, Now.AddSeconds(20)));

        Assert.Equal([first, second], found.Select(x => x.Id).ToArray());
        Assert.Equal([LockedEntity.Task, LockedEntity.Status], found.Select(x => x.Entity).ToArray());
        Assert.Equal([Ivan, Anna], found.Select(x => x.Holder).ToArray());
        Assert.All(found, x => Assert.Equal(project, x.ProjectId));
        Assert.Empty(await Run(x => x.GetByProject(Guid.NewGuid(), Now)));
    }

    [Fact]
    public async Task Renewing_keeps_the_project_and_a_lock_taken_without_one_gets_it_on_renewal()
    {
        var project = Guid.NewGuid();
        var id = Guid.NewGuid();

        await Run(x => x.Acquire(LockedEntity.Task, id, Ivan, Now, Now + Span, project));
        var renewed = await Run(x => x.Acquire(LockedEntity.Task, id, Ivan, Now.AddSeconds(30), Now.AddSeconds(30) + Span, project));

        Assert.Equal(project, renewed.ProjectId);
        Assert.Equal(project, (await Run(x => x.Get(LockedEntity.Task, id, Now.AddSeconds(31))))!.ProjectId);
        Assert.Single(await Run(x => x.GetByProject(project, Now.AddSeconds(31))));
    }

    [Fact]
    public async Task Concurrent_acquires_have_exactly_one_winner()
    {
        for (var round = 0; round < 5; round++)
        {
            var id = Guid.NewGuid();
            var holders = Enumerable.Range(0, 6).Select(i => new EditHolder($"user:{i}", $"User {i}")).ToArray();

            var results = await Task.WhenAll(holders.Select(h => Task.Run(() => Acquire(id, h, Now))));

            var winners = results.Select(x => x.Holder.Key).Distinct().ToArray();
            Assert.Single(winners);
            Assert.Equal(winners[0], (await Run(x => x.Get(LockedEntity.Task, id, Now)))!.Holder.Key);
        }
    }
}

public sealed class FilesEditLockStorageTests : EditLockStorageContract
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private IContainer[] _containers = [];
    private int _next;

    public override Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        // Несколько контейнеров на одну папку — как несколько процессов.
        _containers = Enumerable.Range(0, 3).Select(_ =>
        {
            var builder = new ContainerBuilder();
            builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
            builder.RegisterModule(new FileStorageModule(_root));
            return builder.Build();
        }).ToArray();
        return Task.CompletedTask;
    }

    public override async Task DisposeAsync()
    {
        foreach (var container in _containers)
            await container.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    protected override Task<T> Run<T>(Func<IEditLockStorage, Task<T>> action) =>
        action(_containers[Interlocked.Increment(ref _next) % _containers.Length].Resolve<IEditLockStorage>());

    [Fact]
    public async Task Lock_file_is_in_the_cache_that_git_ignores_and_survives_a_new_process()
    {
        var id = Guid.NewGuid();
        await Run(x => x.Acquire(LockedEntity.Task, id, Ivan, Now, Now + Span));

        var file = Directory.GetFiles(Path.Combine(_root, ".tasker", ".cache", "edit-locks"), $"*{id}*").Single();
        Assert.EndsWith(".json", file);
        Assert.Contains("/.cache/", await File.ReadAllTextAsync(Path.Combine(_root, ".tasker", ".gitignore")));

        // Повреждённый файл — такая же свободная запись, как и отсутствующий.
        await File.WriteAllTextAsync(file, "{ not json");
        Assert.Null(await Run(x => x.Get(LockedEntity.Task, id, Now)));
        Assert.Equal(Anna, (await Run(x => x.Acquire(LockedEntity.Task, id, Anna, Now, Now + Span))).Holder);
    }
}

public sealed class DbEditLockStorageTests : EditLockStorageContract
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private IContainer _container = null!;

    public override async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var builder = new ContainerBuilder();
        builder.RegisterModule(new DbStorageModule(new DbConfigs { SqliteFile = Path.Combine(_root, "tasker.db") }));
        _container = builder.Build();
        await _container.Resolve<IStorageLifecycle>().Start(default);
    }

    public override async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    protected override async Task<T> Run<T>(Func<IEditLockStorage, Task<T>> action)
    {
        await using var scope = _container.BeginLifetimeScope();
        return await action(scope.Resolve<IEditLockStorage>());
    }
}
