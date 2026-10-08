namespace Tasker.Storage.Db.Models;

internal class ProjectDbModel
{
    public Guid Id { get; set; }
    public required string Name { get; set; }

    // UTC, см. TaskDbModel.
    public DateTime CreatedAt { get; set; }

    public int Version { get; set; }
}
