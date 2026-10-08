using Tasker.Core.Dto;
using Tasker.Core.Links;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>Один тип — из файла, списки — из индекса (см. <see cref="WorkspaceIndex"/>).</summary>
internal class LinkTypeStorage(TaskerDirectory directory, WorkspaceIndex index) : ILinkTypeStorage
{
    private readonly EntityFiles<LinkType> files = new(directory, index, EntityFolders.LinkTypes,
        x => x.Id, x => x.ProjectId, x => x.Name, LinkTypeFile.Read, LinkTypeFile.Write, LinkTypeFile.Update);

    public Task<LinkType?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => files.GetById(projectId, id, ct);

    public Task<LinkType[]> GetAll(Guid projectId, CancellationToken ct = default) =>
        index.All<LinkType>(Query(projectId), ct);

    public Task<ListDto<LinkType>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        index.GetRange<LinkType>(Query(projectId), page, ct);

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => files.CountUnreadable(projectId, ct);

    public Task<string> Add(LinkType type, CancellationToken ct = default) => files.Add(type, ct);

    public Task<string?> Update(LinkType type, string expectedVersion, CancellationToken ct = default) =>
        files.Update(type, expectedVersion, ct);

    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) =>
        files.Delete(projectId, id, expectedVersion, ct);

    private static IndexQuery Query(Guid projectId) => new(IndexKind.LinkType) { ProjectId = projectId };
}
