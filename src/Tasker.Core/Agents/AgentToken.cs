using Tasker.Core.Dto;

namespace Tasker.Core.Agents;

/// <summary>
/// Токен агента для MCP (только сервер). Сам токен показывается один раз при выпуске;
/// хранится только его хэш. В REST-API токен агента не принимается.
/// </summary>
public record AgentToken
{
    public required Guid Id { get; init; }
    public required Guid AgentId { get; init; }

    /// <summary>Для людей: где используется токен («Claude Code на ноутбуке»).</summary>
    public required string Name { get; init; }

    /// <summary>Начало токена (<c>tsk_Ab12Cd34</c>) — чтобы узнать его в списке, не храня целиком.</summary>
    public required string Prefix { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>null — бессрочный (до отзыва).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public interface IAgentTokenStorage
{
    /// <returns>Токен и id агента-пользователя, которому он принадлежит.</returns>
    Task<AgentToken?> GetByHash(string tokenHash, CancellationToken ct = default);

    /// <summary>Страница токенов агента, новые первыми.</summary>
    Task<ListDto<AgentToken>> GetRange(Guid agentId, Page page, CancellationToken ct = default);

    Task Add(AgentToken token, string tokenHash, CancellationToken ct = default);

    /// <returns>false — токена нет или он уже отозван.</returns>
    Task<bool> Revoke(Guid agentId, Guid tokenId, DateTimeOffset now, CancellationToken ct = default);

    Task Touch(Guid tokenId, DateTimeOffset now, CancellationToken ct = default);
}
