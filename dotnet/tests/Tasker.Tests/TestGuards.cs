using Tasker.Core.Agents;
using Tasker.Core.Users;

namespace Tasker.Tests;

internal static class TestGuards
{
    public static AgentGuard Open => new(new Nobody(), null!, new UserOptions(false));

    private sealed class Nobody : ICurrentUser
    {
        public Guid? Id => null;
    }
}
