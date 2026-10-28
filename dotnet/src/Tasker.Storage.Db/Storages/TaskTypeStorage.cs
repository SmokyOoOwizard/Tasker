using Microsoft.EntityFrameworkCore;
using Tasker.Core.Dto;
using Tasker.Core.Tasks;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

internal class TaskTypeStorage(AppDbContext context) : ITaskTypeStorage
{
    public async Task<TaskType?> GetById(Guid projectId, Guid id, CancellationToken ct = default)
    {
        var model = await context.TaskTypes
            .AsNoTracking()
            .Include(x => x.Fields)
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == id, ct);

        return model == null ? null : Map(model);
    }

    public async Task<TaskType[]> GetAll(Guid projectId, CancellationToken ct = default)
    {
        var models = await Ordered(projectId).ToArrayAsync(ct);
        return models.Select(Map).ToArray();
    }

    public Task<ListDto<TaskType>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        Ordered(projectId).ToPage(page, Map, ct);

    private IOrderedQueryable<TaskTypeDbModel> Ordered(Guid projectId) => context.TaskTypes
        .AsNoTracking()
        .Include(x => x.Fields)
        .Where(x => x.ProjectId == projectId)
        .OrderBy(x => x.Name)
        .ThenBy(x => x.Id);

    public async Task<string> Add(TaskType type, CancellationToken ct = default)
    {
        var model = new TaskTypeDbModel
        {
            Id = type.Id,
            ProjectId = type.ProjectId,
            Name = type.Name,
            Description = DbDescription.ToColumn(type.Description),
            StatusSetId = type.StatusSetId,
            Version = DbVersion.Initial,
            Fields = Fields(type)
        };
        context.TaskTypes.Add(model);
        try
        {
            await context.SaveChangesAsync(ct);
        }
        finally
        {
            context.Entry(model).State = EntityState.Detached;
            foreach (var field in model.Fields)
                context.Entry(field).State = EntityState.Detached;
        }
        return DbVersion.ToText(DbVersion.Initial);
    }

    public async Task<string?> Update(TaskType type, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return null;

        // Строку типа не пересоздаём: на неё ссылаются задачи. Сначала — условное обновление версии; список полей заменяем целиком
        // в одной транзакции с именем и версией (внутри IWriteScope транзакция уже открыта — берём её).
        var own = context.Database.CurrentTransaction == null
            ? await context.Database.BeginTransactionAsync(ct)
            : null;
        await using (own)
        {
            var updated = await context.TaskTypes
                .Where(x => x.ProjectId == type.ProjectId && x.Id == type.Id && x.Version == expected)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Name, type.Name)
                    .SetProperty(x => x.Description, DbDescription.ToColumn(type.Description))
                    .SetProperty(x => x.StatusSetId, type.StatusSetId)
                    .SetProperty(x => x.Version, expected + 1), ct);
            if (updated == 0)
                return null;

            await context.Set<TaskTypeFieldDbModel>().Where(x => x.TypeId == type.Id).ExecuteDeleteAsync(ct);
            var fields = Fields(type);
            if (fields.Count > 0)
            {
                context.Set<TaskTypeFieldDbModel>().AddRange(fields);
                try
                {
                    await context.SaveChangesAsync(ct);
                }
                finally
                {
                    foreach (var field in fields)
                        context.Entry(field).State = EntityState.Detached;
                }
            }

            if (own != null)
                await own.CommitAsync(ct);
            return DbVersion.ToText(expected + 1);
        }
    }

    public async Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return false;

        return await context.TaskTypes
            .Where(x => x.ProjectId == projectId && x.Id == id && x.Version == expected)
            .ExecuteDeleteAsync(ct) > 0;
    }

    private static List<TaskTypeFieldDbModel> Fields(TaskType type) => type.Fields
        .Select((x, i) => new TaskTypeFieldDbModel { TypeId = type.Id, FieldId = x.FieldId, Required = x.Required, Position = i })
        .ToList();

    private static TaskType Map(TaskTypeDbModel model) => new()
    {
        Id = model.Id,
        ProjectId = model.ProjectId,
        Name = model.Name,
        Description = DbDescription.FromColumn(model.Description),
        StatusSetId = model.StatusSetId,
        Fields = model.Fields.OrderBy(x => x.Position).Select(x => new TaskTypeField(x.FieldId, x.Required)).ToArray(),
        Version = DbVersion.ToText(model.Version)
    };
}
