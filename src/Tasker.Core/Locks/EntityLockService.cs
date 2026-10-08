using Tasker.Core.Boards;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;
using Tasker.Core.Users;

namespace Tasker.Core.Locks;

/// <summary>Блокировка в виде для клиентов (REST, MCP, CLI): без внутреннего ключа держателя.</summary>
/// <param name="Holder">Имя держателя — «правит Иван».</param>
/// <param name="Mine">Блокировку держит тот, кто спрашивает.</param>
public record LockInfo(LockedEntity Entity, Guid Id, string Holder, bool Mine, DateTimeOffset AcquiredAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Блокировка сущности по её виду и id — для клиентов, у которых нет сервиса сущности под рукой:
/// проверяет, что сущность есть (иначе null — для REST это 404), и называет её в сообщении об отказе
/// так же, как сервисы сущностей. Саму блокировку берёт и снимает <see cref="EditLockService"/>.
/// </summary>
public class EntityLockService(
    EditLockService locks,
    IEditorIdentity identity,
    IProjectStorage projects,
    ITaskStorage tasks,
    ITaskTypeStorage taskTypes,
    IStatusStorage statuses,
    IStatusSetStorage statusSets,
    IBoardStorage boards,
    ISeriesStorage series,
    IUserStorage users,
    Links.ILinkTypeStorage linkTypes,
    Fields.IFieldStorage fields,
    Fields.IFieldEnumStorage fieldEnums)
{
    /// <summary>Сущности внутри проекта — для них при блокировке нужен <c>projectId</c>.</summary>
    public static bool IsProjectScoped(LockedEntity entity) => entity is not (LockedEntity.Project or LockedEntity.User);

    /// <summary>Разбор вида сущности из текста клиента: <c>task</c>, <c>taskType</c>, <c>status-set</c> и т. п., регистр не важен.</summary>
    /// <exception cref="TaskerValidationException">Такого вида нет.</exception>
    public static LockedEntity ParseEntity(string? text)
    {
        var normalized = (text ?? "").Replace("-", "").Replace("_", "").Trim();
        foreach (var entity in Enum.GetValues<LockedEntity>())
        {
            if (string.Equals(entity.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                return entity;
        }

        throw new TaskerValidationException(
            $"Entity: unknown kind '{text}'; expected one of {string.Join(", ", Enum.GetNames<LockedEntity>().Select(x => char.ToLowerInvariant(x[0]) + x[1..]))}");
    }

    /// <summary>Берёт блокировку или продлевает свою.</summary>
    /// <returns>null — такой сущности нет.</returns>
    /// <exception cref="TaskerLockedException">Блокировку держит другой.</exception>
    public async Task<LockInfo?> Acquire(Guid? projectId, LockedEntity entity, Guid id, CancellationToken ct = default)
    {
        if (await Subject(projectId, entity, id, ct) is not { } subject)
            return null;
        // У самого проекта «проект сущности» — он сам.
        return await ToInfo(await locks.Acquire(entity, id, subject, entity == LockedEntity.Project ? id : projectId, ct), ct);
    }

    /// <summary>Действующие блокировки проекта — для индикаторов «правит Иван» в списках. Старые первыми.</summary>
    public async Task<LockInfo[]> GetByProject(Guid projectId, CancellationToken ct = default)
    {
        var found = await locks.GetByProject(projectId, ct);
        return await Task.WhenAll(found.OrderBy(x => x.AcquiredAt).ThenBy(x => x.Id).Select(x => ToInfo(x, ct)));
    }

    /// <summary>Снимает свою блокировку; нет блокировки, она чужая или сущности нет — ничего не происходит.</summary>
    public Task<bool> Release(LockedEntity entity, Guid id, CancellationToken ct = default) =>
        locks.Release(entity, id, ct);

    /// <returns>null — не заблокирована.</returns>
    public async Task<LockInfo?> Get(LockedEntity entity, Guid id, CancellationToken ct = default) =>
        await locks.Get(entity, id, ct) is { } held ? await ToInfo(held, ct) : null;

    private async Task<LockInfo> ToInfo(EditLock held, CancellationToken ct) =>
        new(held.Entity, held.Id, held.Holder.Name, held.Holder.Key == (await identity.Current(ct)).Key, held.AcquiredAt, held.ExpiresAt);

    /// <summary>Название сущности для сообщения («Task 'Fix login'»); null — сущности нет.</summary>
    private async Task<string?> Subject(Guid? projectId, LockedEntity entity, Guid id, CancellationToken ct)
    {
        if (IsProjectScoped(entity) && projectId == null)
            throw new TaskerValidationException($"ProjectId is required to lock a {entity}");
        var project = projectId.GetValueOrDefault();

        return entity switch
        {
            LockedEntity.Project => await projects.GetById(id, ct) is { } x ? $"Project '{x.Name}'" : null,
            LockedEntity.Task => await tasks.GetById(project, id, ct) is { } x ? $"Task '{x.Title}'" : null,
            LockedEntity.TaskType => await taskTypes.GetById(project, id, ct) is { } x ? $"Task type '{x.Name}'" : null,
            LockedEntity.Status => await statuses.GetById(project, id, ct) is { } x ? $"Status '{x.Name}'" : null,
            LockedEntity.StatusSet => await statusSets.GetById(project, id, ct) is { } x ? $"Status set '{x.Name}'" : null,
            LockedEntity.Board => await boards.GetById(project, id, ct) is { } x ? $"Board '{x.Name}'" : null,
            LockedEntity.Series => await series.GetById(project, id, ct) is { } x ? $"Series '{x.Name}'" : null,
            LockedEntity.User => await users.GetById(id, ct) is { } x ? $"User '{x.Username}'" : null,
            LockedEntity.LinkType => await linkTypes.GetById(project, id, ct) is { } x ? $"Link type '{x.Name}'" : null,
            LockedEntity.Field => await fields.GetById(project, id, ct) is { } x ? $"Field '{x.Name}'" : null,
            LockedEntity.Enum => await fieldEnums.GetById(project, id, ct) is { } x ? $"Enum '{x.Name}'" : null,
            _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, null)
        };
    }
}
