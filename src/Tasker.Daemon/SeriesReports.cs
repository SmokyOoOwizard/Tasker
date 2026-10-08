using Tasker.Core;
using Tasker.Core.Dto;
using Tasker.Core.Links;
using Tasker.Core.Projects;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;

namespace Tasker.Daemon;

/// <summary>Дубликат номера в серии: <c>TSK-5</c> есть у нескольких задач.</summary>
/// <param name="Reference">Ссылка как её пишут люди (<c>TSK-5</c>); серию нельзя определить — «series &lt;guid&gt;-5».</param>
public sealed record SeriesNumberProblem(string Reference, Guid SeriesId, int Number, Guid[] TaskIds);

/// <summary>Одинаковый префикс у нескольких серий проекта.</summary>
public sealed record SeriesPrefixProblem(string Prefix, Guid[] SeriesIds);

/// <summary>Цикл из связей типа, который циклов не допускает (после слияния веток git): <c>TSK-1 → TSK-2 → TSK-1</c>.</summary>
/// <param name="Path">Путь цикла как его читают люди (первая и последняя задача одна и та же).</param>
/// <param name="TaskIds">Задачи пути по порядку (первая повторяется в конце).</param>
public sealed record LinkCycleProblem(Guid TypeId, string TypeName, string Path, Guid[] TaskIds);

/// <summary>Что не в порядке с сериями и связями одного проекта. Сверка только сообщает, ничего не пишет.</summary>
/// <param name="TasksWithInvalidSeries">Сколько задач ссылаются на несуществующую серию (убирает <c>tasker cleanup</c>).</param>
/// <param name="UnreadableSeriesFiles">Сколько файлов серий не прочитано (конфликт слияния git).</param>
/// <param name="Error">Не удалось проверить проект: причина; остальные поля тогда пусты.</param>
/// <param name="TasksWithInvalidLinks">Сколько задач имеют связь на несуществующую задачу или тип связи (убирает <c>tasker cleanup</c>).</param>
/// <param name="UnreadableLinkFiles">Сколько файлов задач и типов связей не прочитано: связи тогда не проверяются.</param>
/// <param name="LinkCycles">Циклы из связей типов, которые циклов не допускают; <c>cleanup</c> их не убирает — связь снимает человек (<c>task unlink</c>).</param>
public sealed record ProjectSeriesHealth(
    Guid ProjectId,
    string ProjectName,
    SeriesNumberProblem[] NumberConflicts,
    SeriesPrefixProblem[] PrefixConflicts,
    int TasksWithInvalidSeries,
    int UnreadableSeriesFiles,
    string? Error = null,
    int TasksWithInvalidLinks = 0,
    int UnreadableLinkFiles = 0,
    LinkCycleProblem[]? LinkCycles = null)
{
    public LinkCycleProblem[] LinkCycles { get; init; } = LinkCycles ?? [];

    public bool HasProblems =>
        Error != null || NumberConflicts.Length > 0 || PrefixConflicts.Length > 0 || TasksWithInvalidSeries > 0 || UnreadableSeriesFiles > 0
        || TasksWithInvalidLinks > 0 || UnreadableLinkFiles > 0 || LinkCycles.Length > 0;

    /// <summary>Есть проблемы именно у связей (а не серий).</summary>
    public bool HasLinkProblems => TasksWithInvalidLinks > 0 || UnreadableLinkFiles > 0 || LinkCycles.Length > 0;
}

/// <summary>
/// Сводка по сериям для <c>tasker sync</c> и демона: проекты, у которых что-то не в порядке. Только чтение.
/// Сбой одного проекта не роняет остальные и сверку целиком: он попадает в <see cref="ProjectSeriesHealth.Error"/>.
/// </summary>
public static class SeriesReports
{
    public const string PrefixHint = "rename a series with 'tasker series update <id> --prefix ...'";

    public static async Task<ProjectSeriesHealth[]> Compute(
        IProjectStorage projects, ITaskStorage tasks, ISeriesStorage series, ILinkTypeStorage linkTypes, CancellationToken ct = default)
    {
        var all = new List<Project>();
        try
        {
            while (true)
            {
                var page = await projects.GetRange(null, new Page(all.Count, Page.MaxLimit), ct);
                all.AddRange(page.Data);
                if (page.Data.Length == 0 || all.Count >= page.TotalCount)
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [new ProjectSeriesHealth(Guid.Empty, "(projects)", [], [], 0, 0, $"cannot list projects: {e.Message}")];
        }

        var health = new SeriesHealthService(tasks, series);
        var linkHealth = new LinkHealthService(tasks, linkTypes);
        var result = new List<ProjectSeriesHealth>();
        foreach (var project in all)
        {
            try
            {
                var found = await health.Check(project.Id, ct);
                var links = await linkHealth.Check(project.Id, ct);
                var known = (await series.GetAll(project.Id, ct)).ToDictionary(x => x.Id, x => x.Prefix);

                var numbers = found.NumberConflicts
                    // Серия, которой нет, — это недействительная ссылка (её считает TasksWithInvalidSeries); при нечитаемых сериях не понять.
                    .Where(x => known.ContainsKey(x.SeriesId) || found.UnreadableSeriesFiles > 0)
                    .Select(x => new SeriesNumberProblem(
                        $"{(known.TryGetValue(x.SeriesId, out var prefix) ? prefix : $"series {x.SeriesId}")}-{x.Number}", x.SeriesId, x.Number, x.TaskIds))
                    .ToArray();
                var item = new ProjectSeriesHealth(project.Id, project.Name, numbers,
                    found.PrefixConflicts.Select(x => new SeriesPrefixProblem(x.Prefix, x.SeriesIds)).ToArray(),
                    found.TasksWithInvalidSeries, found.UnreadableSeriesFiles, null, links.TasksWithInvalidLinks, links.UnreadableFiles,
                    links.Cycles.Select(x => new LinkCycleProblem(x.TypeId, x.TypeName, x.Format(known), x.Path.Select(t => t.Id).ToArray())).ToArray());
                if (item.HasProblems)
                    result.Add(item);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                result.Add(new ProjectSeriesHealth(project.Id, project.Name, [], [], 0, 0, $"cannot check the series: {e.Message}"));
            }
        }

        return result.ToArray();
    }

    /// <summary>Строки проблем проекта для консоли (без заголовка проекта) с подсказками, что делать.</summary>
    public static IEnumerable<string> Describe(ProjectSeriesHealth health)
    {
        if (health.Error != null)
        {
            yield return health.Error;
            yield break;
        }

        foreach (var conflict in health.NumberConflicts)
            yield return $"{conflict.Reference}: tasks {string.Join(", ", conflict.TaskIds)} (run 'tasker cleanup --resolve-conflicts' or 'tasker series renumber-task')";
        foreach (var conflict in health.PrefixConflicts)
            yield return $"series rename required: series prefix '{conflict.Prefix}' is used by several series: {string.Join(", ", conflict.SeriesIds)}: {PrefixHint}";
        if (health.TasksWithInvalidSeries > 0)
            yield return $"{health.TasksWithInvalidSeries} task(s) refer to a series that does not exist (run 'tasker cleanup')";
        if (health.UnreadableSeriesFiles > 0)
            yield return $"{health.UnreadableSeriesFiles} series file(s) cannot be read (merge conflict?): fix them, 'tasker cleanup' skips until then";
    }

    /// <summary>Строки проблем связей проекта для консоли (без заголовка проекта) с подсказками, что делать.</summary>
    public static IEnumerable<string> DescribeLinks(ProjectSeriesHealth health)
    {
        if (health.TasksWithInvalidLinks > 0)
            yield return $"{health.TasksWithInvalidLinks} task(s) have a link to a task or link type that does not exist (run 'tasker cleanup')";
        if (health.UnreadableLinkFiles > 0)
            yield return $"{health.UnreadableLinkFiles} task or link type file(s) cannot be read (merge conflict?): fix them, 'tasker cleanup' does not check links until then";
        foreach (var cycle in health.LinkCycles)
            yield return CycleLine(cycle);
    }

    /// <summary>Цикл связей одной строкой с подсказкой: связь снимает человек, чистка её сама не удалит.</summary>
    public static string CycleLine(LinkCycleProblem cycle) =>
        $"Link cycle: {cycle.Path} (link type {cycle.TypeName}): remove one of the links with 'tasker task unlink' (cleanup does not remove links of a cycle)";

    /// <summary>Есть ли конфликт префиксов — «требуется переименование серии».</summary>
    public static bool NeedsRename(IEnumerable<ProjectSeriesHealth> health) => health.Any(x => x.PrefixConflicts.Length > 0);
}
