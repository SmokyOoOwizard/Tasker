using Xunit;

namespace Tasker.Tests;

/// <summary><c>--all</c> в командах <c>list</c>: всё без ограничения по количеству, а не страница по 50.</summary>
public class ListAllTests
{
    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    /// <summary>Проект Demo (единственный: --project не нужен) с типом Bug и набором Flow; statuses — сколько лишних статусов создать.</summary>
    private static async Task Seed(TestWorkspace ws, int extraStatuses = 0)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("status", "create", "Todo"));
        await Ok(ws.Run("status", "create", "Done"));
        await Ok(ws.Run("status-set", "create", "Flow", "--status", "Todo", "Done"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Flow"));
        for (var i = 1; i <= extraStatuses; i++)
            await Ok(ws.Run("status", "create", $"S{i:000}"));
    }

    /// <summary>Строки данных: первая строка списка («Found …», TSK-107) не в счёт.</summary>
    private static string[] Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where((x, i) => i > 0 || !x.StartsWith("Found ")).ToArray();

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task All_shows_everything_where_a_page_shows_only_fifty(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws, extraStatuses: 53); // всего 55

        var page = await Ok(ws.Run("status", "list"));
        Assert.Equal(50, Lines(page.Out).Length);
        Assert.Equal("Found 55, shown 1-50 (use --offset/--limit)", page.Out.Split('\n')[0]);
        Assert.Empty(page.Err);

        var all = await Ok(ws.Run("status", "list", "--all"));
        Assert.Equal(55, Lines(all.Out).Length);
        Assert.Equal("Found 55", all.Out.Split('\n')[0]); // страницы нет: показано всё
        Assert.Empty(all.Err);
        Assert.Equal(55, Lines(all.Out).Distinct().Count());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task All_in_json_is_one_page_with_every_item(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws, extraStatuses: 53);

        var json = (await Ok(ws.Run("status", "list", "--all", "--json"))).Json;

        Assert.Equal(55, json["totalCount"]!.GetValue<int>());
        Assert.Equal(0, json["offset"]!.GetValue<int>());
        Assert.Equal(55, json["limit"]!.GetValue<int>());
        Assert.Equal(55, json["data"]!.AsArray().Count);

        // Без --all формат прежний: страница по умолчанию.
        var page = (await Ok(ws.Run("status", "list", "--json"))).Json;
        Assert.Equal(50, page["limit"]!.GetValue<int>());
        Assert.Equal(50, page["data"]!.AsArray().Count);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task All_cannot_be_combined_with_offset_or_limit(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        foreach (var extra in new[] { new[] { "--limit", "5" }, new[] { "--offset", "1" }, new[] { "--limit", "50" } })
        {
            var result = await ws.Run(["status", "list", "--all", .. extra]);
            Assert.Equal(1, result.Code);
            Assert.Contains("either --all or --offset/--limit", result.Err);
            Assert.Empty(result.Out);
        }

        // Сами по себе — как раньше.
        Assert.Single(Lines((await Ok(ws.Run("status", "list", "--limit", "1"))).Out));
        Assert.Single(Lines((await Ok(ws.Run("status", "list", "--offset", "1"))).Out));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task All_works_in_every_list_command(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(ws.Run("series", "create", "Tasks", "--prefix", "TSK"));
        await Ok(ws.Run("board", "create", "Main", "--status-set", "Flow", "--column", "All=Todo,Done"));
        await Ok(ws.Run("user", "create", "Ivan"));
        await Ok(ws.Run("agent", "create", "bot"));
        await Ok(ws.Run("task", "create", "Fix", "--type", "Bug", "--series", "TSK"));

        foreach (var command in new[] { "project", "status", "status-set", "task-type", "board", "series", "task", "user", "agent" })
        {
            var all = await Ok(ws.Run(command, "list", "--all"));
            Assert.NotEmpty(Lines(all.Out));
            Assert.Equal(Lines((await Ok(ws.Run(command, "list"))).Out), Lines(all.Out)); // мало элементов: то же, что страница
        }
    }

    [Fact]
    public async Task All_reads_several_pages_when_there_are_more_than_two_hundred_items()
    {
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        for (var i = 1; i <= 205; i++)
            await Ok(ws.Run("task", "create", $"Task {i:000}", "--type", "Bug", "--status", i % 5 == 0 ? "Done" : "Todo"));

        var all = (await Ok(ws.Run("task", "list", "--all", "--json"))).Json;
        var ids = all["data"]!.AsArray().Select(x => x!["id"]!.GetValue<Guid>()).ToArray();

        Assert.Equal(205, all["totalCount"]!.GetValue<int>());
        Assert.Equal(205, ids.Length);
        Assert.Equal(205, ids.Distinct().Count()); // ни пропусков, ни повторов на стыке страниц
        Assert.Equal(205, Lines((await Ok(ws.Run("task", "list", "--all"))).Out).Length);

        // Фильтр и --all вместе: считаются только подходящие.
        Assert.Equal(41, Lines((await Ok(ws.Run("task", "list", "--all", "--status", "Done"))).Out).Length);
        Assert.Equal(50, Lines((await Ok(ws.Run("task", "list"))).Out).Length);
    }
}
