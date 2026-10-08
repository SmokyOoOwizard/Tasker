using Tasker.Core.Boards;
using Tasker.Core.Tasks;

namespace Tasker.Core.Statuses;

/// <param name="StatusIds">Статусы проекта в порядке отображения.</param>
public record CreateStatusSet(string Name, Guid[] StatusIds);

/// <summary>
/// Поля null — не меняются. StatusIds заменяет список целиком (и порядок).
/// Version — версия, которую видел клиент (см. <see cref="Versioning"/>).
/// </summary>
public record UpdateStatusSet(string? Name, Guid[]? StatusIds, string? Version);

public class StatusSetService(
    IStatusSetStorage sets,
    IStatusStorage statuses,
    ITaskTypeStorage types,
    IBoardStorage boards,
    ITaskStorage tasks,
    Locks.EditLockService locks
)
{
    public async Task<StatusSet> Create(Guid projectId, CreateStatusSet command, CancellationToken ct = default)
    {
        var set = new StatusSet
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = Validate.Name(command.Name, "Status set name"),
            StatusIds = await ValidateStatuses(projectId, command.StatusIds, ct),
            Version = Versioning.New
        };

        return set with { Version = await sets.Add(set, ct) };
    }

    /// <summary>
    /// Убрать статус из набора нельзя, если он ещё нужен: его используют задачи типов с этим набором
    /// или доски, в которые набор входит.
    /// </summary>
    /// <returns>null — набора нет.</returns>
    public async Task<StatusSet?> Update(Guid projectId, Guid id, UpdateStatusSet command, CancellationToken ct = default)
    {
        var set = await sets.GetById(projectId, id, ct);
        if (set == null)
            return null;
        await locks.EnsureWritable(Locks.LockedEntity.StatusSet, set.Id, Subject(set), ct);
        var expected = Versioning.Check(set.Version, command.Version, Subject(set));

        var name = command.Name == null ? set.Name : Validate.Name(command.Name, "Status set name");
        var statusIds = command.StatusIds == null ? set.StatusIds : await ValidateStatuses(projectId, command.StatusIds, ct);

        var removed = set.StatusIds.Except(statusIds).ToArray();
        if (removed.Length > 0)
            await EnsureCanRemove(projectId, set, removed, ct);

        var updated = set with { Name = name, StatusIds = statusIds };
        var version = await sets.Update(updated, expected, ct) ?? throw Versioning.Modified(Subject(set));
        return updated with { Version = version };
    }

    /// <summary>Нельзя удалить набор, который используют типы задач или доски.</summary>
    /// <returns>false — набора нет.</returns>
    public async Task<bool> Delete(Guid projectId, Guid id, string? version, CancellationToken ct = default)
    {
        var set = await sets.GetById(projectId, id, ct);
        if (set == null)
            return false;
        await locks.EnsureWritable(Locks.LockedEntity.StatusSet, set.Id, Subject(set), ct);
        var expected = Versioning.Check(set.Version, version, Subject(set));

        var usages = new Usages(Subject(set));
        foreach (var type in await types.GetAll(projectId, ct))
        {
            if (type.StatusSetId == id)
                usages.Add($"task type '{type.Name}'");
        }
        foreach (var board in await boards.GetAll(projectId, ct))
        {
            if (board.StatusSetIds.Contains(id))
                usages.Add($"board '{board.Name}'");
        }
        usages.ThrowIfAny("deleted");

        if (!await sets.Delete(projectId, id, expected, ct))
            throw Versioning.Modified(Subject(set));
        await locks.Forget(Locks.LockedEntity.StatusSet, set.Id, ct);
        return true;
    }

    private static string Subject(StatusSet set) => $"Status set '{set.Name}'";

    private async Task EnsureCanRemove(Guid projectId, StatusSet set, Guid[] removed, CancellationToken ct)
    {
        var names = (await statuses.GetAll(projectId, ct)).ToDictionary(x => x.Id, x => x.Name);
        var projectSets = (await sets.GetAll(projectId, ct)).ToDictionary(x => x.Id);
        var subject = removed.Length == 1
            ? $"Status '{names.GetValueOrDefault(removed[0])}'"
            : $"Statuses {string.Join(", ", removed.Select(x => $"'{names.GetValueOrDefault(x)}'"))}";
        var usages = new Usages(subject);

        var typeIds = (await types.GetAll(projectId, ct))
            .Where(x => x.StatusSetId == set.Id)
            .Select(x => x.Id)
            .ToArray();
        if (typeIds.Length > 0)
            usages.AddTasks(await tasks.Count(projectId, new TaskFilter { TypeIds = typeIds, StatusIds = removed }, ct));

        foreach (var board in await boards.GetAll(projectId, ct))
        {
            if (!board.StatusSetIds.Contains(set.Id))
                continue;

            // Статусы, которые после изменения останутся хоть в одном наборе доски.
            var stillOnBoard = board.StatusSetIds
                .Where(x => x != set.Id)
                .SelectMany(x => projectSets.TryGetValue(x, out var other) ? other.StatusIds : [])
                .ToHashSet();

            foreach (var column in board.Columns)
            {
                var dropsRemoved = column.GetDropStatus(set.Id) is { } drop && removed.Contains(drop);
                var columnLosesStatus = column.StatusIds.Any(x => removed.Contains(x) && !stillOnBoard.Contains(x));
                if (dropsRemoved || columnLosesStatus)
                    usages.AddBoardColumn(board, column);
            }
        }

        usages.ThrowIfAny($"removed from status set '{set.Name}'");
    }

    private async Task<Guid[]> ValidateStatuses(Guid projectId, Guid[]? ids, CancellationToken ct)
    {
        var statusIds = Validate.Distinct(ids, "StatusIds");
        if (statusIds.Length == 0)
            throw new TaskerValidationException("Status set must contain at least one status");

        var projectStatuses = await statuses.GetAll(projectId, ct);
        Validate.AllKnown(statusIds, projectStatuses.Select(x => x.Id), "StatusIds");
        return statusIds;
    }
}
