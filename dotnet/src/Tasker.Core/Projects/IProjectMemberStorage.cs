namespace Tasker.Core.Projects;

/// <summary>
/// Участники проектов. Только на сервере (хранилище в БД): на десктопе входа нет и доступ не ограничивается.
/// </summary>
public interface IProjectMemberStorage
{
    Task<Guid[]> GetUserIds(Guid projectId, CancellationToken ct = default);

    Task<Guid[]> GetProjectIds(Guid userId, CancellationToken ct = default);

    Task<bool> IsMember(Guid projectId, Guid userId, CancellationToken ct = default);

    /// <summary>Повторное добавление — не ошибка.</summary>
    Task Add(Guid projectId, Guid userId, CancellationToken ct = default);

    /// <returns>false — пользователь не был участником.</returns>
    Task<bool> Remove(Guid projectId, Guid userId, CancellationToken ct = default);
}
