namespace Tasker.Core.Auth;

/// <summary>
/// Refresh-токен (только сервер). Хранится только хэш: сам токен знает лишь клиент.
/// Все токены одной сессии (цепочка обменов от одного входа) имеют общий <see cref="FamilyId"/>.
/// </summary>
public record RefreshToken
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }
    public required Guid FamilyId { get; init; }

    /// <summary>SHA-256 токена в hex.</summary>
    public required string TokenHash { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Когда токен погашен: обменян на новый, сессия завершена или отозвана. null — действующий.</summary>
    public DateTimeOffset? RevokedAt { get; init; }
}

public interface IRefreshTokenStorage
{
    Task<RefreshToken?> GetByHash(string tokenHash, CancellationToken ct = default);

    Task Add(RefreshToken token, CancellationToken ct = default);

    /// <summary>Атомарно гасит токен, если он ещё действующий.</summary>
    /// <returns>false — токен уже погашен (например, параллельным обменом того же токена).</returns>
    Task<bool> TryRevoke(Guid id, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Отзывает сессию целиком: гасит её токены и помечает сессию закрытой, так что токен,
    /// дописанный в неё позже (параллельным обменом), тоже не будет принят.
    /// </summary>
    Task RevokeFamily(Guid familyId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Отзывает все сессии пользователя, как <see cref="RevokeFamily"/>.</summary>
    Task RevokeAllForUser(Guid userId, DateTimeOffset now, CancellationToken ct = default);

    Task<bool> IsFamilyRevoked(Guid familyId, CancellationToken ct = default);
}
