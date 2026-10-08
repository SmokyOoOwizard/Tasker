using System.Text.Json.Nodes;
using Tasker.Global;
using Tasker.Storage.Files.Workspaces;
using Xunit;

namespace Tasker.Tests;

/// <summary>Глобальные настройки: общий файл десктопа, командной строки и демона.</summary>
public class SettingsTests : IDisposable
{
    private readonly IsolatedHome _home = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _home.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_folder, name)).FullName;

    [Fact]
    public void Missing_file_gives_defaults_and_is_not_created_by_reading()
    {
        var settings = _home.Store.Load();

        Assert.Equal(5719, settings.Mcp.Port);
        Assert.Empty(settings.Mcp.Workspaces);
        Assert.False(File.Exists(_home.SettingsFile));
    }

    [Fact]
    public async Task Settings_are_stored_as_readable_json()
    {
        var store = _home.Store;
        await store.AddWorkspace(WorkspaceLocation.Files(Folder("a")));
        await store.SetPort(6000);

        var json = JsonNode.Parse(await File.ReadAllTextAsync(_home.SettingsFile))!;
        Assert.Equal(6000, json["mcp"]!["port"]!.GetValue<int>());
        var workspace = json["mcp"]!["workspaces"]![0]!;
        Assert.Equal("files", workspace["kind"]!.GetValue<string>());
        Assert.Equal(WorkspaceLocation.Files(Folder("a")).Path, workspace["path"]!.GetValue<string>());

        var loaded = store.Load();
        Assert.Equal(6000, loaded.Mcp.Port);
        Assert.Equal(WorkspaceKind.Files, Assert.Single(loaded.Mcp.Workspaces).Kind);
    }

    [Fact]
    public async Task Same_folder_reached_another_way_is_one_workspace()
    {
        var store = _home.Store;
        var real = Folder("real");
        var link = Path.Combine(_folder, "link");
        Directory.CreateSymbolicLink(link, real);
        Directory.CreateDirectory(Path.Combine(real, ".tasker"));

        Assert.True((await store.AddWorkspace(WorkspaceLocation.Files(real))).Added);
        Assert.False((await store.AddWorkspace(WorkspaceLocation.Files(link))).Added);
        Assert.False((await store.AddWorkspace(WorkspaceLocation.Files(Path.Combine(real, ".tasker")))).Added);

        Assert.Single(store.Load().Mcp.Workspaces);
        Assert.True(await store.RemoveWorkspace(WorkspaceLocation.Files(link)));
        Assert.False(await store.RemoveWorkspace(WorkspaceLocation.Files(link)));
        Assert.Empty(store.Load().Mcp.Workspaces);
    }

    [Fact]
    public async Task Update_that_changes_nothing_does_not_rewrite_the_file()
    {
        var store = _home.Store;
        await store.SetPort(6000);
        var before = File.GetLastWriteTimeUtc(_home.SettingsFile);
        await Task.Delay(50);

        await store.SetPort(6000);

        Assert.Equal(before, File.GetLastWriteTimeUtc(_home.SettingsFile));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public async Task Port_must_be_a_valid_port(int port)
    {
        var error = await Assert.ThrowsAsync<SettingsException>(() => _home.Store.SetPort(port));
        Assert.Contains("from 1 to 65535", error.Message);
    }

    [Fact]
    public async Task Broken_file_is_reported_and_never_overwritten()
    {
        await File.WriteAllTextAsync(_home.SettingsFile, "{ not json");

        Assert.Throws<SettingsException>(() => _home.Store.Load());
        await Assert.ThrowsAsync<SettingsException>(() => _home.Store.SetPort(6000));

        Assert.Equal("{ not json", await File.ReadAllTextAsync(_home.SettingsFile));
    }

    [Fact]
    public async Task Unknown_fields_from_a_newer_version_are_ignored()
    {
        await File.WriteAllTextAsync(_home.SettingsFile, """{"mcp":{"port":6001,"future":true},"other":1}""");

        Assert.Equal(6001, _home.Store.Load().Mcp.Port);
    }

    [Fact]
    public async Task Parallel_updates_are_not_lost()
    {
        var store = _home.Store;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => store.AddWorkspace(WorkspaceLocation.Files(Folder($"w{i}"))))));

        Assert.Equal(20, store.Load().Mcp.Workspaces.Count);
    }

    [Fact]
    public async Task Parallel_processes_do_not_lose_each_others_workspaces()
    {
        var folders = Enumerable.Range(0, 6).Select(i => Folder($"p{i}")).ToArray();

        var results = await Task.WhenAll(folders.Select(x => TaskerProcess.Run("mcp", "workspace", "add", x)));

        Assert.All(results, r => Assert.True(r.Code == 0, r.Err));
        Assert.Equal(6, _home.Store.Load().Mcp.Workspaces.Count);
    }

    [Fact]
    public async Task Watch_reports_changes_made_by_another_process()
    {
        var store = _home.Store;
        await store.SetPort(6000);
        var seen = new List<GlobalSettings>();
        using var watch = store.Watch(x =>
        {
            lock (seen)
                seen.Add(x);
        });

        // Другой процесс — например, командная строка при запущенном десктопе.
        var changed = await TaskerProcess.Run("mcp", "port", "6100");
        Assert.Equal(0, changed.Code);

        await Eventually(() =>
        {
            lock (seen)
                return seen.Any(x => x.Mcp.Port == 6100);
        });
    }

    [Fact]
    public async Task Watch_ignores_rewrites_with_the_same_content_and_broken_files()
    {
        var store = _home.Store;
        await store.SetPort(6000);
        var calls = 0;
        using var watch = store.Watch(_ => Interlocked.Increment(ref calls));

        await File.WriteAllTextAsync(_home.SettingsFile, await File.ReadAllTextAsync(_home.SettingsFile));
        await Task.Delay(800);
        Assert.Equal(0, calls);

        await File.WriteAllTextAsync(_home.SettingsFile, "{ broken");
        await Task.Delay(800);
        Assert.Equal(0, calls);

        await File.WriteAllTextAsync(_home.SettingsFile, """{"mcp":{"port":6200}}""");
        await Eventually(() => Volatile.Read(ref calls) == 1);
    }

    [Fact]
    public async Task Watch_stops_after_dispose()
    {
        var store = _home.Store;
        var calls = 0;
        var watch = store.Watch(_ => Interlocked.Increment(ref calls));
        watch.Dispose();

        await store.SetPort(6300);
        await Task.Delay(800);

        Assert.Equal(0, calls);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(100);

        Assert.True(condition(), "the change did not arrive in 10 seconds");
    }
}

/// <summary>Команды <c>tasker mcp workspace|config|port</c>.</summary>
public class McpSettingsCommandTests : IDisposable
{
    private readonly IsolatedHome _home = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));

    public McpSettingsCommandTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _home.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static Task<CliResult> Cli(params string[] args) => TestWorkspace.Invoke(args);

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_folder, name)).FullName;

    [Fact]
    public async Task Workspaces_are_added_listed_and_removed()
    {
        var a = Folder("a");
        var b = Folder("b");

        Assert.Equal("Found 0", (await Cli("mcp", "workspace", "list")).Out.Trim());

        var added = await Cli("mcp", "workspace", "add", a);
        Assert.Equal(0, added.Code);
        Assert.StartsWith("Added files workspace", added.Out);
        await Cli("mcp", "workspace", "add", b);

        var listed = await Cli("mcp", "workspace", "list");
        Assert.Equal("Found 2", listed.Found);
        Assert.Null((await Cli("mcp", "workspace", "list", "-q")).Found);
        var list = listed.Data;
        Assert.Contains(WorkspaceLocation.Files(a).Path, list);
        Assert.Contains(WorkspaceLocation.Files(b).Path, list);

        Assert.StartsWith("Removed", (await Cli("mcp", "workspace", "remove", a)).Out);
        var left = (await Cli("mcp", "workspace", "list", "--json")).Json.AsArray();
        Assert.Equal(WorkspaceLocation.Files(b).Path, Assert.Single(left)!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adding_twice_is_reported_and_harmless()
    {
        var a = Folder("a");
        await Cli("mcp", "workspace", "add", a);

        var again = await Cli("mcp", "workspace", "add", a);

        Assert.Equal(0, again.Code);
        Assert.StartsWith("Already in the list", again.Out);
        Assert.Single(_home.Store.Load().Mcp.Workspaces);
    }

    [Fact]
    public async Task Sqlite_file_is_recognised_by_being_a_file()
    {
        var db = Path.Combine(_folder, "tasker.db");
        Assert.Equal(0, (await TestWorkspace.Invoke(["project", "create", "P", "--sqlite", db])).Code);

        var added = await Cli("mcp", "workspace", "add", db);

        Assert.StartsWith("Added sqlite workspace", added.Out);
        Assert.Equal(WorkspaceKind.Sqlite, Assert.Single(_home.Store.Load().Mcp.Workspaces).Kind);
    }

    [Fact]
    public async Task Without_a_path_the_current_folder_is_used()
    {
        var folder = Folder("here");

        // Текущая папка — свойство процесса, поэтому команды идут отдельными процессами tasker.
        Assert.Equal(0, (await TaskerProcess.RunIn(folder, "mcp", "workspace", "add")).Code);
        Assert.Equal(WorkspaceLocation.Files(folder).Path, Assert.Single(_home.Store.Load().Mcp.Workspaces).Path);
        Assert.Equal(0, (await TaskerProcess.RunIn(folder, "mcp", "workspace", "remove")).Code);
    }

    [Fact]
    public async Task Missing_path_and_unlisted_workspace_are_errors()
    {
        var missing = Path.Combine(_folder, "nope");

        var add = await Cli("mcp", "workspace", "add", missing);
        Assert.Equal(1, add.Code);
        Assert.Contains("Not found", add.Err);

        var remove = await Cli("mcp", "workspace", "remove", Folder("known"));
        Assert.Equal(1, remove.Code);
        Assert.Contains("Not in the list", remove.Err);
    }

    [Fact]
    public async Task A_deleted_folder_can_still_be_removed_from_the_list()
    {
        var gone = Folder("gone");
        await Cli("mcp", "workspace", "add", gone);
        Directory.Delete(gone);

        Assert.Equal(0, (await Cli("mcp", "workspace", "remove", gone)).Code);
        Assert.Empty(_home.Store.Load().Mcp.Workspaces);
    }

    [Fact]
    public async Task Port_is_shown_and_changed()
    {
        Assert.Equal("5719", (await Cli("mcp", "port")).Out.Trim());

        var set = await Cli("mcp", "port", "6100");
        Assert.Equal(0, set.Code);
        Assert.Contains("restart", set.Out);
        Assert.Equal("6100", (await Cli("mcp", "port")).Out.Trim());

        var bad = await Cli("mcp", "port", "70000");
        Assert.Equal(1, bad.Code);
        Assert.Contains("from 1 to 65535", bad.Err);
    }

    [Fact]
    public async Task Config_shows_the_file_and_the_values()
    {
        await Cli("mcp", "workspace", "add", Folder("a"));
        await Cli("mcp", "port", "6100");

        var text = (await Cli("mcp", "config")).Out;
        Assert.Contains(_home.SettingsFile, text);
        Assert.Contains("6100", text);

        var json = (await Cli("mcp", "config", "--json")).Json;
        Assert.Equal(6100, json["mcp"]!["port"]!.GetValue<int>());
        Assert.Single(json["mcp"]!["workspaces"]!.AsArray());
    }

    [Fact]
    public async Task Broken_settings_file_is_an_error_not_a_crash()
    {
        await File.WriteAllTextAsync(_home.SettingsFile, "{ nope");

        var result = await Cli("mcp", "config");

        Assert.Equal(1, result.Code);
        Assert.Contains("Cannot read", result.Err);
    }
}
