using Microsoft.EntityFrameworkCore;
using Tasker.Core.Dto;
using Tasker.Core.Links;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

internal class LinkTypeStorage(AppDbContext context) : ILinkTypeStorage
{
    public async Task<LinkType?> GetById(Guid projectId, Guid id, CancellationToken ct = default)
    {
        var model = await context.LinkTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == id, ct);

        return model == null ? null : Map(model);
    }

    public async Task<LinkType[]> GetAll(Guid projectId, CancellationToken ct = default)
    {
        var models = await Ordered(projectId).ToArrayAsync(ct);
        return models.Select(Map).ToArray();
    }

    public Task<ListDto<LinkType>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        Ordered(projectId).ToPage(page, Map, ct);

    private IOrderedQueryable<LinkTypeDbModel> Ordered(Guid projectId) => context.LinkTypes
        .AsNoTracking()
        .Where(x => x.ProjectId == projectId)
        .OrderBy(x => x.Name)
        .ThenBy(x => x.Id);

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => Task.FromResult(0);

    public async Task<string> Add(LinkType type, CancellationToken ct = default)
    {
        var model = new LinkTypeDbModel
        {
            Id = type.Id,
            ProjectId = type.ProjectId,
            Name = type.Name,
            OutwardName = type.OutwardName,
            InwardName = type.InwardName,
            AllowCycles = type.AllowCycles,
            Hierarchical = type.Hierarchical,
            Version = DbVersion.Initial
        };
        context.LinkTypes.Add(model);
        try
        {
            await context.SaveChangesAsync(ct);
        }
        finally
        {
            // Отвергнутая запись (тот же id уже есть) не должна висеть в трекере и повторяться со следующим сохранением.
            context.Entry(model).State = EntityState.Detached;
        }
        return DbVersion.ToText(DbVersion.Initial);
    }

    public async Task<string?> Update(LinkType type, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return null;

        var updated = await context.LinkTypes
            .Where(x => x.ProjectId == type.ProjectId && x.Id == type.Id && x.Version == expected)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, type.Name)
                .SetProperty(x => x.OutwardName, type.OutwardName)
                .SetProperty(x => x.InwardName, type.InwardName)
                .SetProperty(x => x.AllowCycles, type.AllowCycles)
                .SetProperty(x => x.Hierarchical, type.Hierarchical)
                .SetProperty(x => x.Version, expected + 1), ct);

        return updated == 0 ? null : DbVersion.ToText(expected + 1);
    }

    public async Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return false;

        return await context.LinkTypes
            .Where(x => x.ProjectId == projectId && x.Id == id && x.Version == expected)
            .ExecuteDeleteAsync(ct) > 0;
    }

    private static LinkType Map(LinkTypeDbModel model) => new()
    {
        Id = model.Id,
        ProjectId = model.ProjectId,
        Name = model.Name,
        OutwardName = model.OutwardName,
        InwardName = model.InwardName,
        AllowCycles = model.AllowCycles,
        Hierarchical = model.Hierarchical,
        Version = DbVersion.ToText(model.Version)
    };
}
