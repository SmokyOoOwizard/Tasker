using System.Security.Cryptography;
using System.Text;
using Tasker.Core.Dto;
using Tasker.Core.Users;

namespace Tasker.Core.Agents;

public record CreateAgent(string Username);

/// <summary>Поля null — не меняются. Version — версия агента-пользователя (см. <see cref="Versioning"/>).</summary>
public record UpdateAgent(string? Username, string? Version);

/// <param name="ExpiresAt">null — бессрочный (до отзыва).</param>
public record CreateAgentToken(string Name, DateTimeOffset? ExpiresAt);

/// <summary>Выпущенный токен: <see cref="Value"/> показывается только здесь, один раз.</summary>
public record IssuedAgentToken(AgentToken Token, string Value);

/// <summary>
/// Агенты — пользователи с <see cref="UserKind.Agent"/>.
/// <para>
/// Сервер: агентов создаёт человек для себя (он — владелец); управляет агентом и его токенами владелец,
/// админ видит и управляет всеми. Агент сам не может создавать агентов и токены.
/// Токен агента работает только в MCP.
/// </para>
/// <para>
/// Десктоп: входа нет, агенты общие; MCP работает без токена от имени локального агента (<see cref="EnsureLocalAgent"/>).
/// </para>
/// </summary>
/// <param name="tokens">Хранилище токенов — только на сервере (БД); на десктопе токены не нужны.</param>
public class AgentService(
    UserService users,
    IUserStorage userStorage,
    UserOptions options,
    TimeProvider time,
    Locks.EditLockService locks,
    IAgentTokenStorage? tokens = null
)
{
    public const string TokenPrefix = "tsk_";
    public const string LocalAgentName = "agent";

    // LastUsedAt обновляется не чаще раза в минуту — чтобы каждый вызов MCP не был записью в БД.
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    /// <summary>Сервер: свои агенты (админу — все). Десктоп: все агенты.</summary>
    public async Task<ListDto<User>> GetRange(Page page, CancellationToken ct = default)
    {
        var filter = new UserFilter { Kind = UserKind.Agent };
        if (options.RequireCredentials && await users.GetCurrentHuman(ct) is { IsAdmin: false } actor)
            filter = filter with { OwnerId = actor.Id };

        return await userStorage.GetRange(filter, page, ct);
    }

    /// <returns>null — агента нет или он чужой.</returns>
    public async Task<User?> GetById(Guid id, CancellationToken ct = default)
    {
        var agent = await userStorage.GetById(id, ct);
        if (agent is not { IsAgent: true })
            return null;

        return await CanManage(agent, ct) ? agent : null;
    }

    public async Task<User> Create(CreateAgent command, CancellationToken ct = default)
    {
        Guid? ownerId = options.RequireCredentials ? (await users.GetCurrentHuman(ct)).Id : null;
        return await users.AddAgent(command.Username, ownerId, ct);
    }

    /// <returns>null — агента нет или он чужой.</returns>
    public async Task<User?> Update(Guid id, UpdateAgent command, CancellationToken ct = default)
    {
        var agent = await GetById(id, ct);
        if (agent == null)
            return null;
        await locks.EnsureWritable(Locks.LockedEntity.User, agent.Id, Subject(agent), ct);
        var expected = Versioning.Check(agent.Version, command.Version, Subject(agent));

        var username = command.Username == null ? agent.Username : Validate.Username(command.Username);
        await users.EnsureUnique(username, null, except: agent.Id, ct);

        var updated = agent with { Username = username };
        var version = await userStorage.Update(updated, expected, ct) ?? throw Versioning.Modified(Subject(agent));
        return updated with { Version = version };
    }

    /// <summary>Удаляет агента вместе с его токенами и участием в проектах.</summary>
    /// <returns>false — агента нет или он чужой.</returns>
    public async Task<bool> Delete(Guid id, string? version, CancellationToken ct = default)
    {
        var agent = await GetById(id, ct);
        if (agent == null)
            return false;
        await locks.EnsureWritable(Locks.LockedEntity.User, agent.Id, Subject(agent), ct);
        var expected = Versioning.Check(agent.Version, version, Subject(agent));

        if (!await userStorage.Delete(id, expected, ct))
            throw Versioning.Modified(Subject(agent));
        await locks.Forget(Locks.LockedEntity.User, agent.Id, ct);
        return true;
    }

    /// <returns>null — агента нет или он чужой.</returns>
    public async Task<ListDto<AgentToken>?> GetTokens(Guid agentId, Page page, CancellationToken ct = default) =>
        await GetById(agentId, ct) == null ? null : await RequireTokens().GetRange(agentId, page, ct);

    /// <returns>null — агента нет или он чужой.</returns>
    public async Task<IssuedAgentToken?> CreateToken(Guid agentId, CreateAgentToken command, CancellationToken ct = default)
    {
        if (await GetById(agentId, ct) == null)
            return null;

        var now = time.GetUtcNow();
        if (command.ExpiresAt is { } expires && expires <= now)
            throw new TaskerValidationException("ExpiresAt must be in the future");

        var value = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = new AgentToken
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            Name = Validate.Name(command.Name, "Token name"),
            Prefix = value[..(TokenPrefix.Length + 8)],
            CreatedAt = now,
            ExpiresAt = command.ExpiresAt
        };

        await RequireTokens().Add(token, Hash(value), ct);
        return new IssuedAgentToken(token, value);
    }

    /// <returns>false — агента или токена нет, токен чужой или уже отозван.</returns>
    public async Task<bool> RevokeToken(Guid agentId, Guid tokenId, CancellationToken ct = default) =>
        await GetById(agentId, ct) != null && await RequireTokens().Revoke(agentId, tokenId, time.GetUtcNow(), ct);

    /// <summary>Вход агента в MCP по токену.</summary>
    /// <returns>null — токен неизвестен, отозван, истёк, или агента уже нет.</returns>
    public async Task<User?> Authenticate(string? value, CancellationToken ct = default)
    {
        if (tokens == null || string.IsNullOrWhiteSpace(value) || !value.StartsWith(TokenPrefix, StringComparison.Ordinal))
            return null;

        var token = await tokens.GetByHash(Hash(value), ct);
        var now = time.GetUtcNow();
        if (token == null || token.RevokedAt != null || token.ExpiresAt <= now)
            return null;

        var agent = await userStorage.GetById(token.AgentId, ct);
        if (agent is not { IsAgent: true })
            return null;

        if (token.LastUsedAt == null || now - token.LastUsedAt >= TouchInterval)
            await tokens.Touch(token.Id, now, ct);
        return agent;
    }

    /// <summary>
    /// Десктоп: агент, от имени которого работает MCP без токена. Создаётся при первом обращении.
    /// Если имя «agent» уже занято человеком — берётся следующее свободное (agent-2, …).
    /// </summary>
    public async Task<User> EnsureLocalAgent(CancellationToken ct = default)
    {
        var name = LocalAgentName;
        for (var i = 2; await userStorage.GetByUsername(name, ct) is { } taken; i++)
        {
            if (taken is { IsAgent: true, OwnerId: null })
                return taken;
            name = $"{LocalAgentName}-{i}";
        }

        return await users.AddAgent(name, ownerId: null, ct);
    }

    private async Task<bool> CanManage(User agent, CancellationToken ct)
    {
        if (!options.RequireCredentials)
            return true;

        var actor = await users.GetCurrentHuman(ct);
        return actor.IsAdmin || agent.OwnerId == actor.Id;
    }

    private IAgentTokenStorage RequireTokens() =>
        tokens ?? throw new TaskerValidationException("Agent tokens are available only on the server: local MCP needs no token");

    private static string Subject(User agent) => $"Agent '{agent.Username}'";

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
