using HotChocolate;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Tasker.Web.Graph.Types;
using Tasker.Web.Workspaces;

namespace Tasker.Web.Graph;

internal sealed class SubscriptionType : ObjectType
{
    protected override void Configure(IObjectTypeDescriptor descriptor)
    {
        descriptor.Name("Subscription");

        descriptor.Field("workspaceChanges")
            .Returns<WorkspaceChangeType>()
            .Resolve(x => x.GetEventMessage<WorkspaceChange>())
            .Subscribe(x =>
            {
                x.RequireWorkspace("workspaceChanges");
                var workspace = x.Service<IHttpContextAccessor>().HttpContext?.Features.Get<WorkspaceFeature>()
                    ?? throw new Tasker.Core.TaskerNotFoundException("workspaceChanges needs a workspace address: /w/{folder}/api/graphql");
                return x.Service<WorkspaceEvents>().Watch(workspace.Closing, x.RequestAborted);
            });
    }
}
