using Microsoft.EntityFrameworkCore;
using Tasker.Core.Projects;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

internal class ProjectMemberStorage(AppDbContext context) : IProjectMemberStorage
{
    public Task<Guid[]> GetUserIds(Guid projectId, CancellationToken ct = default) =>
        context.ProjectMembers.Where(x => x.ProjectId == projectId).Select(x => x.UserId).ToArrayAsync(ct);

    public Task<Guid[]> GetProjectIds(Guid userId, CancellationToken ct = default) =>
        context.ProjectMembers.Where(x => x.UserId == userId).Select(x => x.ProjectId).ToArrayAsync(ct);

    public Task<bool> IsMember(Guid projectId, Guid userId, CancellationToken ct = default) =>
        context.ProjectMembers.AnyAsync(x => x.ProjectId == projectId && x.UserId == userId, ct);

    public async Task Add(Guid projectId, Guid userId, CancellationToken ct = default)
    {
        if (await IsMember(projectId, userId, ct))
            return;

        context.ProjectMembers.Add(new ProjectMemberDbModel { ProjectId = projectId, UserId = userId });
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            // Параллельный запрос уже добавил этого участника — результат тот же.
        }
    }

    public async Task<bool> Remove(Guid projectId, Guid userId, CancellationToken ct = default) =>
        await context.ProjectMembers
            .Where(x => x.ProjectId == projectId && x.UserId == userId)
            .ExecuteDeleteAsync(ct) > 0;
}
