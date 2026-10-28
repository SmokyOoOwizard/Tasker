namespace Tasker.Storage.Db.Models;

internal class StatusDbModel
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public required string Color { get; set; }
    public string? Description { get; set; }
    public int Version { get; set; }
}
