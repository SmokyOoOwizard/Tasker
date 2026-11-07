namespace Tasker.Storage.Db.Models;

internal class TaskCommentDbModel
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid TaskId { get; set; }
    public Guid? ParentId { get; set; }
    public Guid AuthorId { get; set; }
    public required string AuthorName { get; set; }
    public int AuthorKind { get; set; }
    public required string Body { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EditedAt { get; set; }
    public bool Deleted { get; set; }
    public int Version { get; set; }
}
