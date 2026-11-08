using Tasker.Core.Agents;

namespace Tasker.Core.Projects;

public record ProjectMember(Guid ProjectId, Guid UserId, AgentRights? Rights);

/// <summary>
/// Участники проектов. Только на сервере (хранилище в БД): на десктопе входа нет и доступ не ограничивается.
/// </summary>
public interface IProjectMemberStorage
{
    Task<Guid[]> GetUserIds(Guid projectId, CancellationToken ct = default);

    Task<Guid[]> GetProjectIds(Guid userId, CancellationToken ct = default);

    Task<bool> IsMember(Guid projectId, Guid userId, CancellationToken ct = default);

    /// <summary>Повторное добавление — не ошибка.</summary>
    Task Add(Guid projectId, Guid userId, CancellationToken ct = default) => Add(projectId, userId, null, ct);

    Task Add(Guid projectId, Guid userId, AgentRights? rights, CancellationToken ct = default);

    Task<ProjectMember?> GetMember(Guid projectId, Guid userId, CancellationToken ct = default);

    Task<ProjectMember[]> GetMembers(Guid projectId, CancellationToken ct = default);

    Task<ProjectMember[]> GetMemberships(Guid userId, CancellationToken ct = default);

    Task<bool> SetRights(Guid projectId, Guid userId, AgentRights? rights, CancellationToken ct = default);

    /// <returns>false — пользователь не был участником.</returns>
    Task<bool> Remove(Guid projectId, Guid userId, CancellationToken ct = default);
}
