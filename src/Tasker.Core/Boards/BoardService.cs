using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;

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
/// <param name="FieldFilters">
/// Условия по полям каталога (И со статусами), текстом, как в <c>task list --field</c>: <c>Имя=значение</c>, <c>Имя!=значение</c>, <c>Имя&gt;=3</c>,
/// <c>Имя:set</c> и так далее (см. <see cref="TaskService.ParseFieldFilters"/>; имя поля — без учёта регистра, значение enum — название или id).
/// Хранятся по id поля и значения. null: у существующей колонки (с <paramref name="Id"/>) условия остаются прежними, у новой — их нет;
/// пустой список — условий нет.
/// </param>
public record BoardColumnInput(Guid? Id, string Name, Guid[] StatusIds, Dictionary<Guid, Guid>? DropStatuses, string[]? FieldFilters = null);

/// <param name="Version">Версия задачи, которую видел клиент: перенос меняет статус задачи.</param>
public record MoveTask(Guid TaskId, string? Version);

public class BoardService(
    IBoardStorage boards,
    IStatusSetStorage sets,
    ITaskTypeStorage taskTypes,
    ITaskStorage tasks,
    IFieldStorage fields,
    IFieldEnumStorage enums,
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

        // Колонки не переданы — оставляем текущие (условия по полям у них остаются: null у колонки с Id), но заново проверяем
        // их против (возможно, новых) наборов.
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

    private static string Subject(Board board) => $"Board '{board.Name}'";

    /// <summary>
    /// Перенос задачи в колонку: задача получает статус из правила переноса колонки для набора её типа.
    /// Задача, не подходящая под условия колонки по полям, не переносится: статус это не исправит (она сразу «пропала» бы с колонки),
    /// а поля задачи перенос не меняет — сначала нужно поправить поля самой задачи. Отказ называет условия.
    /// </summary>
    /// <exception cref="TaskerValidationException">Нет правила переноса для типа задачи или задача не подходит под условия колонки.</exception>
    /// <returns>null — нет доски, колонки или задачи.</returns>
    public async Task<TaskItem?> MoveTask(Guid projectId, Guid boardId, Guid columnId, MoveTask command, CancellationToken ct = default)
    {
        var board = await boards.GetById(projectId, boardId, ct);
        var column = board?.Columns.FirstOrDefault(x => x.Id == columnId);
        var task = await tasks.GetById(projectId, command.TaskId, ct);
        if (board == null || column == null || task == null)
            return null;

        var type = await taskTypes.GetById(projectId, task.TypeId, ct)
            ?? throw new TaskerNotFoundException($"Task type {task.TypeId} of task {task.Id} not found (the project may have been deleted)");

        if (!board.StatusSetIds.Contains(type.StatusSetId))
            throw new TaskerValidationException($"Task type '{type.Name}' is not on board '{board.Name}'");

        var statusId = column.GetDropStatus(type.StatusSetId)
            ?? throw new TaskerValidationException(
                $"Column '{column.Name}' has no drop rule for tasks of type '{type.Name}'");

        if (column.FieldConditions.Length > 0)
        {
            var conditions = ColumnFilters.ToConditions(
                column.FieldConditions, await taskTypes.GetAll(projectId, ct), (await fields.GetAll(projectId, ct)).ToDictionary(x => x.Id));
            if (!conditions.All(x => x.Matches(task)))
                throw new TaskerValidationException(
                    $"Task '{task.Title}' does not match the field conditions of column '{column.Name}' " +
                    $"({string.Join(", ", await DescribeFilters(projectId, column, ct))}): change the task's fields first, a move does not change them");
        }

        return await taskService.Update(projectId, task.Id, new UpdateTask(null, null, null, statusId, command.Version), ct);
    }

    /// <summary>
    /// Задачи колонки: тип задачи использует один из наборов статусов доски,
    /// а статус — один из статусов колонки, и выполнены все условия колонки по полям (<see cref="BoardColumn.FieldConditions"/>). null — нет такой доски или колонки.
    /// <paramref name="fieldFilters"/> — необязательный срез по значениям полей («Имя=значение», И; см. <see cref="TaskService.ParseFieldFilters"/>):
    /// это условие просмотра, в колонке оно не хранится; оно добавляется к условиям колонки (И). <paramref name="descriptionLength"/> — усечение описаний, см. <see cref="DescriptionPreview"/>.
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
        string? sort = null
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

        var filter = new TaskFilter
        {
            TypeIds = typeIds,
            StatusIds = column.StatusIds,
            FieldValues = column.FieldConditions.Length > 0
                ? ColumnFilters.ToConditions(column.FieldConditions, allTypes, (await fields.GetAll(projectId, ct)).ToDictionary(x => x.Id))
                : null
        };
        return await taskService.List(projectId, filter, fieldFilters, page, descriptionLength, ct, sort);
    }

    /// <summary>
    /// Условия колонки текстом, как их пишут клиенты (<c>Имя=значение</c>, <c>Имя&gt;=3</c>, <c>Имя:set</c>): поле и значение enum — названиями из каталога.
    /// </summary>
    public async Task<string[]> DescribeFilters(Guid projectId, BoardColumn column, CancellationToken ct = default)
    {
        if (column.FieldConditions.Length == 0)
            return [];
        var catalog = (await fields.GetAll(projectId, ct)).ToDictionary(x => x.Id);
        var enumerations = (await enums.GetAll(projectId, ct)).ToDictionary(x => x.Id);
        return column.FieldConditions.Select(x => ColumnFilters.Text(x, catalog, enumerations)).ToArray();
    }

    /// <summary>Общая проверка доски для создания и изменения.</summary>
    /// <param name="existingColumns">Колонки, которые уже есть у доски: только их Id можно передавать; у них остаются условия по полям, если не переданы новые.</param>
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
        var boardName = Validate.Name(name, "Board name");

        var setIds = Validate.Distinct(statusSetIds, "StatusSetIds");
        if (setIds.Length == 0)
            throw new TaskerValidationException("Board must include at least one status set");

        var projectSets = (await sets.GetAll(projectId, ct)).ToDictionary(x => x.Id);
        Validate.AllKnown(setIds, projectSets.Keys, "StatusSetIds");
        var boardStatusIds = setIds.SelectMany(x => projectSets[x].StatusIds).ToHashSet();

        if (columns is not { Length: > 0 })
            throw new TaskerValidationException("Board must have at least one column");

        // Условия колонок по полям: переданный текст разбирается по каталогу в id поля и значения; без текста у существующей колонки остаются
        // её условия. Хранимые условия проверяются тем же правилом, что введённые.
        var catalog = (await fields.GetAll(projectId, ct)).ToDictionary(x => x.Id);
        var enumerations = (await enums.GetAll(projectId, ct)).ToDictionary(x => x.Id);
        var filters = new ColumnFieldFilter[columns.Length][];
        for (var i = 0; i < columns.Length; i++)
        {
            var c = columns[i];
            var where = $"Columns[{i}].FieldFilters";
            if (c.FieldFilters == null)
                filters[i] = c.Id is { } keptId && existingColumns.TryGetValue(keptId, out var kept) ? kept.FieldConditions : [];
            else
            {
                try
                {
                    filters[i] = (await taskService.ParseFieldFilters(projectId, c.FieldFilters, ct) ?? [])
                        .Select(x => new ColumnFieldFilter(
                            // Колонка хранит условия по id поля каталога: собственные поля задач (у каждой задачи свой id) в неё не входят.
                            x.FieldId ?? throw new TaskerValidationException($"'{x.Name}' is not a field of the catalog: a column condition needs one"),
                            x.Operator, x.Value))
                        .ToArray();
                }
                catch (TaskerValidationException e)
                {
                    throw new TaskerValidationException($"{where}: {e.Message}");
                }
            }

            filters[i] = filters[i].Distinct().ToArray();
            foreach (var filter in filters[i])
                ColumnFilters.Validate(filter, catalog, enumerations, where);
        }

        var usedColumnIds = new HashSet<Guid>();
        var built = columns.Select((c, i) =>
        {
            var field = $"Columns[{i}]";

            if (c.Id is { } columnId)
            {
                if (!existingColumns.ContainsKey(columnId))
                    throw new TaskerValidationException($"{field}.Id: column {columnId} not found on the board");
                if (!usedColumnIds.Add(columnId))
                    throw new TaskerValidationException($"{field}.Id: column {columnId} is listed twice");
            }

            var columnName = Validate.Name(c.Name, $"{field}.Name");

            var statusIds = Validate.Distinct(c.StatusIds, $"{field}.StatusIds");
            Validate.AllKnown(statusIds, boardStatusIds, $"{field}.StatusIds (statuses of the board's status sets)");

            var drop = c.DropStatuses ?? [];
            foreach (var (setId, statusId) in drop)
            {
                if (!setIds.Contains(setId))
                    throw new TaskerValidationException($"{field}.DropStatuses: status set {setId} is not on the board");
                if (!projectSets[setId].StatusIds.Contains(statusId))
                    throw new TaskerValidationException($"{field}.DropStatuses: status {statusId} is not in status set {setId}");
                // Иначе перетянутая задача сразу «уехала» бы в другую колонку.
                if (!statusIds.Contains(statusId))
                    throw new TaskerValidationException($"{field}.DropStatuses: status {statusId} is not one of the column's statuses");
            }

            return new BoardColumn
            {
                Id = c.Id ?? Guid.NewGuid(),
                Name = columnName,
                StatusIds = statusIds,
                FieldConditions = filters[i],
                DropStatuses = new Dictionary<Guid, Guid>(drop)
            };
        }).ToArray();

        // Статус в двух колонках — задача показывалась бы на доске дважды, если только условия колонок по полям не исключают друг друга
        // (например, «Компонент=API» и «Компонент!=API»).
        if (ColumnFilters.FindOverlap(built, catalog) is var (first, _, shared, at))
            throw new TaskerValidationException(
                $"Columns[{at}].StatusIds: status {shared} is already used by column '{first.Name}', and the columns' field conditions do not " +
                "exclude each other (a status may be shared only by columns with conditions that cannot hold together, such as Field=a and Field!=a)");

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
