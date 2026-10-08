using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

/// <summary>
/// JSON не экранирует Unicode (TSK-88): кириллица, обратные кавычки, «ёлочки», эмодзи и <c>&lt;&gt;&amp;</c> идут как есть —
/// в ответах MCP, REST, <c>--json</c> консоли — и разбираются обратно. Кавычка и обратная косая по-прежнему экранируются.
/// </summary>
public class JsonEscapingTests : IDisposable
{
    private const string Title = "Привет, `мир` «ёлочки» 🙂 <b>&</b> \"кавычки\" \\";

    private static readonly string[][] Seed =
    [
        ["status", "create", "Todo"], ["status", "create", "Done"],
        ["status-set", "create", "Flow", "--status", "Todo", "Done"],
        ["task-type", "create", "Bug", "--status-set", "Flow"]
    ];

    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    private static void AssertRaw(string text)
    {
        Assert.DoesNotContain("\\u", text);
        Assert.Contains("Привет, `мир` «ёлочки» 🙂 <b>&</b>", text);
        // Служебные символы экранируются, как положено: JSON остаётся валидным.
        Assert.Contains("\\\"кавычки\\\" \\\\", text);
    }

    [Fact]
    public async Task Mcp_rest_and_cli_json_keep_unicode_as_is()
    {
        var folder = await _daemon.Workspace("a", "Alpha");
        foreach (var args in Seed)
            Assert.Equal(0, (await TaskerProcess.Run([.. args, "-p", "Alpha", "-w", folder])).Code);
        for (var i = 0; i < 3; i++)
            Assert.Equal(0, (await TaskerProcess.Run("task", "create", Title + i, "--type", "Bug", "-p", "Alpha", "-w", folder)).Code);
        var key = (await _daemon.Start()).Workspaces.Single().Key!;

        // --json консоли.
        var cli = await TaskerProcess.Run("task", "list", "-p", "Alpha", "-w", folder, "--json");
        Assert.Equal(0, cli.Code);
        AssertRaw(cli.Out);
        Assert.Equal(Title + "0", JsonNode.Parse(cli.Out)!["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).Order().First());

        // MCP: сырое тело ответа (конверт JSON-RPC) и текст результата.
        var projectId = JsonNode.Parse(await _daemon.CallTool(key, "list_projects"))!["data"]![0]!["id"]!.GetValue<string>();
        var raw = await RawMcpResponse(key, "list_tasks", new JsonObject { ["projectId"] = projectId });
        var body = raw.Split('\n').First(x => x.StartsWith("data:"))["data:".Length..].Trim();
        var text = JsonNode.Parse(body)!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        AssertRaw(text);
        Assert.Equal(3, JsonNode.Parse(text)!["data"]!.AsArray().Count);

        // На проводе (TSK-95): конверт JSON-RPC кодирует SDK своими опциями, слой WireJson убирает \uXXXX из тела ответа.
        Assert.Contains("Привет, `мир` «ёлочки» 🙂 <b>&</b>", raw);
        Assert.DoesNotContain("\\u", raw);
        // Кавычка и косая в тексте результата экранированы дважды (JSON внутри строки JSON), как и положено.
        Assert.Contains("\\\\\\\"кавычки\\\\\\\" \\\\\\\\", raw);
        // Размер: то же сообщение стандартным кодировщиком (\uXXXX, как у SDK) заметно больше.
        var escaped = JsonNode.Parse(body)!.ToJsonString(new JsonSerializerOptions());
        Assert.True(escaped.Length > body.Length * 1.3, $"{escaped.Length} vs {body.Length}");    }

    [Fact]
    public async Task Rest_keeps_unicode_as_is()
    {
        var folder = await _daemon.Workspace("r", "Alpha", allow: false);
        foreach (var args in Seed)
            Assert.Equal(0, (await TaskerProcess.Run([.. args, "-p", "Alpha", "-w", folder])).Code);
        for (var i = 0; i < 3; i++)
            Assert.Equal(0, (await TaskerProcess.Run("task", "create", Title + i, "--type", "Bug", "-p", "Alpha", "-w", folder)).Code);

        var builder = TaskerWebApp.CreateBuilder<DaemonModule>([], TaskerMode.Local);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapTasker();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        await using var lease = await app.Services.GetRequiredService<WorkspaceRegistry>().Open(WorkspaceLocation.Files(folder));

        using var http = new HttpClient();
        var projectId = JsonNode.Parse(await http.GetStringAsync($"{address}{lease.BasePath}/api/projects"))!["data"]![0]!["id"];
        var rest = await http.GetStringAsync($"{address}{lease.BasePath}/api/projects/{projectId}/tasks");
        AssertRaw(rest);
        Assert.Equal(3, JsonNode.Parse(rest)!["data"]!.AsArray().Count);

        // Размер: тот же ответ со стандартным кодировщиком (\uXXXX) заметно больше.
        var escaped = JsonNode.Parse(rest)!.ToJsonString(new JsonSerializerOptions());
        Assert.True(escaped.Length > rest.Length * 1.3, $"{escaped.Length} vs {rest.Length}");
    }

    [Fact]
    public async Task Cli_json_in_process()
    {
        using var ws = TestWorkspace.Create("files");
        await ws.Run("project", "create", "Demo");
        foreach (var args in Seed)
            Assert.Equal(0, (await ws.InProject("Demo", args)).Code);
        await ws.InProject("Demo", "task", "create", Title, "--type", "Bug");
        var result = await ws.InProject("Demo", "task", "list", "--json");
        AssertRaw(result.Out);
        Assert.Equal(Title, result.Json["data"]![0]!["title"]!.GetValue<string>());
    }

    private async Task<string> RawMcpResponse(string key, string tool, JsonObject arguments)
    {
        arguments["workspace"] = key;
        var call = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments }
        };
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, _daemon.McpUrl)
        {
            Content = new StringContent(call.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
