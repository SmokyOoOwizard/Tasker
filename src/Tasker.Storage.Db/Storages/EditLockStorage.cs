using Microsoft.EntityFrameworkCore;
using Tasker.Core.Locks;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

/// <summary>
/// Блокировки на время правки в таблице <c>edit_locks</c>. Взять блокировку атомарно помогают условный
/// <c>UPDATE … WHERE (держатель — я ИЛИ срок вышел)</c> и первичный ключ при вставке: из двух одновременных
/// запросов выигрывает один, второй видит чужую блокировку.
/// </summary>
internal class EditLockStorage(AppDbContext context) : IEditLockStorage
{
    private const int MaxAttempts = 3;

    public async Task<EditLock?> Get(LockedEntity entity, Guid id, DateTimeOffset now, CancellationToken ct = default)
    {
        var model = await Find(entity, id, ct);
        return model != null && model.ExpiresAt > DbTime.ToDb(now) ? Map(model) : null;
    }

    public async Task<EditLock[]> GetByProject(Guid projectId, DateTimeOffset now, CancellationToken ct = default)
    {
        var nowDb = DbTime.ToDb(now);
        var models = await context.EditLocks
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ExpiresAt > nowDb)
            .OrderBy(x => x.AcquiredAt)
            .ThenBy(x => x.Id)
            .ToArrayAsync(ct);
        return models.Select(Map).ToArray();
    }

    public async Task<EditLock> Acquire(LockedEntity entity, Guid id, EditHolder holder, DateTimeOffset now, DateTimeOffset expiresAt, Guid? projectId = null, CancellationToken ct = default)
    {
        var name = entity.ToString();
        var nowDb = DbTime.ToDb(now);
        var expiresDb = DbTime.ToDb(expiresAt);

        for (var attempt = 1;; attempt++)
        {
            var current = await Find(entity, id, ct);

            if (current == null)
            {
                var model = new EditLockDbModel
                {
                    Entity = name,
                    Id = id,
                    HolderKey = holder.Key,
                    HolderName = holder.Name,
                    ProjectId = projectId,
                    AcquiredAt = nowDb,
                    ExpiresAt = expiresDb
                };
                context.EditLocks.Add(model);
                try
                {
                    await context.SaveChangesAsync(ct);
                    return Map(model);
                }
                catch (Exception e) when (DbErrors.IsUniqueViolation(e) && attempt < MaxAttempts)
                {
                    // Другой запрос успел вставить строку: смотрим, чья она.
                    context.Entry(model).State = EntityState.Detached;
                    continue;
                }
            }

            var mine = current.HolderKey == holder.Key && current.ExpiresAt > nowDb;
            if (!mine && current.ExpiresAt > nowDb)
                return Map(current);

            // Своя (продлеваем, момент взятия сохраняется) или истёкшая (берём заново).
            var acquiredAt = mine ? current.AcquiredAt : nowDb;
            var updated = await context.EditLocks
                .Where(x => x.Entity == name && x.Id == id
                    && (x.HolderKey == holder.Key || x.ExpiresAt <= nowDb))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.HolderKey, holder.Key)
                    .SetProperty(x => x.HolderName, holder.Name)
                    .SetProperty(x => x.ProjectId, projectId)
                    .SetProperty(x => x.AcquiredAt, acquiredAt)
                    .SetProperty(x => x.ExpiresAt, expiresDb), ct);

            if (updated > 0)
                return new EditLock(entity, id, holder, DbTime.FromDb(acquiredAt), expiresAt, projectId);

            // Между чтением и записью блокировку взял другой — читаем заново.
            if (attempt >= MaxAttempts)
                throw new InvalidOperationException($"Could not acquire the lock of {entity} {id}: it keeps changing");
        }
    }

    public async Task<bool> Release(LockedEntity entity, Guid id, string holderKey, CancellationToken ct = default)
    {
        var name = entity.ToString();
        return await context.EditLocks
            .Where(x => x.Entity == name && x.Id == id && x.HolderKey == holderKey)
            .ExecuteDeleteAsync(ct) > 0;
    }

    public async Task Remove(LockedEntity entity, Guid id, CancellationToken ct = default)
    {
        var name = entity.ToString();
        await context.EditLocks.Where(x => x.Entity == name && x.Id == id).ExecuteDeleteAsync(ct);
    }

    private Task<EditLockDbModel?> Find(LockedEntity entity, Guid id, CancellationToken ct)
    {
        var name = entity.ToString();
        return context.EditLocks.AsNoTracking().FirstOrDefaultAsync(x => x.Entity == name && x.Id == id, ct);
    }

    private static EditLock Map(EditLockDbModel model) => new(
        Enum.Parse<LockedEntity>(model.Entity),
        model.Id,
        new EditHolder(model.HolderKey, model.HolderName),
        DbTime.FromDb(model.AcquiredAt),
        DbTime.FromDb(model.ExpiresAt),
        model.ProjectId);
}
