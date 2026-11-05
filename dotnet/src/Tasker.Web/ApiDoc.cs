using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace Tasker.Web;

internal static class ApiDoc
{
    private static readonly string[] Json = ["application/json"];

    public static RouteHandlerBuilder RespondsOk<T>(this RouteHandlerBuilder builder, string operationId) =>
        builder.WithName(operationId).Produces<T>(StatusCodes.Status200OK);

    public static RouteHandlerBuilder RespondsCreated<T>(this RouteHandlerBuilder builder, string operationId) =>
        builder.WithName(operationId).Produces<T>(StatusCodes.Status201Created);

    public static RouteHandlerBuilder RespondsNoContent(this RouteHandlerBuilder builder, string operationId) =>
        builder.WithName(operationId).Produces(StatusCodes.Status204NoContent);

    public static void WithApiErrors(this IEndpointConventionBuilder builder, TaskerMode mode) =>
        builder.Add(endpoint =>
        {
            if (endpoint is not RouteEndpointBuilder route)
                return;

            var readOnly = endpoint.Metadata.OfType<IHttpMethodMetadata>().FirstOrDefault()?.HttpMethods.All(HttpMethods.IsGet) == true;
            Add(endpoint, StatusCodes.Status400BadRequest, typeof(ApiError));
            if (mode == TaskerMode.Server)
            {
                Add(endpoint, StatusCodes.Status401Unauthorized, typeof(ApiError));
                Add(endpoint, StatusCodes.Status403Forbidden, typeof(ApiError));
            }

            if (route.RoutePattern.Parameters.Count > 0)
                Add(endpoint, StatusCodes.Status404NotFound, typeof(ApiError));
            if (!readOnly)
                Add(endpoint, StatusCodes.Status409Conflict, typeof(ApiError));
        });

    private static void Add(EndpointBuilder endpoint, int status, Type type)
    {
        if (!endpoint.Metadata.OfType<IProducesResponseTypeMetadata>().Any(x => x.StatusCode == status))
            endpoint.Metadata.Add(new ProducesResponseTypeMetadata(status, type, Json));
    }
}
