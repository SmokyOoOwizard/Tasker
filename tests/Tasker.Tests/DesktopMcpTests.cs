using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Daemon;
using Tasker.Daemon.Host;
using Tasker.Global;
using Tasker.Mcp;
using Tasker.Storage.Files.Workspaces;
using Tasker.Web;
using Tasker.Web.Workspaces;
using Xunit;

namespace Tasker.Tests;

/// <summary>Как десктоп уживается с демоном MCP: уступает порт и показывает адрес демона.</summary>
public class DesktopMcpPlanTests
{
    private static DaemonStatus Daemon(int port = 5719) => new(123, port, port, DateTimeOffset.UtcNow, []);

    [Fact]
    public void Without_a_daemon_the_desktop_serves_mcp_on_the_port_from_the_settings()
    {
        var plan = DesktopMcpPlan.Decide(null, new McpSettings { Port = 6100 }, null, _ => true);

        Assert.Equal(6100, plan.Port);
        Assert.False(plan.Error);
    }

    [Fact]
    public void An_explicit_port_wins_over_the_settings()
    {
        var plan = DesktopMcpPlan.Decide(6200, new McpSettings { Port = 6100 }, null, _ => true);

        Assert.Equal(6200, plan.Port);
    }

    [Fact]
    public void The_default_port_is_used_when_nothing_is_configured()
    {
        Assert.Equal(5719, DesktopMcpPlan.Decide(null, new McpSettings(), null, _ => true).Port);
    }

    [Fact]
    public void A_running_daemon_keeps_the_desktop_from_starting_its_own_mcp()
    {
        var checkedPorts = new List<int>();

        var plan = DesktopMcpPlan.Decide(null, new McpSettings(), Daemon(), port =>
        {
            checkedPorts.Add(port);
            return true;
        });

        Assert.Null(plan.Port);
        Assert.False(plan.Error);
        Assert.Contains("MCP daemon on port 5719", plan.Message);
        Assert.Empty(checkedPorts);
    }

    [Fact]
    public void A_running_daemon_wins_even_over_an_explicit_port()
    {
        Assert.Null(DesktopMcpPlan.Decide(6200, new McpSettings(), Daemon(), _ => true).Port);
    }

    [Fact]
    public void A_busy_port_means_no_mcp_and_an_error_in_the_log()
    {
        var plan = DesktopMcpPlan.Decide(null, new McpSettings { Port = 6100 }, null, _ => false);

        Assert.Null(plan.Port);
        Assert.True(plan.Error);
        Assert.Contains("6100 is busy", plan.Message);
    }
}

public class DesktopMcpDaemonTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    [Fact]
    public async Task Locator_reports_no_daemon_when_it_is_not_running()
    {
        var folder = await _daemon.Workspace("a", "Alpha");

        var found = await new DaemonMcpLocator().Find(WorkspaceLocation.Files(folder));

        Assert.False(found.DaemonRunning);
        Assert.Null(found.Url);
    }

    [Fact]
    public async Task Locator_finds_the_address_of_an_allowed_workspace()
    {
        var folder = await _daemon.Workspace("a", "Alpha");
        var status = await _daemon.Start();

        var found = await new DaemonMcpLocator().Find(WorkspaceLocation.Files(folder));

        Assert.True(found.DaemonRunning);
        Assert.Equal($"http://127.0.0.1:{_daemon.Port}/mcp", found.Url);
        Assert.Equal(status.Workspaces.Single().Key, found.Workspace);
        // По этому адресу агент и правда получает проекты этой папки.
        Assert.Equal(["Alpha"], DaemonFixture.ProjectNames(await _daemon.CallTool(status.Workspaces.Single().Key!, "list_projects")));
    }

    [Fact]
    public async Task Locator_says_when_the_daemon_does_not_serve_this_workspace()
    {
        await _daemon.Workspace("allowed", "Allowed");
        var notAllowed = await _daemon.Workspace("other", "Other", allow: false);
        await _daemon.Start();

        var found = await new DaemonMcpLocator().Find(WorkspaceLocation.Files(notAllowed));

        Assert.True(found.DaemonRunning);
        Assert.Null(found.Url);
    }

    [Fact]
    public async Task Locator_follows_the_settings_without_a_restart()
    {
        var folder = await _daemon.Workspace("a", "Alpha", allow: false);
        await _daemon.Start();
        var locator = new DaemonMcpLocator();
        Assert.Null((await locator.Find(WorkspaceLocation.Files(folder))).Url);

        // Разрешили из командной строки — десктоп при следующем обращении видит адрес.
        await _daemon.Mcp("workspace", "add", folder);
        await Eventually(async () => (await locator.Find(WorkspaceLocation.Files(folder))).Url != null);

        await _daemon.Mcp("workspace", "remove", folder);
        await Eventually(async () => (await locator.Find(WorkspaceLocation.Files(folder))).Url == null);
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 150 && !await condition(); i++)
            await Task.Delay(100);

        Assert.True(await condition(), "the condition did not become true in 15 seconds");
    }
}

/// <summary>Эндпоинт десктопа <c>/w/{key}/api/workspace/mcp</c>: чей адрес MCP он показывает интерфейсу.</summary>
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

    private sealed class FakeExternal(ExternalMcp result) : IExternalMcp
    {
        public Task<ExternalMcp> Find(WorkspaceLocation location, CancellationToken ct = default) => Task.FromResult(result);
    }

    private async Task<JsonNode> Ask(int? ownPort, ExternalMcp? external)
    {
        var builder = TaskerWebApp.CreateBuilder<DaemonModule>([], TaskerMode.Local);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new LocalMcpAddress(ownPort));
        if (external != null)
            builder.Services.AddSingleton<IExternalMcp>(new FakeExternal(external));

        await using var app = builder.Build();
        app.MapTasker();
        await app.StartAsync();
        try
        {
            await using var lease = await app.Services.GetRequiredService<WorkspaceRegistry>().Open(WorkspaceLocation.Files(_folder));
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

            using var http = new HttpClient();
            var json = JsonNode.Parse(await http.GetStringAsync($"{address}{lease.BasePath}/api/workspace/mcp"))!;
            json["key"] = lease.Key;
            return json;
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Own_mcp_of_the_desktop_is_shown_when_there_is_no_daemon()
    {
        var json = await Ask(5719, new ExternalMcp(false, null));

        // Адрес общий для всех областей, а имя области для агента — отдельно.
        Assert.Equal("http://127.0.0.1:5719/mcp", json["url"]!.GetValue<string>());
        Assert.Equal(json["key"]!.GetValue<string>(), json["workspace"]!.GetValue<string>());
        Assert.Equal("desktop", json["source"]!.GetValue<string>());
        Assert.True(json["allowed"]!.GetValue<bool>());
        Assert.Equal("X-Tasker-Agent", json["agentHeader"]!.GetValue<string>());
    }

    [Fact]
    public async Task Own_mcp_is_shown_when_nothing_knows_about_a_daemon()
    {
        var json = await Ask(5719, null);

        Assert.Equal("desktop", json["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_address_of_the_daemon_is_shown_when_it_serves_the_workspace()
    {
        var json = await Ask(null, new ExternalMcp(true, "http://127.0.0.1:5719/mcp", "repo"));

        Assert.Equal("http://127.0.0.1:5719/mcp", json["url"]!.GetValue<string>());
        Assert.Equal("repo", json["workspace"]!.GetValue<string>());
        Assert.Equal("daemon", json["source"]!.GetValue<string>());
        Assert.True(json["allowed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task No_address_when_the_daemon_runs_but_does_not_serve_the_workspace()
    {
        var json = await Ask(null, new ExternalMcp(true, null));

        Assert.Null(json["url"]);
        Assert.Null(json["workspace"]);
        Assert.Equal("daemon", json["source"]!.GetValue<string>());
        Assert.False(json["allowed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task No_address_when_there_is_neither_a_daemon_nor_an_own_port()
    {
        var json = await Ask(null, new ExternalMcp(false, null));

        Assert.Null(json["url"]);
        Assert.Null(json["source"]);
        Assert.False(json["allowed"]!.GetValue<bool>());
    }
}

/// <summary>Десктоп отпускает порт MCP на лету, когда стартует демон, не останавливая интерфейс.</summary>
public class DesktopYieldsPortTests : IDisposable
{
    private readonly IsolatedHome _home = new();

    public void Dispose() => _home.Dispose();

    private static bool Listening(int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            client.Connect(System.Net.IPAddress.Loopback, port);
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    private static async Task<WebApplication> Start(int mcpPort, LocalMcpAddress address, Action<LocalEndpoints> attach)
    {
        var builder = TaskerWebApp.CreateBuilder<DaemonModule>([], TaskerMode.Local);
        attach(LocalEndpoints.Add(builder, mcpPort, address));
        var app = builder.Build();
        app.MapTasker();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task The_port_is_released_when_the_daemon_appears_and_the_interface_keeps_working()
    {
        var port = DaemonFixture.FreePort();
        var address = new LocalMcpAddress(port);
        LocalEndpoints endpoints = null!;
        await using var app = await Start(port, address, x => endpoints = x);

        var ui = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .Select(x => new Uri(x)).Single(x => x.Port != port);
        Assert.True(Listening(port));

        var daemonRunning = false;
        var yielded = endpoints.YieldToDaemon(() => daemonRunning, TimeSpan.FromMilliseconds(20));
        await Task.Delay(100);
        Assert.True(Listening(port));
        Assert.Equal(port, address.Port);

        daemonRunning = true;
        await yielded.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(Listening(port));
        Assert.Null(address.Port);
        Assert.Null(endpoints.McpPort);
        // Порт теперь можно занять (как делает демон), а интерфейс десктопа работает.
        using (var taken = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port))
            taken.Start();
        using var http = new HttpClient();
        // Хост отвечает (фронтенд в тестовой сборке не встроен — 404, но не отказ в соединении).
        using var response = await http.GetAsync(ui);
        Assert.True(Listening(ui.Port));
        await app.StopAsync();
    }

    [Fact]
    public async Task Waiting_for_the_daemon_can_be_cancelled_and_keeps_the_port()
    {
        var port = DaemonFixture.FreePort();
        var address = new LocalMcpAddress(port);
        LocalEndpoints endpoints = null!;
        await using var app = await Start(port, address, x => endpoints = x);

        using var cancel = new CancellationTokenSource(150);
        await endpoints.YieldToDaemon(() => false, TimeSpan.FromMilliseconds(20), cancel.Token);

        Assert.True(Listening(port));
        Assert.Equal(port, address.Port);
        await app.StopAsync();
    }
}
