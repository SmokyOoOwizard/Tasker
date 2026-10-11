using Tasker.Global;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Обрезка строк списков консолью по ширине (<c>--truncate</c>, <c>--width</c>, <c>TASKER_WIDTH</c>, <c>COLUMNS</c> и <c>LINES</c> под
/// watch). Вывод тут — не терминал: обрезка по ширине окна терминала по умолчанию проверена модульными тестами Rust (<c>terminal.rs</c>).
/// </summary>
public class TruncateTests
{
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
        Assert.All(lines, x => Assert.True(TextWidth.Of(x) <= 30));
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
    public async Task Redirected_output_is_whole_unless_cutting_is_asked_for(string storage)
    {
        using var ws = await Seed(storage);
        using var columns = AppEnvironment.Override("COLUMNS", "30");
        using var lines = AppEnvironment.Override("LINES", null);

        Assert.Equal($"TSK-1  Todo  Bug  {LongTitle}", Lines(await ws.InProject("Demo", "task", "list"))[0]);
        Assert.Equal("TSK-1  Todo  Bug  Очень длинн…", Lines(await ws.InProject("Demo", "task", "list", "--no-wrap"))[0]);
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
        Assert.All(board, x => Assert.True(TextWidth.Of(x) <= 30));

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
}
