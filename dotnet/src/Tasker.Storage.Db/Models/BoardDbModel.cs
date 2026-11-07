namespace Tasker.Storage.Db.Models;

internal class BoardDbModel
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public int Version { get; set; }

    public List<BoardStatusSetDbModel> StatusSets { get; set; } = [];
    public List<BoardColumnDbModel> Columns { get; set; } = [];
}

/// <summary>Набор статусов, включённый в доску.</summary>
internal class BoardStatusSetDbModel
{
    public Guid BoardId { get; set; }
    public Guid StatusSetId { get; set; }
    public int Position { get; set; }
}

internal class BoardColumnDbModel
{
    public Guid Id { get; set; }
    public Guid BoardId { get; set; }
    public required string Name { get; set; }
    public int Position { get; set; }

    public List<BoardColumnStatusDbModel> Statuses { get; set; } = [];
    public List<BoardColumnDropStatusDbModel> DropStatuses { get; set; } = [];
    public string? Query { get; set; }
}

/// <summary>Статус, задачи с которым попадают в колонку.</summary>
internal class BoardColumnStatusDbModel
{
    public Guid ColumnId { get; set; }
    public Guid StatusId { get; set; }
    public int Position { get; set; }
}

/// <summary>Какой статус получает задача набора StatusSetId, перетянутая в колонку.</summary>
internal class BoardColumnDropStatusDbModel
{
    public Guid ColumnId { get; set; }
    public Guid StatusSetId { get; set; }
    public Guid StatusId { get; set; }
}
