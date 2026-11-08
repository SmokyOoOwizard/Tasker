using Microsoft.EntityFrameworkCore;
using Tasker.Core.Agents;
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

    public async Task Add(Guid projectId, Guid userId, AgentRights? rights, CancellationToken ct = default)
    {
        if (await IsMember(projectId, userId, ct))
            return;

        context.ProjectMembers.Add(new ProjectMemberDbModel { ProjectId = projectId, UserId = userId, AgentRights = rights?.ToMask() });
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

    public async Task<ProjectMember?> GetMember(Guid projectId, Guid userId, CancellationToken ct = default) =>
        await context.ProjectMembers.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.UserId == userId)
            .Select(x => new { x.ProjectId, x.UserId, x.AgentRights })
            .FirstOrDefaultAsync(ct) is { } row
            ? Map(row.ProjectId, row.UserId, row.AgentRights)
            : null;

    public async Task<ProjectMember[]> GetMembers(Guid projectId, CancellationToken ct = default) =>
        (await context.ProjectMembers.AsNoTracking().Where(x => x.ProjectId == projectId)
            .Select(x => new { x.ProjectId, x.UserId, x.AgentRights }).ToArrayAsync(ct))
        .Select(x => Map(x.ProjectId, x.UserId, x.AgentRights)).ToArray();

    public async Task<ProjectMember[]> GetMemberships(Guid userId, CancellationToken ct = default) =>
        (await context.ProjectMembers.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => new { x.ProjectId, x.UserId, x.AgentRights }).ToArrayAsync(ct))
        .Select(x => Map(x.ProjectId, x.UserId, x.AgentRights)).ToArray();

    public async Task<bool> SetRights(Guid projectId, Guid userId, AgentRights? rights, CancellationToken ct = default)
    {
        int? mask = rights?.ToMask();
        return await context.ProjectMembers
            .Where(x => x.ProjectId == projectId && x.UserId == userId)
            .ExecuteUpdateAsync(x => x.SetProperty(m => m.AgentRights, mask), ct) > 0;
    }

    private static ProjectMember Map(Guid projectId, Guid userId, int? mask) =>
        new(projectId, userId, mask is { } value ? AgentRights.FromMask(value) : null);
}
