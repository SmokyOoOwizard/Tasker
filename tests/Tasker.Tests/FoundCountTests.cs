using Tasker.Cli;
using Xunit;

namespace Tasker.Tests;

/// <summary>TSK-107: каждый текстовый список начинается строкой с количеством найденного (в stdout, до строк данных).</summary>
[InProcess]
public class FoundCountTests
{
    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Проект Demo с типом Bug, набором Flow (Todo, Done), серией TSK и доской Main (колонки Open, Closed); tasks задач Bug: нечётные Todo, чётные Done.</summary>
    private static async Task Seed(TestWorkspace ws, int tasks = 0)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("status", "create", "Todo"));
        await Ok(ws.Run("status", "create", "Done"));
        await Ok(ws.Run("status-set", "create", "Flow", "--status", "Todo", "Done"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(ws.Run("series", "create", "Tasks", "--prefix", "TSK"));
        await Ok(ws.Run("board", "create", "Main", "--status-set", "Flow", "--column", "Open=Todo", "--column", "Closed=Done"));
        for (var i = 1; i <= tasks; i++)
            await Ok(ws.Run("task", "create", $"Task {i:000}", "--type", "Bug", "--series", "TSK", "--status", i % 2 == 0 ? "Done" : "Todo"));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task An_empty_result_is_Found_0_and_nothing_else(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var empty = await Ok(ws.Run("task", "list"));
        Assert.Equal("Found 0\n", empty.Out);
        Assert.Empty(empty.Err);

        // Фильтр, которому ничего не подходит, — тоже «Found 0», а не молчание.
        await Ok(ws.Run("task", "create", "One", "--type", "Bug", "--series", "TSK"));
        Assert.Equal("Found 0\n", (await Ok(ws.Run("task", "list", "--status", "Done"))).Out);
        Assert.Equal("Found 0\n", (await Ok(ws.Run("task", "links", "TSK-1"))).Out);
        Assert.Equal("Found 0\n", (await Ok(ws.Run("board", "tasks", "Main", "Closed"))).Out);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task A_list_shown_whole_says_Found_N_and_the_line_comes_first(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws, tasks: 3);

        var result = await Ok(ws.Run("task", "list"));
        var lines = Lines(result.Out);
        Assert.Equal("Found 3", lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.All(lines.Skip(1), x => Assert.StartsWith("TSK-", x));
        Assert.Empty(result.Err); // stdout, а не stderr

        Assert.Equal("Found 2", (await Ok(ws.Run("status", "list"))).Found);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task A_page_says_how_much_there_is_and_which_part_is_shown(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws, tasks: 5);

        var first = await Ok(ws.Run("task", "list", "--limit", "2"));
        Assert.Equal("Found 5, shown 1-2 (use --offset/--limit)", first.Found);
        Assert.Equal(3, Lines(first.Out).Length);
        Assert.Empty(first.Err);

        var last = await Ok(ws.Run("task", "list", "--offset", "3", "--limit", "10"));
        Assert.Equal("Found 5, shown 4-5 (use --offset/--limit)", last.Found);

        var beyond = await Ok(ws.Run("task", "list", "--offset", "9"));
        Assert.Equal("Found 5, shown none (use --offset/--limit)", beyond.Out.Trim());

        // --all показывает всё: страницы нет.
        Assert.Equal("Found 5", (await Ok(ws.Run("task", "list", "--all"))).Found);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task The_number_is_counted_after_the_filters(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws, tasks: 6); // 3 Todo, 3 Done

        Assert.Equal("Found 3", (await Ok(ws.Run("task", "list", "--status", "Done"))).Found);
        Assert.Equal("Found 6", (await Ok(ws.Run("task", "list", "--type", "Bug", "--series", "TSK"))).Found);
        Assert.Equal("Found 3, shown 1-1 (use --offset/--limit)", (await Ok(ws.Run("task", "list", "--status", "Todo", "--limit", "1"))).Found);
        Assert.Equal("Found 3", (await Ok(ws.Run("board", "tasks", "Main", "Open"))).Found);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Every_list_command_starts_with_the_line(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws, tasks: 2);
        await Ok(ws.Run("user", "create", "Ivan"));
        await Ok(ws.Run("agent", "create", "bot"));
        await Ok(ws.Run("enum", "create", "Level", "--value", "Low", "--value", "High"));
        await Ok(ws.Run("field", "create", "Estimate", "--type", "int"));
        await Ok(ws.Run("task", "link", "TSK-1", "blocks", "TSK-2"));

        foreach (var (args, count) in new (string[] Args, int Count)[]
        {
            (["project", "list"], 1), (["status", "list"], 2), (["status-set", "list"], 1), (["task-type", "list"], 1), (["series", "list"], 1),
            (["board", "list"], 1), (["task", "list"], 2), (["task", "links", "TSK-1"], 1), (["board", "tasks", "Main", "Open"], 1),
            (["field", "list"], 1), (["enum", "list"], 1), (["user", "list"], 1), (["agent", "list"], 1)
        })
        {
            var result = await Ok(ws.Run(args));
            var lines = Lines(result.Out);
            Assert.True(lines[0] == $"Found {count}", $"{string.Join(' ', args)}: {result.Out}");
            Assert.Equal(count + 1, lines.Length);
            Assert.Empty(result.Err);
        }

        // link-type list: набор по умолчанию, число зависит от набора; важна лишь форма.
        Assert.Matches(@"^Found \d+\n", (await Ok(ws.Run("link-type", "list"))).Out);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Quiet_removes_the_line_and_leaves_the_data(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws, tasks: 3);
        await Ok(ws.Run("task", "link", "TSK-1", "blocks", "TSK-2"));

        var normal = await Ok(ws.Run("task", "list"));
        foreach (var flag in new[] { "-q", "--quiet" })
        {
            var quiet = await Ok(ws.Run("task", "list", flag));
            Assert.Equal(3, Lines(quiet.Out).Length);
            Assert.Null(quiet.Found);
            Assert.Equal(normal.Data, quiet.Out);
        }

        // Страница и пустая страница с -q — без итога.
        Assert.Empty((await Ok(ws.Run("task", "list", "--offset", "9", "-q"))).Out);
        Assert.Equal(2, Lines((await Ok(ws.Run("task", "list", "--limit", "2", "-q"))).Out).Length);
        Assert.Single(Lines((await Ok(ws.Run("task", "links", "TSK-1", "-q"))).Out));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Json_is_unchanged(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws, tasks: 3);

        var json = await Ok(ws.Run("task", "list", "--json", "--limit", "2"));
        Assert.StartsWith("{", json.Out); // сразу JSON, без строки итога
        Assert.Equal(3, json.Json["totalCount"]!.GetValue<int>());
        Assert.Equal(2, json.Json["data"]!.AsArray().Count);
        Assert.Empty(json.Err);

        Assert.Equal(json.Out, (await Ok(ws.Run("task", "list", "--json", "--limit", "2", "-q"))).Out);
        Assert.StartsWith("{", (await Ok(ws.Run("task", "list", "--status", "Done", "--offset", "5", "--json"))).Out);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task The_line_is_not_cut_by_the_width_and_does_not_join_the_table(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws, tasks: 3);

        var narrow = await Ok(ws.Run("task", "list", "--limit", "2", "--width", "20"));
        var lines = Lines(narrow.Out);
        Assert.Equal("Found 3, shown 1-2 (use --offset/--limit)", lines[0]); // длиннее 20 знаков, но не обрезана
        Assert.All(lines.Skip(1), x => Assert.True(Table.Width(x) <= 20, x)); // данные обрезаны

        // Колонки выровнены без учёта строки итога: те же строки данных, что с -q.
        var data = (await Ok(ws.Run("task", "list"))).Data;
        Assert.Equal((await Ok(ws.Run("task", "list", "-q"))).Out, data);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Board_show_and_single_entities_have_no_total_line(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws, tasks: 2);

        var show = await Ok(ws.Run("board", "show", "Main"));
        Assert.DoesNotContain("Found", show.Out);
        Assert.Contains("Open (1)", show.Out); // счётчики колонок остаются
        Assert.DoesNotContain("Found", (await Ok(ws.Run("task", "get", "TSK-1"))).Out);
        Assert.DoesNotContain("Found", (await Ok(ws.Run("status", "get", "Todo"))).Out);
    }
}
