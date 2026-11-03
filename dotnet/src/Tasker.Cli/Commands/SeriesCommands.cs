using System.CommandLine;
using Tasker.Cli.Completion;
using Tasker.Core;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;

namespace Tasker.Cli.Commands;

internal static class SeriesCommands
{
    /// <summary>Серия по id или по префиксу (регистр важен); затем — по короткому id (<see cref="ShortId"/>).</summary>
    internal static async Task<Series> Find(Context ctx, Guid projectId, string reference) =>
        await ctx.Get<SeriesService>().Find(projectId, reference, ctx.Ct)
            ?? Refs.ByIdPrefix(await ctx.Get<ISeriesStorage>().GetAll(projectId, ctx.Ct), reference, x => x.Id, "series")
            ?? throw new CliException($"No series '{reference}': give the id, the short id or the exact prefix (case matters)");

    /// <summary>
    /// Серии для фильтра списка («ИЛИ» внутри параметра): каждая — как в <see cref="Find"/>; повторы убираются, неизвестная — ошибка с перечнем префиксов.
    /// </summary>
    internal static async Task<Guid[]> FindDistinct(Context ctx, Guid projectId, IEnumerable<string> references)
    {
        var found = new List<Guid>();
        foreach (var reference in references)
        {
            var series = await ctx.Get<SeriesService>().Find(projectId, reference, ctx.Ct)
                ?? Refs.ByIdPrefix(await ctx.Get<ISeriesStorage>().GetAll(projectId, ctx.Ct), reference, x => x.Id, "series")
                ?? throw new CliException($"No series '{reference}': give the id, the short id or the exact prefix (case matters). " +
                    $"Available: {string.Join(", ", (await ctx.Get<ISeriesStorage>().GetAll(projectId, ctx.Ct)).Select(x => x.Prefix).Order(StringComparer.Ordinal))}");
            if (!found.Contains(series.Id))
                found.Add(series.Id);
        }

        return found.ToArray();
    }

    /// <summary>Префиксы серий проекта по id — чтобы показывать номера задач как <c>TSK-5</c>.</summary>
    internal static async Task<Dictionary<Guid, string>> Prefixes(Context ctx, Guid projectId) =>
        (await ctx.Get<ISeriesStorage>().GetAll(projectId, ctx.Ct)).ToDictionary(x => x.Id, x => x.Prefix);

    /// <summary>Ссылки задачи: <c>TSK-5</c>; серии нет — недействительная ссылка, помечается явно.</summary>
    internal static string[] References(TaskItem task, IReadOnlyDictionary<Guid, string> prefixes) =>
        References(task.SeriesNumbers, prefixes);

    internal static string[] References(IEnumerable<TaskSeriesNumber> numbers, IReadOnlyDictionary<Guid, string> prefixes) =>
        numbers
            // Порядок в хранилищах разный, а вывод должен быть одинаков: по префиксу, недействительные ссылки в конце.
            .OrderBy(x => prefixes.ContainsKey(x.SeriesId) ? 0 : 1)
            .ThenBy(x => prefixes.GetValueOrDefault(x.SeriesId), StringComparer.Ordinal)
            .ThenBy(x => x.SeriesId.ToString("D"), StringComparer.Ordinal)
            .Select(x => prefixes.TryGetValue(x.SeriesId, out var prefix) ? $"{prefix}-{x.Number}" : $"(invalid series {x.SeriesId}) #{x.Number}")
            .ToArray();

    public static Command Build(GlobalOptions g)
    {
        var name = Kit.Name("name", "Series name");
        var prefix = new Option<string>("--prefix") { Description = "Prefix of task references: Latin letters and digits, up to 20, case matters (TSK-5)", Required = true };
        var reference = new Argument<string>("series") { Description = "Series (id or exact prefix)" };
        var newName = new Option<string?>("--name") { Description = "New name" };
        var newPrefix = new Option<string?>("--prefix") { Description = "New prefix: references like TSK-5 written elsewhere stop working" };
        var expected = Kit.ExpectedVersion();

        var addSeries = new Argument<string>("series") { Description = "Series (id or exact prefix)" };
        var addTask = new Argument<string>("task") { Description = "Task (id or reference like TSK-5)" };
        var removeSeries = new Argument<string>("series") { Description = "Series (id or exact prefix)" };
        var removeNumber = new Argument<int>("number") { Description = "Number of the task in the series" }.NoSuggestions();
        var renumberSeries = new Argument<string>("series") { Description = "Series (id or exact prefix)" };
        var renumberTask = new Argument<string>("task") { Description = "Task (id or reference like TSK-5)" };
        var to = new Option<int?>("--to") { Description = "The new number (must be free). Default: the next free number (the maximum + 1)" };

        reference.Suggests(g, Sources.Series);
        addSeries.Suggests(g, Sources.Series);
        removeSeries.Suggests(g, Sources.Series);
        renumberSeries.Suggests(g, Sources.Series);
        addTask.Suggests(g, Sources.TaskReferences());
        renumberTask.Suggests(g, Sources.TaskReferences());
        
        return Kit.Group("series", "Task series: numbered references like TSK-5",
            Kit.Leaf(g, "create", "Creates a series", c =>
            {
                c.Arguments.Add(name);
                c.Options.Add(prefix);
            }, async (parse, ctx) =>
            {
                var series = await ctx.Get<SeriesService>().Create(
                    await ctx.ProjectId(), new CreateSeries(parse.GetRequiredValue(name), parse.GetRequiredValue(prefix)), ctx.Ct);
                ctx.Print(series, $"Created series '{series.Name}' ({series.Prefix}) {series.Id}");
            }),
            Kit.List(g, "Lists series",
                async (ctx, page) => await ctx.Get<ISeriesStorage>().GetRange(await ctx.ProjectId(), page, ctx.Ct),
                x => Kit.Row(x.Id, x.Prefix, x.Name)),
            Kit.Leaf(g, "get", "Shows a series", c => c.Arguments.Add(reference), async (parse, ctx) =>
            {
                var series = await Find(ctx, await ctx.ProjectId(), parse.GetRequiredValue(reference));
                ctx.Print(series, Kit.Fields(("id", series.Id), ("name", series.Name), ("prefix", series.Prefix), ("version", series.Version)));
            }),
            Kit.Leaf(g, "update", "Changes a series (a new prefix renames it)", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(newName);
                c.Options.Add(newPrefix);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                Kit.RequireChange(parse, newName, newPrefix);
                var projectId = await ctx.ProjectId();
                var series = await Find(ctx, projectId, parse.GetRequiredValue(reference));
                var updated = await ctx.Get<SeriesService>().Update(projectId, series.Id,
                    new UpdateSeries(parse.GetValue(newName), parse.GetValue(newPrefix), parse.GetValue(expected) ?? series.Version), ctx.Ct)
                    ?? throw new CliException($"No series '{series.Id}'");
                ctx.Print(updated, $"Updated series '{updated.Name}' ({updated.Prefix}) {updated.Id}");
            }),
            Kit.Leaf(g, "delete", "Deletes a series; its tasks stay and lose the series (and their numbers in it)", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var series = await Find(ctx, projectId, parse.GetRequiredValue(reference));
                var tasks = await ctx.Get<ITaskStorage>().Count(projectId, new TaskFilter { SeriesIds = [series.Id] }, ctx.Ct);
                if (!await ctx.Get<SeriesService>().Delete(projectId, series.Id, parse.GetValue(expected) ?? series.Version, ctx.Ct))
                    throw new CliException($"No series '{series.Id}'");
                ctx.Print(new { deleted = series.Id, tasksAffected = tasks },
                    Kit.Deleted("series", series.Name, series.Id) + (tasks > 0 ? $"\nRemoved the series from {tasks} task(s)" : ""));
            }),
            Kit.Leaf(g, "add-task", "Puts a task into a series: it gets the next number", c =>
            {
                c.Arguments.Add(addSeries);
                c.Arguments.Add(addTask);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var series = await Find(ctx, projectId, parse.GetRequiredValue(addSeries));
                var task = await TaskCommands.FindOne(ctx, projectId, parse.GetRequiredValue(addTask));
                var updated = await ctx.Get<TaskService>().AddToSeries(projectId, task.Id, series.Id, task.Version, ctx.Ct)
                    ?? throw new CliException($"No task '{task.Id}' or series '{series.Id}'");
                var number = updated.SeriesNumbers.First(x => x.SeriesId == series.Id).Number;
                ctx.Print(updated, $"Task '{updated.Title}' is {series.Prefix}-{number} {updated.Id}");
            }),
            Kit.Leaf(g, "remove-task", "Takes the task with this number out of a series (the number becomes free)", c =>
            {
                c.Arguments.Add(removeSeries);
                c.Arguments.Add(removeNumber);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var series = await Find(ctx, projectId, parse.GetRequiredValue(removeSeries));
                var number = parse.GetRequiredValue(removeNumber);
                var updated = await ctx.Get<TaskService>().RemoveFromSeriesByNumber(projectId, series.Id, number, ctx.Ct)
                    ?? throw new CliException($"No task {series.Prefix}-{number}");
                ctx.Print(updated, $"Removed task '{updated.Title}' from series {series.Prefix} (was {series.Prefix}-{number}) {updated.Id}");
            }),
            Kit.Leaf(g, "renumber-task", "Changes the number of a task in a series", c =>
            {
                c.Arguments.Add(renumberSeries);
                c.Arguments.Add(renumberTask);
                c.Options.Add(to);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var series = await Find(ctx, projectId, parse.GetRequiredValue(renumberSeries));
                var task = await TaskCommands.FindOne(ctx, projectId, parse.GetRequiredValue(renumberTask));
                var updated = await ctx.Get<TaskService>().Renumber(projectId, task.Id, series.Id, parse.GetValue(to), task.Version, ctx.Ct)
                    ?? throw new CliException($"Task '{task.Title}' {task.Id} is not in series {series.Prefix}");
                var number = updated.SeriesNumbers.First(x => x.SeriesId == series.Id).Number;
                ctx.Print(updated, $"Task '{updated.Title}' is {series.Prefix}-{number} {updated.Id}");
            }));
    }
}
