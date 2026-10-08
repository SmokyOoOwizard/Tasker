namespace Tasker.Storage.Db.Models;

internal class TaskTypeDbModel
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid StatusSetId { get; set; }
    public int Version { get; set; }

    public List<TaskTypeFieldDbModel> Fields { get; set; } = [];
}

/// <summary>Поле каталога, подключённое к типу задачи; Position задаёт порядок. Удаляется вместе с типом; поле, которое подключено, удалить нельзя.</summary>
internal class TaskTypeFieldDbModel
{
    public Guid TypeId { get; set; }
    public Guid FieldId { get; set; }
    public bool Required { get; set; }
    public int Position { get; set; }
}
