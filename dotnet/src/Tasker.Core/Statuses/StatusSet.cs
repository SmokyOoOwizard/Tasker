namespace Tasker.Core.Statuses;

/// <summary>Набор статусов — список возможных статусов для задач определённого типа.</summary>
public record StatusSet
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required string Name { get; init; }

    /// <summary>Статусы набора в порядке отображения (например, колонки доски слева направо).</summary>
    public required Guid[] StatusIds { get; init; }

    /// <summary>См. <see cref="Versioning"/>.</summary>
    public required string Version { get; init; }
}
