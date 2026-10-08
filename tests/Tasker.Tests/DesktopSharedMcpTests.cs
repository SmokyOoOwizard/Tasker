using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Daemon.Host;
using Tasker.Storage.Files.Workspaces;
using Tasker.Web;
using Tasker.Web.Workspaces;
using Xunit;

namespace Tasker.Tests;

/// <summary>MCP десктопа: один адрес <c>/mcp</c>, области — открытые вкладки, адрес <c>/w/{key}/mcp</c> убран.</summary>
public class DesktopSharedMcpTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IsolatedHome _home = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _home.Dispose();
        Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private static async Task<(int Status, string Body)> Post(string url, string method, JsonObject? parameters = null)
    {
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters ?? new JsonObject() }.ToJsonString(),
                Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Вызов инструмента: (признак ошибки, текст результата).</summary>
    private static async Task<(bool IsError, string Text)> Tool(string address, string tool, object args)
    {
        var (status, body) = await Post($"{address}/mcp", "tools/call",
            new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args)) });
        Assert.Equal(200, status);
        var data = body.Split('\n').First(x => x.StartsWith("data:"))["data:".Length..].Trim();
        var result = JsonNode.Parse(data)!["result"]!;
        return (result["isError"]?.GetValue<bool>() ?? false, result["content"]![0]!["text"]!.GetValue<string>());
    }

    private static async Task<string[]> ListNames(string address)
    {
        var (isError, text) = await Tool(address, "list_workspaces", new { });
        Assert.False(isError, text);
        return JsonNode.Parse(text)!["workspaces"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray();
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    [Fact]
    public async Task Open_tabs_are_the_workspaces_and_the_per_workspace_address_is_gone()
    {
        var builder = TaskerWebApp.CreateBuilder<DaemonModule>([], TaskerMode.Local);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapTasker();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var registry = app.Services.GetRequiredService<WorkspaceRegistry>();

        // Вкладок нет — агенту нечего открывать.
        Assert.Empty(await ListNames(address));

        await using var one = await registry.Open(WorkspaceLocation.Files(Folder("one")));
        await using var two = await registry.Open(WorkspaceLocation.Files(Folder("two")));
        Assert.Equal([one.Key, two.Key], await ListNames(address));

        // Без аргумента при двух вкладках — ошибка с перечнем; с аргументом — в нужной области.
        var (isError, text) = await Tool(address, "list_projects", new { });
        Assert.True(isError);
        Assert.StartsWith("[invalid] workspace is required", text);
        Assert.Contains(one.Key, text);
        Assert.Contains(two.Key, text);

        (isError, text) = await Tool(address, "create_project", new { workspace = two.Key, name = "InTwo" });
        Assert.False(isError, text);
        Assert.Contains("InTwo", (await Tool(address, "list_projects", new { workspace = two.Key })).Text);
        Assert.DoesNotContain("InTwo", (await Tool(address, "list_projects", new { workspace = one.Key })).Text);

        // Старый адрес области — 404, а REST по /w/{key}/… работает как раньше.
        Assert.Equal(404, (await Post($"{address}{one.BasePath}/mcp", "tools/list")).Status);
        using (var http = new HttpClient())
        {
            using var response = await http.GetAsync($"{address}{one.BasePath}/api/projects");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        // Закрыли вкладку — область пропала из доступных, и по имени её больше не открыть.
        await two.DisposeAsync();
        Assert.Equal([one.Key], await ListNames(address));
        (isError, text) = await Tool(address, "list_projects", new { workspace = two.Key });
        Assert.True(isError);
        Assert.StartsWith("[not_found]", text);

        // Осталась одна — аргумент не нужен.
        (isError, text) = await Tool(address, "list_projects", new { });
        Assert.False(isError, text);
        await app.StopAsync();
    }
}
