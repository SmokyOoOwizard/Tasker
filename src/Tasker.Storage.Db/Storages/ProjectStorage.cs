using Microsoft.EntityFrameworkCore;
using Tasker.Core.Dto;
using Tasker.Core.Projects;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

internal class ProjectStorage(AppDbContext context) : IProjectStorage
{
    public async Task<Project?> GetById(Guid id, CancellationToken ct = default)
    {
        var model = await context.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        return model == null ? null : Map(model);
    }

    public async Task<ListDto<Project>> GetRange(Guid[]? ids, Page page, CancellationToken ct = default)
    {
        var filtered = context.Projects.AsNoTracking();
        if (ids != null)
            filtered = filtered.Where(x => ids.Contains(x.Id));

        return await filtered.OrderBy(x => x.Name).ThenBy(x => x.Id).ToPage(page, Map, ct);
    }

    public async Task<string> Add(Project project, CancellationToken ct = default)
    {
        context.Projects.Add(new ProjectDbModel
        {
            Id = project.Id,
            Name = project.Name,
            CreatedAt = DbTime.ToDb(project.CreatedAt),
            Version = DbVersion.Initial
        });
        await context.SaveChangesAsync(ct);
        return DbVersion.ToText(DbVersion.Initial);
    }

    public async Task<string?> Update(Project project, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return null;

        var updated = await context.Projects
            .Where(x => x.Id == project.Id && x.Version == expected)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, project.Name)
                .SetProperty(x => x.Version, expected + 1), ct);

        return updated == 0 ? null : DbVersion.ToText(expected + 1);
    }

    // Содержимое проекта удаляется каскадом на уровне БД.
    public async Task<bool> Delete(Guid id, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return false;

        return await context.Projects
            .Where(x => x.Id == id && x.Version == expected)
            .ExecuteDeleteAsync(ct) > 0;
    }

    private static Project Map(ProjectDbModel model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        CreatedAt = DbTime.FromDb(model.CreatedAt),
        Version = DbVersion.ToText(model.Version)
    };
}
