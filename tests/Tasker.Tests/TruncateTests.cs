using Tasker.Cli;
using Tasker.Global;
using Xunit;

namespace Tasker.Tests;

/// <summary>Обрезка строк списков по ширине окна (<c>--truncate</c>, <c>--width</c>, <c>TASKER_WIDTH</c>): <see cref="Table.Truncate"/>, <see cref="Terminal.Limit"/> и команды.</summary>
public class TruncateTests
{
    // ---- Table: один общий приём ----

    [Fact]
    public void A_line_that_fits_is_left_alone()
    {
        Assert.Equal(["TSK-1  Todo  Bug  Short"], Table.Format([["TSK-1", "Todo", "Bug", "Short"]], "", 40));
        Assert.Equal(["TSK-1  Todo  Bug  Short"], Table.Format([["TSK-1", "Todo", "Bug", "Short"]], "", 23)); // ровно по ширине
    }

    [Fact]
    public void The_last_column_is_cut_with_an_ellipsis_and_the_others_stay_aligned()
    {
        var lines = Table.Format([["TSK-1", "Todo", "Bug", "A very long title of a task"], ["TSK-10", "Done", "", "Another very long title"]], "", 25);

        Assert.Equal(["TSK-1   Todo  Bug  A ver…", "TSK-10  Done       Anoth…"], lines);
        Assert.All(lines, x => Assert.Equal(25, Table.Width(x)));
    }

    [Fact]
    public void The_indent_counts_towards_the_width()
    {
        Assert.Equal(["  TSK-1  Todo  Bug  Long…"], Table.Format([["TSK-1", "Todo", "Bug", "Long long title"]], "  ", 25));
    }

    [Fact]
    public void Cyrillic_is_one_column_and_emoji_and_cjk_are_two_at_the_boundary()
    {
        Assert.Equal("Заголов…", Table.Truncate("Заголовок задачи", 8));
        Assert.Equal("Ab🎉…", Table.Truncate("Ab🎉🎉🎉", 5)); // 2+2 знака и «…»
        Assert.Equal("Ab…", Table.Truncate("Ab🎉🎉🎉", 4)); // эмодзи не влезает в оставшиеся 3 знака: не разрывается
        Assert.Equal("日本…", Table.Truncate("日本語の題名", 6));
        Assert.Equal("日…", Table.Truncate("日本語の題名", 4)); // ширина 3 + «…» не помещается
    }

    [Fact]
    public void Graphemes_and_surrogate_pairs_are_never_split()
    {
        var text = "ééééé"; // пять «é» из буквы и комбинируемого знака
        Assert.Equal("éé…", Table.Truncate(text, 3));
        Assert.Equal("🇷🇺…", Table.Truncate("🇷🇺🇷🇺🇷🇺", 3 + 0)); // флаг — одна графема шириной 2
        Assert.Equal("👨‍👩‍👧…", Table.Truncate("👨‍👩‍👧👨‍👩‍👧", 3));
    }

    [Fact]
    public void A_title_shorter_than_the_width_is_not_touched_and_the_head_longer_than_the_width_is_cut()
    {
        Assert.Equal("TSK-1  Todo", Table.Truncate("TSK-1  Todo", 20));
        Assert.Equal("TSK-1  Todo  Bu…", Table.Truncate("TSK-1  Todo  Bug  Title", 16));
        Assert.Equal("…", Table.Truncate("long", 1));
    }

    // ---- Terminal.Limit ----

    [Fact]
    public void Default_is_to_cut_only_in_a_terminal_with_a_known_width()
    {
        Assert.Equal(80, new Terminal(true, 80).Limit(false, false, null));
        Assert.Null(new Terminal(true, 0).Limit(false, false, null)); // неизвестная ширина
        Assert.Null(new Terminal(false, 80).Limit(false, false, null)); // не терминал
    }

    [Fact]
    public void Explicit_requests_work_without_a_terminal()
    {
        Assert.Equal(60, new Terminal(false, 0).Limit(false, false, "60"));
        Assert.Equal(100, new Terminal(false, 100).Limit(true, false, null));
        Assert.Equal(100, new Terminal(false, 100).Limit(false, false, "auto"));
        Assert.Equal(90, new Terminal(false, 90).Limit(false, false, "0"));
        Assert.Null(new Terminal(false, 0).Limit(true, false, null)); // ширину взять неоткуда
        Assert.Null(new Terminal(true, 80).Limit(false, true, null));
        Assert.Null(new Terminal(true, 80).Limit(false, true, "50")); // --no-truncate сильнее
    }

    [Fact]
    public void Width_has_a_floor_and_is_validated()
    {
        Assert.Equal(20, new Terminal(true, 8).Limit(false, false, null));
        Assert.Equal(20, new Terminal(false, 0).Limit(false, false, "5"));
        Assert.Throws<CliException>(() => new Terminal(true, 80).Limit(false, false, "wide"));
        Assert.Throws<CliException>(() => new Terminal(true, 80).Limit(false, false, "-3"));
        Assert.Throws<CliException>(() => new Terminal(true, 80).Limit(true, true, null));
    }

    [Fact]
    public void The_variable_sets_the_width_and_the_option_wins()
    {
        using (AppEnvironment.Override("TASKER_WIDTH", "45"))
        {
            Assert.Equal(45, new Terminal(false, 0).Limit(false, false, null));
            Assert.Equal(30, new Terminal(false, 0).Limit(false, false, "30"));
            Assert.Null(new Terminal(true, 80).Limit(false, true, null));
        }
    }

    [Fact]
    public void The_terminal_width_is_read_from_COLUMNS_when_the_window_is_unknown()
    {
        using (AppEnvironment.Override("COLUMNS", "72"))
            Assert.Equal(72, Terminal.Current(new StringWriter()).Columns);
        using (AppEnvironment.Override("COLUMNS", "junk"))
            Assert.Equal(0, Terminal.Current(new StringWriter()).Columns);
        using (AppEnvironment.Override("COLUMNS", "-5"))
            Assert.Equal(0, Terminal.Current(new StringWriter()).Columns);
    }

    // ---- под watch: не терминал, но заданы COLUMNS и LINES ----

    private static Terminal Piped(string? columns, string? lines)
    {
        using (AppEnvironment.Override("COLUMNS", columns))
        using (AppEnvironment.Override("LINES", lines))
            return Terminal.Current(new StringWriter());
    }

    [Fact]
    public void Both_COLUMNS_and_LINES_make_a_pipe_a_screen()
    {
        Assert.Equal(40, Piped("40", "24").Limit(false, false, null));
        Assert.Equal(20, Piped("8", "24").Limit(false, false, null)); // пол
        Assert.Null(Piped("40", null).Limit(false, false, null));
        Assert.Null(Piped(null, "24").Limit(false, false, null));
        Assert.Null(Piped(null, null).Limit(false, false, null));
        Assert.Null(Piped("0", "24").Limit(false, false, null));
        Assert.Null(Piped("40", "0").Limit(false, false, null));
        Assert.Null(Piped("wide", "24").Limit(false, false, null));
        Assert.Null(Piped("40", "tall").Limit(false, false, null));
        Assert.Null(Piped("-40", "24").Limit(false, false, null));
    }

    [Fact]
    public void Under_watch_the_switches_and_the_variable_still_win()
    {
        var watch = Piped("40", "24");
        Assert.Null(watch.Limit(false, true, null));
        Assert.Equal(60, watch.Limit(false, false, "60"));
        Assert.Equal(40, watch.Limit(true, false, null));
        using (AppEnvironment.Override("TASKER_WIDTH", "off"))
        {
            Assert.Null(watch.Limit(false, false, null));
            Assert.Equal(50, watch.Limit(false, false, "50"));
        }

        using (AppEnvironment.Override("TASKER_WIDTH", "0"))
            Assert.Null(watch.Limit(false, false, null));
        using (AppEnvironment.Override("TASKER_WIDTH", "55"))
            Assert.Equal(55, watch.Limit(false, false, null));
        Assert.Null(watch.Limit(false, false, "off"));
        Assert.Equal(40, watch.Limit(false, false, "auto"));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Under_watch_the_list_is_cut_by_COLUMNS_and_json_is_not_touched(string storage)
    {
        using var ws = await Seed(storage);
        var whole = $"TSK-1  Todo  Bug  {LongTitle}";

        using (AppEnvironment.Override("COLUMNS", "30"))
        using (AppEnvironment.Override("LINES", "24"))
        {
            Assert.Equal("TSK-1  Todo  Bug  Очень длинн…", Lines(await ws.InProject("Demo", "task", "list"))[0]);
            Assert.Equal(whole, Lines(await ws.InProject("Demo", "task", "list", "--no-truncate"))[0]);
            Assert.Equal(whole, Lines(await ws.InProject("Demo", "task", "list", "--width", "off"))[0]);
            Assert.Contains(LongTitle, (await ws.InProject("Demo", "task", "list", "--json")).Out);
            Assert.Equal((await ws.InProject("Demo", "task", "list", "--json", "--width", "200")).Out, (await ws.InProject("Demo", "task", "list", "--json")).Out);
            Assert.Equal(whole, Lines(await ws.InProject("Demo", "task", "list", "--width", "200"))[0]);
        }

        using (AppEnvironment.Override("COLUMNS", "30"))
        using (AppEnvironment.Override("LINES", null))
            Assert.Equal(whole, Lines(await ws.InProject("Demo", "task", "list"))[0]);
    }

    // ---- команды ----

    private static async Task Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
    }

    private const string LongTitle = "Очень длинный заголовок задачи про обрезку строк";

    private static async Task<TestWorkspace> Seed(string storage)
    {
        var ws = TestWorkspace.Create(storage);
        await Ok(ws.Run("project", "create", "Demo"));
        Task<CliResult> P(params string[] args) => ws.InProject("Demo", args);
        await Ok(P("status", "create", "Todo"));
        await Ok(P("status-set", "create", "Flow", "--status", "Todo"));
        await Ok(P("task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(P("series", "create", "Tasks", "--prefix", "TSK"));
        await Ok(P("task", "create", LongTitle, "--type", "Bug", "--series", "TSK"));
        await Ok(P("task", "create", "Короткий", "--type", "Bug", "--series", "TSK"));
        await Ok(P("task", "create", "🎉🎉🎉🎉🎉🎉🎉🎉🎉🎉🎉🎉🎉🎉🎉🎉🎉🎉🎉🎉", "--type", "Bug", "--series", "TSK"));
        await Ok(P("board", "create", "Main", "--status-set", "Flow", "--column", "All=Todo"));
        await Ok(P("task", "link", "TSK-1", "blocks", "TSK-2"));
        return ws;
    }

    private static string[] Lines(CliResult r) => r.Data.TrimEnd().Split('\n');

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Task_list_is_cut_to_the_width_and_aligned(string storage)
    {
        using var ws = await Seed(storage);

        var lines = Lines(await ws.InProject("Demo", "task", "list", "--width", "30"));

        Assert.Equal(["TSK-1  Todo  Bug  Очень длинн…", "TSK-2  Todo  Bug  Короткий", "TSK-3  Todo  Bug  🎉🎉🎉🎉🎉…"], lines);
        Assert.All(lines, x => Assert.True(Table.Width(x) <= 30));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Wide_width_and_a_redirect_leave_lines_whole(string storage)
    {
        using var ws = await Seed(storage);

        Assert.Equal($"TSK-1  Todo  Bug  {LongTitle}", Lines(await ws.InProject("Demo", "task", "list", "--width", "200"))[0]);
        // вывод не в терминал и ничего не просили: не обрезаем, даже если задан COLUMNS
        using (AppEnvironment.Override("COLUMNS", "25"))
        using (AppEnvironment.Override("LINES", null))
            Assert.Equal($"TSK-1  Todo  Bug  {LongTitle}", Lines(await ws.InProject("Demo", "task", "list"))[0]);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Terminal_cuts_by_default_and_no_truncate_turns_it_off(string storage)
    {
        using var ws = await Seed(storage);

        var cut = await Run(ws, new Terminal(true, 30), "task", "list");
        Assert.Equal("TSK-1  Todo  Bug  Очень длинн…", Lines(cut)[0]);

        var whole = await Run(ws, new Terminal(true, 30), "task", "list", "--no-truncate");
        Assert.Equal($"TSK-1  Todo  Bug  {LongTitle}", Lines(whole)[0]);

        var unknown = await Run(ws, new Terminal(true, 0), "task", "list");
        Assert.Equal($"TSK-1  Todo  Bug  {LongTitle}", Lines(unknown)[0]);

        var redirected = await Run(ws, new Terminal(false, 30), "task", "list");
        Assert.Equal($"TSK-1  Todo  Bug  {LongTitle}", Lines(redirected)[0]);

        var forced = await Run(ws, new Terminal(false, 30), "task", "list", "--no-wrap");
        Assert.Equal("TSK-1  Todo  Bug  Очень длинн…", Lines(forced)[0]);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task The_variable_sets_the_width(string storage)
    {
        using var ws = await Seed(storage);

        using (AppEnvironment.Override("TASKER_WIDTH", "30"))
        {
            Assert.Equal("TSK-1  Todo  Bug  Очень длинн…", Lines(await ws.InProject("Demo", "task", "list"))[0]);
            Assert.Equal($"TSK-1  Todo  Bug  {LongTitle}", Lines(await ws.InProject("Demo", "task", "list", "--width", "200"))[0]);
        }
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Json_does_not_change_and_a_bad_width_is_an_error(string storage)
    {
        using var ws = await Seed(storage);

        var plain = await ws.InProject("Demo", "task", "list", "--json");
        var narrow = await ws.InProject("Demo", "task", "list", "--json", "--width", "20");
        Assert.Equal(plain.Out, narrow.Out);
        Assert.Contains(LongTitle, narrow.Out);

        var bad = await ws.InProject("Demo", "task", "list", "--width", "wide");
        Assert.Equal(1, bad.Code);
        Assert.Contains("Width must be", bad.Err);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Links_board_and_get_are_cut_but_descriptions_and_hints_are_not(string storage)
    {
        using var ws = await Seed(storage);

        // task links: «имя  ссылка  статус  заголовок»
        Assert.Equal(["blocks  TSK-2  Todo…"], Lines(await ws.InProject("Demo", "task", "links", "TSK-1", "--width", "20"))); // без заголовка не помещается: режется вся строка
        Assert.Equal(["blocks  TSK-2  Todo  Ко…"], Lines(await ws.InProject("Demo", "task", "links", "TSK-1", "--width", "24")));
        Assert.Equal(["blocks  TSK-2  Todo  Короткий"], Lines(await ws.InProject("Demo", "task", "links", "TSK-1", "--width", "30")));

        // board show: отступ учитывается
        var board = Lines(await ws.InProject("Demo", "board", "show", "Main", "--width", "30"));
        Assert.Contains("  TSK-1  Todo  Bug  Очень дли…", board);
        Assert.All(board, x => Assert.True(Table.Width(x) <= 30));

        // task get: блок links обрезается, описание нет
        await Ok(ws.InProject("Demo", "task", "update", "TSK-1", "--description", "Описание длиннее двадцати знаков, переносится терминалом"));
        var get = (await ws.InProject("Demo", "task", "get", "TSK-1", "--width", "20")).Out;
        Assert.Contains("  blocks  TSK-2  To…", get);
        Assert.DoesNotContain("Короткий", get);
        Assert.Contains("Описание длиннее двадцати знаков, переносится терминалом", get);

        // первая строка с итогом не обрезается
        var page = await ws.InProject("Demo", "task", "list", "--limit", "1", "--width", "20");
        Assert.Equal("Found 3, shown 1-1 (use --offset/--limit)", page.Out.Split('\n')[0]);
    }

    private static async Task<CliResult> Run(TestWorkspace ws, Terminal terminal, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApp.Run([.. args, "--project", "Demo", .. ws.Location], output, error, null, null, new StringReader(""), false, null, terminal);
        return new CliResult(code, output.ToString(), error.ToString());
    }
}
