using Tasker.Core;
using Tasker.Core.Agents;
using Tasker.Daemon;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Web;

public sealed record ApiError(string Error, string Code, IReadOnlyDictionary<string, object?> Params, IReadOnlyList<ErrorUsage> Usages)
{
    public static ApiError From(TaskerException exception) =>
        new(exception.Error.Message, exception.Error.Code, exception.Error.Params, exception.Error.Usages);
}

public sealed record AuthRegistration(bool Open);

public sealed record HealthStatus(string Status, string Mode);

public sealed record AgentTokenIssued(string Token, AgentToken Info);

public sealed record WorkspaceMcpInfo(
    DaemonState Daemon,
    int? Port,
    string? Url,
    string? Workspace,
    WorkspaceState? WorkspaceState,
    string? Error,
    string AgentHeader);

public sealed record GraphQLRequest(string Query, string? OperationName, Dictionary<string, object?>? Variables);

public sealed record GraphQLError(string Message, object[]? Path, Dictionary<string, object?>? Extensions);

public sealed record GraphQLResponse(Dictionary<string, object?>? Data, GraphQLError[]? Errors);
