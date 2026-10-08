namespace Tasker.Storage.Db.Models;

/// <summary>
/// Блокировка сущности на время правки (см. Tasker.Core.Locks). Ссылки на саму сущность нет: тип разный,
/// а блокировка временная — устаревшие строки (сущность удалена, срок вышел) безвредны и перезаписываются.
/// </summary>
internal class EditLockDbModel
{
    public required string Entity { get; set; }
    public Guid Id { get; set; }
    public required string HolderKey { get; set; }
    public required string HolderName { get; set; }

    /// <summary>Проект сущности — для списка блокировок проекта; ссылки нет по той же причине, что и на сущность.</summary>
    public Guid? ProjectId { get; set; }

    // UTC. DateTime, а не DateTimeOffset: провайдер SQLite не умеет сравнивать DateTimeOffset.
    public DateTime AcquiredAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
