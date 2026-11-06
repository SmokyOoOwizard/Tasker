using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autofac;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Tasker.Core;
using Tasker.Core.Tasks;
using Tasker.Daemon.Host;
using Tasker.Web;

namespace Tasker.Tests;

internal static class ApiSchemaGenerator
{
    private sealed class EmptyModule : Module;

    private sealed record Surface(JsonObject Document, string[] Routes, HashSet<string> Secured);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<string> Build()
    {
        var server = await Fetch(TaskerMode.Server);
        var local = await Fetch(TaskerMode.Local);
        var daemon = await FetchDaemon();
        var document = Merge(server, local, daemon);
        return document.ToJsonString(Indented) + "\n";
    }

    public static async Task<string[]> Routes(TaskerMode mode) => (await Fetch(mode)).Routes;

    public static async Task<string[]> DaemonRoutes() => (await FetchDaemon()).Routes;

    private static async Task<Surface> Fetch(TaskerMode mode)
    {
        var builder = TaskerWebApp.CreateBuilder<EmptyModule>([], mode);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        AddOpenApi(builder.Services, path => path.StartsWith("api/", StringComparison.Ordinal));

        await using var app = builder.Build();
        app.MapTasker();
        app.MapOpenApi().AllowAnonymous();
        return await Read(app, "/api/");
    }

    private static async Task<Surface> FetchDaemon()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(options => TaskerJson.Configure(options.SerializerOptions));
        AddOpenApi(builder.Services, _ => true);

        await using var app = builder.Build();
        McpDaemon.MapControl(app, new DaemonRuntimeState(5719, 5719), null!, "token", null!);
        app.MapOpenApi();
        return await Read(app, "/");
    }

    private static void AddOpenApi(IServiceCollection services, Func<string, bool> include) =>
        services.AddOpenApi("v1", options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
            options.ShouldInclude = description => description.RelativePath is { } path && include(path);
            options.AddOperationTransformer(async (operation, context, ct) =>
            {
                switch (operation.OperationId)
                {
                    case "updateTaskType":
                        await Cascade(operation, context, typeof(TaskType), ct);
                        break;
                    case "updateFieldEnum":
                        await Cascade(operation, context, typeof(Tasker.Core.Fields.FieldEnum), ct);
                        break;
                }
            });
        });

    private static async Task Cascade(OpenApiOperation operation, OpenApiOperationTransformerContext context, Type value, CancellationToken ct)
    {
        var schema = await context.GetOrCreateSchemaAsync(value, null, ct);
        operation.Responses!["200"].Content!["application/json"].Schema = new OpenApiSchema
        {
            AllOf =
            [
                schema,
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Required = new HashSet<string> { "affectedTasks" },
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["affectedTasks"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
                        ["affectedColumns"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" }
                    }
                }
            ]
        };
    }

    private static async Task<Surface> Read(WebApplication app, string prefix)
    {
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var document = (JsonObject)JsonNode.Parse(await http.GetStringAsync("/openapi/v1.json"))!;
            Normalize(document);
            Enums(document);

            var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Where(x => x.RoutePattern.RawText is { } text && text.StartsWith(prefix, StringComparison.Ordinal) && !text.StartsWith("/openapi", StringComparison.Ordinal) && !text.Contains("{*") && x.Metadata.GetMetadata<IHttpMethodMetadata>() != null)
                .ToArray();
            var routes = endpoints
                .SelectMany(x => x.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Select(method => $"{method.ToLowerInvariant()} {Path(x.RoutePattern.RawText!)}"))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var secured = endpoints
                .Where(x => x.Metadata.GetMetadata<IAuthorizeData>() != null && x.Metadata.GetMetadata<IAllowAnonymous>() == null)
                .Select(x => x.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName)
                .ToHashSet();
            return new Surface(document, routes, secured);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static string Path(string pattern) => Regex.Replace(pattern, @"\{(\w+)(:[^}]*)?\}", "{$1}");

    private static readonly string[] Methods = ["get", "post", "put", "patch", "delete"];

    private static JsonObject Merge(Surface server, Surface local, Surface daemon)
    {
        var paths = new JsonObject();
        var schemas = new JsonObject();
        var modes = new Dictionary<string, SortedSet<string>>();

        void Take(Surface surface, string mode)
        {
            foreach (var (path, item) in (JsonObject)surface.Document["paths"]!)
            {
                var target = paths[path] as JsonObject ?? (JsonObject)(paths[path] = new JsonObject());
                foreach (var (method, node) in (JsonObject)item!)
                {
                    var operation = (JsonObject)node!;
                    var key = method + " " + path;
                    if (!modes.TryGetValue(key, out var set))
                        modes[key] = set = [];
                    set.Add(mode);

                    if (mode == "server" && operation["operationId"]?.GetValue<string>() is { } id && surface.Secured.Contains(id))
                        operation["security"] = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() });
                    if (mode == "daemon")
                        operation["security"] = operation["responses"]!.AsObject().ContainsKey("401")
                            ? new JsonArray(new JsonObject { ["daemonControl"] = new JsonArray() })
                            : new JsonArray();

                    if (target[method] is JsonObject existing)
                        MergeOperation(existing, operation, mode);
                    else
                        target[method] = operation.DeepClone();
                }
            }

            foreach (var (name, schema) in (JsonObject)surface.Document["components"]!["schemas"]!)
            {
                if (schemas[name] is { } current)
                {
                    if (!JsonNode.DeepEquals(current, schema))
                        throw new InvalidOperationException($"Schema {name} differs between modes: {current.ToJsonString()} vs {schema!.ToJsonString()}");
                }
                else
                {
                    schemas[name] = schema!.DeepClone();
                }
            }
        }

        Take(server, "server");
        Take(local, "local");
        Take(daemon, "daemon");

        foreach (var (path, item) in paths)
        {
            foreach (var (method, node) in (JsonObject)item!)
            {
                var operation = (JsonObject)node!;
                var set = modes[method + " " + path];
                operation["x-tasker-modes"] = new JsonArray(set.Select(x => (JsonNode)x).ToArray());
                operation["tags"] = new JsonArray((JsonNode)Tag(path));
                if (set.Contains("local") && set.Contains("server"))
                    operation["security"] = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() }, new JsonObject());
                else if (set.Contains("local") && !set.Contains("daemon"))
                    operation.Remove("security");
                PathParameters(operation, path);
                Order(operation);
            }

            if (((JsonObject)item!).Any(x => modes[x.Key + " " + path].Contains("daemon")))
                ((JsonObject)item)["servers"] = new JsonArray(new JsonObject
                {
                    ["url"] = "http://127.0.0.1:{port}",
                    ["description"] = "Tasker daemon (control interface, no workspace prefix)",
                    ["variables"] = new JsonObject { ["port"] = new JsonObject { ["default"] = "5719" } }
                });
        }

        var document = new JsonObject
        {
            ["openapi"] = "3.0.4",
            ["info"] = new JsonObject
            {
                ["title"] = "Tasker API",
                ["version"] = "1.0.0",
                ["description"] = Description
            },
            ["servers"] = new JsonArray(
                new JsonObject
                {
                    ["url"] = "{scheme}://{host}",
                    ["description"] = "Tasker server (Server mode): sign in with POST /api/auth/login and send the access token as a Bearer token",
                    ["variables"] = new JsonObject
                    {
                        ["scheme"] = new JsonObject { ["default"] = "https", ["enum"] = new JsonArray("https", "http") },
                        ["host"] = new JsonObject { ["default"] = "localhost:5000" }
                    }
                },
                new JsonObject
                {
                    ["url"] = "http://127.0.0.1:{port}/w/{workspace}",
                    ["description"] = "Tasker daemon (Local mode): no sign-in, loopback only; the workspace is the key of an open folder",
                    ["variables"] = new JsonObject
                    {
                        ["port"] = new JsonObject { ["default"] = "5719" },
                        ["workspace"] = new JsonObject { ["default"] = "workspace" }
                    }
                }),
            ["paths"] = Sort(paths),
            ["components"] = new JsonObject
            {
                ["schemas"] = Sort(schemas),
                ["securitySchemes"] = new JsonObject
                {
                    ["bearerAuth"] = new JsonObject
                    {
                        ["type"] = "http",
                        ["scheme"] = "bearer",
                        ["bearerFormat"] = "JWT",
                        ["description"] = "Server mode: the access token from POST /api/auth/login, /api/auth/register or /api/auth/refresh."
                    },
                    ["daemonControl"] = new JsonObject
                    {
                        ["type"] = "apiKey",
                        ["in"] = "header",
                        ["name"] = "X-Tasker-Control",
                        ["description"] = "Daemon control interface: the secret from the daemon's daemon.json, readable only by its owner."
                    }
                }
            }
        };

        Deduplicate(document["paths"]!, (JsonObject)document["components"]!["schemas"]!, null, top: true);
        RequireResponseProperties(document);
        Finish(document);
        Prune(document);
        return document;
    }

    private const string Description =
        "REST API of Tasker. The same API is served by the Tasker server (Server mode, JWT sign-in) and by the local daemon (Local mode, no sign-in, loopback only; every request is addressed to one workspace through the /w/{workspace} prefix). " +
        "x-tasker-modes on an operation lists where it exists: server, local, daemon (control interface of the daemon, which has its own address and secret). " +
        "Data is read through GraphQL (operation executeGraphQL, schema graphql/schema.graphql); this REST API changes data (POST, PATCH, DELETE), signs in, reports health and controls the daemon. PATCH changes only the fields that are sent (null or absent keep the value) and needs the version the client saw; DELETE takes it in the version query parameter. " +
        "Errors are {error}; a conflict (409) also carries code: in_use (the entity is used), modified (someone changed it first: reload and retry), locked (someone is editing it, heldBy says who) or unsupported_format.";

    private static string Tag(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return "other";
        if (segments[0] != "api")
            return segments[0] == "daemon" ? "daemon" : "daemon";
        if (segments.Length >= 4 && segments[1] == "projects")
            return segments[3] == "lock" ? "locks" : segments[3];
        return segments.Length >= 2 ? segments[1] : "other";
    }

    private static void MergeOperation(JsonObject existing, JsonObject incoming, string mode)
    {
        var responses = (JsonObject)existing["responses"]!;
        foreach (var (code, response) in (JsonObject)incoming["responses"]!)
        {
            if (!responses.ContainsKey(code))
                responses[code] = response!.DeepClone();
        }

        var left = (JsonObject)existing.DeepClone();
        var right = (JsonObject)incoming.DeepClone();
        foreach (var node in new[] { left, right })
        {
            node.Remove("responses");
            node.Remove("security");
        }

        if (!JsonNode.DeepEquals(left, right))
            throw new InvalidOperationException($"Operation {existing["operationId"]} differs between modes ({mode})");
    }

    private static JsonObject Sort(JsonObject node)
    {
        var sorted = new JsonObject();
        foreach (var (key, value) in node.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray())
            sorted[key] = value?.DeepClone();
        return sorted;
    }

    private static void PathParameters(JsonObject operation, string path)
    {
        var declared = (operation["parameters"] as JsonArray)?.Select(x => x!.DeepClone()).ToList() ?? [];
        var inPath = Regex.Matches(path, @"\{(\w+)\}").Select(x => x.Groups[1].Value).ToArray();
        if (inPath.All(name => declared.Any(x => IsPathParameter(x, name))))
            return;

        var parameters = new JsonArray();
        foreach (var name in inPath)
            parameters.Add(declared.FirstOrDefault(x => IsPathParameter(x, name)) ?? new JsonObject
            {
                ["name"] = name,
                ["in"] = "path",
                ["required"] = true,
                ["schema"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" }
            });
        foreach (var parameter in declared.Where(x => x["in"]?.GetValue<string>() != "path"))
            parameters.Add(parameter);

        var properties = operation.ToArray();
        operation.Clear();
        foreach (var (key, value) in properties)
        {
            if (key == "parameters")
                continue;
            operation[key] = value?.DeepClone();
            if (key == "operationId")
                operation["parameters"] = parameters;
        }
    }

    private static bool IsPathParameter(JsonNode parameter, string name) =>
        parameter["in"]?.GetValue<string>() == "path" && parameter["name"]?.GetValue<string>() == name;

    private static void Order(JsonObject operation)
    {
        var responses = (JsonObject)operation["responses"]!;
        var sorted = new JsonObject();
        foreach (var (code, value) in responses.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray())
            sorted[code] = value!.DeepClone();
        operation["responses"] = sorted;
    }

    private static readonly Regex NumberPattern = new(@"^-\?\(\?:0\|\[1-9\]\\d\*\)", RegexOptions.Compiled);

    private static void Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                    Normalize(item);
                break;
            case JsonObject schema:
                foreach (var (_, value) in schema.ToArray())
                    Normalize(value);
                NumberFromString(schema);
                NullableEnum(schema);
                NullableReference(schema);
                OptionalNullable(schema);
                break;
        }
    }

    private static void NumberFromString(JsonObject schema)
    {
        if (schema["anyOf"] is not JsonArray any || schema["pattern"] is null)
            return;

        var types = any.OfType<JsonObject>().Select(x => x["type"]?.GetValue<string>()).ToArray();
        var number = types.FirstOrDefault(x => x is "integer" or "number");
        if (number == null || !types.Contains("string"))
            return;

        schema.Remove("anyOf");
        schema.Remove("pattern");
        schema["type"] = number;
        Reorder(schema, "type");
    }

    private static void Enums(JsonObject document)
    {
        foreach (var (_, node) in (JsonObject)document["components"]!["schemas"]!)
        {
            if (node is JsonObject { } schema && schema["enum"] is JsonArray values && values.All(x => x is JsonValue v && v.TryGetValue<string>(out _)))
            {
                schema.Remove("nullable");
                if (schema["type"] == null)
                {
                    schema["type"] = "string";
                    Reorder(schema, "type");
                }
            }
        }
    }

    private static void NullableEnum(JsonObject schema)
    {
        if (schema["enum"] is not JsonArray values)
            return;

        var at = values.ToList().FindIndex(x => x == null);
        if (at >= 0 && values.Count > 1)
        {
            values.RemoveAt(at);
            schema["nullable"] = true;
        }

        if (schema["type"] == null && values.Count > 0 && values.All(x => x is JsonValue v && v.TryGetValue<string>(out _)))
        {
            schema["type"] = "string";
            Reorder(schema, "type");
        }
    }

    private static void NullableReference(JsonObject schema)
    {
        if (schema["oneOf"] is not JsonArray one || one.Count != 2)
            return;

        var nullIndex = one.ToList().FindIndex(x => x is JsonObject o && o["enum"] is JsonArray { Count: 1 } e && e[0] == null);
        if (nullIndex < 0)
            return;

        var other = (JsonObject)one[1 - nullIndex]!.DeepClone();
        schema.Remove("oneOf");
        if (other.ContainsKey("$ref"))
            schema["allOf"] = new JsonArray(other);
        else
            foreach (var (key, value) in other.ToArray())
                schema[key] = value?.DeepClone();
        schema["nullable"] = true;
    }

    private static void OptionalNullable(JsonObject schema)
    {
        if (schema["required"] is not JsonArray required || schema["properties"] is not JsonObject properties)
            return;

        for (var i = required.Count - 1; i >= 0; i--)
        {
            if (properties[required[i]!.GetValue<string>()] is JsonObject { } property && property["nullable"]?.GetValue<bool>() == true)
                required.RemoveAt(i);
        }

        if (required.Count == 0)
            schema.Remove("required");
    }

    private static void Reorder(JsonObject schema, string first)
    {
        var entries = schema.ToArray();
        foreach (var (key, _) in entries)
            schema.Remove(key);
        schema[first] = entries.First(x => x.Key == first).Value;
        foreach (var (key, value) in entries.Where(x => x.Key != first))
            schema[key] = value;
    }

    private static void Deduplicate(JsonNode node, JsonObject schemas, string? self, bool top)
    {
        switch (node)
        {
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } item)
                    {
                        Deduplicate(item, schemas, self, false);
                        if (Match(item, schemas, self) is { } name)
                            array[i] = new JsonObject { ["$ref"] = $"#/components/schemas/{name}" };
                    }
                }
                break;
            case JsonObject obj:
                foreach (var (key, value) in obj.ToArray())
                {
                    if (value == null)
                        continue;
                    Deduplicate(value, schemas, self, false);
                    if (key != "properties" && Match(value, schemas, self) is { } name)
                        obj[key] = new JsonObject { ["$ref"] = $"#/components/schemas/{name}" };
                }
                break;
        }
    }

    private static string? Match(JsonNode node, JsonObject schemas, string? self)
    {
        if (node is not JsonObject { } candidate || candidate["type"]?.GetValue<string>() is not ("object" or "string") || candidate.ContainsKey("$ref"))
            return null;
        if (candidate["properties"] == null && candidate["enum"] == null)
            return null;

        return schemas.FirstOrDefault(x => x.Key != self && JsonNode.DeepEquals(x.Value, candidate)).Key;
    }

    private static void RequireResponseProperties(JsonObject document)
    {
        var schemas = (JsonObject)document["components"]!["schemas"]!;
        var requested = new HashSet<string>();
        var answered = new HashSet<string>();

        foreach (var (_, item) in (JsonObject)document["paths"]!)
        {
            foreach (var (method, node) in (JsonObject)item!)
            {
                if (node is not JsonObject operation || method == "servers")
                    continue;
                if (operation["requestBody"] is { } body)
                    Reach(body, schemas, requested);
                if (operation["responses"] is { } responses)
                    Reach(responses, schemas, answered);
            }
        }

        foreach (var name in answered.Except(requested))
        {
            var schema = (JsonObject)schemas[name]!;
            if (schema["properties"] is not JsonObject properties)
                continue;

            var required = new JsonArray();
            foreach (var (property, value) in properties)
            {
                if (value is JsonObject { } definition && definition["nullable"]?.GetValue<bool>() != true)
                    required.Add(property);
            }

            if (required.Count > 0)
                schema["required"] = required;
            else
                schema.Remove("required");
        }
    }

    private static void Reach(JsonNode? node, JsonObject schemas, HashSet<string> seen)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                    Reach(item, schemas, seen);
                break;
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == "$ref" && value!.GetValue<string>() is { } reference && reference.StartsWith("#/components/schemas/", StringComparison.Ordinal))
                    {
                        var name = reference["#/components/schemas/".Length..];
                        if (seen.Add(name))
                            Reach(schemas[name], schemas, seen);
                    }
                    else
                    {
                        Reach(value, schemas, seen);
                    }
                }
                break;
        }
    }

    private static void Finish(JsonObject document)
    {
        var schemas = (JsonObject)document["components"]!["schemas"]!;

        if (schemas["ApiError"] is JsonObject apiError)
        {
            var code = (JsonObject)apiError["properties"]!["code"]!;
            code["enum"] = new JsonArray(ErrorCatalog.All.Select(x => JsonValue.Create(x.Code)).ToArray<JsonNode?>());
            code["description"] = "Stable refusal code, see docs/api/error-codes.json for its parameters. The same code, params and usages come in GraphQL errors[].extensions.";
            ((JsonObject)apiError["properties"]!["error"]!)["description"] = "English text, the fallback when a client has no translation for the code.";
        }

        if (schemas["ErrorUsage"] is JsonObject usage)
        {
            var kind = (JsonObject)usage["properties"]!["kind"]!;
            kind["enum"] = new JsonArray(ErrorUsage.Kinds.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>());
        }

        foreach (var (_, item) in (JsonObject)document["paths"]!)
        {
            foreach (var (method, node) in (JsonObject)item!)
            {
                if (method == "servers" || node is not JsonObject operation || operation["operationId"]?.GetValue<string>() != "executeGraphQL")
                    continue;

                var content = (JsonObject)operation["responses"]!["200"]!["content"]!;
                if (!content.ContainsKey("application/json"))
                {
                    content["application/json"] = content["text/event-stream"]!.DeepClone();
                    ((JsonObject)operation["responses"]!["200"]!)["description"] = "A query result (application/json) or a subscription stream (text/event-stream)";
                }

                operation["summary"] = "GraphQL endpoint: all reads and live changes";
                operation["description"] =
                    "Reads go through GraphQL, not GET (schema: graphql/schema.graphql). Body: {query, operationName, variables}. A query returns application/json {data, errors}; an error answers 400, or 401 when sign-in is needed (Server mode). " +
                    "A subscription (workspaceChanges, the replacement of the old GET /events) answers text/event-stream: each result is one `data: <GraphQLResponse as JSON>` line followed by an empty line, lines starting with a colon are comments to ignore (`: connected` right after the connection opens, `: ping` every 20 seconds while idle). " +
                    "Treat a change as a hint to reload what it names; after a reconnect reload everything, events are not replayed.";
            }
        }
    }

    private static void Prune(JsonObject document)
    {
        var schemas = (JsonObject)document["components"]!["schemas"]!;
        var used = new HashSet<string>();
        var queue = new Queue<JsonNode>();
        queue.Enqueue(document["paths"]!);

        while (queue.Count > 0)
        {
            Walk(queue.Dequeue());
        }

        foreach (var name in schemas.Select(x => x.Key).Where(x => !used.Contains(x)).ToArray())
            schemas.Remove(name);

        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonArray array:
                    foreach (var item in array)
                        Walk(item);
                    break;
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                    {
                        if (key == "$ref" && value!.GetValue<string>() is { } reference && reference.StartsWith("#/components/schemas/", StringComparison.Ordinal))
                        {
                            var name = reference["#/components/schemas/".Length..];
                            if (used.Add(name))
                                queue.Enqueue(schemas[name]!);
                        }
                        else
                        {
                            Walk(value);
                        }
                    }
                    break;
            }
        }
    }
}
