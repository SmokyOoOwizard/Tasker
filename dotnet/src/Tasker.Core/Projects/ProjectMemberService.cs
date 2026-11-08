using Tasker.Core.Agents;
using Tasker.Core.Dto;
using Tasker.Core.Users;

namespace Tasker.Core.Projects;

public record AddProjectMember(Guid UserId, AgentRights? Rights = null);

public record UpdateProjectMember(AgentRights? Rights);

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
        var rights = (await members.GetMembers(projectId, ct)).ToDictionary(x => x.UserId, x => x.Rights);
        var found = await users.GetRange(new UserFilter { Ids = rights.Keys.ToArray() }, page, ct);
        return new ListDto<User>
        {
            TotalCount = found.TotalCount,
            Offset = found.Offset,
            Limit = found.Limit,
            Data = found.Data.Select(x => WithRights(x, rights.GetValueOrDefault(x.Id))).ToArray()
        };
    }

    /// <summary>Агента можно добавить только своего (админ — любого).</summary>
    public async Task<User> Add(Guid projectId, AddProjectMember command, CancellationToken ct = default)
    {
        var actor = await userService.GetCurrentHuman(ct);
        var user = await users.GetById(command.UserId, ct)
            ?? throw TaskerErrors.Invalid($"UserId: user not found: {command.UserId}");

        if (user.IsAgent && user.OwnerId != actor.Id && !actor.IsAdmin)
            throw TaskerErrors.AgentOfAnotherUser(user.Username);
        if (command.Rights != null && !user.IsAgent)
            throw TaskerErrors.AgentRightsAgentOnly(user.Username);

        if (await members.GetMember(projectId, user.Id, ct) is { } existing)
        {
            if (command.Rights != null)
                await members.SetRights(projectId, user.Id, command.Rights, ct);
            return WithRights(user, command.Rights ?? existing.Rights);
        }

        await members.Add(projectId, user.Id, command.Rights, ct);
        return WithRights(user, command.Rights);
    }

    public async Task<User?> Update(Guid projectId, Guid userId, UpdateProjectMember command, CancellationToken ct = default)
    {
        await userService.GetCurrentHuman(ct);
        if (command.Rights == null)
            throw TaskerErrors.Invalid("Rights: required");

        var user = await users.GetById(userId, ct);
        if (user == null || await members.GetMember(projectId, userId, ct) == null)
            return null;
        if (!user.IsAgent)
            throw TaskerErrors.AgentRightsAgentOnly(user.Username);

        await members.SetRights(projectId, userId, command.Rights, ct);
        return WithRights(user, command.Rights);
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
            throw TaskerErrors.LastPerson();

        return await members.Remove(projectId, userId, ct);
    }

    private static User WithRights(User user, AgentRights? rights) =>
        user.IsAgent ? user with { Rights = rights ?? AgentRights.Full } : user;
}
