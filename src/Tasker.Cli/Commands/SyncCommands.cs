using System.CommandLine;
using Tasker.Core.Dto;
using Tasker.Core.Projects;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;
using Tasker.Core.Workspace;
using Tasker.Daemon;
using Tasker.Storage.Files;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Cli.Commands;

/// <summary>
/// <c>tasker sync</c>: привести кэш рабочей папки в соответствие с её файлами — после <c>git pull</c>, <c>checkout</c>,
/// слияния. Если демон MCP обслуживает эту папку, просим его (он уже держит индекс), иначе сверяем сами.
/// Команда возвращается, когда кэш актуален; а какие файлы прочитать не удалось (конфликты слияния git) — показывает сразу.
/// </summary>
internal static class SyncCommands
{
    public static Command Build(GlobalOptions g)
    {
        var quiet = g.Quiet;

        return Kit.Plain(g, "sync", "Brings the cache of a workspace up to date with its files (after git pull, checkout, merge)",
            _ => { }, async (parse, ctx) =>
            {
                var location = Locate(parse.GetValue(g.Workspace), parse.GetValue(g.Sqlite), parse.GetValue(quiet));
                if (location == null)
                    return;

                if (location.Kind == WorkspaceKind.Sqlite)
                {
                    if (!parse.GetValue(quiet))
                        ctx.Print(new { path = location.Path, via = (string?)null, problemCount = 0 }, "A SQLite workspace has no cache: nothing to sync");
                    return;
                }

                var (result, via) = await Sync(location, parse.GetValue(g.Workspace), ctx);
                Show(ctx, result, via, parse.GetValue(quiet));
            });
    }

    /// <returns>null — не рабочая папка, и молчим (режим хука).</returns>
    private static WorkspaceLocation? Locate(string? folder, string? sqlite, bool quiet)
    {
        WorkspaceLocation location;
        try
        {
            location = Session.Locate(new WorkspaceSettings(folder, sqlite));
        }
        catch (CliException) when (quiet && sqlite == null)
        {
            return null;
        }

        if (location.Kind == WorkspaceKind.Files && !Directory.Exists(Path.Combine(location.Path, TaskerDirectory.Name)))
        {
            if (quiet)
                return null;
            throw new CliException($"{location.Path} is not a Tasker workspace: there is no {TaskerDirectory.Name} folder");
        }

        return location;
    }

    private static async Task<(SyncResult Result, string Via)> Sync(WorkspaceLocation location, string? folder, Context ctx)
    {
        if (await DaemonClient.Sync(location, ctx.Ct) is { } served)
            return (served, "daemon");

        // Демон не работает или эту папку не обслуживает: открытие области уже сверяет индекс, повторная сверка — явная гарантия.
        await using var session = await Session.Open(new WorkspaceSettings(folder, null), ctx.Ct);
        var index = session.Get<IWorkspaceIndex>();
        await index.Rescan(ctx.Ct);
        var problems = await index.GetProblems(Page.Of(0, SyncResult.ProblemsShown), ctx.Ct);
        var series = await SeriesReports.Compute(session.Get<IProjectStorage>(), session.Get<ITaskStorage>(), session.Get<ISeriesStorage>(), session.Get<Tasker.Core.Links.ILinkTypeStorage>(), ctx.Ct);
        return (new SyncResult(location.Path, problems.TotalCount, problems.Data) { Series = series }, "direct");
    }

    private static void Show(Context ctx, SyncResult result, string via, bool quiet)
    {
        var lines = new List<string>();
        if (!quiet)
            lines.Add($"Synced {result.Path} (via {via})");
        if (result.ProblemCount > 0)
        {
            lines.Add($"{result.ProblemCount} file(s) cannot be read and are missing from the lists until fixed:");
            lines.AddRange(result.Problems.Select(x => $"  {x.Path}: {x.Error}"));
            if (result.ProblemCount > result.Problems.Length)
                lines.Add($"  … and {result.ProblemCount - result.Problems.Length} more (see the problems list of the workspace)");
        }

        foreach (var project in result.Series)
        {
            var seriesLines = SeriesReports.Describe(project).ToList();
            if (seriesLines.Count > 0)
            {
                lines.Add($"Series problems in project '{project.ProjectName}':");
                lines.AddRange(seriesLines.Select(x => $"  {x}"));
            }

            if (project.HasLinkProblems)
            {
                lines.Add($"Link problems in project '{project.ProjectName}':");
                lines.AddRange(SeriesReports.DescribeLinks(project).Select(x => $"  {x}"));
            }
        }

        if (quiet && result.ProblemCount == 0 && result.Series.Length == 0)
            return;
        ctx.Print(new { path = result.Path, via, problemCount = result.ProblemCount, problems = result.Problems, series = result.Series }, string.Join('\n', lines));
    }
}
