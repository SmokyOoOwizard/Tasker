using Xunit;

namespace Tasker.Tests;

/// <summary><c>tasker board tasks</c> (задачи колонки) и <c>tasker board show</c> (вся доска по колонкам).</summary>
public class BoardConsoleTests
{
    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    private static string[] Lines(CliResult result) => result.Data.Split('\n');

    /// <summary>
    /// Проект Demo (один: --project не нужен), статусы Backlog/Todo/Doing/Done, доска Main: колонки «Backlog» (Backlog + Todo), «In work», «Done».
    /// Задачи: TSK-1 First (Backlog), TSK-2 Second (Todo), TSK-3 Third (Doing), TSK-4 Fourth (Done), TSK-5 Fifth (Backlog).
    /// </summary>
    private static async Task Seed(TestWorkspace ws, int extraBacklog = 0)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        foreach (var status in new[] { "Backlog", "Todo", "Doing", "Done" })
            await Ok(ws.Run("status", "create", status));
        await Ok(ws.Run("status-set", "create", "Flow", "--status", "Backlog", "Todo", "Doing", "Done"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(ws.Run("series", "create", "Tasks", "--prefix", "TSK"));
        await Ok(ws.Run("board", "create", "Main", "--status-set", "Flow", "--column", "Backlog=Backlog,Todo", "--column", "In work=Doing", "--column", "Done=Done"));
        foreach (var (title, status) in new[] { ("First", "Backlog"), ("Second", "Todo"), ("Third", "Doing"), ("Fourth", "Done"), ("Fifth", "Backlog") })
            await Ok(ws.Run("task", "create", title, "--type", "Bug", "--series", "TSK", "--status", status));
        for (var i = 0; i < extraBacklog; i++)
            await Ok(ws.Run("task", "create", $"Extra {i:000}", "--type", "Bug", "--series", "TSK", "--status", "Backlog"));
    }

    // ---- board tasks ----

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Board_tasks_lists_the_tasks_of_one_column_in_the_task_list_format(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var backlog = Lines(await Ok(ws.Run("board", "tasks", "Main", "Backlog"))).Where(x => x.Length > 0).ToArray();
        Assert.Equal(["TSK-1  Backlog  Bug  First", "TSK-2  Todo     Bug  Second", "TSK-5  Backlog  Bug  Fifth"], backlog);
        Assert.Equal(["TSK-3  Doing  Bug  Third"], Lines(await Ok(ws.Run("board", "tasks", "Main", "In work"))).Where(x => x.Length > 0).ToArray());
        Assert.Equal(["TSK-4  Done  Bug  Fourth"], Lines(await Ok(ws.Run("board", "tasks", "Main", "done"))).Where(x => x.Length > 0).ToArray()); // без учёта регистра
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Board_tasks_accepts_ids_and_pages_like_every_list(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws, extraBacklog: 55);
        var board = (await Ok(ws.Run("board", "get", "Main", "--json"))).Json;
        var boardId = board["id"]!.GetValue<string>();
        var columnId = board["columns"]![0]!["id"]!.GetValue<string>();

        var page = await Ok(ws.Run("board", "tasks", boardId, columnId));
        Assert.Equal(50, Lines(page).Count(x => x.Length > 0));
        Assert.Equal("Found 58, shown 1-50 (use --offset/--limit)", page.Found);

        var all = await Ok(ws.Run("board", "tasks", "Main", "Backlog", "--all"));
        Assert.Equal(58, Lines(all).Count(x => x.Length > 0));
        Assert.Empty(all.Err);
        var second = await Ok(ws.Run("board", "tasks", "Main", "Backlog", "--offset", "56", "--limit", "10"));
        Assert.Equal(2, Lines(second).Count(x => x.Length > 0));

        var json = (await Ok(ws.Run("board", "tasks", "Main", "Backlog", "--all", "--json"))).Json;
        Assert.Equal(58, json["totalCount"]!.GetValue<int>());
        Assert.Equal(58, json["data"]!.AsArray().Count);
        Assert.Equal(1, (await ws.Run("board", "tasks", "Main", "Backlog", "--all", "--limit", "5")).Code);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Board_tasks_reports_an_unknown_board_or_column(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var board = await ws.Run("board", "tasks", "Nope", "Backlog");
        Assert.Equal(1, board.Code);
        Assert.Contains("No board 'Nope'", board.Err);

        var column = await ws.Run("board", "tasks", "Main", "Nope");
        Assert.Equal(1, column.Code);
        Assert.Contains("No column of board 'Main' 'Nope'", column.Err);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task A_task_moves_between_columns_as_its_status_changes(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        await Ok(ws.Run("task", "update", "TSK-1", "--status", "Doing"));
        await Ok(ws.Run("task", "update", "TSK-2", "--status", "Backlog")); // обе статуса колонки Backlog: остаётся в ней
        var task = await Ok(ws.Run("task", "create", "Sixth", "--type", "Bug", "--series", "TSK")); // статус по умолчанию — первый статус набора

        Assert.Equal(["TSK-2  Backlog  Bug  Second", "TSK-5  Backlog  Bug  Fifth", "TSK-6  Backlog  Bug  Sixth"],
            Lines(await Ok(ws.Run("board", "tasks", "Main", "Backlog"))).Where(x => x.Length > 0).ToArray());
        Assert.Equal(["TSK-1  Doing  Bug  First", "TSK-3  Doing  Bug  Third"],
            Lines(await Ok(ws.Run("board", "tasks", "Main", "In work"))).Where(x => x.Length > 0).ToArray());
        Assert.Contains("TSK-6", task.Out);
    }

    // ---- board show ----

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Board_show_prints_every_column_in_order_with_its_tasks(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var show = await Ok(ws.Run("board", "show", "Main"));

        Assert.Equal(
            [
                "Backlog (3)",
                "  TSK-1  Backlog  Bug  First",
                "  TSK-2  Todo     Bug  Second",
                "  TSK-5  Backlog  Bug  Fifth",
                "",
                "In work (1)",
                "  TSK-3  Doing    Bug  Third",
                "",
                "Done (1)",
                "  TSK-4  Done     Bug  Fourth"
            ],
            show.Out.TrimEnd('\n').Split('\n'));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Board_show_aligns_columns_across_the_whole_board(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws, extraBacklog: 6); // TSK-6..TSK-11: ссылки разной длины
        await Ok(ws.Run("task", "update", "TSK-3", "--title", "Третья"));

        var lines = (await Ok(ws.Run("board", "show", "Main", "--all"))).Out.TrimEnd('\n').Split('\n').Where(x => x.StartsWith("  TSK-")).ToArray();

        Assert.Contains("  TSK-1   Backlog  Bug  First", lines);
        Assert.Contains("  TSK-10  Backlog  Bug  Extra 004", lines);
        Assert.Contains("  TSK-3   Doing    Bug  Третья", lines);
        Assert.Single(lines.Select(x => x.IndexOf("Bug  ", StringComparison.Ordinal)).Distinct());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Board_show_limits_each_column_and_all_shows_everything(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws, extraBacklog: 55);

        var limited = (await Ok(ws.Run("board", "show", "Main", "--limit", "2"))).Out.Split('\n');
        Assert.Equal("Backlog (58)", limited[0]);
        Assert.Equal(2, limited.Skip(1).TakeWhile(x => x.StartsWith("  TSK-")).Count());
        Assert.Contains("  ... and 56 more (use --all, or 'board tasks Main \"Backlog\"')", limited);
        Assert.Contains("In work (1)", limited);
        Assert.DoesNotContain(limited, x => x.Contains("more") && x.Contains("In work"));

        var byDefault = (await Ok(ws.Run("board", "show", "Main"))).Out.Split('\n');
        Assert.Equal(50, byDefault.Skip(1).TakeWhile(x => x.StartsWith("  TSK-")).Count()); // по умолчанию — первые 50 на колонку

        var all = (await Ok(ws.Run("board", "show", "Main", "--all"))).Out.Split('\n');
        Assert.Equal(58 + 1 + 1, all.Count(x => x.StartsWith("  TSK-")));
        Assert.DoesNotContain(all, x => x.Contains("... and"));

        Assert.Equal(1, (await ws.Run("board", "show", "Main", "--all", "--limit", "5")).Code);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Board_show_marks_empty_columns_and_prints_json(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(ws.Run("task", "update", "TSK-4", "--status", "Backlog")); // колонка Done опустела

        var text = (await Ok(ws.Run("board", "show", "Main"))).Out.TrimEnd('\n').Split('\n');
        Assert.Equal(["Done (0)", "  (no tasks)"], text.TakeLast(2).ToArray());

        var json = (await Ok(ws.Run("board", "show", "Main", "--json"))).Json;
        Assert.Equal("Main", json["board"]!["name"]!.GetValue<string>());
        var columns = json["columns"]!.AsArray();
        Assert.Equal(["Backlog", "In work", "Done"], columns.Select(x => x!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal([4, 1, 0], columns.Select(x => x!["totalCount"]!.GetValue<int>()).ToArray());
        Assert.Equal(4, columns[0]!["tasks"]!.AsArray().Count);
        Assert.Equal("Fourth", columns[0]!["tasks"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).Single(x => x == "Fourth"));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Tasks_of_a_status_set_that_is_not_on_the_board_are_not_shown(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(ws.Run("status", "create", "Open"));
        await Ok(ws.Run("status-set", "create", "Simple", "--status", "Open"));
        await Ok(ws.Run("task-type", "create", "Note", "--status-set", "Simple"));
        await Ok(ws.Run("task", "create", "Elsewhere", "--type", "Note"));

        var show = (await Ok(ws.Run("board", "show", "Main", "--all"))).Out;

        Assert.DoesNotContain("Elsewhere", show);
        Assert.Contains("Backlog (3)", show);
    }

    [Fact]
    public async Task Board_show_of_an_unknown_board_is_an_error()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);

        var result = await ws.Run("board", "show", "Nope");

        Assert.Equal(1, result.Code);
        Assert.Contains("No board 'Nope'", result.Err);
    }
}
