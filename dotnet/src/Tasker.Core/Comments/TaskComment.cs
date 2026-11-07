using Tasker.Core.Users;

namespace Tasker.Core.Comments;

public record TaskComment
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid TaskId { get; init; }
    public Guid? ParentId { get; init; }
    public required Guid AuthorId { get; init; }
    public required string AuthorName { get; init; }
    public UserKind AuthorKind { get; init; } = UserKind.Human;
    public string Body { get; init; } = "";
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? EditedAt { get; init; }
    public bool Deleted { get; init; }
    public required string Version { get; init; }
}

public record CommentAuthor(Guid Id, string Name, UserKind Kind);

public record TaskCommentView(
    Guid Id,
    Guid TaskId,
    Guid? ParentId,
    int Depth,
    CommentAuthor? Author,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    bool Deleted,
    bool Mine,
    string Version);

public record AddTaskComment(string Body, Guid? ParentId = null);

public record UpdateTaskComment(string Body, string? Version);

public interface ITaskCommentStorage
{
    Task<TaskComment?> GetById(Guid projectId, Guid id, CancellationToken ct = default);

    Task<TaskComment[]> GetByTask(Guid projectId, Guid taskId, CancellationToken ct = default);

    Task<TaskComment[]> GetAll(Guid projectId, CancellationToken ct = default);

    Task<string> Add(TaskComment comment, CancellationToken ct = default);

    Task<string?> Update(TaskComment comment, string expectedVersion, CancellationToken ct = default);

    Task<int> DeleteByTask(Guid projectId, Guid taskId, CancellationToken ct = default);

    Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default);

    Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default);
}
