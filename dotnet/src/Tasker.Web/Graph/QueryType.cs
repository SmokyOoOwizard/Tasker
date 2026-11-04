using HotChocolate.Resolvers;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Core.Agents;
using Tasker.Core.Projects;
using Tasker.Core.Users;
using Tasker.Core.Workspace;
using Tasker.Daemon;
using Tasker.Web.Graph.Types;
using Tasker.Web.Workspaces;

namespace Tasker.Web.Graph;

internal sealed class QueryType : ObjectType
{
    public const string Registration = "registration";

    protected override void Configure(IObjectTypeDescriptor descriptor)
    {
        descriptor.Name("Query");

        descriptor.Read(Registration)
            .Returns<RegistrationType>()
            .Resolve(async x =>
            {
                x.RequireServer(Registration);
                return new Registration(await x.Service<IUserStorage>().Count(ct: x.RequestAborted) == 0);
            });

        descriptor.Read("me")
            .Use(SignedIn)
            .Returns<UserType>()
            .Resolve(async x =>
            {
                x.RequireServer("me");
                return await x.Service<UserService>().GetCurrent(x.RequestAborted);
            });

        descriptor.Read("users").Use(SignedIn).Paged()
            .Returns<PageType<User>>()
            .Resolve(async x => await x.Service<IUserStorage>().GetRange(null, x.Page(), x.RequestAborted));

        descriptor.Read("user").Use(SignedIn).Id()
            .Type<UserType>()
            .Resolve(async x => await x.Service<IUserStorage>().GetById(x.Id(), x.RequestAborted));

        descriptor.Read("agents").Use(SignedIn).Paged()
            .Returns<PageType<User>>()
            .Resolve(async x => await x.Service<AgentService>().GetRange(x.Page(), x.RequestAborted));

        descriptor.Read("agent").Use(SignedIn).Id()
            .Type<UserType>()
            .Resolve(async x => await x.Service<AgentService>().GetById(x.Id(), x.RequestAborted));

        descriptor.Read("agentTokens").Use(SignedIn).Id("agentId").Paged()
            .Type<PageType<AgentToken>>()
            .Resolve(async x =>
            {
                x.RequireServer("agentTokens");
                return await x.Service<AgentService>().GetTokens(x.Id("agentId"), x.Page(), x.RequestAborted);
            });

        descriptor.Read("projects").Use(SignedIn).Paged()
            .Returns<PageType<Project>>()
            .Resolve(async x => await x.Service<ProjectService>().GetRange(x.Page(), x.RequestAborted));

        descriptor.Read("project").Use(SignedIn).Id()
            .Type<ProjectType>()
            .Resolve(async x => await x.Service<ProjectService>().GetById(x.Id(), x.RequestAborted));

        descriptor.Read("workspaceProblems").Use(SignedIn).Paged()
            .Returns<PageType<WorkspaceProblem>>()
            .Resolve(async x =>
            {
                x.RequireWorkspace("workspaceProblems");
                return x.Services.GetService<IWorkspaceIndex>() is { } workspace
                    ? await workspace.GetProblems(x.Page(), x.RequestAborted)
                    : x.Page().All(Array.Empty<WorkspaceProblem>());
            });

        descriptor.Read("workspaceMcp").Use(SignedIn)
            .Returns<WorkspaceMcpType>()
            .Resolve(async x =>
            {
                x.RequireWorkspace("workspaceMcp");
                var feature = x.Service<IHttpContextAccessor>().HttpContext?.Features.Get<WorkspaceFeature>();
                return feature != null && x.Services.GetService<IDaemonMcp>() is { } daemon
                    ? await daemon.Find(feature.Location, x.RequestAborted)
                    : new WorkspaceMcp(DaemonState.Stopped, null, null, null, null, null);
            });
    }

    public static readonly FieldMiddleware SignedIn = next => context =>
    {
        if (context.Mode() == TaskerMode.Server
            && context.Service<IHttpContextAccessor>().HttpContext?.User.Identity?.IsAuthenticated != true)
            throw new GraphUnauthenticatedException();
        return next(context);
    };
}
