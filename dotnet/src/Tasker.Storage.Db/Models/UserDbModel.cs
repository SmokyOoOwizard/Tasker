namespace Tasker.Storage.Db.Models;

internal class UserDbModel
{
    public Guid Id { get; set; }
    public required string Username { get; set; }

    /// <summary>Для уникального индекса без учёта регистра (см. DbText.Normalize).</summary>
    public required string NormalizedUsername { get; set; }

    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }

    /// <summary>null — пользователь без пароля (десктоп на БД).</summary>
    public string? PasswordHash { get; set; }

    public Guid SecurityStamp { get; set; }
    public bool IsAdmin { get; set; }

    /// <summary>Human / Agent — строкой, чтобы в БД читалось.</summary>
    public Tasker.Core.Users.UserKind Kind { get; set; }

    /// <summary>Владелец агента; агенты удаляются вместе с ним.</summary>
    public Guid? OwnerId { get; set; }

    // UTC, см. TaskDbModel.
    public DateTime CreatedAt { get; set; }

    public int Version { get; set; }
}

/// <summary>Токен агента для MCP; хранится только хэш.</summary>
internal class AgentTokenDbModel
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public required string Name { get; set; }
    public required string Prefix { get; set; }
    public required string TokenHash { get; set; }

    // UTC, см. TaskDbModel.
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>Участник проекта (только сервер).</summary>
internal class ProjectMemberDbModel
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
}
