using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

/// <summary>Серверный режим (SQLite в памяти, JWT): маршруты серий требуют входа и уважают доступ к проектам.</summary>
public class SeriesServerApiTests : IAsyncLifetime
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

    [Fact]
    public async Task Series_routes_need_a_login()
    {
        var project = Guid.NewGuid();
        var task = Guid.NewGuid();

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, $"/api/projects/{project}/series"),
            (HttpMethod.Get, $"/api/projects/{project}/series/health"),
            (HttpMethod.Post, $"/api/projects/{project}/series"),
            (HttpMethod.Get, $"/api/projects/{project}/series/{Guid.NewGuid()}"),
            (HttpMethod.Patch, $"/api/projects/{project}/series/{Guid.NewGuid()}"),
            (HttpMethod.Delete, $"/api/projects/{project}/series/{Guid.NewGuid()}?version=v"),
            (HttpMethod.Get, $"/api/projects/{project}/tasks/resolve?ref=TSK-1"),
            (HttpMethod.Post, $"/api/projects/{project}/tasks/{task}/series"),
            (HttpMethod.Delete, $"/api/projects/{project}/tasks/{task}/series/{Guid.NewGuid()}?version=v"),
            (HttpMethod.Post, $"/api/projects/{project}/tasks/{task}/series/{Guid.NewGuid()}/renumber"),
        })
        {
            var (status, _) = await Send(method, path, null, method == HttpMethod.Get || method == HttpMethod.Delete ? null : new { });
            Assert.True(status == HttpStatusCode.Unauthorized, $"{method} {path}: {status}");
        }
    }

    [Fact]
    public async Task A_member_uses_series_and_a_non_member_gets_404_for_the_project()
    {
        var owner = (await Send(HttpMethod.Post, "/api/auth/register", null, new { username = "owner", email = "owner@example.com", password = "Passw0rd!123" })).Body!;
        var ownerToken = owner["accessToken"]!.GetValue<string>();
        var created = (await Send(HttpMethod.Post, "/api/users", ownerToken, new { username = "guest", email = "guest@example.com", password = "Passw0rd!123" })).Body!;
        Assert.NotNull(created["id"]);
        var guestToken = (await Send(HttpMethod.Post, "/api/auth/login", null, new { login = "guest", password = "Passw0rd!123" })).Body!["accessToken"]!.GetValue<string>();

        var project = (await Send(HttpMethod.Post, "/api/projects", ownerToken, new { name = "Secret" })).Body!;
        var p = $"/api/projects/{Id(project)}";
        var status = (await Send(HttpMethod.Post, p + "/statuses", ownerToken, new { name = "Todo", color = "#112233" })).Body!;
        var set = (await Send(HttpMethod.Post, p + "/status-sets", ownerToken, new { name = "Set", statusIds = new[] { Id(status) } })).Body!;
        var type = (await Send(HttpMethod.Post, p + "/task-types", ownerToken, new { name = "Task", statusSetId = Id(set) })).Body!;

        // Участник: полный цикл.
        var (createdStatus, series) = await Send(HttpMethod.Post, p + "/series", ownerToken, new { name = "Tasks", prefix = "TSK" });
        Assert.Equal(HttpStatusCode.Created, createdStatus);
        var task = (await Send(HttpMethod.Post, p + "/tasks", ownerToken, new { title = "t", typeId = Id(type), seriesIds = new[] { Id(series!) } })).Body!;
        Assert.Equal(1, task["seriesNumbers"]![0]!["number"]!.GetValue<int>());
        Assert.Single((await Send(HttpMethod.Get, p + "/tasks/resolve?ref=TSK-1", ownerToken)).Body!.AsArray());
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, p + "/series/health", ownerToken)).Status);

        // Не участник: проект для него не существует — 404 на всех маршрутах серий, без утечки данных.
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, "/series", null),
            (HttpMethod.Get, "/series/health", null),
            (HttpMethod.Post, "/series", new { name = "Mine", prefix = "MINE" }),
            (HttpMethod.Get, $"/series/{Id(series!)}", null),
            (HttpMethod.Patch, $"/series/{Id(series!)}", new { name = "Hacked", version = series!["version"]!.GetValue<string>() }),
            (HttpMethod.Delete, $"/series/{Id(series!)}?version={series!["version"]!.GetValue<string>()}", null),
            (HttpMethod.Get, "/tasks/resolve?ref=TSK-1", null),
            (HttpMethod.Get, $"/tasks?seriesId={Id(series!)}", null),
            (HttpMethod.Post, $"/tasks/{Id(task)}/series", new { seriesId = Id(series!), version = task["version"]!.GetValue<string>() }),
            (HttpMethod.Post, $"/tasks/{Id(task)}/series/{Id(series!)}/renumber", new { version = task["version"]!.GetValue<string>() }),
            (HttpMethod.Delete, $"/tasks/{Id(task)}/series/{Id(series!)}?version={task["version"]!.GetValue<string>()}", null),
        })
        {
            var (code, _) = await Send(method, p + path, guestToken, body);
            Assert.True(code == HttpStatusCode.NotFound, $"{method} {path}: {code}");
        }

        // Ничего не изменилось.
        var after = (await Send(HttpMethod.Get, p + $"/series/{Id(series!)}", ownerToken)).Body!;
        Assert.Equal("Tasks", after["name"]!.GetValue<string>());
        Assert.Single((await Send(HttpMethod.Get, p + "/tasks/resolve?ref=TSK-1", ownerToken)).Body!.AsArray());
    }
}
