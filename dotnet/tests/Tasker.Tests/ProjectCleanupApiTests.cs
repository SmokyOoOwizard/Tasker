using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Tasker.Tests;

public class ProjectCleanupApiTests
{
    private static async Task<T> Eventually<T>(Func<Task<T?>> attempt) where T : class
    {
        for (var i = 0; i < 100; i++)
        {
            if (await attempt() is { } value)
                return value;
            await Task.Delay(150);
        }
        throw new Xunit.Sdk.XunitException("the condition did not become true in 15 seconds");
    }

    private static async Task<JsonNode> LinkHealth(ApiHost api, HttpClient http)
    {
        var query = $"{{ project(id: \"{api.ProjectId}\") {{ linkHealth {{ tasksWithInvalidLinks unreadableFiles needsAttention cycles {{ typeName path {{ id title }} }} }} }} }}";
        var (status, body) = await GraphReads.Query(http, api.BaseUrl, query);
        Assert.Equal(HttpStatusCode.OK, status);
        return body!["data"]!["project"]!["linkHealth"]!;
    }

    [Fact]
    public async Task Cleanup_removes_links_to_missing_tasks_and_link_health_reports_them()
    {
        await using var api = await ApiHost.Start("files");
        using var http = new HttpClient();

        var source = await api.NewTask("source");
        var target = await api.NewTask("target");
        var types = (await api.Get(api.P("/link-types"))).Body!["data"]!.AsArray();
        var typeId = types[0]!["id"]!.GetValue<string>();
        var (linked, _) = await api.Post(api.P($"/tasks/{source["id"]}/links"), new { typeId, targetId = target["id"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, linked);

        File.Delete(api.TaskFile(target));
        var broken = await Eventually(async () =>
        {
            var health = await LinkHealth(api, http);
            return health["tasksWithInvalidLinks"]!.GetValue<int>() == 1 ? health : null;
        });
        Assert.True(broken["needsAttention"]!.GetValue<bool>());

        var (status, report) = await api.Post(api.P("/cleanup"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(report!["skipped"]!.GetValue<bool>());
        Assert.Single(report["changes"]!.AsArray());

        var clean = await Eventually(async () =>
        {
            var health = await LinkHealth(api, http);
            return health["tasksWithInvalidLinks"]!.GetValue<int>() == 0 ? health : null;
        });
        Assert.False(clean["needsAttention"]!.GetValue<bool>());
        Assert.Empty(clean["cycles"]!.AsArray());
    }

    [Fact]
    public async Task Cleanup_of_a_healthy_project_changes_nothing()
    {
        await using var api = await ApiHost.Start("files");
        await api.NewTask("only");

        var (status, report) = await api.Post(api.P("/cleanup"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(report!["changes"]!.AsArray());
    }

    [Fact]
    public async Task Cleanup_of_an_unknown_project_is_not_found()
    {
        await using var api = await ApiHost.Start("files");

        Assert.Equal(HttpStatusCode.NotFound, (await api.Post($"/projects/{Guid.NewGuid()}/cleanup")).Status);
    }
}
