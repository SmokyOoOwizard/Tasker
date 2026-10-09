using Tasker.Core;
using Tasker.Core.Locks;
using Tasker.Core.TaskSeries;
using Tasker.Tests.SeriesCore;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Единое правило массовых правок задач (удаление серии, чистка): задачи, которые правка меняет, не должны быть заняты другим.
/// Проверка — до первой записи; занятая, но не затрагиваемая задача не мешает. В этих тестах ожидание нулевое (<see cref="EditLockService.CascadeTimeout"/>),
/// ожидание и снятие — в <c>TaskFieldsTests</c> на обоих хранилищах.
/// </summary>
public class CascadeLockTests
{
    private static async Task LockedByAnna(SeriesEnv env, Guid taskId)
    {
        env.Editor.Holder = FakeEditor.Anna;
        await env.Locks.Acquire(LockedEntity.Task, taskId, "Task");
        env.Editor.Holder = FakeEditor.Ivan;
    }

    [Fact]
    public async Task Deleting_a_series_is_refused_while_a_task_with_its_number_is_locked_and_nothing_is_written()
    {
        var env = new SeriesEnv();
        var series = env.AddSeries("TSK");
        var locked = await env.Create("Locked", series.Id);
        var free = await env.Create("Free", series.Id);
        await LockedByAnna(env, locked.Id);

        var error = await Assert.ThrowsAsync<TaskerLockedException>(() => env.SeriesSvc.Delete(env.Project, series.Id, series.Version));

        Assert.Contains("Task 'Locked'", error.Message);
        Assert.DoesNotContain("Free", error.Message);
        Assert.NotNull(await env.SeriesSvc.Find(env.Project, series.Id.ToString()));
        Assert.Single(env.Get(free.Id).SeriesNumbers);
        Assert.Single(env.Get(locked.Id).SeriesNumbers);
    }

    [Fact]
    public async Task A_locked_task_outside_the_series_does_not_get_in_the_way_and_a_released_lock_lets_the_delete_through()
    {
        var env = new SeriesEnv();
        var series = env.AddSeries("TSK");
        var inSeries = await env.Create("In", series.Id);
        var outside = await env.Create("Outside");
        await LockedByAnna(env, outside.Id);
        await LockedByAnna(env, inSeries.Id);

        await Assert.ThrowsAsync<TaskerLockedException>(() => env.SeriesSvc.Delete(env.Project, series.Id, series.Version));

        env.Editor.Holder = FakeEditor.Anna;
        await env.Locks.Release(LockedEntity.Task, inSeries.Id);
        env.Editor.Holder = FakeEditor.Ivan;
        Assert.True(await env.SeriesSvc.Delete(env.Project, series.Id, series.Version));
        Assert.Empty(env.Get(inSeries.Id).SeriesNumbers);
    }

    [InProcess]
    [Fact]
    public async Task Cleanup_is_refused_before_any_write_when_a_task_it_would_change_is_locked_but_a_dry_run_and_unaffected_locks_are_fine()
    {
        var env = new SeriesEnv();
        var ghost = Guid.NewGuid();
        var broken = env.Seed("Broken", Nums((ghost, 3)));
        var alsoBroken = env.Seed("AlsoBroken", Nums((Guid.NewGuid(), 1)));
        var clean = env.Seed("Clean");
        await LockedByAnna(env, broken.Id);
        await LockedByAnna(env, clean.Id);

        var error = await Assert.ThrowsAsync<TaskerLockedException>(() => env.Cleanup.Run(env.Project, new CleanupOptions()));
        Assert.Contains("Task 'Broken'", error.Message);
        Assert.DoesNotContain("Task 'Clean'", error.Message);
        Assert.Single(env.Get(alsoBroken.Id).SeriesNumbers);
        Assert.Single(env.Get(broken.Id).SeriesNumbers);

        // Пробный запуск ничего не пишет и блокировки не проверяет.
        var dry = await env.Cleanup.Run(env.Project, new CleanupOptions(DryRun: true));
        Assert.Equal(2, dry.Changes.Length);

        env.Editor.Holder = FakeEditor.Anna;
        await env.Locks.Release(LockedEntity.Task, broken.Id);
        env.Editor.Holder = FakeEditor.Ivan;
        var done = await env.Cleanup.Run(env.Project, new CleanupOptions());
        Assert.Equal(2, done.Changes.Length);
        Assert.Empty(env.Get(broken.Id).SeriesNumbers);
    }

    private static (Guid, int)[] Nums(params (Guid, int)[] numbers) => numbers;
}
