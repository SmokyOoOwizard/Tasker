using Tasker.Core.Projects;
using Tasker.Core.Users;

namespace Tasker.Core.Agents;

public class AgentGuard(ICurrentUser currentUser, IUserStorage users, UserOptions options, IProjectMemberStorage? members = null)
{
    public async Task Require(Guid projectId, AgentRight right, CancellationToken ct = default)
    {
        if (await CurrentAgent(ct) is not { } agent)
            return;

        var rights = await RightsOf(agent, projectId, ct);
        if (!rights.Has(right))
            throw TaskerErrors.AgentRightMissing(agent.Username, AgentRights.Wire(right));
    }

    public async Task RequireHuman(CancellationToken ct = default)
    {
        if (await CurrentAgent(ct) != null)
            throw TaskerErrors.PersonOnly();
    }

    public async Task<AgentRights?> RightsIn(Guid projectId, CancellationToken ct = default) =>
        await CurrentAgent(ct) is { } agent ? await RightsOf(agent, projectId, ct) : null;

    public async Task<User?> CurrentAgent(CancellationToken ct = default) =>
        currentUser.Id is { } id && await users.GetById(id, ct) is { IsAgent: true } agent ? agent : null;

    public async Task<ProjectAgentRights[]?> ProjectRights(User agent, CancellationToken ct = default)
    {
        if (!options.RequireCredentials || members == null)
            return null;

        return (await members.GetMemberships(agent.Id, ct))
            .Select(x => new ProjectAgentRights(x.ProjectId, x.Rights ?? AgentRights.Full))
            .ToArray();
    }

    public async Task<AgentRights> RightsOf(User agent, Guid projectId, CancellationToken ct = default)
    {
        if (!options.RequireCredentials || members == null)
            return agent.Rights ?? AgentRights.Full;

        return await members.GetMember(projectId, agent.Id, ct) is { } member
            ? member.Rights ?? AgentRights.Full
            : AgentRights.ReadOnly;
    }
}
