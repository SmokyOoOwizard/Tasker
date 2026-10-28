using Tasker.Core.Dto;
using Tasker.Core.Statuses;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>Одна сущность — из файла, списки — из индекса (см. <see cref="WorkspaceIndex"/>).</summary>
internal class StatusStorage(TaskerDirectory directory, WorkspaceIndex index) : IStatusStorage
{
    private readonly EntityFiles<Status> files = new(directory, index, EntityFolders.Statuses,
        x => x.Id, x => x.ProjectId, x => x.Name, StatusFile.Read, StatusFile.Write, StatusFile.Update);

    public Task<Status?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => files.GetById(projectId, id, ct);

    public Task<Status[]> GetAll(Guid projectId, CancellationToken ct = default) =>
        index.All<Status>(Query(projectId), ct);

    public Task<ListDto<Status>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        index.GetRange<Status>(Query(projectId), page, ct);

    public Task<string> Add(Status status, CancellationToken ct = default) => files.Add(status, ct);

    public Task<string?> Update(Status status, string expectedVersion, CancellationToken ct = default) =>
        files.Update(status, expectedVersion, ct);

    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) =>
        files.Delete(projectId, id, expectedVersion, ct);

    private static IndexQuery Query(Guid projectId) => new(IndexKind.Status) { ProjectId = projectId };
}
