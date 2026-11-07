using Tasker.Core.Comments;
using Tasker.Core.Users;

namespace Tasker.Storage.Files.Storages;

internal static class TaskCommentFile
{
    public static async Task<TaskComment?> Read(Guid projectId, string path, CancellationToken ct) =>
        await YamlFile.Read<TaskCommentFileModel>(path, ct) is { } file ? Map(projectId, file) : null;

    public static Task<string> Write(string path, TaskComment comment, CancellationToken ct) =>
        YamlFile.Write(path, ToFile(comment), ct);

    public static Task<string?> Update(string path, string newPath, TaskComment comment, string expectedVersion, CancellationToken ct) =>
        YamlFile.WriteIfMatch(path, newPath, ToFile(comment), expectedVersion, ct);

    public static string FileSlug(Guid taskId) => EntityFileNames.IdPrefix(taskId);

    private static TaskCommentFileModel ToFile(TaskComment comment) => new()
    {
        Id = comment.Id,
        TaskId = comment.TaskId,
        ParentId = comment.ParentId,
        AuthorId = comment.AuthorId,
        AuthorName = comment.AuthorName,
        AuthorKind = comment.AuthorKind == UserKind.Agent ? "agent" : "human",
        CreatedAt = comment.CreatedAt,
        EditedAt = comment.EditedAt,
        Deleted = comment.Deleted ? true : null,
        Body = comment.Deleted ? null : comment.Body
    };

    private static TaskComment Map(Guid projectId, Versioned<TaskCommentFileModel> file) => new()
    {
        Id = file.Model.Id,
        ProjectId = projectId,
        TaskId = file.Model.TaskId,
        ParentId = file.Model.ParentId,
        AuthorId = file.Model.AuthorId,
        AuthorName = file.Model.AuthorName ?? "",
        AuthorKind = string.Equals(file.Model.AuthorKind, "agent", StringComparison.OrdinalIgnoreCase) ? UserKind.Agent : UserKind.Human,
        CreatedAt = file.Model.CreatedAt,
        EditedAt = file.Model.EditedAt,
        Deleted = file.Model.Deleted ?? false,
        Body = file.Model.Body ?? "",
        Version = file.Version
    };

    private class TaskCommentFileModel : FileModel
    {
        public Guid Id { get; set; }
        public Guid TaskId { get; set; }
        public Guid? ParentId { get; set; }
        public Guid AuthorId { get; set; }
        public string? AuthorName { get; set; }
        public string? AuthorKind { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? EditedAt { get; set; }
        public bool? Deleted { get; set; }
        public string? Body { get; set; }
    }
}
