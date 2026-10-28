namespace Tasker.Storage.Db.Models;

internal class StatusSetDbModel
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public int Version { get; set; }

    public List<StatusSetItemDbModel> Items { get; set; } = [];
}

/// <summary>Статус внутри набора; Position задаёт порядок.</summary>
internal class StatusSetItemDbModel
{
    public Guid StatusSetId { get; set; }
    public Guid StatusId { get; set; }
    public int Position { get; set; }
}
