using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Core.Statuses;
using Tasker.Core.TaskSeries;

namespace Tasker.Core.Tasks;

/// <param name="Fields">Поля типа в порядке отображения (поля каталога проекта, без повторов); null или пусто — без полей.</param>
public record CreateTaskType(string Name, Guid StatusSetId, TaskTypeField[]? Fields = null, string? Description = null);

/// <summary>Что сделать со значениями поля, которое убрали из типа, у задач этого типа.</summary>
public enum RemovedFieldValues
{
    /// <summary>Убрать значения этого поля у всех задач типа.</summary>
    Clear,

    /// <summary>Оставить: поле становится дополнительным полем каждой задачи, у которой есть значения (необязательным; его можно убрать у задачи вручную).</summary>
    Keep
}

/// <summary>
/// Поля null — не меняются; Description: пустая строка — очистить. Version — версия, которую видел клиент (см. <see cref="Versioning"/>).
/// <para>
/// Fields заменяет список полей типа целиком (и порядок). Убрать поле можно всегда, но если у задач типа есть его значения,
/// нужно явно выбрать, что с ними делать (<paramref name="RemovedFields"/>): без выбора — ошибка с числом затронутых задач.
/// Если значений нет ни у одной задачи, выбор не нужен. Выбор относится ко всем убираемым полям сразу.
/// Добавление поля (в том числе обязательного) и смена обязательности задачи не затрагивают: обязательность проверяется
/// при создании и правке задачи.
/// </para>
/// </summary>
public record UpdateTaskType(string? Name, Guid? StatusSetId, string? Version, TaskTypeField[]? Fields = null, RemovedFieldValues? RemovedFields = null, string? Description = null);

public class TaskTypeService(
    ITaskTypeStorage types,
    IStatusSetStorage sets,
    ITaskStorage tasks,
    IFieldStorage fields,
    IWriteScope writes,
    TimeProvider time,
    Locks.EditLockService locks)
{
    private const int CascadeAttempts = 3;

    /// <summary>Страница типов по имени; описания усечены до <paramref name="descriptionLength"/> (см. <see cref="DescriptionPreview"/>).</summary>
    public async Task<ListDto<TaskTypeListItem>> List(Guid projectId, Page page, int descriptionLength = DescriptionPreview.Full, CancellationToken ct = default)
    {
        DescriptionPreview.Check(descriptionLength);
        var found = await types.GetRange(projectId, page, ct);
        return new ListDto<TaskTypeListItem>
        {
            TotalCount = found.TotalCount,
            Offset = found.Offset,
            Limit = found.Limit,
            Data = found.Data.Select(x => new TaskTypeListItem(x, descriptionLength)).ToArray()
        };
    }

    public async Task<TaskType> Create(Guid projectId, CreateTaskType command, CancellationToken ct = default)
    {
        var name = Validate.Name(command.Name, "Task type name");
        await GetSet(projectId, command.StatusSetId, ct);

        return await writes.Exclusive(projectId, async () =>
        {
            var type = new TaskType
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Name = name,
                Description = Validate.Description(command.Description) ?? "",
                StatusSetId = command.StatusSetId,
                Fields = await ValidateFields(projectId, command.Fields, ct),
                Version = Versioning.New
            };

            return type with { Version = await types.Add(type, ct) };
        }, ct);
    }

    /// <summary>
    /// Сменить набор статусов можно, только если статусы всех задач этого типа есть в новом наборе —
    /// иначе сначала нужно перевести задачи в подходящие статусы.
    /// Убранное поле, значения которого есть у задач, — только с явным выбором (<see cref="UpdateTaskType.RemovedFields"/>).
    /// Выбор «убрать» меняет задачи и тип в одной атомарной секции записи: в БД — одной транзакцией, в файлах — под общей блокировкой
    /// записи, сначала задачи (каждая по версии, с повтором), потом тип; если что-то не вышло, операцию можно повторить с тем же выбором.
    /// </summary>
    /// <returns>
    /// null — типа нет. Иначе тип и число затронутых задач (<see cref="CascadeResult{T}.AffectedTasks"/>): задачи, у которых убрали значения
    /// убранных полей («убрать») или оставили их дополнительными («оставить»); 0 — убранных полей нет или значений у задач не было.
    /// </returns>
    /// <exception cref="TaskerConflictException">Статусы задач не в новом наборе; у задач есть значения убираемого поля, а выбора нет.</exception>
    public async Task<CascadeResult<TaskType>?> Update(Guid projectId, Guid id, UpdateTaskType command, CancellationToken ct = default)
    {
        var type = await types.GetById(projectId, id, ct);
        if (type == null)
            return null;
        await locks.EnsureWritable(Locks.LockedEntity.TaskType, type.Id, Subject(type), ct);
        var expected = Versioning.Check(type.Version, command.Version, Subject(type));

        var name = command.Name == null ? type.Name : Validate.Name(command.Name, "Task type name");
        var description = command.Description == null ? type.Description : Validate.Description(command.Description) ?? "";
        var setId = command.StatusSetId ?? type.StatusSetId;
        if (command.RemovedFields is { } choice && !Enum.IsDefined(choice))
            throw new TaskerValidationException($"RemovedFields: unknown choice '{choice}'");

        if (setId != type.StatusSetId)
        {
            var newSet = await GetSet(projectId, setId, ct);
            var oldSet = await GetSet(projectId, type.StatusSetId, ct);
            var lost = oldSet.StatusIds.Except(newSet.StatusIds).ToArray();

            var count = lost.Length == 0
                ? 0
                : await tasks.Count(projectId, new TaskFilter { TypeIds = [id], StatusIds = lost }, ct);
            if (count > 0)
                throw new TaskerConflictException(
                    $"{count} task(s) of type '{type.Name}' have statuses that are not in status set '{newSet.Name}'; " +
                    "move them to other statuses first");
        }

        // «Убрать значения»: ждём блокировки затрагиваемых задач до секции записи (ожидание внутри транзакции БД держало бы
        // и писателей, и самого держателя блокировки); внутри секции блокировки проверяются ещё раз, без ожидания.
        if (command.RemovedFields == RemovedFieldValues.Clear && command.Fields != null)
        {
            var removedNow = type.Fields.Select(x => x.FieldId).Except(command.Fields.Select(x => x.FieldId)).ToArray();
            if (removedNow.Length > 0)
                await TaskRewrites.WaitForLocks(locks, await FindHolders(projectId, type, removedNow, ct), ct);
        }

        // Список полей и значения задач проверяются и меняются в одной секции записи, чтобы между проверкой и записью
        // никто не успел поставить значение убираемому полю.
        return await writes.Exclusive<CascadeResult<TaskType>?>(projectId, async () =>
        {
            var newFields = command.Fields == null ? type.Fields : await ValidateFields(projectId, command.Fields, ct);
            var removed = type.Fields.Select(x => x.FieldId).Except(newFields.Select(x => x.FieldId)).ToArray();
            var affected = removed.Length > 0 ? await ApplyRemoval(projectId, type, removed, command.RemovedFields, ct) : 0;

            var updated = type with { Name = name, Description = description, StatusSetId = setId, Fields = newFields };
            var version = await types.Update(updated, expected, ct) ?? throw Versioning.Modified(Subject(type));
            return new CascadeResult<TaskType>(updated with { Version = version }, affected);
        }, ct);
    }

    /// <summary>Нельзя удалить тип, у которого есть задачи.</summary>
    /// <returns>false — типа нет.</returns>
    public async Task<bool> Delete(Guid projectId, Guid id, string? version, CancellationToken ct = default)
    {
        var type = await types.GetById(projectId, id, ct);
        if (type == null)
            return false;
        await locks.EnsureWritable(Locks.LockedEntity.TaskType, type.Id, Subject(type), ct);
        var expected = Versioning.Check(type.Version, version, Subject(type));

        var usages = new Usages(Subject(type));
        usages.AddTasks(await tasks.Count(projectId, new TaskFilter { TypeIds = [id] }, ct));
        usages.ThrowIfAny("deleted");

        if (!await types.Delete(projectId, id, expected, ct))
            throw Versioning.Modified(Subject(type));
        await locks.Forget(Locks.LockedEntity.TaskType, type.Id, ct);
        return true;
    }

    private static string Subject(TaskType type) => $"Task type '{type.Name}'";

    private async Task<StatusSet> GetSet(Guid projectId, Guid setId, CancellationToken ct) =>
        await sets.GetById(projectId, setId, ct)
        ?? throw new TaskerValidationException($"StatusSetId: not found in the project: {setId}");

    /// <summary>Поля типа: все из каталога проекта, без повторов.</summary>
    private async Task<IReadOnlyList<TaskTypeField>> ValidateFields(Guid projectId, TaskTypeField[]? input, CancellationToken ct)
    {
        var list = input ?? [];
        if (list.Select(x => x.FieldId).Distinct().Count() != list.Length)
            throw new TaskerValidationException("Fields contains duplicates");

        var catalog = (await fields.GetAll(projectId, ct)).Select(x => x.Id).ToHashSet();
        var missing = list.Select(x => x.FieldId).Where(x => !catalog.Contains(x)).ToArray();
        if (missing.Length > 0)
            throw new TaskerValidationException($"Fields: not found in the project: {string.Join(", ", missing)}");
        return list.Select(x => new TaskTypeField(x.FieldId, x.Required)).ToArray();
    }

    /// <summary>Задачи типа, у которых есть значения убираемых полей.</summary>
    private async Task<TaskItem[]> FindHolders(Guid projectId, TaskType type, Guid[] removed, CancellationToken ct) =>
        (await tasks.GetAll(projectId, new TaskFilter { TypeIds = [type.Id], FieldIds = removed }, ct))
            .Where(t => t.Fields.Any(f => f.Own == null && removed.Contains(f.FieldId) && f.Values.Count > 0))
            .ToArray();

    /// <summary>
    /// Убранные из типа поля и значения задач: есть значения — нужен выбор. «Оставить» ничего не пишет: значения уже лежат в задачах,
    /// а поле, которого нет в типе, для задачи — дополнительное. «Убрать» удаляет значения (и запись о поле) у всех задач типа.
    /// </summary>
    /// <returns>Сколько задач затронуто (у «оставить» — задачи, у которых поле стало дополнительным).</returns>
    private async Task<int> ApplyRemoval(Guid projectId, TaskType type, Guid[] removed, RemovedFieldValues? choice, CancellationToken ct)
    {
        var holders = await FindHolders(projectId, type, removed, ct);
        if (holders.Length == 0)
            return 0;

        if (choice == null)
        {
            var names = (await fields.GetAll(projectId, ct)).Where(x => removed.Contains(x.Id)).Select(x => $"'{x.Name}'").ToArray();
            throw new TaskerConflictException(
                $"{holders.Length} task(s) of type '{type.Name}' have values in the field(s) being removed ({string.Join(", ", names)}): " +
                "choose to clear those values or to keep them as additional fields of the tasks");
        }

        if (choice == RemovedFieldValues.Keep)
            return holders.Length;

        // Массовая правка по общему правилу блокировок: сначала ждём все затрагиваемые задачи, потом пишем.
        return await TaskRewrites.ModifyAll(tasks, time, locks, holders,
            t => t.Fields.Any(f => f.Own == null && removed.Contains(f.FieldId) && f.Values.Count > 0)
                ? t with { Fields = t.Fields.Where(f => !(f.Own == null && removed.Contains(f.FieldId) && f.Values.Count > 0)).ToArray() }
                : null,
            CascadeAttempts, ct);
    }
}
