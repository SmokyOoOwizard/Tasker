using System.Security.Cryptography;
using System.Text;
using Serilog;
using Tasker.Core.Users;

namespace Tasker.Core.Auth;

/// <summary>Выдача access-токенов (JWT) — реализует веб-слой.</summary>
public interface IAccessTokenIssuer
{
    (string Token, DateTimeOffset ExpiresAt) Issue(User user, UserCredentials credentials);
}

public record AuthSessionOptions(TimeSpan RefreshTokenLifetime);

/// <summary>
/// Результат входа или обмена: короткий access-токен для запросов и refresh-токен для получения нового.
/// </summary>
public record AuthSession(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    User User
);

public record RefreshSession(string RefreshToken);

/// <summary>
/// Сессии на сервере: вход, обмен refresh-токена, выход.
/// <para>
/// Refresh-токен одноразовый: при обмене он гасится и выдаётся новая пара (ротация).
/// Предъявление уже погашенного токена означает, что его, вероятно, украли, — тогда гасится
/// вся сессия (<see cref="RefreshToken.FamilyId"/>): и вору, и владельцу придётся войти заново.
/// </para>
/// </summary>
public class AuthService(
    UserService users,
    IUserStorage userStorage,
    IRefreshTokenStorage tokens,
    IAccessTokenIssuer accessTokens,
    AuthSessionOptions options,
    TimeProvider time,
    ILogger log
)
{
    public async Task<AuthSession> Register(RegisterUser command, CancellationToken ct = default)
    {
        var user = await users.Register(command, ct);
        return await SignIn(new SignIn(user.Username, command.Password), ct)
            ?? throw new InvalidOperationException($"Cannot sign in just registered user {user.Id}");
    }

    /// <returns>null — неверные имя/почта или пароль.</returns>
    public async Task<AuthSession?> SignIn(SignIn command, CancellationToken ct = default)
    {
        if (await users.Authenticate(command, ct) is not { } authenticated)
            return null;

        return await Issue(authenticated.User, authenticated.Credentials, familyId: Guid.NewGuid(), ct);
    }

    /// <returns>null — токен неизвестен, истёк или уже использован; нужно войти заново.</returns>
    public async Task<AuthSession?> Refresh(RefreshSession command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken))
            return null;

        var stored = await tokens.GetByHash(Hash(command.RefreshToken), ct);
        if (stored == null)
            return null;

        var now = time.GetUtcNow();
        if (stored.RevokedAt != null)
        {
            log.Warning("Reuse of revoked refresh token {TokenId} of user {UserId}: revoking the session", stored.Id, stored.UserId);
            await tokens.RevokeFamily(stored.FamilyId, now, ct);
            return null;
        }
        if (stored.ExpiresAt <= now)
            return null;

        // Токен ещё действующий, но сессию уже отозвали: например, он выдан параллельным обменом,
        // который проиграл гонку и отозвал сессию как повтор.
        if (await tokens.IsFamilyRevoked(stored.FamilyId, ct))
            return null;

        // Проигравший параллельный обмен того же токена — такая же повторная попытка.
        if (!await tokens.TryRevoke(stored.Id, now, ct))
        {
            log.Warning("Concurrent reuse of refresh token {TokenId} of user {UserId}: revoking the session", stored.Id, stored.UserId);
            await tokens.RevokeFamily(stored.FamilyId, now, ct);
            return null;
        }

        var user = await userStorage.GetById(stored.UserId, ct);
        var credentials = user == null ? null : await userStorage.GetCredentials(user.Id, ct);
        if (user == null || credentials == null)
            return null;

        return await Issue(user, credentials, stored.FamilyId, ct);
    }

    /// <summary>
    /// Завершает сессию: её refresh-токены больше не обмениваются. Уже выданный access-токен
    /// действует до своего (короткого) срока.
    /// </summary>
    public async Task SignOut(RefreshSession command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken))
            return;

        if (await tokens.GetByHash(Hash(command.RefreshToken), ct) is { } stored)
            await tokens.RevokeFamily(stored.FamilyId, time.GetUtcNow(), ct);
    }

    /// <summary>
    /// Смена пароля завершает все сессии пользователя: refresh-токены гасятся,
    /// access-токены перестают приниматься сразу (меняется SecurityStamp).
    /// </summary>
    public async Task<bool> ChangePassword(Guid userId, ChangePassword command, CancellationToken ct = default)
    {
        if (!await users.ChangePassword(userId, command, ct))
            return false;

        await tokens.RevokeAllForUser(userId, time.GetUtcNow(), ct);
        return true;
    }

    private async Task<AuthSession> Issue(User user, UserCredentials credentials, Guid familyId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var refreshToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        var refreshExpiresAt = now + options.RefreshTokenLifetime;

        await tokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = Hash(refreshToken),
            CreatedAt = now,
            ExpiresAt = refreshExpiresAt
        }, ct);

        var (accessToken, accessExpiresAt) = accessTokens.Issue(user, credentials);
        return new AuthSession(accessToken, accessExpiresAt, refreshToken, refreshExpiresAt, user);
    }

    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
