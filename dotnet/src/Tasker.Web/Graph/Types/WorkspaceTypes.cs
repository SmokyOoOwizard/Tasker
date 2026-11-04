using HotChocolate.Types;
using Tasker.Core.Workspace;
using Tasker.Daemon;
using Tasker.Web.Workspaces;

namespace Tasker.Web.Graph.Types;

internal sealed class WorkspaceProblemType : ObjectType<WorkspaceProblem>
{
    protected override void Configure(IObjectTypeDescriptor<WorkspaceProblem> descriptor)
    {
        descriptor.Name("WorkspaceProblem");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Path);
        descriptor.Field(x => x.Error);
    }
}

internal sealed class WorkspaceMcpType : ObjectType<WorkspaceMcp>
{
    protected override void Configure(IObjectTypeDescriptor<WorkspaceMcp> descriptor)
    {
        descriptor.Name("WorkspaceMcp");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Daemon);
        descriptor.Field(x => x.Port);
        descriptor.Field(x => x.Url);
        descriptor.Field(x => x.Workspace);
        descriptor.Field(x => x.WorkspaceState);
        descriptor.Field(x => x.Error);
        descriptor.Field("agentHeader").Type<NonNullType<StringType>>().Resolve(DaemonClient.AgentHeader);
    }
}

internal sealed class WorkspaceChangeType : ObjectType<WorkspaceChange>
{
    protected override void Configure(IObjectTypeDescriptor<WorkspaceChange> descriptor)
    {
        descriptor.Name("WorkspaceChange");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Entity);
        descriptor.Field(x => x.ProjectId);
        descriptor.Field(x => x.Id);
    }
}
