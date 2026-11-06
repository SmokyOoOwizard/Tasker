using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Core.Users;
using Tasker.Web.Workspaces;

namespace Tasker.Web.Auth;

internal static class LocalHumanRequests
{
    public static void UseLocalHuman(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Features.Get<WorkspaceFeature>() != null)
            {
                var human = context.RequestServices.GetRequiredService<LocalHuman>();
                var users = context.RequestServices.GetRequiredService<IUserStorage>();
                var id = WorkspaceEventsEndpoint.ChangesData(context.Request)
                    ? await human.Ensure(users, context.RequestAborted)
                    : await human.Find(users, context.RequestAborted);

                if (id != null)
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(TokenService.UserIdClaim, id.Value.ToString())], "LocalHuman"));
            }

            await next(context);
        });
}
