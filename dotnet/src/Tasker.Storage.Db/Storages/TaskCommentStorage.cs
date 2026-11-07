using Microsoft.EntityFrameworkCore;
using Tasker.Core.Comments;
using Tasker.Core.Users;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

internal class TaskCommentStorage(AppDbContext context) : ITaskCommentStorage
{
    public async Task<TaskComment?> GetById(Guid projectId, Guid id, CancellationToken ct = default)
    {
        var model = await context.TaskComments.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == id, ct);
        return model == null ? null : Map(model);
    }

    public async Task<TaskComment[]> GetByTask(Guid projectId, Guid taskId, CancellationToken ct = default)
    {
        var models = await context.TaskComments.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.TaskId == taskId)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToArrayAsync(ct);
        return models.Select(Map).ToArray();
    }

    public async Task<TaskComment[]> GetAll(Guid projectId, CancellationToken ct = default)
    {
        var models = await context.TaskComments.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToArrayAsync(ct);
        return models.Select(Map).ToArray();
    }

    public async Task<string> Add(TaskComment comment, CancellationToken ct = default)
    {
        context.TaskComments.Add(new TaskCommentDbModel
        {
            Id = comment.Id,
            ProjectId = comment.ProjectId,
            TaskId = comment.TaskId,
            ParentId = comment.ParentId,
            AuthorId = comment.AuthorId,
            AuthorName = comment.AuthorName,
            AuthorKind = (int)comment.AuthorKind,
            Body = comment.Body,
            CreatedAt = DbTime.ToDb(comment.CreatedAt),
            EditedAt = comment.EditedAt is { } edited ? DbTime.ToDb(edited) : null,
            Deleted = comment.Deleted,
            Version = DbVersion.Initial
        });
        await context.SaveChangesAsync(ct);
        return DbVersion.ToText(DbVersion.Initial);
    }

    public async Task<string?> Update(TaskComment comment, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return null;

        DateTime? edited = comment.EditedAt is { } value ? DbTime.ToDb(value) : null;
        var updated = await context.TaskComments
            .Where(x => x.ProjectId == comment.ProjectId && x.Id == comment.Id && x.Version == expected)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Body, comment.Body)
                .SetProperty(x => x.EditedAt, edited)
                .SetProperty(x => x.Deleted, comment.Deleted)
                .SetProperty(x => x.Version, expected + 1), ct);

        return updated == 0 ? null : DbVersion.ToText(expected + 1);
    }

    public Task<int> DeleteByTask(Guid projectId, Guid taskId, CancellationToken ct = default) =>
        context.TaskComments.Where(x => x.ProjectId == projectId && x.TaskId == taskId).ExecuteDeleteAsync(ct);

    public async Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return false;

        return await context.TaskComments
            .Where(x => x.ProjectId == projectId && x.Id == id && x.Version == expected)
            .ExecuteDeleteAsync(ct) > 0;
    }

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => Task.FromResult(0);

    private static TaskComment Map(TaskCommentDbModel model) => new()
    {
        Id = model.Id,
        ProjectId = model.ProjectId,
        TaskId = model.TaskId,
        ParentId = model.ParentId,
        AuthorId = model.AuthorId,
        AuthorName = model.AuthorName,
        AuthorKind = (UserKind)model.AuthorKind,
        Body = model.Body,
        CreatedAt = DbTime.FromDb(model.CreatedAt),
        EditedAt = model.EditedAt is { } edited ? DbTime.FromDb(edited) : null,
        Deleted = model.Deleted,
        Version = DbVersion.ToText(model.Version)
    };
}
