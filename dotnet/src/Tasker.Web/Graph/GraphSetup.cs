using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Execution.Configuration;
using HotChocolate.Transport.Formatters;
using HotChocolate.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tasker.Core;
using Tasker.Web.Graph.Types;
using Tasker.Web.Workspaces;

namespace Tasker.Web.Graph;

public static class GraphSetup
{
    public const string Schema = "tasker";
    public const string Path = "/graphql";
    public const int MaxExecutionDepth = 8;

    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    private static readonly JsonResultFormatter Json = new(new JsonResultFormatterOptions { Encoder = TaskerJson.Options.Encoder });

    private sealed record GraphRequest(string? Query, string? OperationName, JsonElement? Variables);

    public static IServiceCollection AddTaskerGraph(this IServiceCollection services)
    {
        services.AddGraphQL(Schema)
            .AddTaskerSchema()
            .ModifyOptions(x => x.DefaultQueryDependencyInjectionScope = DependencyInjectionScope.Request)
            .AddMaxExecutionDepthRule(MaxExecutionDepth)
            .AddErrorFilter<GraphErrorFilter>();
        return services;
    }

    public static IRequestExecutorBuilder AddTaskerSchema(this IRequestExecutorBuilder builder) =>
        builder
            .AddConvention<HotChocolate.Types.Descriptors.INamingConventions>(new GraphNaming())
            .AddQueryType<QueryType>()
            .AddSubscriptionType<SubscriptionType>()
            .AddTaskerTypes();

    public static async Task<ISchemaDefinition> BuildSchema(CancellationToken ct = default) =>
        await new ServiceCollection()
            .AddGraphQL()
            .AddTaskerSchema()
            .BuildSchemaAsync(cancellationToken: ct);

    public static async Task<string> Print(CancellationToken ct = default) =>
        (await BuildSchema(ct)).ToString().ReplaceLineEndings("\n").TrimEnd() + "\n";

    public static void MapTaskerGraph(this IEndpointRouteBuilder api) =>
        api.MapPost(Path, (Delegate)(Func<HttpContext, Task>)Execute)
            .Accepts<GraphQLRequest>("application/json")
            .Produces<GraphQLResponse>(StatusCodes.Status200OK)
            .Produces<GraphQLResponse>(StatusCodes.Status200OK, "text/event-stream")
            .Produces<GraphQLResponse>(StatusCodes.Status400BadRequest)
            .Produces<GraphQLResponse>(StatusCodes.Status401Unauthorized)
            .WithName("executeGraphQL");

    private static async Task Execute(HttpContext http)
    {
        GraphRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<GraphRequest>(http.Request.Body, TaskerJson.Options, http.RequestAborted);
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body is not { Query.Length: > 0 })
        {
            await WriteRejected(http, ["Body must be a GraphQL request: {query, operationName, variables}"]);
            return;
        }

        var request = OperationRequestBuilder.New()
            .SetDocument(body.Query)
            .SetOperationName(body.OperationName)
            .SetServices(http.RequestServices)
            .SetGlobalState(GraphScope.Key, new GraphScope());
        if (body.Variables is { ValueKind: JsonValueKind.Object } variables)
            request.SetVariableValues(variables.GetRawText());

        var executors = http.RequestServices.GetRequiredService<IRequestExecutorProvider>();
        var executor = await executors.GetExecutorAsync(Schema, http.RequestAborted);

        var appStopping = http.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        var workspaceClosing = http.Features.Get<WorkspaceFeature>()?.Closing ?? CancellationToken.None;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, workspaceClosing, appStopping);

        await using var result = await executor.ExecuteAsync(request.Build(), stop.Token);
        if (result is IResponseStream stream)
        {
            await WriteStream(http, stream, stop.Token);
            return;
        }

        var operation = result.ExpectOperationResult();
        http.RequestAborted.ThrowIfCancellationRequested();
        var failure = operation.Errors?.Select(x => x.Exception).FirstOrDefault(x => x != null && !GraphErrorFilter.IsDomainError(x));
        if (failure != null)
            ExceptionDispatchInfo.Throw(failure);

        if (operation.Data == null && operation.Errors is { } rejected && rejected.Any(x => !GraphErrorFilter.IsRefused(x)))
        {
            await WriteRejected(http, rejected.Select(x => x.Message));
            return;
        }

        http.Response.StatusCode = operation.Errors?.Any(GraphErrorFilter.IsUnauthenticated) == true
            ? StatusCodes.Status401Unauthorized
            : operation.Data == null ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK;
        http.Response.ContentType = "application/json; charset=utf-8";
        await Json.FormatAsync(result, http.Response.BodyWriter, ExecutionResultFormatFlags.None, http.RequestAborted);
    }

    private static async Task WriteStream(HttpContext http, IResponseStream stream, CancellationToken stop)
    {
        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        try
        {
            await response.WriteAsync(": connected\n\n", stop);
            await response.Body.FlushAsync(stop);

            await using var results = stream.ReadResultsAsync().GetAsyncEnumerator(stop);
            var next = results.MoveNextAsync().AsTask();
            while (true)
            {
                if (await Task.WhenAny(next, Task.Delay(Heartbeat, stop)) != next)
                {
                    stop.ThrowIfCancellationRequested();
                    await response.WriteAsync(": ping\n\n", stop);
                    await response.Body.FlushAsync(stop);
                    continue;
                }

                if (!await next)
                    break;

                await using (var item = results.Current)
                    await response.WriteAsync($"event: next\ndata: {await Format(item, stop)}\n\n", stop);
                await response.Body.FlushAsync(stop);
                next = results.MoveNextAsync().AsTask();
            }

            await response.WriteAsync("event: complete\ndata:\n\n", stop);
            await response.Body.FlushAsync(stop);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task WriteRejected(HttpContext http, IEnumerable<string> messages)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        await http.Response.WriteAsJsonAsync(new
        {
            errors = messages.Select(TaskerErrors.Invalid).Select(x => new
            {
                message = x.Message,
                extensions = new { status = x.Status, code = x.Code, @params = x.Error.Params, usages = x.Error.Usages }
            })
        });
    }

    private static async Task<string> Format(IExecutionResult result, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var writer = PipeWriter.Create(buffer, new StreamPipeWriterOptions(leaveOpen: true));
        await Json.FormatAsync(result, writer, ExecutionResultFormatFlags.None, ct);
        await writer.CompleteAsync();
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
