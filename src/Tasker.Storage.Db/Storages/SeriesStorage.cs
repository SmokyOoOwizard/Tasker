using Microsoft.EntityFrameworkCore;
using Tasker.Core.Dto;
using Tasker.Core.TaskSeries;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

internal class SeriesStorage(AppDbContext context) : ISeriesStorage
{
    public async Task<Series?> GetById(Guid projectId, Guid id, CancellationToken ct = default)
    {
        var model = await context.Series
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == id, ct);

        return model == null ? null : Map(model);
    }

    public async Task<Series[]> GetAll(Guid projectId, CancellationToken ct = default) =>
        Order(await Load(projectId, ct));

    public async Task<ListDto<Series>> GetRange(Guid projectId, Page page, CancellationToken ct = default)
    {
        var all = Order(await Load(projectId, ct));
        return new()
        {
            TotalCount = all.Length,
            Offset = page.Offset,
            Limit = page.Limit,
            Data = all.Skip(page.Offset).Take(page.Limit).ToArray()
        };
    }

    private Task<SeriesDbModel[]> Load(Guid projectId, CancellationToken ct) => context.Series
        .AsNoTracking()
        .Where(x => x.ProjectId == projectId)
        .ToArrayAsync(ct);

    // Порядок — по префиксу посимвольно (ordinal), затем по id. В памяти, а не в SQL: порядок строк в БД зависит
    // от сортировки провайдера (у Postgres она по локали и не различает регистр так, как SQLite), а серий в проекте немного.
    private static Series[] Order(SeriesDbModel[] models) => models
        .OrderBy(x => x.Prefix, StringComparer.Ordinal)
        .ThenBy(x => x.Id)
        .Select(Map)
        .ToArray();

    public async Task<string> Add(Series series, CancellationToken ct = default)
    {
        var model = new SeriesDbModel
        {
            Id = series.Id,
            ProjectId = series.ProjectId,
            Name = series.Name,
            Prefix = series.Prefix,
            Version = DbVersion.Initial
        };
        context.Series.Add(model);
        try
        {
            await context.SaveChangesAsync(ct);
        }
        finally
        {
            // Записанная (или отвергнутая, например, уникальным индексом префикса) серия не должна висеть в трекере:
            // иначе следующее сохранение на этом контексте повторит её вставку.
            context.Entry(model).State = EntityState.Detached;
        }
        return DbVersion.ToText(DbVersion.Initial);
    }

    public async Task<string?> Update(Series series, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return null;

        var updated = await context.Series
            .Where(x => x.ProjectId == series.ProjectId && x.Id == series.Id && x.Version == expected)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, series.Name)
                .SetProperty(x => x.Prefix, series.Prefix)
                .SetProperty(x => x.Version, expected + 1), ct);

        return updated == 0 ? null : DbVersion.ToText(expected + 1);
    }

    /// <summary>Номера задач в этой серии остаются недействительными ссылками — их уберёт чистка.</summary>
    public async Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return false;

        return await context.Series
            .Where(x => x.ProjectId == projectId && x.Id == id && x.Version == expected)
            .ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>В БД нечитаемых серий не бывает.</summary>
    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => Task.FromResult(0);

    private static Series Map(SeriesDbModel model) => new()
    {
        Id = model.Id,
        ProjectId = model.ProjectId,
        Name = model.Name,
        Prefix = model.Prefix,
        Version = DbVersion.ToText(model.Version)
    };
}
