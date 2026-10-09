using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Tasker.Cli;
using Tasker.Daemon;
using Xunit;

namespace Tasker.Tests;

/// <summary>Команда <c>tasker sync</c> и <c>/daemon/sync</c>. Консоль запускается отдельным процессом (годится для <c>TASKER_BIN</c>).</summary>
public class SyncTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    private static Task<CliResult> Cli(params string[] args) => TaskerProcess.Run(args);

    private static async Task<string> BrokenFile(string workspace)
    {
        var project = Directory.GetDirectories(Path.Combine(workspace, ".tasker", "projects")).First();
        var file = Path.Combine(project, "tasks", Guid.NewGuid() + ".yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "<<<<<<< HEAD\ntitle: a\n=======\ntitle: b\n>>>>>>> branch\n");
        return file;
    }

    [Fact]
    public async Task Sync_without_a_daemon_syncs_the_folder_itself()
    {
        var folder = await _daemon.Workspace("a", "Alpha", allow: false);

        var result = await Cli("sync", "-w", folder);

        Assert.Equal(0, result.Code);
        Assert.Contains($"Synced {Tasker.Storage.Files.Workspaces.WorkspaceLocation.Files(folder).Path} (via direct)", result.Out);
        Assert.Equal("direct", (await Cli("sync", "-w", folder, "--json")).Json["via"]!.GetValue<string>());
    }

    [Fact]
    public async Task Quiet_sync_prints_nothing_when_all_is_well()
    {
        var folder = await _daemon.Workspace("a", "Alpha", allow: false);

        var result = await Cli("sync", "--quiet", "-w", folder);

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Out);
        Assert.Empty(result.Err);
    }

    [Fact]
    public async Task Sync_lists_files_that_cannot_be_read_even_when_quiet()
    {
        var folder = await _daemon.Workspace("a", "Alpha", allow: false);
        await BrokenFile(folder);

        var loud = await Cli("sync", "-w", folder);
        Assert.Equal(0, loud.Code);
        Assert.Contains("1 file(s) cannot be read", loud.Out);
        Assert.Contains("Unresolved git merge conflict (line 1)", loud.Out);

        var quiet = await Cli("sync", "--quiet", "-w", folder);
        Assert.Equal(0, quiet.Code);
        Assert.DoesNotContain("Synced", quiet.Out);
        Assert.Contains("1 file(s) cannot be read", quiet.Out);

        var json = (await Cli("sync", "-w", folder, "--json")).Json;
        Assert.Equal(1, json["problemCount"]!.GetValue<int>());
        Assert.EndsWith(".yaml", json["problems"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Many_problems_are_cut_with_a_count()
    {
        var folder = await _daemon.Workspace("a", "Alpha", allow: false);
        for (var i = 0; i < 12; i++)
            await BrokenFile(folder);

        var result = await Cli("sync", "-w", folder);

        Assert.Contains("12 file(s) cannot be read", result.Out);
        Assert.Contains("… and 2 more", result.Out);
        Assert.Equal(10, result.Out.Split('\n').Count(x => x.StartsWith("  projects/")));
    }

    [Fact]
    public async Task Sqlite_workspace_has_nothing_to_sync()
    {
        using var ws = TestWorkspace.Create("sqlite", asProcess: true);
        await ws.Run("project", "create", "P");

        var loud = await ws.Run("sync");
        Assert.Equal(0, loud.Code);
        Assert.Contains("nothing to sync", loud.Out);

        Assert.Empty((await ws.Run("sync", "--quiet")).Out);
    }

    [Fact]
    public async Task A_folder_without_a_workspace_is_an_error_but_silent_when_quiet()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_daemon.Root, "empty")).FullName;

        var loud = await Cli("sync", "-w", empty);
        Assert.Equal(1, loud.Code);
        Assert.Contains("is not a Tasker workspace", loud.Err);
        Assert.False(Directory.Exists(Path.Combine(empty, ".tasker")), "sync must not create a workspace");

        var quiet = await Cli("sync", "--quiet", "-w", empty);
        Assert.Equal(0, quiet.Code);
        Assert.Empty(quiet.Out + quiet.Err);
        Assert.False(Directory.Exists(Path.Combine(empty, ".tasker")));
    }

    [Fact]
    public async Task A_missing_folder_is_an_error_but_silent_when_quiet()
    {
        var missing = Path.Combine(_daemon.Root, "nope");

        var loud = await Cli("sync", "-w", missing);
        Assert.Equal(1, loud.Code);
        Assert.Contains("Folder not found", loud.Err);

        var quiet = await Cli("sync", "--quiet", "-w", missing);
        Assert.Equal(0, quiet.Code);
        Assert.Empty(quiet.Out + quiet.Err);
    }

    [Fact]
    public async Task Workspace_and_sqlite_together_are_rejected()
    {
        var result = await Cli("sync", "-w", _daemon.Root, "--sqlite", Path.Combine(_daemon.Root, "x.db"));

        Assert.Equal(1, result.Code);
        Assert.Contains("not both", result.Err);
    }

    // ---- через демон ----

    /// <summary>Копирует проект из другой папки в эту — как если бы <c>git pull</c> принёс новый проект.</summary>
    private static async Task PullProjectFrom(string source, string target)
    {
        var project = Directory.GetDirectories(Path.Combine(source, ".tasker", "projects")).Single();
        var destination = Path.Combine(target, ".tasker", "projects", Path.GetFileName(project));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(project))
            await File.WriteAllBytesAsync(Path.Combine(destination, Path.GetFileName(file)), await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task Sync_asks_the_daemon_and_returns_when_the_cache_is_up_to_date()
    {
        var folder = await _daemon.Workspace("a", "Alpha");
        var other = await _daemon.Workspace("other", "Pulled", allow: false);
        var status = await _daemon.Start();
        var key = status.Workspaces.Single().Key!;

        await PullProjectFrom(other, folder);
        // Слежение за файлами ждёт около 300 мс тишины; sync — не ждёт: возвращается, когда кэш уже готов.
        var result = await Cli("sync", "-w", folder, "--json");

        Assert.Equal(0, result.Code);
        Assert.Equal("daemon", result.Json["via"]!.GetValue<string>());
        Assert.Equal(["Alpha", "Pulled"], DaemonFixture.ProjectNames(await _daemon.CallTool(key, "list_projects")));
    }

    [Fact]
    public async Task Daemon_reports_the_problems_it_found()
    {
        var folder = await _daemon.Workspace("a", "Alpha");
        await _daemon.Start();
        await BrokenFile(folder);

        var result = await Cli("sync", "-w", folder, "--json");

        Assert.Equal("daemon", result.Json["via"]!.GetValue<string>());
        Assert.Equal(1, result.Json["problemCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_folder_the_daemon_does_not_serve_is_synced_directly()
    {
        await _daemon.Workspace("served", "Served");
        var notServed = await _daemon.Workspace("other", "Other", allow: false);
        await _daemon.Start();

        var result = await Cli("sync", "-w", notServed, "--json");

        Assert.Equal(0, result.Code);
        Assert.Equal("direct", result.Json["via"]!.GetValue<string>());
    }

    [Fact]
    public async Task If_the_daemon_stops_sync_falls_back_to_the_folder()
    {
        var folder = await _daemon.Workspace("a", "Alpha");
        await _daemon.Start();
        Assert.Equal("daemon", (await Cli("sync", "-w", folder, "--json")).Json["via"]!.GetValue<string>());

        await _daemon.Mcp("stop");

        Assert.Equal("direct", (await Cli("sync", "-w", folder, "--json")).Json["via"]!.GetValue<string>());
    }

    [Fact]
    public async Task Sync_endpoint_needs_the_secret_and_a_known_workspace()
    {
        var folder = await _daemon.Workspace("a", "Alpha");
        var status = await _daemon.Start();
        var token = DaemonFiles.ReadInfo()!.Token;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{status.Port}") };
        var body = new { kind = "files", path = folder };

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/daemon/sync", body)).StatusCode);

        HttpRequestMessage Request(object? content)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/daemon/sync") { Content = JsonContent.Create(content) };
            request.Headers.Add(DaemonClient.ControlHeader, token);
            return request;
        }

        var ok = await http.SendAsync(Request(body));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(0, JsonNode.Parse(await ok.Content.ReadAsStringAsync())!["problemCount"]!.GetValue<int>());

        var unknown = await http.SendAsync(Request(new { kind = "files", path = Path.Combine(_daemon.Root, "unknown") }));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Request(new { kind = "files", path = "" }))).StatusCode);
    }
}
