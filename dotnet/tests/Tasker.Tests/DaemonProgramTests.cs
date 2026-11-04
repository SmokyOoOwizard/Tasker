using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Daemon;
using Tasker.Storage.Files.Workspaces;
using Tasker.Web;
using Tasker.Web.Workspaces;
using Xunit;

namespace Tasker.Tests;

/// <summary>Где консоль ищет программу демона.</summary>
public class DaemonProgramTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Program(string directory)
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, directory)).FullName, DaemonLauncher.FileName);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void The_installed_console_comes_first_then_the_own_build()
    {
        var home = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
        var own = Path.Combine(_root, "own");

        var candidates = DaemonLauncher.Candidates(own, path: "", home: home);

        Assert.Equal(Path.Combine(own, DaemonLauncher.FileName), candidates[^1]);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(Path.Combine(home, ".local", "share", "tasker", "app", DaemonLauncher.FileName), candidates[0]);
    }

    [PlatformFact(TestPlatform.Unix)]
    public void The_console_from_path_is_followed_to_its_real_directory()
    {
        var app = Program("prefix/share/tasker/app");
        var console = Path.Combine(Path.GetDirectoryName(app)!, "tasker");
        File.WriteAllText(console, "");
        var bin = Directory.CreateDirectory(Path.Combine(_root, "prefix", "bin")).FullName;
        File.CreateSymbolicLink(Path.Combine(bin, "tasker"), console);

        var candidates = DaemonLauncher.Candidates(Path.Combine(_root, "own"), path: bin, home: Path.Combine(_root, "home"));

        Assert.Equal(app, candidates[0]);
        Assert.Equal(app, DaemonLauncher.Find(candidates));
    }

    [Fact]
    public void Missing_programs_are_skipped()
    {
        var own = Program("own");

        Assert.Equal(own, DaemonLauncher.Find(DaemonLauncher.Candidates(Path.GetDirectoryName(own)!, path: "", home: Path.Combine(_root, "home"))));
        Assert.Null(DaemonLauncher.Find([Path.Combine(_root, "nowhere", DaemonLauncher.FileName)]));
    }
}

/// <summary>Эндпоинт локального хоста <c>/w/{key}/api/workspace/mcp</c>: состояние демона и MCP этой области для интерфейса; своего MCP у хоста нет.</summary>
public class WorkspaceMcpEndpointTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IsolatedHome _home = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _home.Dispose();
        Directory.Delete(_folder, recursive: true);
        return Task.CompletedTask;
    }

    private sealed class FakeDaemon(WorkspaceMcp result) : IDaemonMcp
    {
        public Task<WorkspaceMcp> Find(WorkspaceLocation location, CancellationToken ct = default) => Task.FromResult(result);
    }

    private async Task<(JsonNode Json, string Address)> Ask(WorkspaceMcp? daemon, Func<string, Task>? also = null)
    {
        var builder = TaskerWebApp.CreateBuilder<WebTestModule>([], TaskerMode.Local);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (daemon != null)
            builder.Services.AddSingleton<IDaemonMcp>(new FakeDaemon(daemon));

        await using var app = builder.Build();
        app.MapTasker();
        await app.StartAsync();
        try
        {
            await using var lease = await app.Services.GetRequiredService<WorkspaceRegistry>().Open(WorkspaceLocation.Files(_folder));
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

            using var http = new HttpClient();
            var json = JsonNode.Parse(await http.GetStringAsync($"{address}{lease.BasePath}/api/workspace/mcp"))!;
            if (also != null)
                await also(address);
            return (json, address);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task The_address_of_the_daemon_is_shown_when_it_serves_the_workspace()
    {
        var (json, _) = await Ask(new WorkspaceMcp(DaemonState.Running, 5719, "http://127.0.0.1:5719/mcp", "repo", WorkspaceState.Open, null));

        Assert.Equal("running", json["daemon"]!.GetValue<string>());
        Assert.Equal(5719, json["port"]!.GetValue<int>());
        Assert.Equal("http://127.0.0.1:5719/mcp", json["url"]!.GetValue<string>());
        Assert.Equal("repo", json["workspace"]!.GetValue<string>());
        Assert.Equal("open", json["workspaceState"]!.GetValue<string>());
        Assert.Equal("X-Tasker-Agent", json["agentHeader"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_workspace_the_daemon_is_still_opening_has_no_address_yet()
    {
        var (json, _) = await Ask(new WorkspaceMcp(DaemonState.Running, 5719, null, null, WorkspaceState.Opening, null));

        Assert.Equal("running", json["daemon"]!.GetValue<string>());
        Assert.Null(json["url"]);
        Assert.Equal("opening", json["workspaceState"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(DaemonState.Starting, null, "starting")]
    [InlineData(DaemonState.Failed, "tasker-mcpd is not found", "failed")]
    [InlineData(DaemonState.Stopped, null, "stopped")]
    public async Task Without_a_running_daemon_there_is_no_address_but_its_state(DaemonState state, string? error, string expected)
    {
        var (json, _) = await Ask(new WorkspaceMcp(state, null, null, null, null, error));

        Assert.Equal(expected, json["daemon"]!.GetValue<string>());
        Assert.Null(json["url"]);
        Assert.Null(json["workspace"]);
        Assert.Equal(error, json["error"]?.GetValue<string>());
    }

    [Fact]
    public async Task Nothing_knows_about_a_daemon_means_it_is_stopped()
    {
        var (json, _) = await Ask(null);

        Assert.Equal("stopped", json["daemon"]!.GetValue<string>());
        Assert.Null(json["url"]);
    }

    [Fact]
    public async Task The_local_host_has_no_mcp_of_its_own()
    {
        await Ask(null, async address =>
        {
            foreach (var path in new[] { "/mcp", $"/w/{Path.GetFileName(_folder)}/mcp" })
            {
                using var http = new HttpClient();
                var request = new HttpRequestMessage(HttpMethod.Post, address + path)
                {
                    Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8,
                        new MediaTypeHeaderValue("application/json"))
                };
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Accept.ParseAdd("text/event-stream");
                using var response = await http.SendAsync(request);
                Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.DoesNotContain("\"tools\"", await response.Content.ReadAsStringAsync());
            }
        });
    }
}
