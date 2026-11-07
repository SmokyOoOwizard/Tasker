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

public class TaskSearchApiTests : IAsyncLifetime
{
    private sealed class EmptyModule : Module;

    private const string Query = """
        query Search($project: UUID!, $query: String!) {
          project(id: $project) {
            taskSearch(query: $query) {
              score field match
              snippet { text ranges { start length } }
              task { title seriesNumbers { number } }
            }
          }
        }
        """;

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

    private async Task<JsonNode> Post(string path, string? token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();

    [Fact]
    public async Task The_server_answers_search_through_graphql()
    {
        var token = (await Post("/api/auth/register", null, new { username = "owner", email = "owner@example.com", password = "Passw0rd!123" }))["accessToken"]!.GetValue<string>();
        var project = await Post("/api/projects", token, new { name = "Search" });
        var p = $"/api/projects/{Id(project)}";
        var status = await Post(p + "/statuses", token, new { name = "Todo", color = "#112233" });
        var set = await Post(p + "/status-sets", token, new { name = "Set", statusIds = new[] { Id(status) } });
        var type = await Post(p + "/task-types", token, new { name = "Task", statusSetId = Id(set) });
        var series = await Post(p + "/series", token, new { name = "Tasks", prefix = "TSK" });
        await Post(p + "/tasks", token, new { title = "Первая", typeId = Id(type), seriesIds = new[] { Id(series) } });
        await Post(p + "/tasks", token, new { title = "Вторая", description = "упомянуто редкое слово", typeId = Id(type), seriesIds = new[] { Id(series) } });

        var apiBase = new Uri(_http.BaseAddress!, "/api").ToString();
        var (code, byNumber) = await GraphReads.Query(_http, apiBase, Query, new { project = Id(project), query = "tsk-1" }, token);
        Assert.Equal(HttpStatusCode.OK, code);
        var hit = byNumber!["data"]!["project"]!["taskSearch"]![0]!;
        Assert.Equal("Первая", hit["task"]!["title"]!.GetValue<string>());
        Assert.Equal("number", hit["field"]!.GetValue<string>());
        Assert.Equal("exact", hit["match"]!.GetValue<string>());

        var (_, byText) = await GraphReads.Query(_http, apiBase, Query, new { project = Id(project), query = "РЕДКОЕ" }, token);
        var inText = byText!["data"]!["project"]!["taskSearch"]![0]!;
        Assert.Equal("description", inText["field"]!.GetValue<string>());
        Assert.Contains("редкое", inText["snippet"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_stranger_does_not_see_the_tasks()
    {
        var owner = (await Post("/api/auth/register", null, new { username = "owner", email = "owner@example.com", password = "Passw0rd!123" }))["accessToken"]!.GetValue<string>();
        await Post("/api/users", owner, new { username = "guest", email = "guest@example.com", password = "Passw0rd!123" });
        var guest = (await Post("/api/auth/login", null, new { login = "guest", password = "Passw0rd!123" }))["accessToken"]!.GetValue<string>();
        var project = await Post("/api/projects", owner, new { name = "Secret" });

        var apiBase = new Uri(_http.BaseAddress!, "/api").ToString();
        var (_, body) = await GraphReads.Query(_http, apiBase, Query, new { project = Id(project), query = "секрет" }, guest);

        Assert.Null(body!["data"]?["project"]);
    }
}
