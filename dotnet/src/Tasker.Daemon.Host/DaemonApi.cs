using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Tasker.Daemon.Host;

public sealed record ControlError(string Error);

public sealed record StopResponse(bool Stopping);

public sealed record ReadyResponse(bool Ready, int Pid);

public sealed record NotReadyResponse(bool Ready, int Opening, bool Draining);

public sealed record DaemonHealth(string Status, string Mode, int Pid);

internal static class DaemonApi
{
    public static RouteHandlerBuilder RespondsControl<T>(this RouteHandlerBuilder builder, string operationId, params int[] errors)
    {
        builder.WithName(operationId).Produces<T>(StatusCodes.Status200OK).Produces(StatusCodes.Status401Unauthorized);
        foreach (var status in errors)
            builder.Produces<ControlError>(status);
        return builder;
    }

    public static RouteHandlerBuilder RespondsOk<T>(this RouteHandlerBuilder builder, string operationId) =>
        builder.WithName(operationId).Produces<T>(StatusCodes.Status200OK);
}
