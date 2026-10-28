namespace Tasker.Core;

public enum ConflictCode
{
    /// <summary>Сущность используется другими (например, статус — задачами) и не может быть удалена или изменена.</summary>
    InUse,

    /// <summary>Сущность изменил кто-то другой с тех пор, как клиент её прочитал (см. <see cref="Versioning"/>).</summary>
    Modified,

    /// <summary>Сущность сейчас правит другой (блокировка на время правки, см. <see cref="Locks.EditLockService"/>).</summary>
    Locked,

    /// <summary>Файл записан более новым Tasker: формат неизвестен (см. <see cref="Workspace.UnsupportedFormatException"/>).</summary>
    UnsupportedFormat
}

/// <summary>
/// Операция противоречит текущим данным — например, удаляется статус, который используют задачи,
/// или изменяется запись, которую уже изменил кто-то другой. В API — 409 с кодом.
/// </summary>
public class TaskerConflictException(string message, ConflictCode code = ConflictCode.InUse) : Exception(message)
{
    public ConflictCode Code { get; } = code;
}
