using Microsoft.EntityFrameworkCore;
using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

internal class FieldStorage(AppDbContext context) : IFieldStorage
{
    public async Task<FieldDefinition?> GetById(Guid projectId, Guid id, CancellationToken ct = default)
    {
        var model = await context.Fields.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == id, ct);
        return model == null ? null : Map(model);
    }

    public async Task<FieldDefinition[]> GetAll(Guid projectId, CancellationToken ct = default) =>
        (await Ordered(projectId).ToArrayAsync(ct)).Select(Map).ToArray();

    public Task<ListDto<FieldDefinition>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        Ordered(projectId).ToPage(page, Map, ct);

    private IOrderedQueryable<FieldDbModel> Ordered(Guid projectId) => context.Fields
        .AsNoTracking()
        .Where(x => x.ProjectId == projectId)
        .OrderBy(x => x.Name)
        .ThenBy(x => x.Id);

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => Task.FromResult(0);

    public async Task<string> Add(FieldDefinition field, CancellationToken ct = default)
    {
        var model = new FieldDbModel
        {
            Id = field.Id,
            ProjectId = field.ProjectId,
            Name = field.Name,
            Type = (int)field.Type,
            Multiple = field.Multiple,
            EnumId = field.EnumId,
            Version = DbVersion.Initial
        };
        context.Fields.Add(model);
        try
        {
            await context.SaveChangesAsync(ct);
        }
        finally
        {
            context.Entry(model).State = EntityState.Detached;
        }
        return DbVersion.ToText(DbVersion.Initial);
    }

    public async Task<string?> Update(FieldDefinition field, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return null;

        var updated = await context.Fields
            .Where(x => x.ProjectId == field.ProjectId && x.Id == field.Id && x.Version == expected)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, field.Name)
                .SetProperty(x => x.Type, (int)field.Type)
                .SetProperty(x => x.Multiple, field.Multiple)
                .SetProperty(x => x.EnumId, field.EnumId)
                .SetProperty(x => x.Version, expected + 1), ct);

        return updated == 0 ? null : DbVersion.ToText(expected + 1);
    }

    public async Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return false;

        return await context.Fields
            .Where(x => x.ProjectId == projectId && x.Id == id && x.Version == expected)
            .ExecuteDeleteAsync(ct) > 0;
    }

    private static FieldDefinition Map(FieldDbModel model) => new()
    {
        Id = model.Id,
        ProjectId = model.ProjectId,
        Name = model.Name,
        Type = (FieldType)model.Type,
        Multiple = model.Multiple,
        EnumId = model.EnumId,
        Version = DbVersion.ToText(model.Version)
    };
}

internal class FieldEnumStorage(AppDbContext context) : IFieldEnumStorage
{
    public async Task<FieldEnum?> GetById(Guid projectId, Guid id, CancellationToken ct = default)
    {
        var model = await Query(projectId).FirstOrDefaultAsync(x => x.Id == id, ct);
        return model == null ? null : Map(model);
    }

    public async Task<FieldEnum[]> GetAll(Guid projectId, CancellationToken ct = default) =>
        (await Query(projectId).OrderBy(x => x.Name).ThenBy(x => x.Id).ToArrayAsync(ct)).Select(Map).ToArray();

    public Task<ListDto<FieldEnum>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        Query(projectId).OrderBy(x => x.Name).ThenBy(x => x.Id).ToPage(page, Map, ct);

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => Task.FromResult(0);

    public async Task<string> Add(FieldEnum value, CancellationToken ct = default)
    {
        var model = new FieldEnumDbModel
        {
            Id = value.Id,
            ProjectId = value.ProjectId,
            Name = value.Name,
            Version = DbVersion.Initial,
            Values = Values(value)
        };
        context.FieldEnums.Add(model);
        try
        {
            await context.SaveChangesAsync(ct);
        }
        finally
        {
            context.Entry(model).State = EntityState.Detached;
            foreach (var item in model.Values)
                context.Entry(item).State = EntityState.Detached;
        }
        return DbVersion.ToText(DbVersion.Initial);
    }

    // Строку перечисления не пересоздаём: на неё ссылаются поля. Сначала — условное обновление версии; значения заменяем целиком
    // в одной транзакции с именем и версией (внутри IWriteScope транзакция уже открыта — берём её).
    public async Task<string?> Update(FieldEnum value, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return null;

        var own = context.Database.CurrentTransaction == null
            ? await context.Database.BeginTransactionAsync(ct)
            : null;
        await using (own)
        {
            var updated = await context.FieldEnums
                .Where(x => x.ProjectId == value.ProjectId && x.Id == value.Id && x.Version == expected)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Name, value.Name)
                    .SetProperty(x => x.Version, expected + 1), ct);
            if (updated == 0)
                return null;

            await context.Set<FieldEnumValueDbModel>().Where(x => x.EnumId == value.Id).ExecuteDeleteAsync(ct);
            var values = Values(value);
            context.Set<FieldEnumValueDbModel>().AddRange(values);
            try
            {
                await context.SaveChangesAsync(ct);
            }
            finally
            {
                foreach (var item in values)
                    context.Entry(item).State = EntityState.Detached;
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

        return await context.FieldEnums
            .Where(x => x.ProjectId == projectId && x.Id == id && x.Version == expected)
            .ExecuteDeleteAsync(ct) > 0;
    }

    private static List<FieldEnumValueDbModel> Values(FieldEnum value) => value.Values
        .Select((x, i) => new FieldEnumValueDbModel { EnumId = value.Id, Id = x.Id, Name = x.Name, Position = i })
        .ToList();

    private IQueryable<FieldEnumDbModel> Query(Guid projectId) => context.FieldEnums
        .AsNoTracking()
        .Include(x => x.Values)
        .Where(x => x.ProjectId == projectId);

    private static FieldEnum Map(FieldEnumDbModel model) => new()
    {
        Id = model.Id,
        ProjectId = model.ProjectId,
        Name = model.Name,
        Values = model.Values.OrderBy(x => x.Position).Select(x => new FieldEnumValue(x.Id, x.Name)).ToArray(),
        Version = DbVersion.ToText(model.Version)
    };
}
