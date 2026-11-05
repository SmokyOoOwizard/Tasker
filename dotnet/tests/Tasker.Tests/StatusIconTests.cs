using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Tasker.Core.Workspace;
using Tasker.Storage.Files.Storages;
using Xunit;

namespace Tasker.Tests;

public class StatusIconTests
{
    public static IEnumerable<object[]> Storages() => TestWorkspace.Storages();

    private static string Str(JsonNode node, string name) => node[name]!.GetValue<string>();

    [Theory, MemberData(nameof(Storages))]
    public async Task Rest_status_icon_is_optional_and_can_be_set_changed_kept_and_reset(string storage)
    {
        await using var api = await ApiHost.Start(storage);

        var plain = (await api.Post(api.P("/statuses"), new { name = "Plain", color = "#112233" })).Body!;
        Assert.Equal("", Str(plain, "icon"));

        var created = (await api.Post(api.P("/statuses"), new { name = "Doing", color = "#445566", icon = "half" })).Body!;
        var id = Str(created, "id");
        Assert.Equal("half", Str(created, "icon"));
        Assert.Equal("half", Str((await api.Get(api.P($"/statuses/{id}"))).Body!, "icon"));

        var renamed = (await api.Patch(api.P($"/statuses/{id}"), new { name = "Doing 2", version = Str(created, "version") })).Body!;
        Assert.Equal("half", Str(renamed, "icon"));

        var changed = (await api.Patch(api.P($"/statuses/{id}"), new { icon = "star", version = Str(renamed, "version") })).Body!;
        Assert.Equal("star", Str(changed, "icon"));
        Assert.NotEqual(Str(renamed, "version"), Str(changed, "version"));

        var reset = (await api.Patch(api.P($"/statuses/{id}"), new { icon = "", version = Str(changed, "version") })).Body!;
        Assert.Equal("", Str(reset, "icon"));
        Assert.Equal("", Str((await api.Get(api.P($"/statuses/{id}"))).Body!, "icon"));

        var listed = (await api.Get(api.P("/statuses"))).Body!["data"]!.AsArray().Single(x => Str(x!, "name") == "Doing 2")!;
        Assert.Equal("", Str(listed, "icon"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Rest_unknown_status_icon_is_rejected(string storage)
    {
        await using var api = await ApiHost.Start(storage);

        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post(api.P("/statuses"), new { name = "Bad", color = "#112233", icon = "rocket" })).Status);

        var created = (await api.Post(api.P("/statuses"), new { name = "Good", color = "#112233", icon = "done" })).Body!;
        var rejected = await api.Patch(api.P($"/statuses/{Str(created, "id")}"), new { icon = "Done", version = Str(created, "version") });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.Status);
    }

    private static string[] StatusFiles(TestWorkspace ws) =>
        Directory.GetFiles(Path.Combine(ws.Root, ".tasker"), "*.yaml", SearchOption.AllDirectories)
            .Where(x => Path.GetFileName(Path.GetDirectoryName(x)) == "statuses")
            .ToArray();

    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    [Fact]
    public async Task The_file_has_the_icon_key_only_while_the_icon_is_set_and_an_old_file_without_it_is_read()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("status", "create", "Todo"));

        Assert.DoesNotContain("icon", File.ReadAllText(StatusFiles(ws).Single()));
        Assert.Equal("", Str((await Ok(ws.Run("status", "get", "Todo", "--json"))).Json, "icon"));

        await Ok(ws.Run("status", "update", "Todo", "--icon", "review"));
        var text = File.ReadAllText(StatusFiles(ws).Single());
        Assert.Contains("icon: review", text);
        Assert.StartsWith($"formatVersion: {FormatVersions.Current}\n", text);
        Assert.Equal("review", Str((await Ok(ws.Run("status", "get", "Todo", "--json"))).Json, "icon"));

        var bad = await ws.Run("status", "update", "Todo", "--icon", "rocket");
        Assert.NotEqual(0, bad.Code);

        await Ok(ws.Run("status", "update", "Todo", "--icon", ""));
        Assert.DoesNotContain("icon", File.ReadAllText(StatusFiles(ws).Single()));

        var path = StatusFiles(ws).Single();
        var old = File.ReadAllText(path).Replace($"formatVersion: {FormatVersions.Current}", "formatVersion: 9");
        File.WriteAllText(path, old);
        Assert.Equal("", Str((await Ok(ws.Run("status", "get", "Todo", "--json"))).Json, "icon"));
    }

    [Fact]
    public void The_step_to_format_11_changes_only_the_version_and_repeating_it_changes_nothing()
    {
        Assert.Equal(11, FormatVersions.Current);

        const string body = "id: 1\nname: Doing\ncolor: '#112233'\nicon: half\n";
        var upgraded = FormatVersions.Upgrade("formatVersion: 10\n" + body, "x.yaml");

        Assert.Equal("formatVersion: 11\n" + body, upgraded);
        Assert.Equal(upgraded, FormatVersions.Upgrade(upgraded, "x.yaml"));
        Assert.Throws<UnsupportedFormatException>(() => FormatVersions.Upgrade("formatVersion: 12\n" + body, "x.yaml"));
    }

    private static async Task<(string WithIcon, string WithoutIcon)> TwoStatuses(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("status", "create", "Plain"));
        await Ok(ws.Run("status", "create", "Doing"));
        await Ok(ws.Run("status", "update", "Doing", "--icon", "half"));
        var files = StatusFiles(ws);
        return (files.Single(x => Path.GetFileName(x).StartsWith("doing")), files.Single(x => Path.GetFileName(x).StartsWith("plain")));
    }

    private static void SetFormat(string file, int version) =>
        File.WriteAllText(file, FormatVersions.SetVersion(File.ReadAllText(file), version));

    [Fact]
    public async Task Migrate_keeps_the_icon_finishes_an_interrupted_run_and_a_second_run_changes_nothing()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        var (withIcon, withoutIcon) = await TwoStatuses(ws);
        var project = Directory.GetFiles(Path.Combine(ws.Root, ".tasker"), "project.yaml", SearchOption.AllDirectories).Single();

        SetFormat(withIcon, 10);
        SetFormat(withoutIcon, 10);
        SetFormat(project, 10);
        var iconBody = File.ReadAllText(withIcon).Split('\n', 2)[1];
        var plainBody = File.ReadAllText(withoutIcon).Split('\n', 2)[1];
        Assert.Contains("icon: half", iconBody);
        Assert.DoesNotContain("icon", plainBody);

        SetFormat(withoutIcon, 11);
        Assert.Equal(2, (await ws.Run("migrate", "--check")).Code);

        await Ok(ws.Run("migrate"));
        Assert.Equal("formatVersion: 11\n" + iconBody, File.ReadAllText(withIcon));
        Assert.Equal("formatVersion: 11\n" + plainBody, File.ReadAllText(withoutIcon));
        Assert.StartsWith("formatVersion: 11\n", File.ReadAllText(project));
        Assert.Equal(0, (await ws.Run("migrate", "--check")).Code);

        var written = File.GetLastWriteTimeUtc(withIcon);
        await Ok(ws.Run("migrate"));
        Assert.Equal(written, File.GetLastWriteTimeUtc(withIcon));
        Assert.Equal("half", Str((await Ok(ws.Run("status", "get", "Doing", "--json"))).Json, "icon"));
        Assert.Equal("", Str((await Ok(ws.Run("status", "get", "Plain", "--json"))).Json, "icon"));
    }

    [Fact]
    public async Task The_icon_and_its_absence_survive_a_write_of_another_field_and_a_write_stamps_only_that_file()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        var (withIcon, withoutIcon) = await TwoStatuses(ws);
        SetFormat(withIcon, 10);
        SetFormat(withoutIcon, 10);

        await Ok(ws.Run("status", "update", "Doing", "--description", "text"));
        Assert.StartsWith("formatVersion: 11\n", File.ReadAllText(withIcon));
        Assert.Contains("icon: half", File.ReadAllText(withIcon));
        Assert.StartsWith("formatVersion: 10\n", File.ReadAllText(withoutIcon));

        await Ok(ws.Run("status", "update", "Plain", "--description", "text"));
        Assert.StartsWith("formatVersion: 11\n", File.ReadAllText(withoutIcon));
        Assert.DoesNotContain("icon", File.ReadAllText(withoutIcon));
    }

    [Fact]
    public async Task A_file_of_a_newer_format_is_refused_and_keeps_its_icon()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        var (withIcon, _) = await TwoStatuses(ws);
        var id = Str((await Ok(ws.Run("status", "get", "Doing", "--json"))).Json, "id");
        SetFormat(withIcon, 12);
        var before = File.ReadAllText(withIcon);

        var refused = await ws.Run("status", "update", id, "--description", "text");
        var sync = await ws.Run("sync");
        var migrate = await ws.Run("migrate");

        Assert.NotEqual(0, refused.Code);
        Assert.Contains("format version 12 is newer", sync.Out);
        Assert.NotEqual(0, migrate.Code);
        Assert.Equal(before, File.ReadAllText(withIcon));
    }

    [Fact]
    public async Task A_cached_problem_left_by_an_older_program_does_not_hide_files_after_the_update()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await TwoStatuses(ws);

        var cache = Path.Combine(ws.Root, ".tasker", ".cache", "index.db");
        await using (var connection = new SqliteConnection($"Data Source={cache};Pooling=false"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE files SET error = 'format version 11 is newer than this Tasker supports (10): update Tasker'; PRAGMA user_version = 10;";
            await command.ExecuteNonQueryAsync();
        }

        var statuses = (await Ok(ws.Run("status", "list", "--json"))).Json["data"]!.AsArray();

        Assert.Equal(2, statuses.Count);
        Assert.DoesNotContain("newer than this Tasker", (await Ok(ws.Run("sync"))).Out);
    }
}
