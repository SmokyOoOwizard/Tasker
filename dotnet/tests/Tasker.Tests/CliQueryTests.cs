using Xunit;

namespace Tasker.Tests;

public class CliQueryTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    public static IEnumerable<object[]> Storages() => TestWorkspace.Storages();

    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    private static Task<CliResult> P(TestWorkspace ws, params string[] args) => ws.InProject("Demo", args);

    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        foreach (var status in new[] { "Todo", "Doing", "Done" })
            await Ok(P(ws, "status", "create", status));
        await Ok(P(ws, "status-set", "create", "Flow", "--status", "Todo", "Doing", "Done"));
        await Ok(P(ws, "task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(P(ws, "task-type", "create", "Story", "--status-set", "Flow"));
        await Ok(P(ws, "series", "create", "Tasks", "--prefix", "TSK"));
        await Ok(P(ws, "field", "create", "Estimate", "--type", "int"));
        await New(ws, "one", "Bug", "Todo", "Estimate=8");
        await New(ws, "two", "Story", "Todo", "Estimate=2");
        await New(ws, "three", "Bug", "Doing", "Estimate=5");
        await New(ws, "four", "Story", "Done", "Estimate=13");
        await New(ws, "five", "Bug", "Done", "Estimate=1");
        await Ok(ws.Run("board", "create", "Main", "--status-set", "Flow", "--column", "Open=Todo,Doing", "--column", "Done=Done"));
    }

    private static Task New(TestWorkspace ws, string title, string type, string status, string field) =>
        Ok(P(ws, "task", "create", title, "--type", type, "--status", status, "--series", "TSK", "--field", field));

    private static string[] Titles(CliResult result) =>
        result.Json["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray();

    private static Task<CliResult> List(TestWorkspace ws, params string[] more) => Ok(P(ws, ["task", "list", "--json", "--all", .. more]));

    [Theory, MemberData(nameof(Storages))]
    public async Task Query_selects_and_sorts_tasks(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["four", "one"], Titles(await List(ws, "--query", ".Field.Estimate > 5 && .Status == [\"Todo\", \"Done\"] | sort .Title")));
        Assert.Equal(["five", "two", "three", "one", "four"], Titles(await List(ws, "--query", "| sort .Field.Estimate")));
        Assert.Equal(["four", "one", "three", "two", "five"], Titles(await List(ws, "--query", "| sort -.Field.Estimate")));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Query_holds_together_with_old_filters_and_overrides_sort(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["one"], Titles(await List(ws, "--status", "Todo", "--type", "Bug", "--query", ".Field.Estimate > 1")));
        Assert.Equal(["one", "three"], Titles(await List(ws, "--type", "Bug", "--series", "TSK", "--field", "Estimate>=5", "--query", ".Status != \"Done\" | sort .Title")));
        Assert.Empty(Titles(await List(ws, "--status", "Done", "--query", ".Status == \"Todo\"")));
        Assert.Equal(["five", "two", "three", "one", "four"], Titles(await List(ws, "--sort", "-title", "--query", "| sort .Field.Estimate")));
        Assert.Equal(["two", "three", "one", "four"], Titles(await List(ws, "--sort", "title", "--query", ".Field.Estimate > 1 | sort .Field.Estimate")));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Query_works_in_the_tree_view(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var result = await Ok(P(ws, "task", "list", "--query", ".Title == \"two\""));
        Assert.Contains("two", result.Out);
        Assert.DoesNotContain("three", result.Out);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Query_error_shows_the_place(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var broken = await P(ws, "task", "list", "--query", ".Status == \"Nope\"");
        Assert.Equal(1, broken.Code);
        Assert.Contains("Query error at", broken.Err);
        Assert.Contains(".Status == \"Nope\"", broken.Err);
        Assert.Contains("^", broken.Err);

        var unknown = await P(ws, "task", "list", "--query", ".Bogus == 1");
        Assert.Equal(1, unknown.Code);
        Assert.Contains("Query error at 2-", unknown.Err);

        var open = await P(ws, "board", "tasks", "Main", "Open", "--query", ".Title ==");
        Assert.Equal(1, open.Code);
        Assert.Contains("Query error at", open.Err);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Query_filters_board_column_tasks(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var all = Titles(await Ok(P(ws, "board", "tasks", "Main", "Open", "--all", "--json")));
        Assert.Equal(3, all.Length);
        Assert.Equal(["one", "three", "two"], Titles(await Ok(P(ws, "board", "tasks", "Main", "Open", "--all", "--json", "--query", ".Field.Estimate > 1 | sort -.Field.Estimate"))));
        Assert.Equal(["one"], Titles(await Ok(P(ws, "board", "tasks", "Main", "Open", "--all", "--json", "--query", ".Status == \"Todo\"", "--field", "Estimate>=5"))));
        Assert.Equal(["two", "three", "one"], Titles(await Ok(P(ws, "board", "tasks", "Main", "Open", "--all", "--json", "--sort", "-title", "--query", "| sort .Field.Estimate"))));
    }
}
