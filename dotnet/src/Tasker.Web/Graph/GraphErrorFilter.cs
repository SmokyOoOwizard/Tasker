using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Tasker.Core;
using Tasker.Core.Locks;

namespace Tasker.Web.Graph;

internal sealed class GraphErrorFilter : IErrorFilter
{
    public const string BadRequest = "BAD_REQUEST";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";

    public IError OnError(IError error)
    {
        if (!IsDomainError(error.Exception))
            return error;

        var exception = error.Exception!;
        var (code, status) = exception switch
        {
            GraphUnauthenticatedException => (Unauthenticated, StatusCodes.Status401Unauthorized),
            TaskerValidationException => (BadRequest, StatusCodes.Status400BadRequest),
            TaskerNotFoundException => (NotFound, StatusCodes.Status404NotFound),
            TaskerForbiddenException => (Forbidden, StatusCodes.Status403Forbidden),
            _ => (Conflict, StatusCodes.Status409Conflict)
        };

        var result = error.WithMessage(exception.Message).WithCode(code).SetExtension("status", status);
        if (exception is TaskerConflictException conflict)
            result = result.SetExtension("conflict", TaskerApi.ConflictCodeName(conflict.Code));
        if (exception is TaskerLockedException locked)
            result = result.SetExtension("heldBy", new Dictionary<string, object?>
            {
                ["name"] = locked.HeldBy.Holder.Name,
                ["expiresAt"] = locked.HeldBy.ExpiresAt
            });

        return result.WithException(null);
    }

    public static bool IsDomainError(Exception? exception) =>
        exception is GraphUnauthenticatedException or TaskerValidationException or TaskerNotFoundException
            or TaskerForbiddenException or TaskerConflictException;

    public static bool IsUnauthenticated(IError error) => error.Code == Unauthenticated;
}

internal sealed class GraphUnauthenticatedException() : Exception("Sign in to read data");
