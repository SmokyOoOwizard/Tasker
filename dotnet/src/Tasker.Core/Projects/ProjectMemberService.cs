using Tasker.Core.Dto;
using Tasker.Core.Users;

namespace Tasker.Core.Projects;

public record AddProjectMember(Guid UserId);

/// <summary>
/// Участники проекта (только сервер). Управлять ими могут люди — участники проекта и админы;
/// доступ к самому проекту проверяется до вызова (фильтр API). Агенты участвуют, но не управляют.
/// Ролей внутри проекта пока нет: любой участник-человек может добавить или убрать другого.
/// </summary>
public class ProjectMemberService(IProjectMemberStorage members, IUserStorage users, UserService userService)
{
    /// <summary>Участники проекта по имени (и люди, и агенты).</summary>
    public async Task<ListDto<User>> GetRange(Guid projectId, Page page, CancellationToken ct = default)
    {
        var ids = (await members.GetUserIds(projectId, ct)).ToArray();
        return await users.GetRange(new UserFilter { Ids = ids }, page, ct);
    }

    /// <summary>Агента можно добавить только своего (админ — любого).</summary>
    public async Task<User> Add(Guid projectId, AddProjectMember command, CancellationToken ct = default)
    {
        var actor = await userService.GetCurrentHuman(ct);
        var user = await users.GetById(command.UserId, ct)
            ?? throw new TaskerValidationException($"UserId: user not found: {command.UserId}");

        if (user.IsAgent && user.OwnerId != actor.Id && !actor.IsAdmin)
            throw new TaskerForbiddenException($"Agent '{user.Username}' belongs to another user");

        await members.Add(projectId, user.Id, ct);
        return user;
    }

    /// <summary>
    /// Последнего человека убрать нельзя: агенты участниками не управляют,
    /// и проектом смогли бы управлять только админы.
    /// </summary>
    /// <returns>false — пользователь не участник проекта.</returns>
    public async Task<bool> Remove(Guid projectId, Guid userId, CancellationToken ct = default)
    {
        await userService.GetCurrentHuman(ct);

        var memberIds = (await members.GetUserIds(projectId, ct)).ToHashSet();
        if (!memberIds.Contains(userId))
            return false;

        var others = memberIds.Where(x => x != userId).ToArray();
        var humansLeft = await users.Count(new UserFilter { Kind = UserKind.Human, Ids = others }, ct);
        var removed = await users.GetById(userId, ct);
        if (removed is { IsAgent: false } && humansLeft == 0)
            throw new TaskerConflictException("The last person cannot be removed from the project");

        return await members.Remove(projectId, userId, ct);
    }
}
