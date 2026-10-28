namespace Tasker.Storage.Db.Models;

internal class LinkTypeDbModel
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public required string OutwardName { get; set; }
    public required string InwardName { get; set; }

    /// <summary>Допускает ли тип циклы (<see cref="Tasker.Core.Links.LinkType.AllowCycles"/>).</summary>
    public bool AllowCycles { get; set; } = true;

    /// <summary>Иерархический ли тип (<see cref="Tasker.Core.Links.LinkType.Hierarchical"/>).</summary>
    public bool Hierarchical { get; set; }

    public int Version { get; set; }
}

/// <summary>
/// Исходящая связь задачи. TaskId — источник (удаляется вместе с задачей); TargetId — без внешнего ключа, как SeriesId у номеров:
/// удаление задачи-цели оставляет недействительную ссылку у источника, её убирает каскад сервиса и <c>cleanup</c>.
/// Тип связи — внешний ключ: тип, по которому есть связи, удалить нельзя.
/// </summary>
internal class TaskLinkDbModel
{
    public Guid TaskId { get; set; }
    public Guid TypeId { get; set; }
    public Guid TargetId { get; set; }
}
