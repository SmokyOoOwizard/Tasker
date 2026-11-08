using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.Tasks.Query;

namespace Tasker.Core.Boards;

public record CreateBoard(string Name, Guid[] StatusSetIds, BoardColumnInput[] Columns);

/// <summary>
/// Поля null — не меняются. Columns заменяет колонки целиком (и порядок): колонка с Id сохраняет его,
/// без Id — создаётся новая. Колонки, которых нет в списке, удаляются.
/// Version — версия доски, которую видел клиент (см. <see cref="Versioning"/>).
/// </summary>
public record UpdateBoard(string? Name, Guid[]? StatusSetIds, BoardColumnInput[]? Columns, string? Version);

/// <param name="Id">Существующая колонка доски; null — новая колонка.</param>
/// <param name="StatusIds">Статусы из наборов доски, задачи с которыми попадают в колонку.</param>
/// <param name="DropStatuses">Набор статусов → статус, который получает перетянутая в колонку задача.</param>
public record BoardColumnInput(Guid? Id, string Name, Guid[] StatusIds, Dictionary<Guid, Guid>? DropStatuses, string? Query = null);

/// <param name="Version">Версия задачи, которую видел клиент: перенос меняет статус задачи.</param>
public record MoveTask(Guid TaskId, string? Version);

public class BoardService(
    IBoardStorage boards,
    IStatusSetStorage sets,
    IStatusStorage statuses,
    ITaskTypeStorage taskTypes,
    ITaskStorage tasks,
    IFieldStorage fields,
    TaskService taskService,
    TaskSeries.IWriteScope writes,
    Locks.EditLockService locks
)
{
    public async Task<Board> Create(Guid projectId, CreateBoard command, CancellationToken ct = default)
    {
        // Проверка по каталогу и запись — в одной секции записи: поле, которое использует колонка, не удалят между ними (см. FieldService.Delete).
        return await writes.Exclusive(projectId, async () =>
        {
            var board = await Build(projectId, Guid.NewGuid(), command.Name, command.StatusSetIds, command.Columns, new Dictionary<Guid, BoardColumn>(), ct);
            return board with { Version = await boards.Add(board, ct) };
        }, ct);
    }

    /// <returns>null — доски нет.</returns>
    public async Task<Board?> Update(Guid projectId, Guid id, UpdateBoard command, CancellationToken ct = default)
    {
        var board = await boards.GetById(projectId, id, ct);
        if (board == null)
            return null;
        await locks.EnsureWritable(Locks.LockedEntity.Board, board.Id, Subject(board), ct);
        var expected = Versioning.Check(board.Version, command.Version, Subject(board));

        var columns = command.Columns ?? board.Columns
            .Select(c => new BoardColumnInput(c.Id, c.Name, c.StatusIds, c.DropStatuses.ToDictionary()))
            .ToArray();

        return await writes.Exclusive<Board?>(projectId, async () =>
        {
            var updated = await Build(
                projectId,
                id,
                command.Name ?? board.Name,
                command.StatusSetIds ?? board.StatusSetIds,
                columns,
                board.Columns.ToDictionary(x => x.Id),
                ct
            );

            var version = await boards.Update(updated, expected, ct) ?? throw Versioning.Modified(Subject(board));
            return updated with { Version = version };
        }, ct);
    }

    /// <summary>На доски ничего не ссылается, поэтому удаление без проверок ссылок.</summary>
    /// <returns>false — доски нет.</returns>
    public async Task<bool> Delete(Guid projectId, Guid id, string? version, CancellationToken ct = default)
    {
        var board = await boards.GetById(projectId, id, ct);
        if (board == null)
            return false;
        await locks.EnsureWritable(Locks.LockedEntity.Board, board.Id, Subject(board), ct);
        var expected = Versioning.Check(board.Version, version, Subject(board));

        if (!await boards.Delete(projectId, id, expected, ct))
            throw Versioning.Modified(Subject(board));
        await locks.Forget(Locks.LockedEntity.Board, board.Id, ct);
        return true;
    }

    public const string ColumnQueryInvalidCode = "board.columnQueryInvalid";

    private static ErrorSubject Subject(Board board) => ErrorSubject.Board(board.Name);

    /// <summary>
    /// Перенос задачи в колонку: задача получает статус из правила переноса колонки для набора её типа.
    /// Задача, которая после переноса не подходила бы под запрос колонки, не переносится: перенос меняет только статус, остальное нужно поправить в самой задаче.
    /// Отказ называет запрос.
    /// </summary>
    /// <exception cref="TaskerValidationException">Нет правила переноса для типа задачи или задача не подходит под запрос колонки.</exception>
    /// <returns>null — нет доски, колонки или задачи.</returns>
    public async Task<TaskItem?> MoveTask(Guid projectId, Guid boardId, Guid columnId, MoveTask command, CancellationToken ct = default)
    {
        var board = await boards.GetById(projectId, boardId, ct);
        var column = board?.Columns.FirstOrDefault(x => x.Id == columnId);
        var task = await tasks.GetById(projectId, command.TaskId, ct);
        if (board == null || column == null || task == null)
            return null;

        var type = await taskTypes.GetById(projectId, task.TypeId, ct)
            ?? throw TaskerErrors.EntityNotFound($"Task type {task.TypeId} of task {task.Id} not found (the project may have been deleted)");

        if (!board.StatusSetIds.Contains(type.StatusSetId))
            throw TaskerErrors.BoardTaskTypeNotOnBoard(type.Name, board.Name);

        var statusId = column.GetDropStatus(type.StatusSetId)
            ?? throw TaskerErrors.BoardNoDropRule(column.Name, type.Name);

        if (await ColumnQuery(projectId, column, ct) is { Predicate: { } predicate }
            && (await tasks.GetAll(projectId, new TaskFilter { Ids = [task.Id], Predicate = AfterMove(predicate, task.StatusId, statusId) }, ct)).Length == 0)
            throw TaskerErrors.BoardTaskNotMatchColumn(task.Title, column.Name, column.Query!);

        return await taskService.Update(projectId, task.Id, new UpdateTask(null, null, null, statusId, command.Version), ct);
    }

    /// <summary>
    /// Задачи колонки: тип задачи использует один из наборов статусов доски,
    /// а статус — один из статусов колонки, и задача подходит под запрос колонки (<see cref="BoardColumn.Query"/>), а его сортировка задаёт порядок, если клиент не задал свою. null — нет такой доски или колонки.
    /// <paramref name="fieldFilters"/> — необязательный срез по значениям полей («Имя=значение», И; см. <see cref="TaskService.ParseFieldFilters"/>):
    /// это условие просмотра, в колонке оно не хранится; оно добавляется к запросу колонки (И), как и <paramref name="query"/>. <paramref name="descriptionLength"/> — усечение описаний, см. <see cref="DescriptionPreview"/>.
    /// <paramref name="sort"/> — порядок задач внутри колонки (<see cref="TaskService.ParseSort"/>); по умолчанию — по времени создания.
    /// </summary>
    public async Task<ListDto<TaskListItem>?> GetColumnTasks(
        Guid projectId,
        Guid boardId,
        Guid columnId,
        Page page,
        CancellationToken ct = default,
        IEnumerable<string>? fieldFilters = null,
        int descriptionLength = DescriptionPreview.Full,
        string? sort = null,
        string? query = null
    )
    {
        var board = await boards.GetById(projectId, boardId, ct);
        var column = board?.Columns.FirstOrDefault(x => x.Id == columnId);
        if (board == null || column == null)
            return null;

        var allTypes = await taskTypes.GetAll(projectId, ct);
        var typeIds = allTypes
            .Where(x => board.StatusSetIds.Contains(x.StatusSetId))
            .Select(x => x.Id)
            .ToArray();

        var parsed = await ColumnQuery(projectId, column, ct);
        var filter = new TaskFilter
        {
            TypeIds = typeIds,
            StatusIds = column.StatusIds,
            Predicate = parsed?.Predicate,
            Sort = parsed?.Sort
        };
        return await taskService.List(projectId, filter, fieldFilters, page, descriptionLength, ct, sort, query);
    }

    private static TaskPredicate AfterMove(TaskPredicate predicate, Guid current, Guid target) => predicate switch
    {
        TaskStatusIn x => new TaskStatusIn(x.Ids.Contains(target) ? [current] : []),
        TaskAllOf x => new TaskAllOf([.. x.Parts.Select(p => AfterMove(p, current, target))]),
        TaskAnyOf x => new TaskAnyOf([.. x.Parts.Select(p => AfterMove(p, current, target))]),
        TaskNot x => new TaskNot(AfterMove(x.Inner, current, target)),
        _ => predicate
    };

    private async Task<ParsedTaskQuery?> ColumnQuery(Guid projectId, BoardColumn column, CancellationToken ct)
    {
        try
        {
            return await taskService.ParseQuery(projectId, column.Query, ct);
        }
        catch (TaskerValidationException e)
        {
            throw TaskerErrors.BoardColumnQueryInvalid(column.Name, column.Query!, e.Message);
        }
    }

    /// <summary>Общая проверка доски для создания и изменения.</summary>
    /// <param name="existingColumns">Колонки, которые уже есть у доски: только их Id можно передавать; у них остаётся запрос, если не переданы новые.</param>
    private async Task<Board> Build(
        Guid projectId,
        Guid boardId,
        string? name,
        Guid[]? statusSetIds,
        BoardColumnInput[]? columns,
        IReadOnlyDictionary<Guid, BoardColumn> existingColumns,
        CancellationToken ct
    )
    {
        var boardName = Validate.Name(name, ErrorField.BoardName);

        var setIds = Validate.Distinct(statusSetIds, "StatusSetIds");
        if (setIds.Length == 0)
            throw TaskerErrors.BoardNoStatusSets();

        var projectSets = (await sets.GetAll(projectId, ct)).ToDictionary(x => x.Id);
        Validate.AllKnown(setIds, projectSets.Keys, "StatusSetIds");
        var boardStatusIds = setIds.SelectMany(x => projectSets[x].StatusIds).ToHashSet();
        var statusNames = (await statuses.GetAll(projectId, ct)).ToDictionary(x => x.Id, x => x.Name);
        string StatusName(Guid statusId) => statusNames.GetValueOrDefault(statusId) ?? statusId.ToString();

        if (columns is not { Length: > 0 })
            throw TaskerErrors.BoardNoColumns();

        var catalog = (await fields.GetAll(projectId, ct)).ToDictionary(x => x.Id);
        var queries = new string?[columns.Length];
        var predicates = new TaskPredicate?[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            var c = columns[i];
            var kept = c.Id is { } keptId && existingColumns.TryGetValue(keptId, out var existing) ? existing.Query : null;
            var text = c.Query == null ? kept : string.IsNullOrWhiteSpace(c.Query) ? null : c.Query.Trim();
            queries[i] = text;
            try
            {
                predicates[i] = (await taskService.ParseQuery(projectId, text, ct))?.Predicate;
            }
            catch (TaskerValidationException e)
            {
                if (text != kept)
                    throw e.Reworded($"Columns[{i}].Query: {e.Message}", ("columnIndex", i));
                predicates[i] = null;
            }
        }

        var usedColumnIds = new HashSet<Guid>();
        var built = columns.Select((c, i) =>
        {
            var field = $"Columns[{i}]";

            if (c.Id is { } columnId)
            {
                if (!existingColumns.ContainsKey(columnId))
                    throw TaskerErrors.Invalid($"{field}.Id: column {columnId} not found on the board");
                if (!usedColumnIds.Add(columnId))
                    throw TaskerErrors.Invalid($"{field}.Id: column {columnId} is listed twice");
            }

            var columnName = Validate.Name(c.Name, ErrorField.ColumnName, english: $"{field}.Name");

            var statusIds = Validate.Distinct(c.StatusIds, $"{field}.StatusIds");
            Validate.AllKnown(statusIds, boardStatusIds, $"{field}.StatusIds (statuses of the board's status sets)");

            var drop = c.DropStatuses ?? [];
            foreach (var (setId, statusId) in drop)
            {
                if (!setIds.Contains(setId))
                    throw TaskerErrors.BoardDropSetNotOnBoard(i, projectSets.TryGetValue(setId, out var dropSet) ? dropSet.Name : setId.ToString(),
                        $"{field}.DropStatuses: status set {setId} is not on the board");
                if (!projectSets[setId].StatusIds.Contains(statusId))
                    throw TaskerErrors.BoardDropStatusNotInSet(i, StatusName(statusId), projectSets[setId].Name,
                        $"{field}.DropStatuses: status {statusId} is not in status set {setId}");
                // Иначе перетянутая задача сразу «уехала» бы в другую колонку.
                if (!statusIds.Contains(statusId))
                    throw TaskerErrors.BoardDropStatusNotInColumn(i, StatusName(statusId),
                        $"{field}.DropStatuses: status {statusId} is not one of the column's statuses");
            }

            return new BoardColumn
            {
                Id = c.Id ?? Guid.NewGuid(),
                Name = columnName,
                StatusIds = statusIds,
                Query = queries[i],
                DropStatuses = new Dictionary<Guid, Guid>(drop)
            };
        }).ToArray();

        if (ColumnQueries.FindOverlap(built, predicates, catalog) is var (first, _, shared, at))
            throw TaskerErrors.BoardColumnOverlap(at, StatusName(shared), first.Name,
                $"Columns[{at}].StatusIds: status {shared} is already used by column '{first.Name}', and the columns' queries do not " +
                "exclude each other (a status may be shared only by columns whose queries cannot hold together, such as .Field.Component == \"API\" and .Field.Component != \"API\")");

        return new Board
        {
            Id = boardId,
            ProjectId = projectId,
            Name = boardName,
            StatusSetIds = setIds,
            Columns = built,
            Version = Versioning.New
        };
    }
}
