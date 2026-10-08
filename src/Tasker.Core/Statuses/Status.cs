namespace Tasker.Core.Statuses;

/// <summary>
/// Настраиваемый статус задачи (например «Открыта», «В работе», «Готово»).
/// Порядок статусов задаёт <see cref="StatusSet"/>: один статус может стоять в разных наборах на разных местах.
/// </summary>
public record Status
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required string Name { get; init; }

    /// <summary>Цвет в формате #RRGGBB.</summary>
    public required string Color { get; init; }

    /// <summary>Что означает статус и когда его ставить; пустая строка — описания нет. В списках может быть усечено (<see cref="StatusListItem"/>).</summary>
    public string Description { get; init; } = "";

    /// <summary>См. <see cref="Versioning"/>.</summary>
    public required string Version { get; init; }
}
