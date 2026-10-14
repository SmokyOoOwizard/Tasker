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

/// <summary>Десктоп и демон MCP: десктоп запускает настоящий демон tasker-mcpd, если тот не работает, и отдаёт ему открытые папки.</summary>
public class DesktopDaemonTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    /// <summary>Десктоп с программой демона из каталога тестов (её туда кладёт сборка, как в сборку десктопа).</summary>
    private DesktopDaemon Desktop() => new(() => TaskerProcess.DaemonCommand().File, _daemon.Store);

    [Fact]
    public async Task Without_a_daemon_the_desktop_starts_it_and_mcp_answers()
    {
        await _daemon.Workspace("a", "Alpha");
        Assert.Null(await DaemonClient.GetStatus());

        var result = await Desktop().Start();

        Assert.Null(result.Error);
        Assert.True(result.Started);
        var status = result.Status!;
        Assert.Equal(_daemon.Port, status.Port);
        Assert.True(status.Settled);
        Assert.Equal(WorkspaceState.Open, status.Workspaces.Single().State);
        Assert.Equal(["Alpha"], DaemonFixture.ProjectNames(await _daemon.CallTool(status.Workspaces.Single().Key!, "list_projects")));
        // Тот же демон видит и консоль.
        Assert.Equal(status.Pid, (await _daemon.Status())!.Pid);
    }

    [Fact]
    public async Task A_running_daemon_is_left_as_it_is()
    {
        await _daemon.Workspace("a", "Alpha");
        var running = await _daemon.Start();

        var result = await Desktop().Start();

        Assert.Null(result.Error);
        Assert.False(result.Started);
        Assert.Equal(running.Pid, result.Status!.Pid);
        Assert.Equal(running.Pid, (await _daemon.Status())!.Pid);
    }

    [Fact]
    public async Task The_daemon_keeps_running_after_the_desktop_is_gone()
    {
        await _daemon.Workspace("a", "Alpha");
        var pid = (await Desktop().Start()).Status!.Pid;

        // У десктопа нет ничего, что останавливало бы демон: его процесс не дочерний в смысле жизни.
        await Task.Delay(500);
        var status = await _daemon.Status();
        Assert.NotNull(status);
        Assert.Equal(pid, status.Pid);
    }

    [Fact]
    public async Task A_folder_opened_in_a_tab_becomes_a_workspace_of_the_daemon()
    {
        var folder = await _daemon.Workspace("tab", "Tab", allow: false);
        var desktop = Desktop();
        await desktop.Start();
        var locator = new DaemonMcpLocator(desktop);
        var before = await locator.Find(WorkspaceLocation.Files(folder));
        Assert.Equal(DaemonState.Running, before.Daemon);
        Assert.Null(before.Url);
        Assert.Null(before.Workspace);

        Assert.True(await desktop.Allow(WorkspaceLocation.Files(folder)));
        Assert.False(await desktop.Allow(WorkspaceLocation.Files(folder)));

        // Демон следит за настройками: область открывается без перезапуска, и агент работает в ней.
        WorkspaceMcp found = before;
        await Eventually(async () => (found = await locator.Find(WorkspaceLocation.Files(folder))).Url != null);
        Assert.Equal($"http://127.0.0.1:{_daemon.Port}/mcp", found.Url);
        Assert.Equal(WorkspaceState.Open, found.WorkspaceState);
        Assert.Equal(_daemon.Port, found.Port);
        Assert.Equal(["Tab"], DaemonFixture.ProjectNames(await _daemon.CallTool(found.Workspace!, "list_projects")));
        Assert.Contains(folder, (await _daemon.Mcp("workspace", "list")).Out);
    }

    [Fact]
    public async Task Locator_reports_a_stopped_daemon()
    {
        var folder = await _daemon.Workspace("a", "Alpha");

        var found = await new DaemonMcpLocator(Desktop()).Find(WorkspaceLocation.Files(folder));

        Assert.Equal(DaemonState.Stopped, found.Daemon);
        Assert.Null(found.Url);
        Assert.Null(found.Port);
    }

    [Fact]
    public async Task Locator_follows_the_settings_without_a_restart()
    {
        var folder = await _daemon.Workspace("a", "Alpha", allow: false);
        await _daemon.Start();
        var locator = new DaemonMcpLocator(Desktop());
        Assert.Null((await locator.Find(WorkspaceLocation.Files(folder))).Url);

        // Разрешили из командной строки — десктоп при следующем обращении видит адрес.
        await _daemon.Mcp("workspace", "add", folder);
        await Eventually(async () => (await locator.Find(WorkspaceLocation.Files(folder))).Url != null);

        await _daemon.Mcp("workspace", "remove", folder);
        await Eventually(async () => (await locator.Find(WorkspaceLocation.Files(folder))).Url == null);
    }

    [Fact]
    public async Task Without_the_program_the_desktop_says_what_is_missing()
    {
        var desktop = new DesktopDaemon(() => null, _daemon.Store);

        var result = await desktop.Start();

        Assert.Null(result.Status);
        Assert.Contains("tasker-mcpd", result.Error);
        var view = await desktop.GetView();
        Assert.Equal(DaemonState.Failed, view.State);
        Assert.Equal(result.Error, view.Error);
        Assert.False(DaemonFiles.IsRunning());
    }

    [PlatformFact(TestPlatform.Unix)]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_program_that_exits_at_once_is_a_failure_and_not_a_hang()
    {
        var fake = Path.Combine(_daemon.Root, "tasker-mcpd");
        await File.WriteAllTextAsync(fake, "#!/bin/sh\nexit 7\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var started = DateTime.UtcNow;
        var result = await new DesktopDaemon(() => fake, _daemon.Store).Start();

        Assert.Null(result.Status);
        Assert.Contains("exited right after start (code 7)", result.Error);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 150 && !await condition(); i++)
            await Task.Delay(100);

        Assert.True(await condition(), "the condition did not become true in 15 seconds");
    }
}

/// <summary>Где десктоп ищет программу демона.</summary>
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

/// <summary>Эндпоинт десктопа <c>/w/{key}/api/workspace/mcp</c>: состояние демона и MCP этой области для интерфейса; своего MCP у десктопа нет.</summary>
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
    public async Task The_desktop_host_has_no_mcp_of_its_own()
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
