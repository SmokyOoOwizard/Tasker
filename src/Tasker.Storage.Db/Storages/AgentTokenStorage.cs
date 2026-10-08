using Microsoft.EntityFrameworkCore;
using Tasker.Core.Dto;
using Tasker.Core.Agents;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

internal class AgentTokenStorage(AppDbContext context) : IAgentTokenStorage
{
    public async Task<AgentToken?> GetByHash(string tokenHash, CancellationToken ct = default)
    {
        var model = await context.AgentTokens.AsNoTracking().FirstOrDefaultAsync(x => x.TokenHash == tokenHash, ct);
        return model == null ? null : Map(model);
    }

    public Task<ListDto<AgentToken>> GetRange(Guid agentId, Page page, CancellationToken ct = default) =>
        context.AgentTokens
            .AsNoTracking()
            .Where(x => x.AgentId == agentId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToPage(page, Map, ct);

    public async Task Add(AgentToken token, string tokenHash, CancellationToken ct = default)
    {
        context.AgentTokens.Add(new AgentTokenDbModel
        {
            Id = token.Id,
            AgentId = token.AgentId,
            Name = token.Name,
            Prefix = token.Prefix,
            TokenHash = tokenHash,
            CreatedAt = DbTime.ToDb(token.CreatedAt),
            ExpiresAt = token.ExpiresAt == null ? null : DbTime.ToDb(token.ExpiresAt.Value)
        });
        await context.SaveChangesAsync(ct);
    }

    public async Task<bool> Revoke(Guid agentId, Guid tokenId, DateTimeOffset now, CancellationToken ct = default)
    {
        var at = DbTime.ToDb(now);
        return await context.AgentTokens
            .Where(x => x.Id == tokenId && x.AgentId == agentId && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, at), ct) > 0;
    }

    public Task Touch(Guid tokenId, DateTimeOffset now, CancellationToken ct = default)
    {
        var at = DbTime.ToDb(now);
        return context.AgentTokens
            .Where(x => x.Id == tokenId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAt, at), ct);
    }

    private static AgentToken Map(AgentTokenDbModel model) => new()
    {
        Id = model.Id,
        AgentId = model.AgentId,
        Name = model.Name,
        Prefix = model.Prefix,
        CreatedAt = DbTime.FromDb(model.CreatedAt),
        ExpiresAt = model.ExpiresAt == null ? null : DbTime.FromDb(model.ExpiresAt.Value),
        LastUsedAt = model.LastUsedAt == null ? null : DbTime.FromDb(model.LastUsedAt.Value),
        RevokedAt = model.RevokedAt == null ? null : DbTime.FromDb(model.RevokedAt.Value)
    };
}
