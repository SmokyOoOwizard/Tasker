using Tasker.Core.Dto;
using Tasker.Core.Links;
using Tasker.Core.TaskSeries;

namespace Tasker.Core.Tasks;

internal static class TaskUsageRefs
{
    public const int Limit = 6;

    public static async Task<string[]> Load(ITaskStorage tasks, ISeriesStorage series, Guid projectId, TaskFilter filter, CancellationToken ct)
    {
        var found = await tasks.GetRange(projectId, filter, new Page(0, Limit), ct);
        if (found.Data.Length == 0)
            return [];
        var prefixes = (await series.GetAll(projectId, ct)).ToDictionary(x => x.Id, x => x.Prefix);
        return found.Data.Select(x => LinkCycle.Reference(x.Id, x.SeriesNumbers, prefixes)).ToArray();
    }

    public static async Task AddTo(Usages usages, ITaskStorage tasks, ISeriesStorage series, Guid projectId, TaskFilter filter, CancellationToken ct)
    {
        var count = await tasks.Count(projectId, filter, ct);
        if (count > 0)
            usages.AddTasks(count, await Load(tasks, series, projectId, filter, ct));
    }
}
