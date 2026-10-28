using Microsoft.EntityFrameworkCore;
using Tasker.Core.Dto;
using Tasker.Core.Statuses;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

internal class StatusStorage(AppDbContext context) : IStatusStorage
{
    public async Task<Status?> GetById(Guid projectId, Guid id, CancellationToken ct = default)
    {
        var model = await context.Statuses
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == id, ct);

        return model == null ? null : Map(model);
    }

    public async Task<Status[]> GetAll(Guid projectId, CancellationToken ct = default)
    {
        var models = await Ordered(projectId).ToArrayAsync(ct);
        return models.Select(Map).ToArray();
    }

    public Task<ListDto<Status>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        Ordered(projectId).ToPage(page, Map, ct);

    private IOrderedQueryable<StatusDbModel> Ordered(Guid projectId) => context.Statuses
        .AsNoTracking()
        .Where(x => x.ProjectId == projectId)
        .OrderBy(x => x.Name)
        .ThenBy(x => x.Id);

    public async Task<string> Add(Status status, CancellationToken ct = default)
    {
        context.Statuses.Add(new StatusDbModel
        {
            Id = status.Id,
            ProjectId = status.ProjectId,
            Name = status.Name,
            Color = status.Color,
            Description = DbDescription.ToColumn(status.Description),
            Version = DbVersion.Initial
        });
        await context.SaveChangesAsync(ct);
        return DbVersion.ToText(DbVersion.Initial);
    }

    public async Task<string?> Update(Status status, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return null;

        var updated = await context.Statuses
            .Where(x => x.ProjectId == status.ProjectId && x.Id == status.Id && x.Version == expected)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, status.Name)
                .SetProperty(x => x.Color, status.Color)
                .SetProperty(x => x.Description, DbDescription.ToColumn(status.Description))
                .SetProperty(x => x.Version, expected + 1), ct);

        return updated == 0 ? null : DbVersion.ToText(expected + 1);
    }

    public async Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return false;

        return await context.Statuses
            .Where(x => x.ProjectId == projectId && x.Id == id && x.Version == expected)
            .ExecuteDeleteAsync(ct) > 0;
    }

    private static Status Map(StatusDbModel model) => new()
    {
        Id = model.Id,
        ProjectId = model.ProjectId,
        Name = model.Name,
        Color = model.Color,
        Description = DbDescription.FromColumn(model.Description),
        Version = DbVersion.ToText(model.Version)
    };
}
