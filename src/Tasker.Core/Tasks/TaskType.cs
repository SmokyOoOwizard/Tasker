namespace Tasker.Core.Tasks;

/// <summary>Поле каталога, подключённое к типу задачи.</summary>
/// <param name="FieldId">Ссылка на <see cref="Fields.FieldDefinition"/>.</param>
/// <param name="Required">У задачи этого типа должно быть значение: проверяется при создании и правке задачи.</param>
public record TaskTypeField(Guid FieldId, bool Required);

/// <summary>
/// Тип задачи (например «Баг», «Фича»). Определяет, какие статусы доступны задачам этого типа:
/// задача → тип → <see cref="Statuses.StatusSet"/> → статусы; и какие поля есть у каждой задачи типа.
/// </summary>
public record TaskType
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required string Name { get; init; }

    /// <summary>Когда использовать тип; пустая строка — описания нет. В списках может быть усечено (<see cref="TaskTypeListItem"/>).</summary>
    public string Description { get; init; } = "";

    /// <summary>Ссылка на <see cref="Statuses.StatusSet"/>.</summary>
    public required Guid StatusSetId { get; init; }

    /// <summary>Поля типа в порядке отображения: они есть у каждой задачи типа и у задачи не удаляются.</summary>
    public IReadOnlyList<TaskTypeField> Fields { get; init; } = [];

    /// <summary>См. <see cref="Versioning"/>.</summary>
    public required string Version { get; init; }
}
