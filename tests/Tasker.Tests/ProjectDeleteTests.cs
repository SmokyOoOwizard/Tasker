using System.Net;
using Xunit;

namespace Tasker.Tests;

/// <summary>Статистика проекта и подтверждение удаления: консоль и REST, папка и SQLite.</summary>
public class ProjectDeleteTests
{
    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    /// <summary>Проект Demo: 3 статуса, набор, тип, доска, 2 задачи, серия.</summary>
    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.InProject("Demo", "status", "create", "Todo"));
        await Ok(ws.InProject("Demo", "status", "create", "Doing"));
        await Ok(ws.InProject("Demo", "status", "create", "Done"));
        await Ok(ws.InProject("Demo", "status-set", "create", "Flow", "--status", "Todo", "Doing", "Done"));
        await Ok(ws.InProject("Demo", "task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(ws.InProject("Demo", "board", "create", "Main", "--status-set", "Flow",
            "--column", "Todo=Todo", "--column", "In work=Doing", "--column", "Finished=Done"));
        await Ok(ws.InProject("Demo", "task", "create", "One", "--type", "Bug"));
        await Ok(ws.InProject("Demo", "task", "create", "Two", "--type", "Bug"));
        await Ok(ws.InProject("Demo", "series", "create", "Main", "--prefix", "DM"));
    }

    private static async Task<bool> DemoExists(TestWorkspace ws) =>
        (await Ok(ws.Run("project", "list"))).Out.Contains("Demo");

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Prompt_shows_what_will_be_lost_and_yes_deletes(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var result = await ws.RunWithInput("y\n", true, "project", "delete", "Demo");
        Assert.Equal(0, result.Code);
        Assert.Contains("Project 'Demo' will be deleted with everything in it: 2 tasks, 1 board, 3 statuses, 1 status set, 1 task type, 1 series", result.Out);
        Assert.Contains("This cannot be undone.", result.Out);
        Assert.Contains("Delete project 'Demo'? [y/N]", result.Out);
        Assert.Contains("Deleted project 'Demo'", result.Out);
        Assert.False(await DemoExists(ws));
    }

    [Theory]
    [InlineData("files", "yes")]
    [InlineData("files", "Y")]
    [InlineData("sqlite", " YES ")]
    public async Task Yes_in_any_case_deletes(string storage, string answer)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(0, (await ws.RunWithInput(answer + "\n", true, "project", "delete", "Demo")).Code);
        Assert.False(await DemoExists(ws));
    }

    [Theory]
    [InlineData("n\n")]
    [InlineData("\n")]
    [InlineData("")]
    [InlineData("maybe\n")]
    [InlineData("yep\n")]
    public async Task Anything_but_yes_cancels(string answer)
    {
        foreach (var storage in new[] { "files", "sqlite" })
        {
            using var ws = TestWorkspace.Create(storage);
            await Seed(ws);

            var result = await ws.RunWithInput(answer, true, "project", "delete", "Demo");
            Assert.Equal(1, result.Code);
            Assert.Contains("Cancelled, nothing was deleted", result.Err);
            Assert.DoesNotContain("Deleted project", result.Out);
            Assert.True(await DemoExists(ws));
            Assert.Contains("2 tasks", result.Out);
        }
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Redirected_input_without_yes_fails_without_waiting(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // Ответ «y» в потоке есть, но терминала нет: он не должен быть прочитан.
        var result = await ws.RunWithInput("y\n", false, "project", "delete", "Demo");
        Assert.Equal(1, result.Code);
        Assert.Contains("2 tasks", result.Err);
        Assert.Contains("--yes", result.Err);
        Assert.DoesNotContain("[y/N]", result.Out + result.Err);
        Assert.True(await DemoExists(ws));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Yes_flag_deletes_without_any_prompt(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var result = await ws.RunWithInput("n\n", true, "project", "delete", "Demo", "--yes");
        Assert.Equal(0, result.Code);
        Assert.DoesNotContain("[y/N]", result.Out + result.Err);
        Assert.False(await DemoExists(ws));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Json_keeps_stdout_clean_and_prompts_on_stderr(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var result = await ws.RunWithInput("y\n", true, "project", "delete", "Demo", "--json");
        Assert.Equal(0, result.Code);
        Assert.NotNull(result.Json["deleted"]);
        Assert.DoesNotContain("[y/N]", result.Out);
        Assert.Contains("[y/N]", result.Err);
        Assert.Contains("2 tasks", result.Err);
    }

    [Fact]
    public async Task Empty_project_and_unknown_project()
    {
        using var ws = TestWorkspace.Create("files");
        await Ok(ws.Run("project", "create", "Empty"));

        var result = await ws.RunWithInput("n\n", true, "project", "delete", "Empty");
        Assert.Contains("Project 'Empty'", result.Out);

        var unknown = await ws.RunWithInput("y\n", true, "project", "delete", "Nope");
        Assert.Equal(1, unknown.Code);
        Assert.Contains("Nope", unknown.Err);
        Assert.DoesNotContain("[y/N]", unknown.Out);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Rest_stats_count_entities_and_unknown_project_is_404(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        await api.NewTask("A");
        await api.NewTask("B");
        await api.NewSeries("ST");

        var (status, body) = await api.Get(api.P("/stats"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body!["tasks"]!.GetValue<int>());
        Assert.Equal(0, body["boards"]!.GetValue<int>());
        Assert.Equal(1, body["statuses"]!.GetValue<int>());
        Assert.Equal(1, body["statusSets"]!.GetValue<int>());
        Assert.Equal(1, body["taskTypes"]!.GetValue<int>());
        Assert.Equal(1, body["series"]!.GetValue<int>());
        Assert.NotNull(body["linkTypes"]);

        Assert.Equal(HttpStatusCode.NotFound, (await api.Get($"/projects/{Guid.NewGuid()}/stats")).Status);
    }
}
