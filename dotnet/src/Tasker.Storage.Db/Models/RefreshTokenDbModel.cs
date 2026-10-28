namespace Tasker.Storage.Db.Models;

internal class RefreshTokenDbModel
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid FamilyId { get; set; }

    /// <summary>SHA-256 токена в hex — сам токен в БД не хранится.</summary>
    public required string TokenHash { get; set; }

    // UTC, см. TaskDbModel.
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Сессия отозвана целиком (выход, повтор токена, смена пароля). Ставится на все строки сессии,
    /// включая уже использованные, — чтобы токен, дописанный в сессию после отзыва
    /// (параллельный обмен), тоже не прошёл.
    /// </summary>
    public DateTime? FamilyRevokedAt { get; set; }
}
