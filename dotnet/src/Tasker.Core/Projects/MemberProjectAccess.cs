using Tasker.Core.Users;

namespace Tasker.Core.Projects;

/// <summary>
/// Доступ на сервере: проект видят его участники и администраторы.
/// Для остальных проекта как будто нет (404, а не 403) — чтобы не раскрывать, какие проекты существуют.
/// </summary>
public class MemberProjectAccess(ICurrentUser currentUser, IUserStorage users, IProjectMemberStorage members) : IProjectAccess
{
    public async Task<bool> CanAccess(Guid projectId, CancellationToken ct = default)
    {
        if (await GetActor(ct) is not { } actor)
            return false;

        return actor.IsAdmin || await members.IsMember(projectId, actor.Id, ct);
    }

    public async Task<Guid[]?> VisibleProjectIds(CancellationToken ct = default)
    {
        if (await GetActor(ct) is not { } actor)
            return [];
        if (actor.IsAdmin)
            return null;

        return await members.GetProjectIds(actor.Id, ct);
    }

    public async Task OnCreated(Project project, CancellationToken ct = default)
    {
        if (currentUser.Id is { } userId)
            await members.Add(project.Id, userId, ct);
    }

    private async Task<User?> GetActor(CancellationToken ct) =>
        currentUser.Id is { } id ? await users.GetById(id, ct) : null;
}
