using Tasker.Core.Comments;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

internal class TaskCommentStorage(TaskerDirectory directory, WorkspaceIndex index) : ITaskCommentStorage
{
    private readonly EntityFiles<TaskComment> files = new(directory, index, EntityFolders.Comments,
        x => x.Id, x => x.ProjectId, x => TaskCommentFile.FileSlug(x.TaskId), TaskCommentFile.Read, TaskCommentFile.Write, TaskCommentFile.Update);

    public Task<TaskComment?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => files.GetById(projectId, id, ct);

    public Task<TaskComment[]> GetByTask(Guid projectId, Guid taskId, CancellationToken ct = default) =>
        index.All<TaskComment>(new IndexQuery(IndexKind.Comment) { ProjectId = projectId, TypeIds = [taskId] }, ct);

    public Task<TaskComment[]> GetAll(Guid projectId, CancellationToken ct = default) =>
        index.All<TaskComment>(new IndexQuery(IndexKind.Comment) { ProjectId = projectId }, ct);

    public Task<string> Add(TaskComment comment, CancellationToken ct = default) => files.Add(comment, ct);

    public Task<string?> Update(TaskComment comment, string expectedVersion, CancellationToken ct = default) =>
        files.Update(comment, expectedVersion, ct);

    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) =>
        files.Delete(projectId, id, expectedVersion, ct);

    public async Task<int> DeleteByTask(Guid projectId, Guid taskId, CancellationToken ct = default)
    {
        var deleted = 0;
        foreach (var comment in await GetByTask(projectId, taskId, ct))
        {
            if (await files.Delete(projectId, comment.Id, comment.Version, ct))
                deleted++;
        }
        return deleted;
    }

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => files.CountUnreadable(projectId, ct);
}
