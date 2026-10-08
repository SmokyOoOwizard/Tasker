using System.Net;
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

/// <summary>Серверный режим (SQLite в памяти, JWT людей и токены агентов): поля требуют входа и уважают доступ к проектам, в REST и в MCP.</summary>
public class FieldsServerTests : IAsyncLifetime
{
    private sealed class EmptyModule : Module;

    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var builder = TaskerWebApp.CreateBuilder<EmptyModule>([], TaskerMode.Server);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapTasker();
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Send(HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
            request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();
    private static string Version(JsonNode node) => node["version"]!.GetValue<string>();

    private async Task<(string Result, bool IsError)> Mcp(string? token, string tool, object arguments)
    {
        var call = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(arguments)) }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(call.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return (((int)response.StatusCode).ToString(), true);
        var body = await response.Content.ReadAsStringAsync();
        var data = body.Split('\n').First(x => x.StartsWith("data:"))["data:".Length..].Trim();
        var result = JsonNode.Parse(data)!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]?.GetValue<bool>() ?? false);
    }

    [Fact]
    public async Task Field_routes_need_a_login()
    {
        var project = Guid.NewGuid();
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, $"/api/projects/{project}/fields"),
            (HttpMethod.Post, $"/api/projects/{project}/fields"),
            (HttpMethod.Get, $"/api/projects/{project}/fields/{Guid.NewGuid()}"),
            (HttpMethod.Patch, $"/api/projects/{project}/fields/{Guid.NewGuid()}"),
            (HttpMethod.Delete, $"/api/projects/{project}/fields/{Guid.NewGuid()}?version=v"),
            (HttpMethod.Get, $"/api/projects/{project}/enums"),
            (HttpMethod.Post, $"/api/projects/{project}/enums"),
            (HttpMethod.Get, $"/api/projects/{project}/enums/{Guid.NewGuid()}"),
            (HttpMethod.Patch, $"/api/projects/{project}/enums/{Guid.NewGuid()}"),
            (HttpMethod.Delete, $"/api/projects/{project}/enums/{Guid.NewGuid()}?version=v"),
        })
        {
            var (status, _) = await Send(method, path, null, method == HttpMethod.Get || method == HttpMethod.Delete ? null : new { });
            Assert.True(status == HttpStatusCode.Unauthorized, $"{method} {path}: {status}");
        }
    }

    [Fact]
    public async Task A_member_uses_fields_and_a_non_member_gets_404_for_the_project_in_rest_and_mcp()
    {
        var owner = (await Send(HttpMethod.Post, "/api/auth/register", null, new { username = "owner", email = "owner@example.com", password = "Passw0rd!123" })).Body!;
        var ownerToken = owner["accessToken"]!.GetValue<string>();
        await Send(HttpMethod.Post, "/api/users", ownerToken, new { username = "guest", email = "guest@example.com", password = "Passw0rd!123" });
        var guestToken = (await Send(HttpMethod.Post, "/api/auth/login", null, new { login = "guest", password = "Passw0rd!123" })).Body!["accessToken"]!.GetValue<string>();

        var project = (await Send(HttpMethod.Post, "/api/projects", ownerToken, new { name = "Secret" })).Body!;
        var p = $"/api/projects/{Id(project)}";
        var status = (await Send(HttpMethod.Post, p + "/statuses", ownerToken, new { name = "Todo", color = "#112233" })).Body!;
        var set = (await Send(HttpMethod.Post, p + "/status-sets", ownerToken, new { name = "Set", statusIds = new[] { Id(status) } })).Body!;
        var type = (await Send(HttpMethod.Post, p + "/task-types", ownerToken, new { name = "Task", statusSetId = Id(set) })).Body!;

        // Участник: каталог, поле типа, задача со значением и видом полей.
        var levels = (await Send(HttpMethod.Post, p + "/enums", ownerToken, new { name = "Level", values = new[] { "Low", "High" } })).Body!;
        var (createdStatus, field) = await Send(HttpMethod.Post, p + "/fields", ownerToken, new { name = "Level", type = "enum", enumId = Id(levels) });
        Assert.Equal(HttpStatusCode.Created, createdStatus);
        var typed = (await Send(HttpMethod.Patch, p + $"/task-types/{Id(type)}", ownerToken,
            new { version = Version(type), fields = new[] { new { fieldId = Id(field!), required = true } } })).Body!;
        Assert.Single(typed["fields"]!.AsArray());
        var task = (await Send(HttpMethod.Post, p + "/tasks", ownerToken,
            new { title = "t", typeId = Id(type), fields = new { values = new[] { new { fieldId = Id(field!), values = new[] { "High" } } } } })).Body!;
        Assert.Equal("High", task["fieldViews"]![0]!["texts"]![0]!.GetValue<string>());

        // Не участник: проект для него не существует — 404 на всех маршрутах полей, без утечки и без изменений.
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, "/fields", null),
            (HttpMethod.Post, "/fields", new { name = "Mine", type = "int" }),
            (HttpMethod.Get, $"/fields/{Id(field!)}", null),
            (HttpMethod.Patch, $"/fields/{Id(field!)}", new { name = "Hacked", version = Version(field!) }),
            (HttpMethod.Delete, $"/fields/{Id(field!)}?version={Version(field!)}", null),
            (HttpMethod.Get, "/enums", null),
            (HttpMethod.Post, "/enums", new { name = "Mine", values = new[] { "a" } }),
            (HttpMethod.Get, $"/enums/{Id(levels)}", null),
            (HttpMethod.Patch, $"/enums/{Id(levels)}", new { name = "Hacked", version = Version(levels) }),
            (HttpMethod.Delete, $"/enums/{Id(levels)}?version={Version(levels)}", null),
            (HttpMethod.Get, $"/tasks/{Id(task)}", null),
            (HttpMethod.Patch, $"/task-types/{Id(type)}", new { version = Version(typed), fields = Array.Empty<object>(), removedFields = "clear" }),
        })
        {
            var (code, _) = await Send(method, p + path, guestToken, body);
            Assert.True(code == HttpStatusCode.NotFound, $"{method} {path}: {code}");
        }

        Assert.Equal("Level", (await Send(HttpMethod.Get, p + $"/fields/{Id(field!)}", ownerToken)).Body!["name"]!.GetValue<string>());
        Assert.Single((await Send(HttpMethod.Get, p + $"/task-types/{Id(type)}", ownerToken)).Body!["fields"]!.AsArray());

        // Агент: токен выпускает владелец; пока агент не участник проекта, MCP его не видит; участник — работает.
        var agent = (await Send(HttpMethod.Post, "/api/agents", ownerToken, new { username = "bot" })).Body!;
        var issued = (await Send(HttpMethod.Post, $"/api/agents/{Id(agent)}/tokens", ownerToken, new { name = "t" })).Body!;
        var agentToken = issued["token"]!.GetValue<string>();

        Assert.Equal("401", (await Mcp(null, "list_fields", new { projectId = Id(project) })).Result);
        Assert.Equal("401", (await Mcp(guestToken, "list_fields", new { projectId = Id(project) })).Result);
        var (hidden, hiddenError) = await Mcp(agentToken, "list_fields", new { projectId = Id(project) });
        Assert.True(hiddenError);
        Assert.StartsWith("[not_found]", hidden);
        Assert.StartsWith("[not_found]", (await Mcp(agentToken, "create_field", new { projectId = Id(project), name = "X", type = "int" })).Result);
        Assert.StartsWith("[not_found]", (await Mcp(agentToken, "create_enum", new { projectId = Id(project), name = "X", values = new[] { "a" } })).Result);

        await Send(HttpMethod.Post, p + "/members", ownerToken, new { userId = Id(agent) });
        var (listed, listError) = await Mcp(agentToken, "list_fields", new { projectId = Id(project) });
        Assert.False(listError, listed);
        Assert.Equal("Level", JsonNode.Parse(listed)!["data"]![0]!["name"]!.GetValue<string>());

        var (made, madeError) = await Mcp(agentToken, "create_field", new { name = "Estimate", type = "int" });
        Assert.False(madeError, made);
        var (agentTask, agentTaskError) = await Mcp(agentToken, "create_task", new
        {
            title = "by agent", typeId = Id(type), fields = new { values = new[] { new { fieldId = Id(field!), values = new[] { "Low" } } } }
        });
        Assert.False(agentTaskError, agentTask);
        Assert.Equal("Low", JsonNode.Parse(agentTask)!["fieldViews"]![0]!["texts"]![0]!.GetValue<string>());
        var (missing, missingError) = await Mcp(agentToken, "create_task", new { title = "no level", typeId = Id(type) });
        Assert.True(missingError);
        Assert.StartsWith("[invalid]", missing);
    }
}
