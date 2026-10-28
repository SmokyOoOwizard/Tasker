using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Autofac;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Web;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// <c>inputSchema</c> инструментов MCP .NET (сервер, вход по токену агента, в процессе теста): совпадает с эталоном
/// <c>tests/golden/expected/mcp/tools-list.json</c>, по которому сверяется и Rust-демон (<c>tasker-mcpd/tests/mcp.rs</c>), с поправкой на то,
/// что у сервера одна область — нет <c>list_workspaces</c> и аргумента <c>workspace</c>; во вложенных объектах (<c>columns[]</c>, <c>fields[]</c>…)
/// в <c>required</c> только то, без чего не обойтись: вызов без необязательных вложенных свойств проходит.
/// </summary>
public class McpInputSchemaTests : IAsyncLifetime
{
    private sealed class EmptyModule : Module;

    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _agentToken = null!;
    private string _ownerToken = null!;
    private string _agentId = null!;

    public async Task InitializeAsync()
    {
        var builder = TaskerWebApp.CreateBuilder<EmptyModule>([], TaskerMode.Server);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapTasker();
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };

        _ownerToken = (await Rest(HttpMethod.Post, "/api/auth/register", null,
            new { username = "owner", email = "owner@example.com", password = "Passw0rd!123" }))["accessToken"]!.GetValue<string>();
        var agent = await Rest(HttpMethod.Post, "/api/agents", _ownerToken, new { username = "bot" });
        _agentId = agent["id"]!.GetValue<string>();
        _agentToken = (await Rest(HttpMethod.Post, $"/api/agents/{_agentId}/tokens", _ownerToken, new { name = "t" }))["token"]!.GetValue<string>();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task<JsonNode> Rest(HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
            request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!;
    }

    private async Task<JsonNode> Rpc(string method, JsonObject? parameters = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters ?? new JsonObject() }.ToJsonString(),
                Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _agentToken);
        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var data = body.Split('\n').First(x => x.StartsWith("data:"))["data:".Length..].Trim();
        return JsonNode.Parse(data)!["result"]!;
    }

    private async Task<JsonNode> Call(string tool, object args)
    {
        var result = await Rpc("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args)) });
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.False(result["isError"]?.GetValue<bool>() ?? false, $"{tool}: {text}");
        return JsonNode.Parse(text)!;
    }

    private static string GoldenToolsList()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "golden", "expected", "mcp", "tools-list.json");
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException("tests/golden/expected/mcp/tools-list.json not found above the test binaries");
    }

    private static Dictionary<string, JsonNode> ByName(JsonNode tools) =>
        tools.AsArray().ToDictionary(x => x!["name"]!.GetValue<string>(), x => x!);

    /// <summary>Все вложенные объекты схемы (без самого верхнего уровня) с путём к ним.</summary>
    private static IEnumerable<(string Path, JsonObject Schema)> NestedObjects(JsonNode? node, string path, bool top = true)
    {
        if (node is not JsonObject schema)
            yield break;
        if (!top && schema["properties"] is JsonObject)
            yield return (path, schema);
        if (schema["properties"] is JsonObject properties)
            foreach (var (name, property) in properties)
                foreach (var nested in NestedObjects(property, $"{path}.{name}", top: false))
                    yield return nested;
        foreach (var nested in NestedObjects(schema["items"], path + "[]", top: false))
            yield return nested;
    }

    private static string[] Required(JsonObject schema) =>
        schema["required"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? [];

    [Fact]
    public async Task Tools_list_matches_the_golden_snapshot_shared_with_the_rust_daemon_except_the_workspace_argument()
    {
        var actual = ByName((await Rpc("tools/list"))["tools"]!);
        var expected = ByName(JsonNode.Parse(await File.ReadAllTextAsync(GoldenToolsList()))!["result"]!["tools"]!);

        // Эталон снят с демона, где область — аргумент вызова; у сервера область одна: list_workspaces нет, остальные инструменты те же.
        Assert.Equal(["list_workspaces"], expected.Keys.Except(actual.Keys));
        Assert.Empty(actual.Keys.Except(expected.Keys));

        // У общих инструментов отличие одно — аргумент workspace (необязательный, последний); без него схема та же байт в байт.
        foreach (var (name, tool) in actual)
        {
            var golden = expected[name].DeepClone();
            var properties = golden["inputSchema"]!["properties"]!.AsObject();
            Assert.Equal("workspace", properties.Last().Key);
            properties.Remove("workspace");
            Assert.DoesNotContain("workspace", golden["inputSchema"]!["required"]?.ToJsonString() ?? "");
            Assert.Equal(golden.ToJsonString(), tool.ToJsonString());
        }
    }

    [Fact]
    public async Task Nested_objects_require_only_what_cannot_be_omitted()
    {
        var tools = ByName((await Rpc("tools/list"))["tools"]!);

        var nested = tools.SelectMany(t => NestedObjects(t.Value["inputSchema"], t.Key)).ToDictionary(x => x.Path, x => Required(x.Schema));
        Assert.Equal(new Dictionary<string, string[]>
        {
            ["create_board.columns[]"] = ["name", "statusIds"],
            ["update_board.columns[]"] = ["name", "statusIds"],
            ["create_task_type.fields[]"] = ["fieldId"],
            ["update_task_type.fields[]"] = ["fieldId"],
            ["create_task.fields"] = [],
            ["create_task.fields.values[]"] = ["fieldId"],
            ["create_task.fields.newOwnFields[]"] = ["name", "type"],
            ["update_task.fields"] = [],
            ["update_task.fields.values[]"] = ["fieldId"],
            ["update_task.fields.newOwnFields[]"] = ["name", "type"],
            ["update_field.mapping[]"] = ["from", "to"],
            ["update_enum.values[]"] = ["name"],
        }.OrderBy(x => x.Key), nested.OrderBy(x => x.Key));

        // Свойство со значением по умолчанию обязательным не бывает ни на каком уровне.
        foreach (var (name, tool) in tools)
            foreach (var (path, schema) in NestedObjects(tool["inputSchema"], name).Append((name, tool["inputSchema"]!.AsObject())))
                foreach (var property in Required(schema))
                    Assert.Null(schema["properties"]![property]!["default"]);
    }

    [Fact]
    public async Task Calls_without_optional_nested_properties_work()
    {
        var projectId = (await Rest(HttpMethod.Post, "/api/projects", _ownerToken, new { name = "Alpha" }))["id"]!.GetValue<string>();
        await Rest(HttpMethod.Post, $"/api/projects/{projectId}/members", _ownerToken, new { userId = _agentId });
        var todo = (await Call("create_status", new { projectId, name = "Todo", color = "#112233" }))["id"]!.GetValue<string>();
        var set = (await Call("create_status_set", new { projectId, name = "Set", statusIds = new[] { todo } }))["id"]!.GetValue<string>();
        var level = await Call("create_enum", new { projectId, name = "Level", values = new[] { "Low" } });
        var field = (await Call("create_field", new { projectId, name = "Estimate", type = "int" }))["id"]!.GetValue<string>();

        // columns[] без id и dropStatuses.
        var board = await Call("create_board", new { projectId, name = "Main", statusSetIds = new[] { set }, columns = new[] { new { name = "Todo", statusIds = new[] { todo } } } });
        Assert.Equal("Todo", board["columns"]![0]!["name"]!.GetValue<string>());
        var updatedBoard = await Call("update_board", new
        {
            projectId, boardId = board["id"]!.GetValue<string>(), version = board["version"]!.GetValue<string>(),
            columns = new[] { new { name = "All", statusIds = new[] { todo } } }
        });
        Assert.Equal("All", updatedBoard["columns"]![0]!["name"]!.GetValue<string>());

        // fields[] без required: поле подключается необязательным.
        var type = await Call("create_task_type", new { projectId, name = "Task", statusSetId = set, fields = new[] { new { fieldId = field } } });
        Assert.False(type["fields"]![0]!["required"]!.GetValue<bool>());
        var updatedType = await Call("update_task_type", new { projectId, taskTypeId = type["id"]!.GetValue<string>(), version = type["version"]!.GetValue<string>(), fields = new[] { new { fieldId = field } } });
        Assert.False(updatedType["fields"]![0]!["required"]!.GetValue<bool>());

        // values[] у перечисления без id — новое значение; fields.values[] у задачи без values — значения нет.
        var levels = await Call("update_enum", new
        {
            projectId, enumId = level["id"]!.GetValue<string>(), version = level["version"]!.GetValue<string>(),
            values = new object[] { new { id = level["values"]![0]!["id"]!.GetValue<string>(), name = "Low" }, new { name = "High" } }
        });
        Assert.Equal(["Low", "High"], levels["values"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray());
        var task = await Call("create_task", new { projectId, title = "t", typeId = type["id"]!.GetValue<string>(), fields = new { values = new[] { new { fieldId = field } } } });
        Assert.Empty(task["fields"]!.AsArray());
    }
}
