using Tasker.Core.Dto;
using Tasker.Core.Boards;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>Одна сущность — из файла, списки — из индекса (см. <see cref="WorkspaceIndex"/>).</summary>
internal class BoardStorage(TaskerDirectory directory, WorkspaceIndex index) : IBoardStorage
{
    private readonly EntityFiles<Board> files = new(directory, index, EntityFolders.Boards,
        x => x.Id, x => x.ProjectId, x => x.Name, BoardFile.Read, BoardFile.Write, BoardFile.Update);

    public Task<Board?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => files.GetById(projectId, id, ct);

    public Task<Board[]> GetAll(Guid projectId, CancellationToken ct = default) =>
        index.All<Board>(Query(projectId), ct);

    public Task<ListDto<Board>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        index.GetRange<Board>(Query(projectId), page, ct);

    public Task<string> Add(Board board, CancellationToken ct = default) => files.Add(board, ct);

    public Task<string?> Update(Board board, string expectedVersion, CancellationToken ct = default) =>
        files.Update(board, expectedVersion, ct);

    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) =>
        files.Delete(projectId, id, expectedVersion, ct);

    private static IndexQuery Query(Guid projectId) => new(IndexKind.Board) { ProjectId = projectId };
}
