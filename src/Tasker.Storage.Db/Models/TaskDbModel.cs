namespace Tasker.Storage.Db.Models;

internal class TaskDbModel
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }

    public Guid TypeId { get; set; }
    public Guid StatusId { get; set; }

    // UTC. DateTime, а не DateTimeOffset: провайдер SQLite не умеет сортировать DateTimeOffset.
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public int Version { get; set; }
}
