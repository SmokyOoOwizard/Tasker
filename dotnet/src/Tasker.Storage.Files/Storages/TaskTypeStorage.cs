using Tasker.Core.Dto;
using Tasker.Core.Tasks;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>Одна сущность — из файла, списки — из индекса (см. <see cref="WorkspaceIndex"/>).</summary>
internal class TaskTypeStorage(TaskerDirectory directory, WorkspaceIndex index) : ITaskTypeStorage
{
    private readonly EntityFiles<TaskType> files = new(directory, index, EntityFolders.TaskTypes,
        x => x.Id, x => x.ProjectId, x => x.Name, TaskTypeFile.Read, TaskTypeFile.Write, TaskTypeFile.Update);

    public Task<TaskType?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => files.GetById(projectId, id, ct);

    public Task<TaskType[]> GetAll(Guid projectId, CancellationToken ct = default) =>
        index.All<TaskType>(Query(projectId), ct);

    public Task<ListDto<TaskType>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        index.GetRange<TaskType>(Query(projectId), page, ct);

    public Task<string> Add(TaskType type, CancellationToken ct = default) => files.Add(type, ct);

    public Task<string?> Update(TaskType type, string expectedVersion, CancellationToken ct = default) =>
        files.Update(type, expectedVersion, ct);

    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) =>
        files.Delete(projectId, id, expectedVersion, ct);

    private static IndexQuery Query(Guid projectId) => new(IndexKind.TaskType) { ProjectId = projectId };
}
