using System.Diagnostics.CodeAnalysis;
using Tasker.Core.Agents;
using Tasker.Core.Projects;
using Tasker.Core.Users;

namespace Tasker.Mcp;

public record AgentIdentity : User
{
    [SetsRequiredMembers]
    public AgentIdentity(User user) : base(user)
    {
    }

    public ProjectAgentRights[]? Projects { get; init; }
}

public record AgentProject : Project
{
    [SetsRequiredMembers]
    public AgentProject(Project project) : base(project)
    {
    }

    public AgentRights? Rights { get; init; }
}
