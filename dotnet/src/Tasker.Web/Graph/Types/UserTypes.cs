using HotChocolate.Types;
using Tasker.Core.Agents;
using Tasker.Core.Locks;
using Tasker.Core.Users;

namespace Tasker.Web.Graph.Types;

internal sealed class UserType : ObjectType<User>
{
    protected override void Configure(IObjectTypeDescriptor<User> descriptor)
    {
        descriptor.Name("User");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.Username);
        descriptor.Field(x => x.Kind);
        descriptor.Field(x => x.OwnerId);
        descriptor.Field(x => x.Email);
        descriptor.Field(x => x.IsAdmin);
        descriptor.Field(x => x.CreatedAt);
        descriptor.Field(x => x.Version);
        descriptor.Field(x => x.IsAgent);
    }
}

internal sealed class AgentTokenType : ObjectType<AgentToken>
{
    protected override void Configure(IObjectTypeDescriptor<AgentToken> descriptor)
    {
        descriptor.Name("AgentToken");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.AgentId);
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.Prefix);
        descriptor.Field(x => x.CreatedAt);
        descriptor.Field(x => x.ExpiresAt);
        descriptor.Field(x => x.LastUsedAt);
        descriptor.Field(x => x.RevokedAt);
    }
}

internal sealed class LockInfoType : ObjectType<LockInfo>
{
    protected override void Configure(IObjectTypeDescriptor<LockInfo> descriptor)
    {
        descriptor.Name("LockInfo");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Entity);
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.Holder);
        descriptor.Field(x => x.Mine);
        descriptor.Field(x => x.AcquiredAt);
        descriptor.Field(x => x.ExpiresAt);
    }
}
