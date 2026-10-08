namespace Tasker.Core.Users;

public enum UserKind
{
    Human,

    /// <summary>
    /// Агент (например, LLM через MCP). Для остального приложения — обычный пользователь: участвует в проектах,
    /// работает с задачами. Отличия: нет почты и пароля, входит только по своему токену и только в MCP
    /// (<see cref="Agents.AgentService"/>), не может быть админом и управлять пользователями.
    /// </summary>
    Agent
}

/// <summary>
/// Пользователь. Общий для всех проектов (к проектам привязывается через участие).
/// <para>
/// На сервере у человека есть почта и пароль, он входит по JWT. На десктопе пользователь — только имя:
/// приложение локальное, входить не нужно. Пароль в модели не хранится — см. <see cref="UserCredentials"/>.
/// </para>
/// </summary>
public record User
{
    public required Guid Id { get; init; }
    public required string Username { get; init; }

    public UserKind Kind { get; init; } = UserKind.Human;

    /// <summary>Только у агента: человек, который его создал и управляет им. null — агент десктопа.</summary>
    public Guid? OwnerId { get; init; }

    /// <summary>Только у человека на сервере.</summary>
    public string? Email { get; init; }

    /// <summary>Администратор сервера: управляет пользователями и видит все проекты. У агентов и на десктопе всегда false.</summary>
    public required bool IsAdmin { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>См. <see cref="Versioning"/>.</summary>
    public required string Version { get; init; }

    public bool IsAgent => Kind == UserKind.Agent;
}

/// <summary>
/// Данные для входа — отдельно от <see cref="User"/>, чтобы хэш пароля не попадал в ответы API.
/// </summary>
/// <param name="PasswordHash">Хэш пароля (PBKDF2, формат ASP.NET Identity).</param>
/// <param name="SecurityStamp">
/// Меняется при смене пароля: выданные ранее токены с другим значением перестают приниматься.
/// </param>
public record UserCredentials(string PasswordHash, Guid SecurityStamp);
