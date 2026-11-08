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

public class AgentRightsDaemonTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    private async Task<(string Result, bool IsError)> Mcp(string key, string agent, string tool, object? args = null)
    {
        var (isError, text) = await _daemon.CallToolResult(key, tool, args, new Dictionary<string, string> { ["X-Tasker-Agent"] = agent });
        return (text, isError);
    }

    [Fact]
    public async Task Rights_set_through_rest_limit_the_agent_in_the_daemon_mcp()
    {
        await _daemon.Workspace("a", "Alpha");
        var status = await _daemon.Start();
        var key = status.Workspaces.Single().Key!;
        var api = $"http://127.0.0.1:{status.Port}/w/{key}/api";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        using var created = await http.PostAsJsonAsync($"{api}/agents", new { username = "reader" });
        var agent = (await created.Content.ReadFromJsonAsync<JsonNode>())!;
        var agentId = agent["id"]!.GetValue<string>();
        using var limited = await http.PatchAsJsonAsync($"{api}/agents/{agentId}", new
        {
            version = agent["version"]!.GetValue<string>(),
            rights = new { editTasks = true, deleteTasks = false, configureProject = false, manageSeries = false }
        });
        Assert.Equal(HttpStatusCode.OK, limited.StatusCode);

        var (who, whoError) = await Mcp(key, agentId, "whoami");
        Assert.False(whoError, who);
        var rights = JsonNode.Parse(who)!["rights"]!;
        Assert.True(rights["editTasks"]!.GetValue<bool>());
        Assert.False(rights["configureProject"]!.GetValue<bool>());

        var (refused, refusedError) = await Mcp(key, agentId, "create_status", new { name = "Done", color = "#112233" });
        Assert.True(refusedError);
        Assert.StartsWith("[forbidden] Agent 'reader' has no right 'configureProject'", refused);
        Assert.StartsWith("[forbidden]", (await Mcp(key, agentId, "create_project", new { name = "Mine" })).Result);

        var (project, _) = await Mcp(key, agentId, "get_project", new { projectId = DaemonFixture.ProjectIds(await _daemon.CallTool(key, "list_projects"))[0] });
        Assert.False(JsonNode.Parse(project)!["rights"]!["deleteTasks"]!.GetValue<bool>());

        var (_, defaultWho) = await _daemon.CallToolResult(key, "whoami");
        Assert.True(JsonNode.Parse(defaultWho)!["rights"]!["configureProject"]!.GetValue<bool>());
        var (madeError, made) = await _daemon.CallToolResult(key, "create_status", new { name = "Done", color = "#112233" });
        Assert.False(madeError, made);
    }
}

public class AgentRightsServerTests : IAsyncLifetime
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

    private async Task<(string Result, bool IsError)> Mcp(string token, string tool, object arguments)
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var data = body.Split('\n').First(x => x.StartsWith("data:"))["data:".Length..].Trim();
        var result = JsonNode.Parse(data)!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]?.GetValue<bool>() ?? false);
    }

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();

    private static object Rights(bool edit, bool delete, bool configure, bool series) =>
        new { editTasks = edit, deleteTasks = delete, configureProject = configure, manageSeries = series };

    [Fact]
    public async Task Member_rights_limit_the_agent_in_mcp_and_a_person_changes_them_through_rest()
    {
        var owner = (await Send(HttpMethod.Post, "/api/auth/register", null, new { username = "owner", email = "owner@example.com", password = "Passw0rd!123" })).Body!;
        var token = owner["accessToken"]!.GetValue<string>();
        var project = (await Send(HttpMethod.Post, "/api/projects", token, new { name = "Main" })).Body!;
        var p = $"/api/projects/{Id(project)}";
        var status = (await Send(HttpMethod.Post, p + "/statuses", token, new { name = "Todo", color = "#112233" })).Body!;
        var set = (await Send(HttpMethod.Post, p + "/status-sets", token, new { name = "Set", statusIds = new[] { Id(status) } })).Body!;
        var type = (await Send(HttpMethod.Post, p + "/task-types", token, new { name = "Task", statusSetId = Id(set) })).Body!;

        var agent = (await Send(HttpMethod.Post, "/api/agents", token, new { username = "bot" })).Body!;
        var agentToken = (await Send(HttpMethod.Post, $"/api/agents/{Id(agent)}/tokens", token, new { name = "t" })).Body!["token"]!.GetValue<string>();
        var (added, member) = await Send(HttpMethod.Post, p + "/members", token, new { userId = Id(agent), rights = Rights(false, false, false, false) });
        Assert.Equal(HttpStatusCode.Created, added);
        Assert.False(member!["rights"]!["editTasks"]!.GetValue<bool>());

        var (readOnly, readOnlyError) = await Mcp(agentToken, "create_task", new { projectId = Id(project), title = "x", typeId = Id(type) });
        Assert.True(readOnlyError);
        Assert.StartsWith("[forbidden] Agent 'bot' has no right 'editTasks'", readOnly);
        Assert.False((await Mcp(agentToken, "list_tasks", new { projectId = Id(project) })).IsError);

        var (who, _) = await Mcp(agentToken, "whoami", new { });
        var projects = JsonNode.Parse(who)!["projects"]!.AsArray();
        Assert.Equal(Id(project), projects.Single()!["projectId"]!.GetValue<string>());
        Assert.False(projects.Single()!["rights"]!["editTasks"]!.GetValue<bool>());

        var (changed, changedBody) = await Send(HttpMethod.Patch, p + $"/members/{Id(agent)}", token, new { rights = Rights(true, false, false, false) });
        Assert.Equal(HttpStatusCode.OK, changed);
        Assert.True(changedBody!["rights"]!["editTasks"]!.GetValue<bool>());

        var (task, taskError) = await Mcp(agentToken, "create_task", new { projectId = Id(project), title = "by agent", typeId = Id(type) });
        Assert.False(taskError, task);
        var (deleteStatus, deleteError) = await Mcp(agentToken, "delete_status", new { projectId = Id(project), statusId = Id(status), version = status["version"]!.GetValue<string>() });
        Assert.True(deleteError);
        Assert.StartsWith("[forbidden]", deleteStatus);
        Assert.StartsWith("[forbidden]", (await Mcp(agentToken, "create_board", new { projectId = Id(project), name = "B", statusSetIds = new[] { Id(set) }, columns = Array.Empty<object>() })).Result);
        Assert.StartsWith("[forbidden]", (await Mcp(agentToken, "delete_task_type", new { projectId = Id(project), taskTypeId = Id(type), version = type["version"]!.GetValue<string>() })).Result);
        Assert.StartsWith("[forbidden]", (await Mcp(agentToken, "create_project", new { name = "Mine" })).Result);

        var (members, membersBody) = await GraphReads.Query(_http, new Uri(_http.BaseAddress!, "/api").ToString(),
            $"{{ project(id: \"{Id(project)}\") {{ members {{ data {{ username rights {{ editTasks deleteTasks }} }} }} }} }}", null, token);
        Assert.Equal(HttpStatusCode.OK, members);
        var rows = membersBody!["data"]!["project"]!["members"]!["data"]!.AsArray();
        Assert.True(rows.Single(x => x!["username"]!.GetValue<string>() == "bot")!["rights"]!["editTasks"]!.GetValue<bool>());
        Assert.Null(rows.Single(x => x!["username"]!.GetValue<string>() == "owner")!["rights"]);

        var (wrongPlace, wrongBody) = await Send(HttpMethod.Patch, $"/api/agents/{Id(agent)}", token, new { version = agent["version"]!.GetValue<string>(), rights = Rights(true, true, true, true) });
        Assert.Equal(HttpStatusCode.BadRequest, wrongPlace);
        Assert.Equal("agent.rightsOnMembership", wrongBody!["code"]!.GetValue<string>());
    }
}
