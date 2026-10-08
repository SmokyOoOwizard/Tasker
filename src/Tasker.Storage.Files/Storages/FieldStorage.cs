using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>Одно поле — из файла, списки — из индекса (см. <see cref="WorkspaceIndex"/>).</summary>
internal class FieldStorage(TaskerDirectory directory, WorkspaceIndex index) : IFieldStorage
{
    private readonly EntityFiles<FieldDefinition> files = new(directory, index, EntityFolders.Fields,
        x => x.Id, x => x.ProjectId, x => x.Name, FieldFile.Read, FieldFile.Write, FieldFile.Update);

    public Task<FieldDefinition?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => files.GetById(projectId, id, ct);

    public Task<FieldDefinition[]> GetAll(Guid projectId, CancellationToken ct = default) =>
        index.All<FieldDefinition>(Query(projectId), ct);

    public Task<ListDto<FieldDefinition>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        index.GetRange<FieldDefinition>(Query(projectId), page, ct);

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => files.CountUnreadable(projectId, ct);

    public Task<string> Add(FieldDefinition field, CancellationToken ct = default) => files.Add(field, ct);

    public Task<string?> Update(FieldDefinition field, string expectedVersion, CancellationToken ct = default) =>
        files.Update(field, expectedVersion, ct);

    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) =>
        files.Delete(projectId, id, expectedVersion, ct);

    private static IndexQuery Query(Guid projectId) => new(IndexKind.Field) { ProjectId = projectId };
}

/// <summary>Одно перечисление — из файла, списки — из индекса.</summary>
internal class FieldEnumStorage(TaskerDirectory directory, WorkspaceIndex index) : IFieldEnumStorage
{
    private readonly EntityFiles<FieldEnum> files = new(directory, index, EntityFolders.Enums,
        x => x.Id, x => x.ProjectId, x => x.Name, FieldEnumFile.Read, FieldEnumFile.Write, FieldEnumFile.Update);

    public Task<FieldEnum?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => files.GetById(projectId, id, ct);

    public Task<FieldEnum[]> GetAll(Guid projectId, CancellationToken ct = default) =>
        index.All<FieldEnum>(Query(projectId), ct);

    public Task<ListDto<FieldEnum>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        index.GetRange<FieldEnum>(Query(projectId), page, ct);

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => files.CountUnreadable(projectId, ct);

    public Task<string> Add(FieldEnum value, CancellationToken ct = default) => files.Add(value, ct);

    public Task<string?> Update(FieldEnum value, string expectedVersion, CancellationToken ct = default) =>
        files.Update(value, expectedVersion, ct);

    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) =>
        files.Delete(projectId, id, expectedVersion, ct);

    private static IndexQuery Query(Guid projectId) => new(IndexKind.FieldEnum) { ProjectId = projectId };
}
