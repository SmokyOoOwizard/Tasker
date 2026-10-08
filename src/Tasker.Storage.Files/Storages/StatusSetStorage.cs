using Tasker.Core.Dto;
using Tasker.Core.Statuses;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>Одна сущность — из файла, списки — из индекса (см. <see cref="WorkspaceIndex"/>).</summary>
internal class StatusSetStorage(TaskerDirectory directory, WorkspaceIndex index) : IStatusSetStorage
{
    private readonly EntityFiles<StatusSet> files = new(directory, index, EntityFolders.StatusSets,
        x => x.Id, x => x.ProjectId, x => x.Name, StatusSetFile.Read, StatusSetFile.Write, StatusSetFile.Update);

    public Task<StatusSet?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => files.GetById(projectId, id, ct);

    public Task<StatusSet[]> GetAll(Guid projectId, CancellationToken ct = default) =>
        index.All<StatusSet>(Query(projectId), ct);

    public Task<ListDto<StatusSet>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        index.GetRange<StatusSet>(Query(projectId), page, ct);

    public Task<string> Add(StatusSet set, CancellationToken ct = default) => files.Add(set, ct);

    public Task<string?> Update(StatusSet set, string expectedVersion, CancellationToken ct = default) =>
        files.Update(set, expectedVersion, ct);

    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) =>
        files.Delete(projectId, id, expectedVersion, ct);

    private static IndexQuery Query(Guid projectId) => new(IndexKind.StatusSet) { ProjectId = projectId };
}
