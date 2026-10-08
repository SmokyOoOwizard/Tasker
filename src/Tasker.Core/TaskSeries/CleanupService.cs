using Tasker.Core.Tasks;

namespace Tasker.Core.TaskSeries;

/// <param name="ResolveConflicts">Ещё и решить дубликаты номеров: сохраняет номер самая ранняя задача (<c>CreatedAt</c>, затем Guid), остальные получают максимум + 1 в том же порядке.</param>
/// <param name="DryRun">Ничего не менять, только рассказать, что изменится.</param>
public record CleanupOptions(bool ResolveConflicts = false, bool DryRun = false);

public enum CleanupChangeKind
{
    /// <summary>Убрана недействительная ссылка на серию.</summary>
    RemovedInvalidSeries,

    /// <summary>Задаче выдан другой номер в серии (решение дубликата).</summary>
    Renumbered,

    /// <summary>Убрана недействительная связь: на задачу или тип связи, которых нет.</summary>
    RemovedInvalidLink
}

/// <param name="Description">Понятным языком: что сделано (в режиме DryRun — что было бы сделано).</param>
/// <param name="SeriesId">Серия (для изменений номеров); null — у недействительной связи.</param>
/// <param name="LinkTypeId">Тип убранной связи (только для <see cref="CleanupChangeKind.RemovedInvalidLink"/>).</param>
/// <param name="LinkTargetId">Задача, на которую указывала убранная связь (только для <see cref="CleanupChangeKind.RemovedInvalidLink"/>).</param>
public record CleanupChange(
    Guid TaskId, string TaskTitle, CleanupChangeKind Kind, Guid? SeriesId, int? OldNumber, int? NewNumber, string Description,
    Guid? LinkTypeId = null, Guid? LinkTargetId = null);

/// <param name="Changes">Что сделано (при DryRun — что было бы сделано).</param>
/// <param name="RemainingNumberConflicts">Дубликаты, которые остались: без <c>ResolveConflicts</c> — все, иначе пусто.</param>
/// <param name="PrefixConflicts">Дубликаты префиксов серий: чистка их не решает, нужно переименовать серию.</param>
/// <param name="Skipped">Чистка ссылок пропущена — причина в <paramref name="SkipReason"/> (например, есть нечитаемые файлы серий).</param>
/// <param name="LinkCycles">
/// Циклы из связей типов, которые циклов не допускают (после слияния веток git). Чистка их не убирает: какую связь снять, решает человек
/// (<c>task unlink</c>); они только требуют внимания. Пусто — циклов нет.
/// </param>
/// <param name="LinksSkipReason">
/// Не пропущена вся чистка, а только очистка связей между задачами: есть нечитаемые файлы задач или типов связей — нечитаемая задача
/// выглядела бы несуществующей, и связи на неё были бы стёрты. Серии при этом очищены как обычно. null — связи проверены.
/// </param>
public record CleanupReport(
    CleanupChange[] Changes,
    NumberConflict[] RemainingNumberConflicts,
    PrefixConflict[] PrefixConflicts,
    bool Skipped,
    string? SkipReason,
    string? LinksSkipReason = null,
    Links.LinkCycle[]? LinkCycles = null)
{
    public Links.LinkCycle[] LinkCycles { get; init; } = LinkCycles ?? [];
}

/// <summary>
/// Единственное место, где сверка что-то пишет. Работает в рамках одного проекта под <see cref="IWriteScope"/>.
/// Недействительные ссылки не убирает, если среди файлов серий есть нечитаемые (<see cref="ISeriesStorage.CountUnreadable"/>).
/// Недействительные связи между задачами (на задачу или тип, которых нет — после слияния веток) убирает так же, и тоже только
/// когда нечитаемых файлов задач и типов связей нет.
/// Слияние или rebase в процессе (git) проверяет вызывающий: Core про git не знает.
/// </summary>
// taskService оставлен ради стабильности контракта: обновления идут через ITaskStorage, чтобы не выпадать из одной секции записи.
#pragma warning disable CS9113
public class CleanupService(
    ITaskStorage tasks, TaskService taskService, ISeriesStorage series, Links.LinkTypeService linkTypes, IWriteScope writes,
    Locks.EditLockService locks, TimeProvider time)
{
    private const int Attempts = 2;

    public CleanupService(
        ITaskStorage tasks, TaskService taskService, ISeriesStorage series, Links.LinkTypeService linkTypes, IWriteScope writes, Locks.EditLockService locks)
        : this(tasks, taskService, series, linkTypes, writes, locks, TimeProvider.System)
    {
    }

    /// <exception cref="TaskerConflictException">Задачу постоянно меняет кто-то другой (после повтора).</exception>
    public async Task<CleanupReport> Run(Guid projectId, CleanupOptions options, CancellationToken ct = default)
    {
        // Общее правило массовых правок: задачи, которые чистка изменит, не должны быть заняты другим. Ждём (или отказываем) до секции
        // записи и до первой записи — внутри секции (в БД это транзакция) держатель не смог бы снять блокировку; там проверка без ожидания
        // перед каждой записью (TaskRewrites.ModifyAll).
        if (!options.DryRun && await series.CountUnreadable(projectId, ct) == 0)
            await WaitForLocks(projectId, (await series.GetAll(projectId, ct)).Select(x => x.Id).ToHashSet(), options.ResolveConflicts, ct);

        return await RunLocked(projectId, options, ct);
    }

    private Task<CleanupReport> RunLocked(Guid projectId, CleanupOptions options, CancellationToken ct) =>
        writes.Exclusive(projectId, async () =>
        {
            var all = await series.GetAll(projectId, ct);
            var prefixConflicts = SeriesHealthService.FindPrefixConflicts(all);

            if (await series.CountUnreadable(projectId, ct) > 0)
            {
                return new CleanupReport(
                    [],
                    await tasks.GetNumberConflicts(projectId, ct),
                    prefixConflicts,
                    true,
                    "Some series files cannot be read (merge conflict or broken YAML): a series that cannot be read would look missing "
                    + "and references to it would be wiped. Nothing was changed; fix the series files and run cleanup again.");
            }

            var known = all.Select(x => x.Id).ToHashSet();
            var changes = new List<CleanupChange>();

            await RemoveInvalidReferences(projectId, known, options.DryRun, changes, ct);

            // Дубликаты читаем после первого шага. В DryRun ссылки ещё на месте, поэтому дубликаты в несуществующих сериях отбрасываем сами:
            // в настоящем запуске их уже нет.
            var conflicts = (await tasks.GetNumberConflicts(projectId, ct)).Where(x => known.Contains(x.SeriesId)).ToArray();

            if (options.ResolveConflicts)
            {
                await ResolveConflicts(projectId, all, conflicts, options.DryRun, changes, ct);
                conflicts = [];
            }

            var linksSkipReason = await RemoveInvalidLinks(projectId, options.DryRun, changes, ct);

            // Циклы связей — после правок связей (недействительные связи в цикле уже убраны), только чтение.
            var cycles = await Links.LinkCycles.Find(tasks, projectId, await linkTypes.GetAll(projectId, ct), ct);

            return new CleanupReport(changes.ToArray(), conflicts, prefixConflicts, false, null, linksSkipReason, cycles);
        }, ct);

    /// <summary>Задачи, которые изменит чистка (недействительные серии, дубликаты номеров, недействительные связи), — ждём их блокировки.</summary>
    private async Task WaitForLocks(Guid projectId, HashSet<Guid> known, bool resolveConflicts, CancellationToken ct)
    {
        var affected = new Dictionary<Guid, TaskItem>();
        foreach (var task in await tasks.GetWithSeriesNotIn(projectId, known.ToArray(), ct))
            affected[task.Id] = task;

        if (resolveConflicts)
        {
            foreach (var conflict in (await tasks.GetNumberConflicts(projectId, ct)).Where(x => known.Contains(x.SeriesId)))
            {
                var members = new List<TaskItem>();
                foreach (var id in conflict.TaskIds)
                {
                    if (await tasks.GetById(projectId, id, ct) is { } member)
                        members.Add(member);
                }
                // Самая ранняя сохраняет номер и не меняется.
                foreach (var task in members.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id.ToString("D"), StringComparer.Ordinal).Skip(1))
                    affected[task.Id] = task;
            }
        }

        if (await tasks.CountUnreadable(projectId, ct) + await linkTypes.CountUnreadable(projectId, ct) == 0)
        {
            var types = (await linkTypes.GetAll(projectId, ct)).Select(x => x.Id).ToArray();
            foreach (var task in await tasks.GetWithInvalidLinks(projectId, types, ct))
                affected[task.Id] = task;
        }

        await TaskRewrites.WaitForLocks(locks, affected.Values, ct);
    }

    /// <summary>
    /// Убирает связи на задачи и типы связей, которых нет (удалены в другой ветке и после слияния остались у источника).
    /// </summary>
    /// <returns>Причина, по которой связи не проверялись; null — проверены.</returns>
    private async Task<string?> RemoveInvalidLinks(Guid projectId, bool dryRun, List<CleanupChange> changes, CancellationToken ct)
    {
        // Нечитаемая задача или тип выглядели бы несуществующими, и все связи на них были бы стёрты.
        var unreadable = await tasks.CountUnreadable(projectId, ct) + await linkTypes.CountUnreadable(projectId, ct);
        if (unreadable > 0)
            return $"{unreadable} task or link type file(s) cannot be read (merge conflict or broken YAML): a task or a type that cannot be read "
                + "would look missing and the links to it would be wiped. The links were not checked; fix the files and run cleanup again.";

        var types = (await linkTypes.GetAll(projectId, ct)).ToDictionary(x => x.Id, x => x.Name);
        foreach (var task in await tasks.GetWithInvalidLinks(projectId, types.Keys.ToArray(), ct))
        {
            var invalid = new List<Links.TaskLink>();
            foreach (var link in task.Links)
            {
                if (!types.TryGetValue(link.TypeId, out var typeName))
                {
                    invalid.Add(link);
                    changes.Add(new CleanupChange(task.Id, task.Title, CleanupChangeKind.RemovedInvalidLink, null, null, null,
                        $"Task '{task.Title}': removed a link of a link type that does not exist ({link.TypeId}) to {link.TargetId}", link.TypeId, link.TargetId));
                }
                else if (await tasks.GetById(projectId, link.TargetId, ct) == null)
                {
                    invalid.Add(link);
                    changes.Add(new CleanupChange(task.Id, task.Title, CleanupChangeKind.RemovedInvalidLink, null, null, null,
                        $"Task '{task.Title}': removed link '{typeName}' to a task that does not exist ({link.TargetId})", link.TypeId, link.TargetId));
                }
            }

            if (invalid.Count > 0 && !dryRun)
                await TaskRewrites.ModifyAll(tasks, time, locks, [task],
                    t => t.Links.Any(invalid.Contains) ? t with { Links = t.Links.Where(x => !invalid.Contains(x)).ToArray() } : null,
                    Attempts, ct);
        }

        return null;
    }

    private async Task RemoveInvalidReferences(Guid projectId, HashSet<Guid> known, bool dryRun, List<CleanupChange> changes, CancellationToken ct)
    {
        foreach (var task in await tasks.GetWithSeriesNotIn(projectId, known.ToArray(), ct))
        {
            var invalid = task.SeriesNumbers.Where(x => !known.Contains(x.SeriesId)).ToArray();
            if (!dryRun)
                await TaskRewrites.ModifyAll(tasks, time, locks, [task],
                    t => t.SeriesNumbers.Any(x => !known.Contains(x.SeriesId))
                        ? t with { SeriesNumbers = TaskRewrites.Without(t, x => !known.Contains(x.SeriesId)) }
                        : null,
                    Attempts, ct);

            foreach (var reference in invalid)
                changes.Add(new CleanupChange(task.Id, task.Title, CleanupChangeKind.RemovedInvalidSeries, reference.SeriesId,
                    reference.Number, null, $"Task '{task.Title}': removed invalid series reference (was #{reference.Number})"));
        }
    }

    private async Task ResolveConflicts(
        Guid projectId, Series[] all, NumberConflict[] conflicts, bool dryRun, List<CleanupChange> changes, CancellationToken ct)
    {
        // Следующий номер серии считаем сами: в DryRun ничего не записано, а номера, выданные раньше в этом же запуске, занимать нельзя.
        var next = new Dictionary<Guid, int>();

        foreach (var conflict in conflicts)
        {
            var prefix = all.First(x => x.Id == conflict.SeriesId).Prefix;

            var members = new List<TaskItem>();
            foreach (var id in conflict.TaskIds)
            {
                if (await tasks.GetById(projectId, id, ct) is { } member)
                    members.Add(member);
            }

            // Самая ранняя задача сохраняет номер, остальные получают следующие в том же порядке.
            // Порядок по Guid — как в хранилищах (по тексту Guid), чтобы порядок был одинаков везде.
            foreach (var task in members.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id.ToString("D"), StringComparer.Ordinal).Skip(1))
            {
                if (!next.TryGetValue(conflict.SeriesId, out var number))
                    number = await tasks.GetMaxNumber(projectId, conflict.SeriesId, ct) + 1;
                next[conflict.SeriesId] = number + 1;

                if (!dryRun)
                    await TaskRewrites.ModifyAll(tasks, time, locks, [task],
                        t => t with { SeriesNumbers = t.SeriesNumbers.Select(x => x.SeriesId == conflict.SeriesId ? x with { Number = number } : x).ToArray() },
                        Attempts, ct);

                changes.Add(new CleanupChange(task.Id, task.Title, CleanupChangeKind.Renumbered, conflict.SeriesId, conflict.Number, number,
                    $"Task '{task.Title}': duplicate number {prefix}-{conflict.Number} changed to {prefix}-{number}"));
            }
        }
    }
}
