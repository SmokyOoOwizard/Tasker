using System.Net;
using System.Text.Json.Nodes;
using Tasker.Core.TaskSeries;
using Xunit;

namespace Tasker.Tests;

public class SeriesConflictTests
{
    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();

    private static string Clone(ApiHost api, JsonNode task)
    {
        var id = Guid.NewGuid().ToString("D");
        var text = File.ReadAllText(api.TaskFile(task)).Replace(Id(task), id);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(api.TaskFile(task))!, id + ".yaml"), text);
        return id;
    }

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

    private static async Task<(ApiHost Api, JsonNode Series, JsonNode Keeper, string[] Clones, SeriesConflictService Service)> Setup(int clones)
    {
        var api = await ApiHost.Start("files");
        var tsk = await api.NewSeries("TSK");
        var original = await api.NewTask("original", tsk);
        var copies = Enumerable.Range(0, clones).Select(_ => Clone(api, original)).ToArray();
        var service = api.Lease.Resolve<SeriesConflictService>();
        await Eventually(async () =>
            (await api.Get(api.P("/series/health"))).Body!["numberConflicts"]!.AsArray().Count == 1 ? new object() : null);
        return (api, tsk, original, copies, service);
    }

    private static string ResolvePath(ApiHost api, JsonNode series) => api.P($"/series/{Id(series)}/resolve-conflicts");

    [Fact]
    public async Task Plan_lists_the_moves_and_confirming_it_renumbers_all_but_one()
    {
        var (api, tsk, _, _, service) = await Setup(2);
        await using var _api = api;

        var plan = (await service.Plan(api.ProjectId, Guid.Parse(Id(tsk))))!;
        Assert.Equal(2, plan.Steps.Length);
        Assert.Equal([2, 3], plan.Steps.Select(x => x.NewNumber).ToArray());
        Assert.All(plan.Steps, x => Assert.Equal(1, x.OldNumber));
        Assert.Single(plan.Steps.Select(x => x.KeeperId).Distinct());
        Assert.DoesNotContain(plan.Steps, x => x.TaskId == plan.Steps[0].KeeperId);

        var (status, body) = await api.Post(ResolvePath(api, tsk), new { token = plan.Token });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(plan.Token, body!["token"]!.GetValue<string>());

        var health = (await api.Get(api.P("/series/health"))).Body!;
        Assert.Empty(health["numberConflicts"]!.AsArray());
        var numbers = (await api.Get(api.P("/tasks"))).Body!["data"]!.AsArray()
            .Select(x => x!["seriesNumbers"]![0]!["number"]!.GetValue<int>()).Order().ToArray();
        Assert.Equal([1, 2, 3], numbers);
    }

    [Fact]
    public async Task Confirming_the_same_plan_twice_changes_nothing_the_second_time()
    {
        var (api, tsk, _, _, service) = await Setup(1);
        await using var _api = api;
        var plan = (await service.Plan(api.ProjectId, Guid.Parse(Id(tsk))))!;

        Assert.Equal(HttpStatusCode.OK, (await api.Post(ResolvePath(api, tsk), new { token = plan.Token })).Status);
        var second = await api.Post(ResolvePath(api, tsk), new { token = plan.Token });

        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Equal("modified", second.Body!["code"]!.GetValue<string>());
        var numbers = (await api.Get(api.P("/tasks"))).Body!["data"]!.AsArray()
            .Select(x => x!["seriesNumbers"]![0]!["number"]!.GetValue<int>()).Order().ToArray();
        Assert.Equal([1, 2], numbers);
    }

    [Fact]
    public async Task Two_users_confirming_at_once_apply_the_plan_exactly_once()
    {
        var (api, tsk, _, _, service) = await Setup(3);
        await using var _api = api;
        var plan = (await service.Plan(api.ProjectId, Guid.Parse(Id(tsk))))!;

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => api.Post(ResolvePath(api, tsk), new { token = plan.Token })));

        Assert.Equal(1, results.Count(x => x.Status == HttpStatusCode.OK));
        Assert.Equal(3, results.Count(x => x.Status == HttpStatusCode.Conflict));
        var numbers = (await api.Get(api.P("/tasks"))).Body!["data"]!.AsArray()
            .Select(x => x!["seriesNumbers"]![0]!["number"]!.GetValue<int>()).Order().ToArray();
        Assert.Equal([1, 2, 3, 4], numbers);
    }

    [Fact]
    public async Task A_plan_goes_stale_when_a_task_changes_after_it_was_built()
    {
        var (api, tsk, keeper, _, service) = await Setup(1);
        await using var _api = api;
        var plan = (await service.Plan(api.ProjectId, Guid.Parse(Id(tsk))))!;

        var update = await api.Patch(api.P($"/tasks/{plan.Steps[0].TaskId}"), new
        {
            title = "changed meanwhile",
            version = (await api.Get(api.P($"/tasks/{plan.Steps[0].TaskId}"))).Body!["version"]!.GetValue<string>()
        });
        Assert.Equal(HttpStatusCode.OK, update.Status);

        var stale = await api.Post(ResolvePath(api, tsk), new { token = plan.Token });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("modified", stale.Body!["code"]!.GetValue<string>());

        var fresh = (await service.Plan(api.ProjectId, Guid.Parse(Id(tsk))))!;
        Assert.NotEqual(plan.Token, fresh.Token);
        Assert.Equal(HttpStatusCode.OK, (await api.Post(ResolvePath(api, tsk), new { token = fresh.Token })).Status);
        Assert.NotNull(keeper);
    }

    [Fact]
    public async Task Without_conflicts_the_plan_is_empty_and_a_token_is_required()
    {
        await using var api = await ApiHost.Start("files");
        var tsk = await api.NewSeries("TSK");
        await api.NewTask("only", tsk);
        var service = api.Lease.Resolve<SeriesConflictService>();

        var plan = (await service.Plan(api.ProjectId, Guid.Parse(Id(tsk))))!;
        Assert.Empty(plan.Steps);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post(ResolvePath(api, tsk), new { })).Status);
        Assert.Equal(HttpStatusCode.OK, (await api.Post(ResolvePath(api, tsk), new { token = plan.Token })).Status);
    }

    [Fact]
    public async Task An_unknown_series_has_no_plan_and_cannot_be_resolved()
    {
        await using var api = await ApiHost.Start("files");
        var service = api.Lease.Resolve<SeriesConflictService>();
        var missing = Guid.NewGuid();

        Assert.Null(await service.Plan(api.ProjectId, missing));
        Assert.Equal(HttpStatusCode.NotFound, (await api.Post(api.P($"/series/{missing}/resolve-conflicts"), new { token = "x" })).Status);
    }
}
