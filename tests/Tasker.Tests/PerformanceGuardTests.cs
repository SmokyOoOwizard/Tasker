using System.Diagnostics;
using System.CommandLine;
using Tasker.Cli;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Мягкие сторожа скорости консоли (TSK-92). Абсолютное время на разных машинах разное, и под нагрузкой других агентов оно «плывёт»,
/// поэтому проверяется не оно, а ОТНОШЕНИЯ внутри одного прогона с запасом в несколько раз: время вызова не должно расти ни с числом
/// уже выполненных вызовов, ни (сильно) с числом задач. Настоящий стенд с процессами и цифрами — <c>scripts/perf/bench.py</c>.
/// </summary>
[InProcess]
public class PerformanceGuardTests
{
    private static async Task Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
    }

    private static async Task<TestWorkspace> Seeded(int tasks)
    {
        var ws = TestWorkspace.Create("files");
        await Ok(ws.Run("project", "create", "Perf"));
        await Ok(ws.Run("status", "create", "Todo"));
        await Ok(ws.Run("status-set", "create", "Main", "--status", "Todo"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Main"));
        await Ok(ws.Run("series", "create", "Tasks", "--prefix", "TSK"));
        await AddTasks(ws, 0, tasks);
        return ws;
    }

    private static async Task AddTasks(TestWorkspace ws, int from, int count)
    {
        for (var i = from; i < from + count; i++)
            await Ok(ws.Run("task", "create", $"task {i}", "--type", "Bug", "--series", "TSK"));
    }

    /// <summary>Миллисекунды каждого из <paramref name="count"/> подряд идущих вызовов.</summary>
    private static async Task<double[]> Time(TestWorkspace ws, int count, params string[] args)
    {
        var times = new double[count];
        for (var i = 0; i < count; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await Ok(ws.Run(args));
            times[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        return times;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    [Fact]
    public async Task Call_time_does_not_grow_with_the_number_of_calls()
    {
        using var ws = await Seeded(100);
        await Time(ws, 10, "task", "get", "TSK-1"); // прогрев: JIT, кэши ОС

        var times = await Time(ws, 200, "task", "get", "TSK-1");
        var first = Median(times.Take(50));
        var last = Median(times.TakeLast(50));

        // В норме около 1; тройной запас — от шума соседних процессов, а не от накопления чего-либо на вызов.
        Assert.True(last < first * 3 + 20, $"the last 50 calls took {last:0.0} ms each (median), the first 50 — {first:0.0} ms");
    }

    [Fact]
    public async Task Call_time_depends_weakly_on_the_number_of_tasks()
    {
        using var ws = await Seeded(50);
        await Time(ws, 10, "task", "get", "TSK-1");
        var few = Median(await Time(ws, 30, "task", "get", "TSK-1"));

        await AddTasks(ws, 50, 450);
        var many = Median(await Time(ws, 30, "task", "get", "TSK-1"));

        // Каждый вызов сверяет индекс с файлами (O(число файлов) на stat): в 10 раз больше задач — не больше чем в несколько раз дольше.
        Assert.True(many < few * 4 + 30, $"500 tasks: {many:0.0} ms per call (median), 50 tasks: {few:0.0} ms");
    }

    [Fact]
    public async Task Write_time_does_not_grow_with_the_number_of_writes()
    {
        using var ws = await Seeded(20);
        await Time(ws, 5, "task", "create", "warm", "--type", "Bug", "--series", "TSK");

        var times = await Time(ws, 120, "task", "create", "x", "--type", "Bug", "--series", "TSK");
        var first = Median(times.Take(30));
        var last = Median(times.TakeLast(30));

        // Создание сверяет индекс и под блокировкой записи; растёт только число задач (до ~140), поэтому запас шире.
        Assert.True(last < first * 4 + 30, $"the last 30 creates took {last:0.0} ms each (median), the first 30 — {first:0.0} ms");
    }

    /// <summary>
    /// Для одной команды строится только её ветка дерева (остальное нужно справке и автодополнению): ветка должна совпадать с той же
    /// частью полного дерева, а имена в таблице <c>CliApp.BuildRoot</c> — с настоящими именами команд.
    /// </summary>
    [Fact]
    public void A_single_branch_tree_equals_that_branch_of_the_full_tree()
    {
        var output = new StringWriter();
        var full = CliApp.BuildRoot(output, output);
        Assert.NotEmpty(full.Subcommands);

        foreach (var command in full.Subcommands)
        {
            var one = CliApp.BuildRoot(output, output, only: command.Name);
            var branch = Assert.Single(one.Subcommands);
            Assert.Equal(command.Name, branch.Name);
            Assert.Equal(Describe(command), Describe(branch));
        }
    }

    [Fact]
    public void An_unknown_command_name_still_gets_the_whole_tree_for_suggestions()
    {
        var output = new StringWriter();
        var tree = CliApp.BuildRoot(output, output, only: "tsak");
        Assert.Equal(CliApp.BuildRoot(output, output).Subcommands.Count, tree.Subcommands.Count);
    }

    private static string Describe(Command command) =>
        $"{command.Name}({string.Join(",", command.Options.Select(x => x.Name).Order())};{string.Join(",", command.Arguments.Select(x => x.Name))})" +
        $"[{string.Join(";", command.Subcommands.OrderBy(x => x.Name).Select(Describe))}]";
}
