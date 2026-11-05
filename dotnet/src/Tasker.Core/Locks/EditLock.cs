namespace Tasker.Core.Locks;

/// <summary>Какие сущности можно блокировать на время правки.</summary>
public enum LockedEntity
{
    Project,
    Task,
    TaskType,
    Status,
    StatusSet,
    Board,
    Series,
    User,
    LinkType,
    Field,
    Enum
}

/// <summary>Кто держит блокировку.</summary>
/// <param name="Key">Идентичность: один и тот же ключ — один и тот же держатель (<c>user:&lt;id&gt;</c>, <c>local</c>).</param>
/// <param name="Name">Как показывать другим: «правит Иван».</param>
public record EditHolder(string Key, string Name);

/// <summary>Блокировка сущности на время правки. Действует до <see cref="ExpiresAt"/>, дальше считается снятой.</summary>
/// <param name="ProjectId">Проект сущности (у самого проекта — он сам): по нему отбираются блокировки проекта. null — вне проектов или не указан.</param>
public record EditLock(LockedEntity Entity, Guid Id, EditHolder Holder, DateTimeOffset AcquiredAt, DateTimeOffset ExpiresAt, Guid? ProjectId = null);

public class TaskerLockedException : TaskerConflictException
{
    internal TaskerLockedException(TaskerError error, EditLock heldBy) : base(error, ConflictCode.Locked)
    {
        HeldBy = heldBy;
    }

    public EditLock HeldBy { get; }
}
