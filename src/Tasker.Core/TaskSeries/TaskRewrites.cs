using Tasker.Core.Tasks;

namespace Tasker.Core.TaskSeries;

/// <summary>Массовая правка задач (каскад удаления серии, чистка): перечитать, поправить, записать с версией, при конфликте повторить.</summary>
internal static class TaskRewrites
{
    /// <param name="change">Возвращает изменённую задачу или null, если менять нечего.</param>
    /// <param name="attempts">Сколько раз пробовать (каждый раз — с заново прочитанной задачей).</param>
    /// <returns>Актуальная задача; null — задачу тем временем удалили.</returns>
    /// <exception cref="TaskerConflictException">Задачу постоянно меняет кто-то другой.</exception>
    public static async Task<TaskItem?> Modify(
        ITaskStorage tasks, TimeProvider time, TaskItem task, Func<TaskItem, TaskItem?> change, int attempts, CancellationToken ct)
    {
        var current = task;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var changed = change(current);
            if (changed == null)
                return current;

            var updated = changed with { UpdatedAt = time.GetUtcNow() };
            var version = await tasks.Update(updated, current.Version, ct);
            if (version != null)
                return updated with { Version = version };

            var fresh = await tasks.GetById(task.ProjectId, task.Id, ct);
            if (fresh == null)
                return null;
            current = fresh;

            // Параллельные правки одной задачи (десять агентов вешают связи на одну задачу) без паузы сталкиваются снова и снова:
            // случайная пауза, растущая с попыткой (до четверти секунды), разводит их по времени.
            if (attempt + 1 < attempts)
                await Task.Delay(Random.Shared.Next(1, 6 << Math.Min(attempt, 5)), ct);
        }

        throw Versioning.Modified($"Task '{task.Title}'");
    }

    /// <summary>
    /// Единое правило массовых правок (каскады серий, чистка, поля и перечисления): задачи, которые <paramref name="change"/> действительно
    /// меняет, не должны быть заняты другим (<see cref="Locks.EditLockService"/>). Вызывающий сначала ждёт их блокировки
    /// (<see cref="WaitForLocks"/>) <b>до</b> входа в секцию записи — ожидание внутри транзакции БД не дало бы держателю снять блокировку.
    /// Здесь, внутри секции, блокировки ВСЕХ затрагиваемых задач проверяются без ожидания до первой записи (отказ ничего не оставляет
    /// наполовину), потом задачи переписываются по одной, перед каждой — проверка ещё раз (кто-то мог взять блокировку новой задачи: отказ
    /// по тому же правилу). Заблокированная, но не затрагиваемая задача не мешает. Версия задачи остаётся последней защитой (<see cref="Modify"/>).
    /// </summary>
    /// <exception cref="TaskerLockedException">Блокировка не снялась за <see cref="Locks.EditLockService.CascadeTimeout"/>.</exception>
    /// <returns>Сколько задач переписано (задачу, которую тем временем удалили, не считаем).</returns>
    public static async Task<int> ModifyAll(
        ITaskStorage tasks, TimeProvider time, Locks.EditLockService locks, IEnumerable<TaskItem> candidates,
        Func<TaskItem, TaskItem?> change, int attempts, CancellationToken ct)
    {
        var affected = candidates.Where(x => change(x) != null).ToArray();
        await WaitForLocks(locks, affected, ct, TimeSpan.Zero);

        var rewritten = 0;
        foreach (var task in affected)
        {
            // Другой мог взять блокировку, пока переписывались предыдущие задачи.
            await WaitForLocks(locks, [task], ct, TimeSpan.Zero);
            if (await Modify(tasks, time, task, change, attempts, ct) != null)
                rewritten++;
        }
        return rewritten;
    }

    /// <summary>Ждёт снятия чужих блокировок задач (без записи): для проверки «до первой записи». Ожидание — <see cref="Locks.EditLockService.CascadeTimeout"/>, если не задано.</summary>
    public static Task WaitForLocks(Locks.EditLockService locks, IEnumerable<TaskItem> affected, CancellationToken ct, TimeSpan? timeout = null) =>
        locks.WaitUntilWritable(affected.Select(x => (Locks.LockedEntity.Task, x.Id, $"Task '{x.Title}'")).ToArray(), timeout, ct);

    public static IReadOnlyList<TaskSeriesNumber> Without(TaskItem task, Func<TaskSeriesNumber, bool> remove) =>
        task.SeriesNumbers.Where(x => !remove(x)).ToArray();
}
