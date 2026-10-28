using Tasker.Core.Dto;
using Tasker.Core.TaskSeries;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>Одна серия — из файла, списки — из индекса, по префиксу (см. <see cref="WorkspaceIndex"/>).</summary>
internal class SeriesStorage(TaskerDirectory directory, WorkspaceIndex index) : ISeriesStorage
{
    private readonly EntityFiles<Series> files = new(directory, index, EntityFolders.Series,
        x => x.Id, x => x.ProjectId, x => x.Name, SeriesFile.Read, SeriesFile.Write, SeriesFile.Update);

    public Task<Series?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => files.GetById(projectId, id, ct);

    public Task<Series[]> GetAll(Guid projectId, CancellationToken ct = default) =>
        index.All<Series>(Query(projectId), ct);

    public Task<ListDto<Series>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        index.GetRange<Series>(Query(projectId), page, ct);

    public Task<string> Add(Series series, CancellationToken ct = default) => files.Add(series, ct);

    public Task<string?> Update(Series series, string expectedVersion, CancellationToken ct = default) =>
        files.Update(series, expectedVersion, ct);

    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) =>
        files.Delete(projectId, id, expectedVersion, ct);

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => files.CountUnreadable(projectId, ct);

    private static IndexQuery Query(Guid projectId) => new(IndexKind.Series) { ProjectId = projectId };
}
