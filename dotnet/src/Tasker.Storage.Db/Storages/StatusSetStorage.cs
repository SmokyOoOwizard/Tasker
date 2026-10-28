using Microsoft.EntityFrameworkCore;
using Tasker.Core.Dto;
using Tasker.Core.Statuses;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

internal class StatusSetStorage(AppDbContext context) : IStatusSetStorage
{
    public async Task<StatusSet?> GetById(Guid projectId, Guid id, CancellationToken ct = default)
    {
        var model = await Query(projectId).FirstOrDefaultAsync(x => x.Id == id, ct);
        return model == null ? null : Map(model);
    }

    public async Task<StatusSet[]> GetAll(Guid projectId, CancellationToken ct = default)
    {
        var models = await Query(projectId).OrderBy(x => x.Name).ThenBy(x => x.Id).ToArrayAsync(ct);
        return models.Select(Map).ToArray();
    }

    public Task<ListDto<StatusSet>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        Query(projectId).OrderBy(x => x.Name).ThenBy(x => x.Id).ToPage(page, Map, ct);

    public async Task<string> Add(StatusSet set, CancellationToken ct = default)
    {
        context.StatusSets.Add(new StatusSetDbModel
        {
            Id = set.Id,
            ProjectId = set.ProjectId,
            Name = set.Name,
            Version = DbVersion.Initial,
            Items = Items(set)
        });
        await context.SaveChangesAsync(ct);
        return DbVersion.ToText(DbVersion.Initial);
    }

    // Строку набора не пересоздаём: на неё ссылаются типы задач и доски.
    // Сначала — условное обновление версии: если набор уже изменили, дальше не идём.
    // Список статусов заменяем целиком — в одной транзакции с именем и версией.
    public async Task<string?> Update(StatusSet set, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return null;

        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        var updated = await context.StatusSets
            .Where(x => x.ProjectId == set.ProjectId && x.Id == set.Id && x.Version == expected)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, set.Name)
                .SetProperty(x => x.Version, expected + 1), ct);
        if (updated == 0)
            return null;

        await context.Set<StatusSetItemDbModel>()
            .Where(x => x.StatusSetId == set.Id)
            .ExecuteDeleteAsync(ct);

        context.Set<StatusSetItemDbModel>().AddRange(Items(set));
        await context.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);
        return DbVersion.ToText(expected + 1);
    }

    public async Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return false;

        return await context.StatusSets
            .Where(x => x.ProjectId == projectId && x.Id == id && x.Version == expected)
            .ExecuteDeleteAsync(ct) > 0;
    }

    private static List<StatusSetItemDbModel> Items(StatusSet set) => set.StatusIds
        .Select((statusId, i) => new StatusSetItemDbModel { StatusSetId = set.Id, StatusId = statusId, Position = i })
        .ToList();

    private IQueryable<StatusSetDbModel> Query(Guid projectId) => context.StatusSets
        .AsNoTracking()
        .Include(x => x.Items)
        .Where(x => x.ProjectId == projectId);

    private static StatusSet Map(StatusSetDbModel model) => new()
    {
        Id = model.Id,
        ProjectId = model.ProjectId,
        Name = model.Name,
        StatusIds = model.Items
            .OrderBy(x => x.Position)
            .Select(x => x.StatusId)
            .ToArray(),
        Version = DbVersion.ToText(model.Version)
    };
}
