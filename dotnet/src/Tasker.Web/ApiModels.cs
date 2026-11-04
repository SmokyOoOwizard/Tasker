using Tasker.Core.Agents;
using Tasker.Daemon;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Web;

public sealed record ApiError(string Error);

public sealed record LockHolder(string Name, DateTimeOffset ExpiresAt);

public sealed record ConflictError(string Error, string Code, LockHolder? HeldBy);

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
