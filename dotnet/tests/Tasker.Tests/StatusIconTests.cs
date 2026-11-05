using System.Net;
using System.Text.Json.Nodes;
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
}
