namespace Tasker.Core.Boards;

/// <summary>
/// Доска: колонки, по которым раскладываются задачи в зависимости от статуса.
/// На доске видны задачи тех типов, чей набор статусов входит в <see cref="StatusSetIds"/>.
/// </summary>
public record Board
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required string Name { get; init; }

    /// <summary>Наборы статусов, задачи которых попадают на доску.</summary>
    public required Guid[] StatusSetIds { get; init; }

    /// <summary>Колонки слева направо.</summary>
    public required BoardColumn[] Columns { get; init; }

    /// <summary>
    /// См. <see cref="Versioning"/>. Одна версия на всю доску: колонки — часть доски
    /// и меняются только вместе с ней.
    /// </summary>
    public required string Version { get; init; }
}

public class BoardColumn
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Задача попадает в колонку, если её статус — один из этих (из любого набора доски).</summary>
    public required Guid[] StatusIds { get; init; }

    public string? Query { get; init; }

    /// <summary>
    /// Какой статус получает задача, перетянутая в колонку: ключ — набор статусов её типа,
    /// значение — статус из этого набора. Нет записи для набора — в колонку задачу этого набора перетянуть нельзя.
    /// </summary>
    public required IReadOnlyDictionary<Guid, Guid> DropStatuses { get; init; }

    public Guid? GetDropStatus(Guid statusSetId) =>
        DropStatuses.TryGetValue(statusSetId, out var statusId) ? statusId : null;
}
