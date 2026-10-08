using Tasker.Core;
using Xunit;

namespace Tasker.Tests;

/// <summary>Тип задачи в текстовой строке <c>task list</c>: <c>ссылка  статус  тип  заголовок</c>.</summary>
public class TaskListTypeTests
{
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
        await Ok(P(ws, "status", "create", "Todo"));
        await Ok(P(ws, "status", "create", "Done"));
        await Ok(P(ws, "status-set", "create", "Flow", "--status", "Todo", "Done"));
        await Ok(P(ws, "task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(P(ws, "task-type", "create", "Story", "--status-set", "Flow"));
        await Ok(P(ws, "series", "create", "Tasks", "--prefix", "TSK"));
        await Ok(P(ws, "series", "create", "Proj", "--prefix", "PRJ"));
    }

    private static async Task<Guid> New(TestWorkspace ws, string title, string type, params string[] series) =>
        (await Ok(P(ws, ["task", "create", title, "--type", type, .. series.SelectMany(x => new[] { "--series", x }), "--json"]))).Json["id"]!.GetValue<Guid>();

    private static string[] Lines(CliResult r) => r.Data.Trim().Split('\n');

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Type_goes_after_status_for_tasks_with_and_without_series(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await New(ws, "One", "Bug", "TSK");
        await New(ws, "Two", "Story", "PRJ", "TSK");
        var loose = await New(ws, "Loose", "Bug");

        var lines = Lines(await Ok(P(ws, "task", "list")));

        Assert.Equal(
            ["TSK-1".PadRight(11) + "  Todo  Bug    One", "PRJ-1,TSK-2  Todo  Story  Two", $"{ShortId.Of(loose)}".PadRight(11) + "  Todo  Bug    Loose"],
            lines);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Filters_and_all_work_with_the_new_line(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await New(ws, "One", "Bug", "TSK");
        await New(ws, "Two", "Story", "TSK");
        await Ok(P(ws, "task", "update", "TSK-2", "--status", "Done"));

        Assert.Equal(["TSK-1  Todo  Bug  One"], Lines(await Ok(P(ws, "task", "list", "--type", "Bug"))));
        Assert.Equal(["TSK-2  Done  Story  Two"], Lines(await Ok(P(ws, "task", "list", "--status", "Done"))));
        Assert.Equal(["TSK-2  Done  Story  Two"], Lines(await Ok(P(ws, "task", "list", "--series", "TSK", "--type", "Story"))));
        Assert.Equal(["TSK-1  Todo  Bug    One", "TSK-2  Done  Story  Two"], Lines(await Ok(P(ws, "task", "list", "--all"))));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Json_is_unchanged_and_has_no_type_name(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await New(ws, "One", "Bug", "TSK");

        var item = (await Ok(P(ws, "task", "list", "--json"))).Json["data"]![0]!.AsObject();

        Assert.NotEqual(Guid.Empty, item["typeId"]!.GetValue<Guid>());
        Assert.NotEqual(Guid.Empty, item["statusId"]!.GetValue<Guid>());
        Assert.Equal("One", item["title"]!.GetValue<string>());
        Assert.DoesNotContain("Bug", item.ToJsonString());
    }

    [Fact]
    public async Task Unknown_type_leaves_the_type_cell_empty_and_the_columns_in_place()
    {
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        await New(ws, "Known", "Bug", "TSK");
        await New(ws, "Orphan", "Story", "TSK");
        // Тип удалили в другой ветке: файла типа нет, а задача на него ссылается.
        var storyId = (await Ok(P(ws, "task-type", "list", "--json"))).Json["data"]!.AsArray()
            .Single(x => x!["name"]!.GetValue<string>() == "Story")!["id"]!.GetValue<string>();
        File.Delete(FileFinder.Find(ws.Root, "task-types", Guid.Parse(storyId)));

        var lines = Lines(await Ok(P(ws, "task", "list")));

        Assert.Equal(["TSK-1  Todo  Bug  Known", "TSK-2  Todo       Orphan"], lines);
        Assert.DoesNotContain(storyId, string.Join('\n', lines));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task References_of_different_length_cyrillic_and_emoji_stay_aligned(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(P(ws, "status", "create", "Готово 🎉"));
        await Ok(P(ws, "status-set", "create", "Wide", "--status", "Готово 🎉", "Todo"));
        await Ok(P(ws, "task-type", "create", "Фича", "--status-set", "Wide"));
        for (var i = 1; i <= 10; i++)
            await New(ws, $"Задача {i}", i == 10 ? "Фича" : "Bug", "TSK");
        await Ok(P(ws, "task", "update", "TSK-10", "--status", "Готово 🎉"));

        var lines = Lines(await Ok(P(ws, "task", "list", "--all")));

        Assert.Equal(10, lines.Length);
        Assert.Equal("TSK-1   Todo       Bug   Задача 1", lines[0]);
        Assert.Equal("TSK-10  Готово 🎉  Фича  Задача 10", lines[9]);
        // Заголовок начинается в одной колонке терминала у всех строк.
        Assert.Single(lines.Select(x => Tasker.Cli.Table.Width(x[..x.IndexOf("Задача", StringComparison.Ordinal)])).Distinct());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Width_is_taken_from_the_shown_page_and_an_empty_list_prints_nothing(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var empty = await Ok(P(ws, "task", "list"));
        Assert.Equal("Found 0", empty.Out.Trim());
        Assert.Empty(empty.Data.Trim());
        for (var i = 1; i <= 10; i++)
            await New(ws, $"T{i}", "Bug", "TSK");

        Assert.Equal(["TSK-1  Todo  Bug  T1", "TSK-2  Todo  Bug  T2"], Lines(await Ok(P(ws, "task", "list", "--limit", "2"))));
        Assert.Equal(["TSK-9   Todo  Bug  T9", "TSK-10  Todo  Bug  T10"], Lines(await Ok(P(ws, "task", "list", "--offset", "8", "--limit", "2"))));
    }
}
