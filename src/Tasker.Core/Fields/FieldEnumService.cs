using Tasker.Core.Boards;
using Tasker.Core.Dto;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;

namespace Tasker.Core.Fields;

/// <param name="Values">Названия значений в порядке отображения; хотя бы одно, без повторов (без учёта регистра).</param>
public record CreateFieldEnum(string Name, string[] Values);

/// <summary>Значение в правке перечисления: <paramref name="Id"/> — существующее значение (его можно переименовать), null — новое.</summary>
public record FieldEnumValueInput(Guid? Id, string Name);

/// <summary>
/// Что сделать у задач со значением перечисления, которое удаляют, пока оно выбрано хотя бы у одной задачи. Задан ровно один вариант.
/// </summary>
/// <param name="Clear">Убрать значение у всех задач, которые его используют; условие колонки доски на это значение — убрать из колонки.</param>
/// <param name="ReassignTo">Подставить вместо него это значение (id оставшегося значения того же перечисления) у всех таких задач;
/// у поля с несколькими значениями — без повторов. Условие колонки доски на это значение переключается на то же значение.</param>
public record RemovedEnumValues(bool Clear = false, Guid? ReassignTo = null);

/// <summary>
/// Поля null — не меняются. Values заменяет список целиком (и порядок): значения с id остаются теми же значениями
/// (переименование), без id — добавляются, пропавшие — удаляются. Version — версия, которую видел клиент (см. <see cref="Versioning"/>).
/// <para>
/// Удаляемое значение, выбранное у задач или входящее в условие колонки доски, — только с явным выбором <paramref name="Removed"/>
/// (убрать или переназначить); без выбора — ошибка <c>In use</c> с числом задач и колонками. Выбор нужен только когда значение
/// где-то используется и применяется ко всем удаляемым значениям сразу. Обязательное поле, оставшееся без значения после «убрать», у задачи остаётся пустым: это не правка
/// пользователем, обязательность не блокирует.
/// </para>
/// </summary>
public record UpdateFieldEnum(string? Name, FieldEnumValueInput[]? Values, string? Version, RemovedEnumValues? Removed = null);

/// <summary>
/// Перечисления проекта. Уникальность имён проверяется под <see cref="IWriteScope"/>, как у полей.
/// </summary>
public class FieldEnumService(
    IFieldEnumStorage enums, IFieldStorage fields, ITaskTypeStorage types, ITaskStorage tasks, IBoardStorage boards, IWriteScope writes, TimeProvider time,
    Locks.EditLockService locks)
{
    private const int CascadeAttempts = 3;

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => enums.CountUnreadable(projectId, ct);

    public Task<FieldEnum[]> GetAll(Guid projectId, CancellationToken ct = default) => enums.GetAll(projectId, ct);

    public Task<ListDto<FieldEnum>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        enums.GetRange(projectId, page, ct);

    /// <returns>null — перечисления нет.</returns>
    public Task<FieldEnum?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => enums.GetById(projectId, id, ct);

    /// <summary>Перечисление по id или по имени (без учёта регистра).</summary>
    /// <returns>null — такого перечисления нет.</returns>
    public async Task<FieldEnum?> Find(Guid projectId, string reference, CancellationToken ct = default)
    {
        var text = reference?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        var all = await enums.GetAll(projectId, ct);
        if (Guid.TryParse(text, out var id) && all.FirstOrDefault(x => x.Id == id) is { } byId)
            return byId;

        var byName = all.Where(x => string.Equals(x.Name, text, StringComparison.OrdinalIgnoreCase)).ToArray();
        return byName.Length switch
        {
            0 => null,
            1 => byName[0],
            _ => throw new TaskerValidationException(
                $"Several enums are named '{text}', use the id: {string.Join(", ", byName.Select(x => x.Id))}")
        };
    }

    /// <exception cref="TaskerConflictException">Перечисление с таким именем уже есть.</exception>
    public async Task<FieldEnum> Create(Guid projectId, CreateFieldEnum command, CancellationToken ct = default)
    {
        var name = Validate.Name(command.Name, "Enum name");
        var values = ValidateValues((command.Values ?? []).Select(x => new FieldEnumValueInput(null, x)).ToArray(), []);

        return await writes.Exclusive(projectId, async () =>
        {
            await EnsureNameFree(projectId, name, null, ct);

            var value = new FieldEnum { Id = Guid.NewGuid(), ProjectId = projectId, Name = name, Values = values, Version = Versioning.New };
            return value with { Version = await enums.Add(value, ct) };
        }, ct);
    }

    /// <remarks>
    /// Удаление значения, выбранного у задач, требует явного выбора («убрать у задач» или «переназначить», <see cref="UpdateFieldEnum.Removed"/>),
    /// иначе — ошибка <c>In use</c>. Задачи и перечисление меняются в одной атомарной секции записи (в БД — одной транзакцией,
    /// в файлах — под общей блокировкой записи: сначала задачи, каждая по версии и с повтором, потом перечисление;
    /// если что-то не вышло, операцию можно повторить с тем же выбором).
    /// </remarks>
    /// <returns>
    /// null — перечисления нет. Иначе перечисление и число переписанных задач (<see cref="CascadeResult{T}.AffectedTasks"/>):
    /// у которых убрали или заменили удалённое значение; 0 — удалённых значений нет или они нигде не выбраны.
    /// </returns>
    public async Task<CascadeResult<FieldEnum>?> Update(Guid projectId, Guid id, UpdateFieldEnum command, CancellationToken ct = default)
    {
        var current = await enums.GetById(projectId, id, ct);
        if (current == null)
            return null;
        await locks.EnsureWritable(Locks.LockedEntity.Enum, current.Id, Subject(current), ct);
        var expected = Versioning.Check(current.Version, command.Version, Subject(current));

        var name = command.Name == null ? current.Name : Validate.Name(command.Name, "Enum name");
        var values = command.Values == null ? current.Values : ValidateValues(command.Values, current.Values);
        var removed = current.Values.Select(x => x.Id).Except(values.Select(x => x.Id)).ToArray();
        ValidateChoice(command.Removed, values, removed);

        // Выбор сделан и значения где-то выбраны: ждём блокировки затрагиваемых задач до секции записи (см. EditLockService.WaitUntilWritable);
        // внутри секции они проверяются ещё раз, без ожидания.
        if (removed.Length > 0 && command.Removed != null)
        {
            var removedKeys = removed.Select(x => x.ToString("D")).ToHashSet();
            var (holders, _) = await FindHolders(projectId, current, removedKeys, ct);
            await TaskRewrites.WaitForLocks(locks, holders.Values, ct);
            await locks.WaitUntilWritable(
                (await FindBoards(projectId, current, removedKeys, ct)).Select(x => (Locks.LockedEntity.Board, x.Id, BoardSubject(x))).ToArray(), null, ct);
        }

        return await writes.Exclusive<CascadeResult<FieldEnum>?>(projectId, async () =>
        {
            if (name != current.Name)
                await EnsureNameFree(projectId, name, id, ct);
            var (affected, columns) = removed.Length > 0 ? await ApplyRemoval(projectId, current, removed, command.Removed, ct) : (0, 0);

            var updated = current with { Name = name, Values = values };
            var version = await enums.Update(updated, expected, ct) ?? throw Versioning.Modified(Subject(current));
            return new CascadeResult<FieldEnum>(updated with { Version = version }, affected, columns);
        }, ct);
    }

    /// <summary>Нельзя удалить перечисление, на которое ссылаются поля (каталога или собственные поля задач).</summary>
    /// <returns>false — перечисления нет.</returns>
    public async Task<bool> Delete(Guid projectId, Guid id, string? version, CancellationToken ct = default)
    {
        var current = await enums.GetById(projectId, id, ct);
        if (current == null)
            return false;
        await locks.EnsureWritable(Locks.LockedEntity.Enum, current.Id, Subject(current), ct);
        var expected = Versioning.Check(current.Version, version, Subject(current));

        return await writes.Exclusive(projectId, async () =>
        {
            var usages = new Usages(Subject(current));
            foreach (var field in await fields.GetAll(projectId, ct))
            {
                if (field.EnumId == id)
                    usages.Add($"field '{field.Name}'");
            }
            var own = await tasks.Count(projectId, new TaskFilter { EnumIds = [id] }, ct);
            if (own > 0)
                usages.Add($"own fields of {own} task(s)");
            usages.ThrowIfAny("deleted");

            if (!await enums.Delete(projectId, id, expected, ct))
                throw Versioning.Modified(Subject(current));
            await locks.Forget(Locks.LockedEntity.Enum, current.Id, ct);
            return true;
        }, ct);
    }

    /// <summary>Выбор «что с задачами» — ровно один вариант; «переназначить» — на значение, которое остаётся в перечислении.</summary>
    private static void ValidateChoice(RemovedEnumValues? choice, FieldEnumValue[] remaining, Guid[] removed)
    {
        if (choice == null)
            return;
        if (choice.Clear == (choice.ReassignTo != null))
            throw new TaskerValidationException("Removed: choose exactly one of clear (remove the values from the tasks) or reassign (give another value)");
        if (choice.ReassignTo is { } target && (!remaining.Any(x => x.Id == target) || removed.Contains(target)))
            throw new TaskerValidationException($"Removed: the value to reassign to {target} is not among the values the enum keeps");
    }

    /// <summary>Задачи, где выбраны убираемые значения (у полей каталога с этим перечислением и у собственных полей), и признак «поле использует их».</summary>
    private async Task<(Dictionary<Guid, TaskItem> Holders, Func<TaskField, bool> Uses)> FindHolders(
        Guid projectId, FieldEnum current, HashSet<string> removedKeys, CancellationToken ct)
    {
        var catalogFields = (await fields.GetAll(projectId, ct)).Where(x => x.EnumId == current.Id).Select(x => x.Id).ToHashSet();

        bool Uses(TaskField field) =>
            (field.Own == null ? catalogFields.Contains(field.FieldId) : field.Own.EnumId == current.Id)
            && field.Values.Any(removedKeys.Contains);

        var holders = new Dictionary<Guid, TaskItem>();
        foreach (var filter in new[] { new TaskFilter { FieldIds = catalogFields.ToArray() }, new TaskFilter { EnumIds = [current.Id] } })
        {
            foreach (var task in await tasks.GetAll(projectId, filter, ct))
            {
                if (task.Fields.Any(Uses))
                    holders[task.Id] = task;
            }
        }
        return (holders, Uses);
    }

    /// <summary>Поля каталога с этим перечислением: на их значения ссылаются условия колонок досок.</summary>
    private async Task<HashSet<Guid>> EnumFieldIds(Guid projectId, FieldEnum current, CancellationToken ct) =>
        (await fields.GetAll(projectId, ct)).Where(x => x.EnumId == current.Id).Select(x => x.Id).ToHashSet();

    /// <summary>Доски, у колонок которых есть условие на убираемые значения.</summary>
    private async Task<Board[]> FindBoards(Guid projectId, FieldEnum current, HashSet<string> removedKeys, CancellationToken ct)
    {
        var fieldIds = await EnumFieldIds(projectId, current, ct);
        return (await boards.GetAll(projectId, ct))
            .Where(b => b.Columns.Any(c => c.FieldConditions.Any(f => UsesValue(f, fieldIds, removedKeys))))
            .ToArray();
    }

    private static bool UsesValue(ColumnFieldFilter filter, HashSet<Guid> fieldIds, HashSet<string> removedKeys) =>
        fieldIds.Contains(filter.FieldId) && filter.Value != null && removedKeys.Contains(filter.Value);

    private static string BoardSubject(Board board) => $"Board '{board.Name}'";

    /// <summary>
    /// Значения, которые убирают из перечисления, и места, где они выбраны: задачи (у полей каталога с этим перечислением и у собственных
    /// полей) и условия колонок досок. Выбрано хотя бы где-то — нужен выбор; без него ошибка <c>In use</c>.
    /// </summary>
    /// <returns>Сколько задач и колонок досок переписано.</returns>
    private async Task<(int Tasks, int Columns)> ApplyRemoval(Guid projectId, FieldEnum current, Guid[] removed, RemovedEnumValues? choice, CancellationToken ct)
    {
        var removedKeys = removed.Select(x => x.ToString("D")).ToHashSet();
        var (holders, Uses) = await FindHolders(projectId, current, removedKeys, ct);
        var affectedBoards = await FindBoards(projectId, current, removedKeys, ct);
        if (holders.Count == 0 && affectedBoards.Length == 0)
            return (0, 0);

        if (choice == null)
        {
            var fieldIds = await EnumFieldIds(projectId, current, ct);
            var names = current.Values.Where(x => removed.Contains(x.Id)).Select(x => $"'{x.Name}'");
            var places = new List<string>();
            if (holders.Count > 0)
                places.Add($"selected in {holders.Count} task(s)");
            if (affectedBoards.Length > 0)
                places.Add("used in the field conditions of " + string.Join(", ", affectedBoards.SelectMany(b => b.Columns
                    .Where(c => c.FieldConditions.Any(f => UsesValue(f, fieldIds, removedKeys))).Select(c => $"board '{b.Name}' (column '{c.Name}')"))));
            throw new TaskerConflictException(
                $"Value(s) {string.Join(", ", names)} of {Subject(current)} are {string.Join(" and ", places)} and cannot be removed: " +
                "choose to clear them (from the tasks, and the conditions from the columns) or to reassign them to another value of the enum");
        }

        var tasksRewritten = holders.Count > 0 ? await RewriteTasks(projectId, current, removedKeys, choice, holders, Uses, ct) : 0;
        var columnsRewritten = affectedBoards.Length > 0 ? await RewriteBoards(projectId, current, removedKeys, choice, affectedBoards, ct) : 0;
        return (tasksRewritten, columnsRewritten);
    }

    /// <summary>
    /// Колонки досок: условие на убираемое значение — убирается (выбор «убрать») или получает значение-замену (выбор «переназначить»).
    /// Колонка остаётся колонкой. Доска после правки должна остаться корректной (колонки с общим статусом не пересекаются): если нет — отказ
    /// без записи, условия нужно поправить на доске.
    /// </summary>
    /// <returns>Сколько колонок переписано.</returns>
    private async Task<int> RewriteBoards(
        Guid projectId, FieldEnum current, HashSet<string> removedKeys, RemovedEnumValues choice, Board[] affected, CancellationToken ct)
    {
        var fieldIds = await EnumFieldIds(projectId, current, ct);
        var catalog = (await fields.GetAll(projectId, ct)).ToDictionary(x => x.Id);
        var target = choice.ReassignTo?.ToString("D");

        BoardColumn Rewrite(BoardColumn column) => column.FieldConditions.Any(f => UsesValue(f, fieldIds, removedKeys))
            ? new BoardColumn
            {
                Id = column.Id, Name = column.Name, StatusIds = column.StatusIds, DropStatuses = column.DropStatuses,
                FieldConditions = column.FieldConditions
                    .Select(f => UsesValue(f, fieldIds, removedKeys) ? (target == null ? null : f with { Value = target }) : f)
                    .OfType<ColumnFieldFilter>().Distinct().ToArray()
            }
            : column;

        var rewrittenBoards = new List<Board>();
        foreach (var board in affected)
        {
            var columns = board.Columns.Select(Rewrite).ToArray();
            if (ColumnFilters.FindOverlap(columns, catalog) is var (first, second, _, _))
                throw new TaskerConflictException(
                    $"{BoardSubject(board)}: after the change columns '{first.Name}' and '{second.Name}' would show the same tasks (the same status and conditions that " +
                    "no longer exclude each other): change their conditions on the board first");
            rewrittenBoards.Add(board with { Columns = columns });
        }

        // Блокировки досок проверяются до первой записи (без ожидания: ожидание — снаружи секции записи, см. Update).
        await locks.WaitUntilWritable(rewrittenBoards.Select(x => (Locks.LockedEntity.Board, x.Id, BoardSubject(x))).ToArray(), TimeSpan.Zero, ct);

        foreach (var board in rewrittenBoards)
        {
            var next = board;
            for (var attempt = 0; ; attempt++)
            {
                if (await boards.Update(next, next.Version, ct) != null)
                    break;
                // Доску тем временем изменили: колонки переписываются заново по свежей версии.
                var fresh = await boards.GetById(projectId, board.Id, ct);
                if (fresh == null)
                    break;
                if (attempt + 1 >= CascadeAttempts)
                    throw Versioning.Modified(BoardSubject(board));
                next = fresh with { Columns = fresh.Columns.Select(Rewrite).ToArray() };
            }
        }
        return affected.Sum(b => b.Columns.Count(c => c.FieldConditions.Any(f => UsesValue(f, fieldIds, removedKeys))));
    }

    private async Task<int> RewriteTasks(
        Guid projectId, FieldEnum current, HashSet<string> removedKeys, RemovedEnumValues choice, Dictionary<Guid, TaskItem> holders,
        Func<TaskField, bool> Uses, CancellationToken ct)
    {
        var typeFields = (await types.GetAll(projectId, ct)).ToDictionary(x => x.Id, x => x.Fields.Select(f => f.FieldId).ToHashSet());
        var target = choice.ReassignTo?.ToString("D");
        IReadOnlyList<string> Rewrite(IReadOnlyList<string> values)
        {
            var result = new List<string>();
            foreach (var value in values)
            {
                var replaced = removedKeys.Contains(value) ? target : value;
                if (replaced != null && !result.Contains(replaced))
                    result.Add(replaced);
            }
            return result;
        }

        // Массовая правка по общему правилу блокировок: сначала ждём все затрагиваемые задачи, потом пишем.
        return await TaskRewrites.ModifyAll(tasks, time, locks, holders.Values.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id),
            t => t.Fields.Any(Uses)
                ? t with
                {
                    Fields = t.Fields
                        .Select(f => Uses(f) ? f with { Values = Rewrite(f.Values) } : f)
                        // Поле типа без значения в задаче не записывается (см. TaskField): пустое — убираем; дополнительное остаётся.
                        .Where(f => f.Values.Count > 0 || f.Own != null || !(typeFields.TryGetValue(t.TypeId, out var own) && own.Contains(f.FieldId)))
                        .ToArray()
                }
                : null,
            CascadeAttempts, ct);
    }

    /// <summary>Хотя бы одно значение; названия без повторов; id — только существующих значений и по разу.</summary>
    private static FieldEnumValue[] ValidateValues(FieldEnumValueInput[] input, FieldEnumValue[] existing)
    {
        if (input.Length == 0)
            throw new TaskerValidationException("Enum must contain at least one value");

        var known = existing.Select(x => x.Id).ToHashSet();
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FieldEnumValue>();
        foreach (var item in input)
        {
            var valueName = Validate.Name(item.Name, "Enum value");
            if (!names.Add(valueName))
                throw new TaskerValidationException($"Enum value '{valueName}' is repeated: values must be unique");

            var valueId = item.Id ?? Guid.NewGuid();
            if (item.Id != null && !known.Contains(valueId))
                throw new TaskerValidationException($"Enum value id not found in the enum: {valueId}");
            if (!ids.Add(valueId))
                throw new TaskerValidationException($"Enum value id {valueId} is repeated");
            result.Add(new FieldEnumValue(valueId, valueName));
        }
        return result.ToArray();
    }

    private async Task EnsureNameFree(Guid projectId, string name, Guid? exceptId, CancellationToken ct)
    {
        if ((await enums.GetAll(projectId, ct)).Any(x => x.Id != exceptId && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new TaskerConflictException($"Enum '{name}' already exists in the project");
    }

    private static string Subject(FieldEnum value) => $"Enum '{value.Name}'";
}
