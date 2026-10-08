using Microsoft.EntityFrameworkCore;
using Tasker.Core.Auth;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

internal class RefreshTokenStorage(AppDbContext context) : IRefreshTokenStorage
{
    public async Task<RefreshToken?> GetByHash(string tokenHash, CancellationToken ct = default)
    {
        var model = await context.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(x => x.TokenHash == tokenHash, ct);
        return model == null ? null : Map(model);
    }

    public async Task Add(RefreshToken token, CancellationToken ct = default)
    {
        context.RefreshTokens.Add(new RefreshTokenDbModel
        {
            Id = token.Id,
            UserId = token.UserId,
            FamilyId = token.FamilyId,
            TokenHash = token.TokenHash,
            CreatedAt = DbTime.ToDb(token.CreatedAt),
            ExpiresAt = DbTime.ToDb(token.ExpiresAt),
            RevokedAt = token.RevokedAt == null ? null : DbTime.ToDb(token.RevokedAt.Value)
        });
        await context.SaveChangesAsync(ct);
    }

    // Условное обновление: из двух параллельных обменов одного токена погасит его только один.
    public async Task<bool> TryRevoke(Guid id, DateTimeOffset now, CancellationToken ct = default)
    {
        var revokedAt = DbTime.ToDb(now);
        return await context.RefreshTokens
            .Where(x => x.Id == id && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, revokedAt), ct) > 0;
    }

    public Task RevokeFamily(Guid familyId, DateTimeOffset now, CancellationToken ct = default) =>
        CloseSessions(context.RefreshTokens.Where(x => x.FamilyId == familyId), now, ct);

    public Task RevokeAllForUser(Guid userId, DateTimeOffset now, CancellationToken ct = default) =>
        CloseSessions(context.RefreshTokens.Where(x => x.UserId == userId), now, ct);

    public Task<bool> IsFamilyRevoked(Guid familyId, CancellationToken ct = default) =>
        context.RefreshTokens.AnyAsync(x => x.FamilyId == familyId && x.FamilyRevokedAt != null, ct);

    // Отметка ставится на все строки сессии, в том числе уже использованные: см. FamilyRevokedAt.
    private static async Task CloseSessions(IQueryable<RefreshTokenDbModel> tokens, DateTimeOffset now, CancellationToken ct)
    {
        var at = DbTime.ToDb(now);
        await tokens
            .Where(x => x.FamilyRevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.FamilyRevokedAt, at)
                .SetProperty(x => x.RevokedAt, x => x.RevokedAt ?? at), ct);
    }

    private static RefreshToken Map(RefreshTokenDbModel model) => new()
    {
        Id = model.Id,
        UserId = model.UserId,
        FamilyId = model.FamilyId,
        TokenHash = model.TokenHash,
        CreatedAt = DbTime.FromDb(model.CreatedAt),
        ExpiresAt = DbTime.FromDb(model.ExpiresAt),
        RevokedAt = model.RevokedAt == null ? null : DbTime.FromDb(model.RevokedAt.Value)
    };
}
