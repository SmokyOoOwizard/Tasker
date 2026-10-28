namespace Tasker.Core.Locks;

/// <summary>
/// Хранилище блокировок на время правки. Блокировка — временное состояние, не часть данных:
/// в файлах она лежит в .tasker/.cache (в git не попадает), в БД — в отдельной таблице.
/// Истёкшая блокировка равна отсутствующей; каждый вызов получает текущее время, чтобы хранилище ничего не знало о часах.
/// </summary>
public interface IEditLockStorage
{
    /// <returns>null — сущность не заблокирована (или блокировка истекла).</returns>
    Task<EditLock?> Get(LockedEntity entity, Guid id, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Берёт блокировку или продлевает свою — атомарно. Занята другим и не истекла — не меняет ничего.
    /// </summary>
    /// <returns>Действующая блокировка: своя, если получилось, или чужая.</returns>
    /// <param name="projectId">Проект сущности — запоминается, чтобы <see cref="GetByProject"/> находил блокировку.</param>
    Task<EditLock> Acquire(LockedEntity entity, Guid id, EditHolder holder, DateTimeOffset now, DateTimeOffset expiresAt, Guid? projectId = null, CancellationToken ct = default);

    /// <summary>Действующие блокировки сущностей проекта (и самого проекта) — для индикаторов в списках. Старые первыми.</summary>
    Task<EditLock[]> GetByProject(Guid projectId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Снимает блокировку, только если её держит <paramref name="holderKey"/>.</summary>
    /// <returns>false — блокировки не было или её держит другой.</returns>
    Task<bool> Release(LockedEntity entity, Guid id, string holderKey, CancellationToken ct = default);

    /// <summary>Снимает блокировку независимо от держателя — когда сущность удалена.</summary>
    Task Remove(LockedEntity entity, Guid id, CancellationToken ct = default);
}
