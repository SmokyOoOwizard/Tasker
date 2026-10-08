using System.CommandLine;
using Tasker.Core.Projects;
using Tasker.Core.TaskSeries;
using Tasker.Daemon;
using Tasker.Daemon.Services;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Cli.Commands;

/// <summary>
/// <c>tasker cleanup</c>: единственная команда, которая правит данные серий и связей после слияния веток git — убирает недействительные
/// ссылки на серии и недействительные связи между задачами (на задачу или тип, которых нет) и (с <c>--resolve-conflicts</c>) решает дубликаты номеров. Работает напрямую с рабочей областью, не через демон.
/// </summary>
internal static class CleanupCommands
{
    /// <summary>Код выхода <c>--check</c>: чистка что-то изменила бы или что-то требует внимания.</summary>
    public const int NeedsCleanup = 2;

    public static Command Build(GlobalOptions g) => Build(g, new GitOperations(new SystemProcessRunner()));

    internal static Command Build(GlobalOptions g, GitOperations git)
    {
        var resolve = new Option<bool>("--resolve-conflicts") { Description = "Also give duplicate numbers new ones: the earliest task keeps its number, the others get the next numbers" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Write nothing, only show what would change" };
        var check = new Option<bool>("--check") { Description = "Write nothing; exit with code 2 if the cleanup would change something or something needs attention (for scripts and CI)" };

        return Kit.Leaf(g, "cleanup",
            "Removes references to missing series and links to missing tasks or link types after a git merge (with --resolve-conflicts also renumbers duplicate numbers); link cycles are only reported, remove one link with 'task unlink'",
            c =>
            {
                c.Options.Add(resolve);
                c.Options.Add(dryRun);
                c.Options.Add(check);
            }, async (parse, ctx) =>
            {
                var readOnly = parse.GetValue(dryRun) || parse.GetValue(check);
                if (!readOnly && parse.GetValue(g.Sqlite) == null)
                {
                    var folder = Session.Locate(new WorkspaceSettings(parse.GetValue(g.Workspace), null)).Path;
                    if (await git.InProgress(folder) is { } operation)
                        throw new CliException(
                            $"A git {operation} is in progress: cleanup would treat half-merged files as final. Finish or abort it, then run cleanup again "
                            + "(--dry-run and --check work meanwhile)");
                }

                var project = await ctx.OptionalProject();
                var projects = project == null ? await ctx.AllProjects() : [project];
                var options = new CleanupOptions(parse.GetValue(resolve), readOnly);

                var results = new List<ProjectCleanup>();
                foreach (var item in projects.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var report = await ctx.Get<CleanupService>().Run(item.Id, options, ctx.Ct);
                    var prefixes = await SeriesCommands.Prefixes(ctx, item.Id);
                    results.Add(new ProjectCleanup(item, report, prefixes));
                }

                var attention = results.Any(x => x.NeedsAttention);
                Show(ctx, results, parse.GetValue(dryRun), parse.GetValue(check), parse.GetValue(resolve), attention);

                if (parse.GetValue(check))
                    ctx.ExitCode = attention ? NeedsCleanup : 0;
                else if (!readOnly && results.Any(x => x.Report.Skipped))
                {
                    // Ничего не сделано, и скрипт должен это заметить.
                    ctx.ExitCode = 1;
                    ctx.Error.WriteLine("Error: the cleanup was skipped, nothing was changed");
                }
                else if (!readOnly && results.Any(x => x.Report.LinksSkipReason != null))
                {
                    // Серии очищены, а связи не проверялись: скрипт должен это заметить.
                    ctx.ExitCode = 1;
                    ctx.Error.WriteLine("Error: the links between tasks were not checked, the rest was cleaned up (see above)");
                }
            });
    }

    private sealed record ProjectCleanup(Project Project, CleanupReport Report, Dictionary<Guid, string> Prefixes)
    {
        public bool NeedsAttention =>
            Report.Changes.Length > 0 || Report.RemainingNumberConflicts.Length > 0 || Report.PrefixConflicts.Length > 0 || Report.Skipped
            || Report.LinksSkipReason != null || Report.LinkCycles.Length > 0;

        public string Label(Guid seriesId, int number) =>
            $"{(Prefixes.TryGetValue(seriesId, out var prefix) ? prefix : $"series {seriesId}")}-{number}";
    }

    private static void Show(Context ctx, List<ProjectCleanup> results, bool dryRun, bool check, bool resolve, bool attention)
    {
        var readOnly = dryRun || check;
        var changes = results.Sum(x => x.Report.Changes.Length);

        var lines = new List<string>();
        if (readOnly)
            lines.Add("Nothing is written (dry run):");
        foreach (var result in results.Where(x => x.NeedsAttention))
        {
            lines.Add($"Project '{result.Project.Name}':");
            lines.AddRange(result.Report.Changes.Select(x => $"  {x.Description}"));
            foreach (var conflict in result.Report.RemainingNumberConflicts)
                lines.Add($"  {result.Label(conflict.SeriesId, conflict.Number)}: tasks {string.Join(", ", conflict.TaskIds)} "
                    + "(duplicate number: run 'tasker cleanup --resolve-conflicts' or 'tasker series renumber-task')");
            foreach (var conflict in result.Report.PrefixConflicts)
                lines.Add($"  series prefix '{conflict.Prefix}' is used by several series: {string.Join(", ", conflict.SeriesIds)}: "
                    + "rename with 'tasker series update <id> --prefix ...'");
            // Циклы связей чистка не убирает: какую связь снять, решает человек.
            foreach (var cycle in result.Report.LinkCycles)
                lines.Add($"  link cycle: {cycle.Format(result.Prefixes)} (link type {cycle.TypeName}): remove one of the links with 'tasker task unlink' (cleanup does not remove links of a cycle)");
            if (result.Report.Skipped)
                lines.Add($"  skipped: {result.Report.SkipReason}");
            if (result.Report.LinksSkipReason != null)
                lines.Add($"  links skipped: {result.Report.LinksSkipReason}");
        }

        if (!attention)
            lines.Add("Nothing to clean up");
        else if (changes > 0)
            lines.Add(readOnly ? $"{changes} change(s) would be made" : $"{changes} change(s) made");

        ctx.Print(new
        {
            dryRun,
            check,
            resolveConflicts = resolve,
            needsAttention = attention,
            changeCount = changes,
            projects = results.Select(x => new
            {
                projectId = x.Project.Id,
                project = x.Project.Name,
                changes = x.Report.Changes,
                remainingNumberConflicts = x.Report.RemainingNumberConflicts.Select(c => new
                {
                    seriesId = c.SeriesId,
                    reference = x.Label(c.SeriesId, c.Number),
                    number = c.Number,
                    taskIds = c.TaskIds
                }),
                prefixConflicts = x.Report.PrefixConflicts,
                skipped = x.Report.Skipped,
                skipReason = x.Report.SkipReason,
                linksSkipReason = x.Report.LinksSkipReason,
                linkCycles = x.Report.LinkCycles.Select(c => new
                {
                    typeId = c.TypeId,
                    linkType = c.TypeName,
                    path = c.Format(x.Prefixes),
                    taskIds = c.Path.Select(t => t.Id)
                })
            })
        }, string.Join('\n', lines));
    }
}
