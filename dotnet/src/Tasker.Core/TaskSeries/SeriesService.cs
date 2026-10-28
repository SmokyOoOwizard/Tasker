using Tasker.Core.Tasks;

namespace Tasker.Core.TaskSeries;

public record CreateSeries(string Name, string Prefix);

/// <summary>Поля null — не меняются. Version — версия, которую видел клиент (см. <see cref="Versioning"/>). Смена префикса — переименование серии.</summary>
public record UpdateSeries(string? Name, string? Prefix, string? Version);

/// <summary>Серии проекта. Реализация — по образцу <see cref="Statuses.StatusService"/>.</summary>
public class SeriesService(ITaskStorage tasks, ISeriesStorage series, IWriteScope writes, TimeProvider time, Locks.EditLockService locks)
{
    private const int CascadeAttempts = 4;

    /// <exception cref="TaskerValidationException">Имя или префикс не подходят.</exception>
    /// <exception cref="TaskerConflictException">Префикс уже занят в проекте (точное совпадение, регистр важен).</exception>
    public async Task<Series> Create(Guid projectId, CreateSeries command, CancellationToken ct = default)
    {
        var name = Validate.Name(command.Name, "Series name");
        var prefix = SeriesPrefix.Validate(command.Prefix);

        return await writes.Exclusive(projectId, async () =>
        {
            await EnsurePrefixFree(projectId, prefix, null, ct);

            var created = new Series
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Name = name,
                Prefix = prefix,
                Version = Versioning.New
            };
            return created with { Version = await series.Add(created, ct) };
        }, ct);
    }

    /// <returns>null — серии нет.</returns>
    public Task<Series?> Update(Guid projectId, Guid id, UpdateSeries command, CancellationToken ct = default) =>
        writes.Exclusive<Series?>(projectId, async () =>
        {
            var current = await series.GetById(projectId, id, ct);
            if (current == null)
                return null;
            await locks.EnsureWritable(Locks.LockedEntity.Series, current.Id, Subject(current), ct);
            var expected = Versioning.Check(current.Version, command.Version, Subject(current));

            var updated = current with
            {
                Name = command.Name == null ? current.Name : Validate.Name(command.Name, "Series name"),
                Prefix = command.Prefix == null ? current.Prefix : SeriesPrefix.Validate(command.Prefix)
            };
            if (updated.Prefix != current.Prefix)
                await EnsurePrefixFree(projectId, updated.Prefix, id, ct);

            var version = await series.Update(updated, expected, ct) ?? throw Versioning.Modified(Subject(current));
            return updated with { Version = version };
        }, ct);

    /// <summary>
    /// Удаляет серию и в той же операции убирает ссылки на неё из задач (серия удаляется первой). Серию с задачами удалять можно.
    /// </summary>
    /// <returns>false — серии нет.</returns>
    public async Task<bool> Delete(Guid projectId, Guid id, string? version, CancellationToken ct = default)
    {
        // Задачи серии не должны быть заняты другим: ждём (или отказываем) до секции записи и до первой записи.
        // Внутри секции (в БД это транзакция) ожидание не годится — держатель не смог бы снять блокировку; там проверка без ожидания.
        if (await series.GetById(projectId, id, ct) != null)
            await TaskRewrites.WaitForLocks(locks, await GetReferencing(projectId, id, ct), ct);

        return await DeleteLocked(projectId, id, version, ct);
    }

    private Task<bool> DeleteLocked(Guid projectId, Guid id, string? version, CancellationToken ct) =>
        writes.Exclusive(projectId, async () =>
        {
            var current = await series.GetById(projectId, id, ct);
            if (current == null)
                return false;
            await locks.EnsureWritable(Locks.LockedEntity.Series, current.Id, Subject(current), ct);
            var expected = Versioning.Check(current.Version, version, Subject(current));

            var referencing = await GetReferencing(projectId, id, ct);
            await TaskRewrites.WaitForLocks(locks, referencing, ct, TimeSpan.Zero);

            if (!await series.Delete(projectId, id, expected, ct))
                throw Versioning.Modified(Subject(current));

            await locks.Forget(Locks.LockedEntity.Series, current.Id, ct);
            await RemoveReferences(referencing, id, ct);
            return true;
        }, ct);

    /// <summary>Серия по id (Guid) или по префиксу (точное совпадение). null — нет такой.</summary>
    public async Task<Series?> Find(Guid projectId, string reference, CancellationToken ct = default)
    {
        var text = reference?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (Guid.TryParse(text, out var id))
            return await series.GetById(projectId, id, ct);

        return (await series.GetAll(projectId, ct)).FirstOrDefault(x => x.Prefix == text);
    }

    private async Task EnsurePrefixFree(Guid projectId, string prefix, Guid? exceptId, CancellationToken ct)
    {
        if ((await series.GetAll(projectId, ct)).Any(x => x.Prefix == prefix && x.Id != exceptId))
            throw new TaskerConflictException($"Series prefix '{prefix}' is already used in the project");
    }

    /// <summary>
    /// Все задачи проекта с номером этой серии. Собирает страницами со сдвигом (порядок createdAt, id — стабильный): не опирается на то,
    /// что переписанные задачи выпадают из фильтра.
    /// </summary>
    private async Task<TaskItem[]> GetReferencing(Guid projectId, Guid seriesId, CancellationToken ct)
    {
        var filter = new TaskFilter { SeriesIds = [seriesId] };
        var found = new Dictionary<Guid, TaskItem>();
        var offset = 0;

        while (true)
        {
            var page = await tasks.GetRange(projectId, filter, new Dto.Page(offset, Dto.Page.MaxLimit), ct);
            if (page.Data.Length == 0)
                break;
            foreach (var task in page.Data)
                found[task.Id] = task;
            offset += page.Data.Length;
        }
        return found.Values.ToArray();
    }

    /// <summary>
    /// Убирает номера этой серии из задач (массовая правка по общему правилу блокировок: <see cref="TaskRewrites.ModifyAll"/>).
    /// Задачу, которую только что заблокировал другой, пропускает с ошибкой в конце; сбой оставляет остаток — его уберёт <c>tasker cleanup</c> (серия к тому моменту уже удалена).
    /// </summary>
    private async Task RemoveReferences(TaskItem[] referencing, Guid seriesId, CancellationToken ct)
    {
        Exception? first = null;
        foreach (var task in referencing)
        {
            try
            {
                // По одной: сбой на одной задаче не останавливает остальные, первая ошибка выбрасывается в конце.
                await TaskRewrites.ModifyAll(tasks, time, locks, [task],
                    t => t.SeriesNumbers.Any(x => x.SeriesId == seriesId)
                        ? t with { SeriesNumbers = TaskRewrites.Without(t, x => x.SeriesId == seriesId) }
                        : null,
                    CascadeAttempts, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                first ??= ex;
            }
        }

        if (first != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
    }

    private static string Subject(Series item) => $"Series '{item.Name}'";
}
