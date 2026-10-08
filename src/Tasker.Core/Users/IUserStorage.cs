using Tasker.Core.Dto;

namespace Tasker.Core.Users;

/// <summary>Фильтр пользователей: заданные поля объединяются через «и», null — без ограничения.</summary>
public record UserFilter
{
    public UserKind? Kind { get; init; }
    public Guid? OwnerId { get; init; }
    public bool? IsAdmin { get; init; }

    /// <summary>Только эти пользователи (например, участники проекта). Пустой массив — никто.</summary>
    public Guid[]? Ids { get; init; }

    public bool Matches(User user) =>
        (Kind == null || user.Kind == Kind)
        && (OwnerId == null || user.OwnerId == OwnerId)
        && (IsAdmin == null || user.IsAdmin == IsAdmin)
        && (Ids == null || Ids.Contains(user.Id));
}

/// <summary>
/// Хранилище пользователей. Пользователи общие для всех проектов.
/// Имя и почта уникальны без учёта регистра.
/// Запись — с проверкой версии, как в <see cref="Projects.IProjectStorage"/>.
/// Данные для входа (<see cref="UserCredentials"/>) поддерживает только хранилище в БД — сервер работает только с ним.
/// </summary>
public interface IUserStorage
{
    Task<User?> GetById(Guid id, CancellationToken ct = default);

    /// <summary>Без учёта регистра.</summary>
    Task<User?> GetByUsername(string username, CancellationToken ct = default);

    /// <summary>Без учёта регистра.</summary>
    Task<User?> GetByEmail(string email, CancellationToken ct = default);

    /// <summary>Страница пользователей по имени; <paramref name="filter"/> null — все.</summary>
    Task<ListDto<User>> GetRange(UserFilter? filter, Page page, CancellationToken ct = default);

    Task<int> Count(UserFilter? filter = null, CancellationToken ct = default);

    Task<UserCredentials?> GetCredentials(Guid id, CancellationToken ct = default);

    /// <exception cref="TaskerConflictException">Имя или почта уже заняты.</exception>
    Task<string> Add(User user, UserCredentials? credentials, CancellationToken ct = default);

    /// <exception cref="TaskerConflictException">Имя или почта уже заняты.</exception>
    Task<string?> Update(User user, string expectedVersion, CancellationToken ct = default);

    /// <summary>Пароль меняется отдельно от профиля и не меняет его версию.</summary>
    /// <returns>false — пользователя нет.</returns>
    Task<bool> SetCredentials(Guid id, UserCredentials credentials, CancellationToken ct = default);

    Task<bool> Delete(Guid id, string expectedVersion, CancellationToken ct = default);
}
