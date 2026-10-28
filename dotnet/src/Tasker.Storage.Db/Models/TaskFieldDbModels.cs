namespace Tasker.Storage.Db.Models;

/// <summary>
/// Поле, записанное в задаче: значения и дополнительные поля. FieldId — поле каталога (без внешнего ключа: общий столбец
/// с собственными полями) или, если заполнено OwnName, id собственного поля в этой задаче. Собственное поле хранит своё полное
/// определение здесь же. Position задаёт порядок; поле удаляется вместе с задачей. OwnEnumId — внешний ключ: перечисление,
/// на которое ссылается собственное поле, удалить нельзя.
/// </summary>
internal class TaskFieldDbModel
{
    public Guid TaskId { get; set; }
    public Guid FieldId { get; set; }
    public int Position { get; set; }

    public string? OwnName { get; set; }

    /// <summary>Имя собственного поля в нижнем регистре (<see cref="Core.Tasks.FieldNames.Key"/>): по нему фильтр находит собственные поля.</summary>
    public string? OwnKey { get; set; }
    public int? OwnType { get; set; }
    public bool OwnRequired { get; set; }
    public bool OwnMultiple { get; set; }
    public Guid? OwnEnumId { get; set; }
}

/// <summary>
/// Значение поля задачи: канонический текст (см. Tasker.Core.Fields.FieldValues); Position — порядок значений. Number — то же значение числом
/// (<see cref="Core.Tasks.FieldNumbers"/>; null, если не число): по нему идут сравнения int и float. Удаляется вместе с полем задачи.
/// </summary>
internal class TaskFieldValueDbModel
{
    public Guid TaskId { get; set; }
    public Guid FieldId { get; set; }
    public int Position { get; set; }
    public required string Value { get; set; }
    public double? Number { get; set; }

    /// <summary>Собственное поле: имя в нижнем регистре и тип (как у <see cref="TaskFieldDbModel"/>); у поля каталога null. Фильтр по собственным полям идёт по ним.</summary>
    public string? OwnKey { get; set; }
    public int? OwnType { get; set; }
}
