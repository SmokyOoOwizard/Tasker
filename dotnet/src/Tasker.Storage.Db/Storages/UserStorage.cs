using Microsoft.EntityFrameworkCore;
using Tasker.Core;
using Tasker.Core.Dto;
using Tasker.Core.Users;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

internal class UserStorage(AppDbContext context) : IUserStorage
{
    public async Task<User?> GetById(Guid id, CancellationToken ct = default)
    {
        var model = await context.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return model == null ? null : Map(model);
    }

    public async Task<User?> GetByUsername(string username, CancellationToken ct = default)
    {
        var normalized = Normalize(username);
        var model = await context.Users.AsNoTracking().FirstOrDefaultAsync(x => x.NormalizedUsername == normalized, ct);
        return model == null ? null : Map(model);
    }

    public async Task<User?> GetByEmail(string email, CancellationToken ct = default)
    {
        var normalized = Normalize(email);
        var model = await context.Users.AsNoTracking().FirstOrDefaultAsync(x => x.NormalizedEmail == normalized, ct);
        return model == null ? null : Map(model);
    }

    public Task<ListDto<User>> GetRange(UserFilter? filter, Page page, CancellationToken ct = default) =>
        Filter(filter).OrderBy(x => x.NormalizedUsername).ThenBy(x => x.Id).ToPage(page, Map, ct);

    public Task<int> Count(UserFilter? filter = null, CancellationToken ct = default) => Filter(filter).CountAsync(ct);

    private IQueryable<UserDbModel> Filter(UserFilter? filter)
    {
        var query = context.Users.AsNoTracking();
        if (filter?.Kind is { } kind)
            query = query.Where(x => x.Kind == kind);
        if (filter?.OwnerId is { } ownerId)
            query = query.Where(x => x.OwnerId == ownerId);
        if (filter?.IsAdmin is { } isAdmin)
            query = query.Where(x => x.IsAdmin == isAdmin);
        if (filter?.Ids is { } ids)
            query = query.Where(x => ids.Contains(x.Id));
        return query;
    }

    public async Task<UserCredentials?> GetCredentials(Guid id, CancellationToken ct = default)
    {
        var row = await context.Users
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.PasswordHash, x.SecurityStamp })
            .FirstOrDefaultAsync(ct);

        return row?.PasswordHash == null ? null : new UserCredentials(row.PasswordHash, row.SecurityStamp);
    }

    public async Task<string> Add(User user, UserCredentials? credentials, CancellationToken ct = default)
    {
        context.Users.Add(new UserDbModel
        {
            Id = user.Id,
            Username = user.Username,
            NormalizedUsername = Normalize(user.Username),
            Email = user.Email,
            NormalizedEmail = user.Email == null ? null : Normalize(user.Email),
            PasswordHash = credentials?.PasswordHash,
            SecurityStamp = credentials?.SecurityStamp ?? Guid.NewGuid(),
            IsAdmin = user.IsAdmin,
            Kind = user.Kind,
            OwnerId = user.OwnerId,
            CreatedAt = DbTime.ToDb(user.CreatedAt),
            Version = DbVersion.Initial
        });

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            throw Taken();
        }
        return DbVersion.ToText(DbVersion.Initial);
    }

    public async Task<string?> Update(User user, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return null;

        int updated;
        try
        {
            updated = await context.Users
                .Where(x => x.Id == user.Id && x.Version == expected)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Username, user.Username)
                    .SetProperty(x => x.NormalizedUsername, Normalize(user.Username))
                    .SetProperty(x => x.Email, user.Email)
                    .SetProperty(x => x.NormalizedEmail, user.Email == null ? null : Normalize(user.Email))
                    .SetProperty(x => x.IsAdmin, user.IsAdmin)
                    .SetProperty(x => x.Version, expected + 1), ct);
        }
        catch (Exception e) when (DbErrors.IsUniqueViolation(e))
        {
            throw Taken();
        }

        return updated == 0 ? null : DbVersion.ToText(expected + 1);
    }

    public async Task<bool> SetCredentials(Guid id, UserCredentials credentials, CancellationToken ct = default) =>
        await context.Users
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.PasswordHash, credentials.PasswordHash)
                .SetProperty(x => x.SecurityStamp, credentials.SecurityStamp), ct) > 0;

    // Участие в проектах, сессии, агенты пользователя и их токены удаляются каскадом на уровне БД.
    public async Task<bool> Delete(Guid id, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return false;

        return await context.Users
            .Where(x => x.Id == id && x.Version == expected)
            .ExecuteDeleteAsync(ct) > 0;
    }

    // Нормализация на стороне .NET, а не SQL: у SQLite UPPER() не работает с кириллицей.
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    private static TaskerConflictException Taken() => new("Username or email is already taken");

    private static User Map(UserDbModel model) => new()
    {
        Id = model.Id,
        Username = model.Username,
        Email = model.Email,
        IsAdmin = model.IsAdmin,
        Kind = model.Kind,
        OwnerId = model.OwnerId,
        CreatedAt = DbTime.FromDb(model.CreatedAt),
        Version = DbVersion.ToText(model.Version)
    };
}
