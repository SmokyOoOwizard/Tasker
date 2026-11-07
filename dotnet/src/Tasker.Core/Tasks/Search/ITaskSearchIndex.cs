namespace Tasker.Core.Tasks.Search;

public sealed record TaskSearchStamp(Guid Id, string Stamp);

public sealed record TaskSearchDocument(Guid Id, string Stamp, string Title, string Description, int[] Numbers);

public sealed record TaskSearchChanges(TaskSearchDocument[] Upserts, Guid[] Removed)
{
    public static readonly TaskSearchChanges None = new([], []);

    public bool IsEmpty => Upserts.Length == 0 && Removed.Length == 0;
}

public interface ITaskSearchIndex
{
    Task Update(
        Guid projectId,
        Func<IReadOnlyDictionary<Guid, string>, CancellationToken, Task<TaskSearchChanges>> changes,
        CancellationToken ct = default);

    Task<Guid[]> Candidates(Guid projectId, TaskSearchQuery query, int limit, CancellationToken ct = default);
}
