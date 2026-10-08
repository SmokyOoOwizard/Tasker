using Tasker.Core.Dto;
using Tasker.Core.Tasks;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>Одна задача — из файла, списки и подсчёты — из индекса (см. <see cref="WorkspaceIndex"/>).</summary>
internal class TaskStorage(TaskerDirectory directory, WorkspaceIndex index) : ITaskStorage
{
    // Файл задачи называется по заголовку (EntityFileNames), поэтому ищется по id: индекс, затем имя — старое (Guid) и новое (суффикс id).
    private readonly EntityFiles<TaskItem> files = new(directory, index, EntityFolders.Tasks,
        x => x.Id, x => x.ProjectId, x => x.Title, TaskFile.Read, TaskFile.Write, TaskFile.Update);

    public Task<TaskItem?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => files.GetById(projectId, id, ct);

    public Task<TaskItem[]> FindByIdPrefix(Guid projectId, string idKey, CancellationToken ct = default) =>
        index.All<TaskItem>(new IndexQuery(IndexKind.Task) { ProjectId = projectId, IdPrefix = idKey }, ct);

    public Task<ListDto<TaskItem>> GetRange(Guid projectId, TaskFilter? filter, Page page, CancellationToken ct = default) =>
        index.GetRange<TaskItem>(Query(projectId, filter), page, ct);

    public Task<TaskItem[]> GetAll(Guid projectId, TaskFilter filter, CancellationToken ct = default) =>
        index.All<TaskItem>(Query(projectId, filter), ct);

    public Task<Guid[]> GetIds(Guid projectId, TaskFilter? filter, CancellationToken ct = default) =>
        index.Ids(Query(projectId, filter), ct);

    public Task<int> Count(Guid projectId, TaskFilter filter, CancellationToken ct = default) =>
        index.Count(Query(projectId, filter), ct);

    public Task<TaskItem[]> GetLinkedTo(Guid projectId, Guid targetId, CancellationToken ct = default) =>
        index.TasksLinkedTo(projectId, targetId, ct);

    public Task<Dictionary<Guid, Guid[]>> GetLinkTargets(Guid projectId, Guid typeId, Guid[] sourceIds, CancellationToken ct = default) =>
        index.LinkTargets(projectId, typeId, sourceIds, ct);

    public Task<Core.Links.LinkEdge[]> GetLinkEdges(Guid projectId, Guid typeId, CancellationToken ct = default) =>
        index.LinkEdges(projectId, typeId, ct);

    public Task<Dictionary<Guid, int>> CountLinkedTo(Guid projectId, Guid[] targetIds, CancellationToken ct = default) =>
        index.CountLinkedTo(projectId, targetIds, ct);

    public Task<OwnFieldKind[]> GetOwnFieldKinds(Guid projectId, string nameKey, CancellationToken ct = default) =>
        index.OwnFieldKinds(projectId, nameKey, ct);

    /// <summary>Новая задача — сразу под именем по заголовку.</summary>
    public Task<string> Add(TaskItem task, CancellationToken ct = default) => files.Add(task, ct);

    /// <summary>Файл переименовывается вместе с заголовком; задача со старым именем (Guid) получает новое при первой же записи.</summary>
    public Task<string?> Update(TaskItem task, string expectedVersion, CancellationToken ct = default) =>
        files.Update(task, expectedVersion, ct);

    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) =>
        files.Delete(projectId, id, expectedVersion, ct);

    // ---- серии и номера: по таблице task_series индекса, только задачи с прочитанным файлом ----

    public Task<int> GetMaxNumber(Guid projectId, Guid seriesId, CancellationToken ct = default) =>
        index.MaxNumber(projectId, seriesId, ct);

    public Task<TaskItem[]> FindByNumber(Guid projectId, Guid seriesId, int number, CancellationToken ct = default) =>
        index.TasksByNumber(projectId, seriesId, number, ct);

    public Task<Core.TaskSeries.NumberConflict[]> GetNumberConflicts(Guid projectId, CancellationToken ct = default) =>
        index.NumberConflicts(projectId, ct);

    public Task<TaskItem[]> GetWithInvalidLinks(Guid projectId, Guid[] knownLinkTypeIds, CancellationToken ct = default) =>
        index.TasksWithInvalidLinks(projectId, knownLinkTypeIds, ct);

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => files.CountUnreadable(projectId, ct);

    public Task<TaskItem[]> GetWithSeriesNotIn(Guid projectId, Guid[] knownSeriesIds, CancellationToken ct = default) =>
        index.TasksWithSeriesNotIn(projectId, knownSeriesIds, ct);

    private static IndexQuery Query(Guid projectId, TaskFilter? filter) => new(IndexKind.Task)
    {
        ProjectId = projectId,
        Ids = filter?.Ids,
        TypeIds = filter?.TypeIds,
        StatusIds = filter?.StatusIds,
        SeriesIds = filter?.SeriesIds,
        LinkTypeIds = filter?.LinkTypeIds,
        FieldIds = filter?.FieldIds,
        EnumIds = filter?.EnumIds,
        FieldValues = filter?.FieldValues,
        Sort = filter?.Sort
    };
}
