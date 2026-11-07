using Tasker.Core.TaskSeries;

namespace Tasker.Core.Tasks.Search;

public sealed class TaskSearchService(ITaskStorage tasks, ISeriesStorage series, ITaskSearchIndex index)
{
    public const int DefaultLimit = 30;
    public const int MaxLimit = 200;
    private const int MaxCandidates = 1000;

    public async Task<TaskSearchHit[]> Search(Guid projectId, string text, int? limit = null, TaskFilter? filter = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        await Synchronize(projectId, ct);

        var query = TaskSearchQuery.Parse(text, await series.GetAll(projectId, ct));
        if (query.Words.Length == 0 && query.Number == null)
            return [];

        var ids = await index.Candidates(projectId, query, MaxCandidates, ct);
        if (ids.Length == 0)
            return [];

        var found = await tasks.GetAll(projectId, new TaskFilter { Ids = ids }, ct);
        var hits = found
            .Where(x => filter == null || filter.Matches(x))
            .Select(x => TaskSearchMatcher.Match(x, query))
            .OfType<TaskSearchHit>();
        return TaskSearchMatcher.Order(hits).Take(Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit)).ToArray();
    }

    private Task Synchronize(Guid projectId, CancellationToken ct) =>
        index.Update(projectId, async (known, token) =>
        {
            var stamps = await tasks.GetSearchStamps(projectId, token);
            var current = new HashSet<Guid>(stamps.Length);
            var changed = new Dictionary<Guid, string>();
            foreach (var stamp in stamps)
            {
                current.Add(stamp.Id);
                if (!known.TryGetValue(stamp.Id, out var existing) || existing != stamp.Stamp)
                    changed[stamp.Id] = stamp.Stamp;
            }

            var removed = known.Keys.Where(x => !current.Contains(x)).ToArray();
            if (changed.Count == 0 && removed.Length == 0)
                return TaskSearchChanges.None;

            var documents = new List<TaskSearchDocument>(changed.Count);
            if (changed.Count > 0)
            {
                var loaded = await tasks.GetAll(projectId, new TaskFilter { Ids = changed.Keys.ToArray() }, token);
                documents.AddRange(loaded.Select(x => new TaskSearchDocument(
                    x.Id, changed[x.Id], x.Title, x.Description ?? "", x.SeriesNumbers.Select(n => n.Number).ToArray())));
            }
            return new TaskSearchChanges(documents.ToArray(), removed);
        }, ct);
}
