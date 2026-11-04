using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using HotChocolate;
using HotChocolate.Types;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Tasker.Web.Graph;
using Xunit;

namespace Tasker.Tests;

public static class GraphReads
{
    private static readonly Lazy<Task<ISchemaDefinition>> Schema = new(() => GraphSetup.BuildSchema());

    private enum Shape
    {
        One,
        List,
        Page,
        Tree
    }

    private sealed record Field(string Name, Dictionary<string, object?> Args);

    private sealed record Read(Field Root, Field? Nested, string Type, Shape Shape);

    public static async Task<(HttpStatusCode Status, JsonNode? Body)> Query(
        HttpClient http, string apiBase, string query, object? variables = null, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, apiBase.TrimEnd('/') + GraphSetup.Path)
        {
            Content = JsonContent.Create(new { query, variables })
        };
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    public static async Task<(HttpStatusCode Status, JsonNode? Body)> Get(HttpClient http, string apiBase, string path, string? token = null)
    {
        var read = Translate(path);
        var (text, variables) = await Compose(read);
        var (status, body) = await Query(http, apiBase, text, variables, token);
        if (body?["errors"] is JsonArray { Count: > 0 } errors)
        {
            var error = errors[0]!;
            var code = error["extensions"]?["status"]?.GetValue<int>() ?? (status == HttpStatusCode.OK ? 400 : (int)status);
            var rest = new JsonObject { ["error"] = error["message"]!.GetValue<string>() };
            if (error["extensions"]?["conflict"] is { } conflict)
                rest["code"] = conflict.GetValue<string>();
            return ((HttpStatusCode)code, rest);
        }

        Assert.Equal(HttpStatusCode.OK, status);
        var node = body!["data"]?[read.Root.Name];
        if (read.Nested != null)
            node = node?[read.Nested.Name];
        return node == null ? (HttpStatusCode.NotFound, null) : (HttpStatusCode.OK, Restore(node.DeepClone()));
    }

    public static async Task<HttpResponseMessage> Subscribe(HttpClient http, string apiBase, string fields = "entity projectId id")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, apiBase.TrimEnd('/') + GraphSetup.Path)
        {
            Content = JsonContent.Create(new { query = $"subscription {{ workspaceChanges {{ {fields} }} }}" })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    public static JsonNode? Change(string line) =>
        line.StartsWith("data:", StringComparison.Ordinal) && line.Length > "data:".Length
            ? JsonNode.Parse(line["data:".Length..])?["data"]?["workspaceChanges"]?.DeepClone()
            : null;

    private static Read Translate(string path)
    {
        var split = path.IndexOf('?');
        var route = split < 0 ? path : path[..split];
        var query = QueryHelpers.ParseQuery(split < 0 ? "" : path[split..]);
        var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["api", ..])
            parts = parts[1..];

        if (parts is ["projects", var projectId, .. var rest])
        {
            var project = new Field("project", new() { ["id"] = projectId });
            if (rest.Length == 0)
                return new Read(project, null, "Project", Shape.One);
            var (nested, type, shape) = ProjectRead(rest, query);
            return new Read(project, nested, type, shape);
        }

        return parts switch
        {
            ["projects"] => new Read(Paged("projects", query), null, "Project", Shape.Page),
            ["users"] => new Read(Paged("users", query), null, "User", Shape.Page),
            ["users", var id] => new Read(One("user", id), null, "User", Shape.One),
            ["agents"] => new Read(Paged("agents", query), null, "User", Shape.Page),
            ["agents", var id] => new Read(One("agent", id), null, "User", Shape.One),
            ["agents", var id, "tokens"] => new Read(Paged("agentTokens", query, ("agentId", id)), null, "AgentToken", Shape.Page),
            ["auth", "me"] => new Read(new Field("me", new()), null, "User", Shape.One),
            ["auth", "registration"] => new Read(new Field("registration", new()), null, "Registration", Shape.One),
            ["workspace", "problems"] => new Read(Paged("workspaceProblems", query), null, "WorkspaceProblem", Shape.Page),
            ["workspace", "mcp"] => new Read(new Field("workspaceMcp", new()), null, "WorkspaceMcp", Shape.One),
            _ => throw new ArgumentException($"No GraphQL read for GET {path}")
        };
    }

    private static (Field Field, string Type, Shape Shape) ProjectRead(string[] rest, Dictionary<string, StringValues> query) => rest switch
    {
        ["stats"] => (new Field("stats", new()), "ProjectStats", Shape.One),
        ["statuses"] => (Described("statuses", query), "StatusListItem", Shape.Page),
        ["statuses", var id] => (One("status", id), "Status", Shape.One),
        ["status-sets"] => (Paged("statusSets", query), "StatusSet", Shape.Page),
        ["status-sets", var id] => (One("statusSet", id), "StatusSet", Shape.One),
        ["task-types"] => (Described("taskTypes", query), "TaskTypeListItem", Shape.Page),
        ["task-types", var id] => (One("taskType", id), "TaskType", Shape.One),
        ["fields"] => (Paged("fields", query), "FieldDefinition", Shape.Page),
        ["fields", var id] => (One("field", id), "FieldDefinition", Shape.One),
        ["enums"] => (Paged("enums", query), "FieldEnum", Shape.Page),
        ["enums", var id] => (One("enum", id), "FieldEnum", Shape.One),
        ["link-types"] => (Paged("linkTypes", query), "LinkType", Shape.Page),
        ["link-types", var id] => (One("linkType", id), "LinkType", Shape.One),
        ["tasks"] when query.TryGetValue("flat", out var flat) && flat == "false" => (Tasks("taskTree", query), "TaskTreeItem", Shape.Tree),
        ["tasks"] => (Tasks("tasks", query), "TaskListItem", Shape.Page),
        ["tasks", "resolve"] => (new Field("resolveTasks", new() { ["ref"] = query.TryGetValue("ref", out var reference) ? reference.ToString() : "" }), "TaskItem", Shape.List),
        ["tasks", var id] => (One("task", id), "TaskDetails", Shape.One),
        ["tasks", var id, "links"] => (One("taskLinks", id), "TaskLinkView", Shape.List),
        ["series"] => (Paged("seriesList", query), "Series", Shape.Page),
        ["series", "health"] => (new Field("seriesHealth", new()), "SeriesHealth", Shape.One),
        ["series", var id] => (One("series", id), "Series", Shape.One),
        ["boards"] => (Paged("boards", query), "Board", Shape.Page),
        ["boards", var id] => (One("board", id), "Board", Shape.One),
        ["boards", var id, "columns", var column, "tasks"] => (ColumnTasks(id, column, query), "TaskListItem", Shape.Page),
        ["locks"] => (Paged("locks", query), "LockInfo", Shape.Page),
        ["members"] => (Paged("members", query), "User", Shape.Page),
        ["lock"] => (new Field("lock", new() { ["entity"] = "project", ["id"] = null }), "LockInfo", Shape.One),
        [var folder, var id, "lock"] => (new Field("lock", new() { ["entity"] = LockedEntity(folder), ["id"] = id }), "LockInfo", Shape.One),
        _ => throw new ArgumentException($"No GraphQL read for GET /projects/…/{string.Join('/', rest)}")
    };

    private static Field One(string name, string id) => new(name, new() { ["id"] = id });

    private static Field Paged(string name, Dictionary<string, StringValues> query, params (string Name, object? Value)[] extra)
    {
        var args = new Dictionary<string, object?> { ["offset"] = Number(query, "offset"), ["limit"] = Number(query, "limit") };
        foreach (var (key, value) in extra)
            args[key] = value;
        return new Field(name, args);
    }

    private static Field Described(string name, Dictionary<string, StringValues> query) =>
        Paged(name, query, ("descriptionLength", Number(query, "descriptionLength")));

    private static Field Tasks(string name, Dictionary<string, StringValues> query)
    {
        var args = new Dictionary<string, object?>(Described(name, query).Args)
        {
            ["statusIds"] = Many(query, "statusId"),
            ["typeIds"] = Many(query, "typeId"),
            ["seriesIds"] = Many(query, "seriesId"),
            ["field"] = Many(query, "field"),
            ["sort"] = query.TryGetValue("sort", out var sort) ? sort.ToString() : null
        };
        if (name == "taskTree")
        {
            args["parentId"] = query.TryGetValue("parentId", out var parent) ? parent.ToString() : null;
            args["levels"] = Number(query, "levels");
        }
        return Described(name, query) with { Args = args };
    }

    private static Field ColumnTasks(string boardId, string columnId, Dictionary<string, StringValues> query) =>
        new("columnTasks", new(Described("columnTasks", query).Args)
        {
            ["boardId"] = boardId,
            ["columnId"] = columnId,
            ["field"] = Many(query, "field"),
            ["sort"] = query.TryGetValue("sort", out var sort) ? sort.ToString() : null
        });

    private static object? Number(Dictionary<string, StringValues> query, string name) =>
        query.TryGetValue(name, out var value) ? int.TryParse(value, out var number) ? number : value.ToString() : null;

    private static string[]? Many(Dictionary<string, StringValues> query, string name) =>
        query.TryGetValue(name, out var value) ? value.Select(x => x ?? "").ToArray() : null;

    private static string LockedEntity(string folder) => folder switch
    {
        "tasks" => "task",
        "task-types" => "taskType",
        "statuses" => "status",
        "status-sets" => "statusSet",
        "boards" => "board",
        "series" => "series",
        "link-types" => "linkType",
        "fields" => "field",
        "enums" => "enum",
        _ => throw new ArgumentException($"No lock for {folder}")
    };

    private static async Task<(string Query, Dictionary<string, object?> Variables)> Compose(Read read)
    {
        var schema = await Schema.Value;
        var declared = new List<string>();
        var variables = new Dictionary<string, object?>();

        string Call(ObjectType owner, Field field, string prefix)
        {
            var args = new List<string>();
            foreach (var (name, given) in field.Args)
            {
                var variable = prefix + name;
                var value = name == "id" && given == null && read.Nested != null ? read.Root.Args["id"] : given;
                declared.Add($"${variable}: {owner.Fields[field.Name].Arguments[name].Type.Print()}");
                variables[variable] = value;
                args.Add($"{name}: ${variable}");
            }

            return args.Count == 0 ? field.Name : $"{field.Name}({string.Join(", ", args)})";
        }

        var selection = Selection(schema, read.Type);
        selection = read.Shape switch
        {
            Shape.Page => $"{{ totalCount offset limit data {selection} }}",
            Shape.Tree => $"{{ totalCount offset limit topLevelCount data {selection} }}",
            _ => selection
        };

        var root = Call((ObjectType)schema.Types["Query"], read.Root, "root_");
        var body = read.Nested == null
            ? $"{{ {root} {selection} }}"
            : $"{{ {root} {{ {Call((ObjectType)schema.Types["Project"], read.Nested, "")} {selection} }} }}";
        var text = declared.Count == 0 ? $"query {body}" : $"query({string.Join(", ", declared)}) {body}";
        return (text, variables);
    }

    private static string Selection(ISchemaDefinition schema, string name)
    {
        var type = (ObjectType)schema.Types[name];
        var fields = new StringBuilder("{");
        foreach (var field in type.Fields)
        {
            if (field.IsIntrospectionField || field.Arguments.Count > 0 || name == "Project" && field.Name is "stats" or "seriesHealth")
                continue;
            fields.Append(' ').Append(field.Name);
            if (field.Type.NamedType() is ObjectType nested)
                fields.Append(' ').Append(Selection(schema, nested.Name));
        }

        return fields.Append(" }").ToString();
    }

    private static JsonNode Restore(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj.ToArray())
                {
                    if (name == "dropStatuses" && value is JsonArray drops)
                    {
                        var map = new JsonObject();
                        foreach (var drop in drops)
                            map[drop!["statusSetId"]!.GetValue<string>()] = drop["statusId"]!.GetValue<string>();
                        obj[name] = map;
                    }
                    else if (value != null)
                        Restore(value);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    if (item != null)
                        Restore(item);
                break;
        }

        return node;
    }
}
