using Tasker.Core.Boards;
using Tasker.Core.Dto;
using Tasker.Core.Tasks;

namespace Tasker.Core.Statuses;

public record CreateStatus(string Name, string Color, string? Description = null);

/// <summary>Поля null — не меняются; Description: пустая строка — очистить. Version — версия, которую видел клиент (см. <see cref="Versioning"/>).</summary>
public record UpdateStatus(string? Name, string? Color, string? Version, string? Description = null);

public class StatusService(IStatusStorage statuses, IStatusSetStorage sets, IBoardStorage boards, ITaskStorage tasks, Locks.EditLockService locks)
{
    public async Task<Status> Create(Guid projectId, CreateStatus command, CancellationToken ct = default)
    {
        var status = new Status
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = Validate.Name(command.Name, "Status name"),
            Color = Validate.Color(command.Color),
            Description = Validate.Description(command.Description) ?? "",
            Version = Versioning.New
        };

        return status with { Version = await statuses.Add(status, ct) };
    }

    /// <summary>Страница статусов по имени; описания усечены до <paramref name="descriptionLength"/> (см. <see cref="DescriptionPreview"/>).</summary>
    public async Task<ListDto<StatusListItem>> List(Guid projectId, Page page, int descriptionLength = DescriptionPreview.Full, CancellationToken ct = default)
    {
        DescriptionPreview.Check(descriptionLength);
        var found = await statuses.GetRange(projectId, page, ct);
        return new ListDto<StatusListItem>
        {
            TotalCount = found.TotalCount,
            Offset = found.Offset,
            Limit = found.Limit,
            Data = found.Data.Select(x => new StatusListItem(x, descriptionLength)).ToArray()
        };
    }

    /// <returns>null — статуса нет.</returns>
    public async Task<Status?> Update(Guid projectId, Guid id, UpdateStatus command, CancellationToken ct = default)
    {
        var status = await statuses.GetById(projectId, id, ct);
        if (status == null)
            return null;
        await locks.EnsureWritable(Locks.LockedEntity.Status, status.Id, Subject(status), ct);
        var expected = Versioning.Check(status.Version, command.Version, Subject(status));

        var updated = status with
        {
            Name = command.Name == null ? status.Name : Validate.Name(command.Name, "Status name"),
            Color = command.Color == null ? status.Color : Validate.Color(command.Color),
            Description = command.Description == null ? status.Description : Validate.Description(command.Description) ?? ""
        };

        var version = await statuses.Update(updated, expected, ct) ?? throw Versioning.Modified(Subject(status));
        return updated with { Version = version };
    }

    /// <summary>Нельзя удалить статус, который используют задачи, наборы статусов или колонки досок.</summary>
    /// <returns>false — статуса нет.</returns>
    public async Task<bool> Delete(Guid projectId, Guid id, string? version, CancellationToken ct = default)
    {
        var status = await statuses.GetById(projectId, id, ct);
        if (status == null)
            return false;
        await locks.EnsureWritable(Locks.LockedEntity.Status, status.Id, Subject(status), ct);
        var expected = Versioning.Check(status.Version, version, Subject(status));

        var usages = new Usages(Subject(status));
        usages.AddTasks(await tasks.Count(projectId, new TaskFilter { StatusIds = [id] }, ct));

        foreach (var set in await sets.GetAll(projectId, ct))
        {
            if (set.StatusIds.Contains(id))
                usages.Add($"status set '{set.Name}'");
        }

        foreach (var board in await boards.GetAll(projectId, ct))
        foreach (var column in board.Columns)
        {
            if (column.StatusIds.Contains(id) || column.DropStatuses.Values.Contains(id))
                usages.AddBoardColumn(board, column);
        }

        usages.ThrowIfAny("deleted");

        if (!await statuses.Delete(projectId, id, expected, ct))
            throw Versioning.Modified(Subject(status));
        await locks.Forget(Locks.LockedEntity.Status, status.Id, ct);
        return true;
    }

    private static string Subject(Status status) => $"Status '{status.Name}'";
}
