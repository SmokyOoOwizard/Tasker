using Tasker.Core.Boards;
using Tasker.Core.Dto;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;

namespace Tasker.Core.Fields;

/// <param name="Multiple">null — одно значение.</param>
/// <param name="EnumId">Перечисление проекта — обязательно для типа enum и запрещено для остальных.</param>
public record CreateField(string Name, FieldType Type, bool? Multiple, Guid? EnumId);

/// <summary>
/// Поля null — не меняются. Version — версия, которую видел клиент (см. <see cref="Versioning"/>).
/// <para>
/// Тип, множественность и перечисление меняются вместе со значениями задач (допустимые смены и преобразование — <see cref="FieldConversion"/>).
/// <paramref name="EnumId"/> — перечисление, на которое переходит поле (при смене типа на enum — обязательно; поле, которое перестаёт быть enum,
/// его не указывает). Если значения задач нельзя сохранить без решения пользователя (не разбираются, нет соответствия в перечислении,
/// несколько значений у поля «с одним»), нужен явный <paramref name="Choice"/>; без него — ошибка с числом задач.
/// </para>
/// </summary>
public record UpdateField(
    string? Name, string? Version, FieldType? Type = null, bool? Multiple = null, Guid? EnumId = null, FieldChangeChoice? Choice = null);

/// <summary>
/// Каталог полей проекта. Уникальность имени и существование перечисления проверяются под <see cref="IWriteScope"/>,
/// чтобы два одновременных запроса не создали одноимённые поля, а перечисление не удалили между проверкой и записью.
/// Задачи считаются «используют поле», если у них записано это поле: со значениями или добавленное (см. <see cref="TaskItem.Fields"/>).
/// Поле используют и колонки досок, если у них есть условие по нему (<see cref="BoardColumn.FieldConditions"/>): такое поле не удаляется
/// и не меняет тип, перечисление и множественность (условие хранит значение в виде этого типа, а от множественности зависит, исключают ли друг друга
/// условия колонок с общим статусом); переименование колонку не затрагивает.
/// </summary>
public class FieldService(
    IFieldStorage fields, IFieldEnumStorage enums, ITaskTypeStorage types, ITaskStorage tasks, IBoardStorage boards, IWriteScope writes, TimeProvider time,
    Locks.EditLockService locks)
{
    private const int CascadeAttempts = 3;

    /// <summary>Сколько файлов полей проекта не удалось прочитать (см. <see cref="IFieldStorage.CountUnreadable"/>).</summary>
    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => fields.CountUnreadable(projectId, ct);

    public Task<FieldDefinition[]> GetAll(Guid projectId, CancellationToken ct = default) => fields.GetAll(projectId, ct);

    public Task<ListDto<FieldDefinition>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        fields.GetRange(projectId, page, ct);

    /// <returns>null — поля нет.</returns>
    public Task<FieldDefinition?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => fields.GetById(projectId, id, ct);

    /// <summary>Поле по id или по имени (без учёта регистра).</summary>
    /// <returns>null — такого поля нет.</returns>
    public async Task<FieldDefinition?> Find(Guid projectId, string reference, CancellationToken ct = default)
    {
        var text = reference?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        var all = await fields.GetAll(projectId, ct);
        if (Guid.TryParse(text, out var id) && all.FirstOrDefault(x => x.Id == id) is { } byId)
            return byId;

        var byName = all.Where(x => string.Equals(x.Name, text, StringComparison.OrdinalIgnoreCase)).ToArray();
        return byName.Length switch
        {
            0 => null,
            1 => byName[0],
            _ => throw new TaskerValidationException(
                $"Several fields are named '{text}', use the id: {string.Join(", ", byName.Select(x => x.Id))}")
        };
    }

    /// <exception cref="TaskerConflictException">Поле с таким именем уже есть.</exception>
    /// <exception cref="TaskerValidationException">Тип и перечисление не согласуются или перечисления нет.</exception>
    public async Task<FieldDefinition> Create(Guid projectId, CreateField command, CancellationToken ct = default)
    {
        var name = Validate.FieldName(command.Name);
        if (!Enum.IsDefined(command.Type))
            throw new TaskerValidationException($"Field type: unknown '{command.Type}'");
        if (command.Type != FieldType.Enum && command.EnumId != null)
            throw new TaskerValidationException("EnumId: only a field of type enum has an enum");
        if (command.Type == FieldType.Enum && command.EnumId == null)
            throw new TaskerValidationException("EnumId: a field of type enum must refer to an enum of the project");

        return await writes.Exclusive(projectId, async () =>
        {
            if (command.EnumId is { } enumId && await enums.GetById(projectId, enumId, ct) == null)
                throw new TaskerValidationException($"EnumId: enum not found in the project: {enumId}");
            await EnsureNameFree(projectId, name, null, ct);

            var field = new FieldDefinition
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Name = name,
                Type = command.Type,
                Multiple = command.Multiple ?? false,
                EnumId = command.EnumId,
                Version = Versioning.New
            };
            return field with { Version = await fields.Add(field, ct) };
        }, ct);
    }

    /// <remarks>
    /// Смена типа, множественности или перечисления меняет значения у задач (<see cref="FieldConversion"/>) и определение в одной
    /// атомарной секции записи (в БД — одной транзакцией, в файлах — под общей блокировкой записи: сначала задачи, каждая по версии
    /// и с повтором, потом поле; если что-то не вышло, операцию можно повторить с тем же выбором). Обязательность не проверяется:
    /// значение, убранное у задачи выбором, оставляет обязательное поле пустым до следующей правки задачи.
    /// </remarks>
    /// <returns>null — поля нет.</returns>
    /// <exception cref="TaskerConflictException">Значения задач нельзя сохранить без выбора пользователя; в сообщении — число задач.</exception>
    public async Task<FieldDefinition?> Update(Guid projectId, Guid id, UpdateField command, CancellationToken ct = default)
    {
        var field = await fields.GetById(projectId, id, ct);
        if (field == null)
            return null;
        await locks.EnsureWritable(Locks.LockedEntity.Field, field.Id, Subject(field), ct);
        var expected = Versioning.Check(field.Version, command.Version, Subject(field));

        // Поле, созданное до правила «имя из букв и цифр», с прежним именем правится как раньше; новое имя проверяется целиком.
        var name = command.Name == null ? field.Name
            : command.Name.Trim() == field.Name ? field.Name
            : Validate.FieldName(command.Name);
        var type = command.Type ?? field.Type;
        if (!Enum.IsDefined(type))
            throw new TaskerValidationException($"Field type: unknown '{type}'");
        if (type != FieldType.Enum && command.EnumId != null)
            throw new TaskerValidationException("EnumId: only a field of type enum has an enum");
        var enumId = type == FieldType.Enum ? command.EnumId ?? field.EnumId : null;
        if (type == FieldType.Enum && enumId == null)
            throw new TaskerValidationException("EnumId: a field of type enum must refer to an enum of the project");
        var changed = field with { Name = name, Type = type, Multiple = command.Multiple ?? field.Multiple, EnumId = enumId };

        // Значения задач меняются: ждём блокировки затрагиваемых задач до секции записи (см. EditLockService.WaitUntilWritable);
        // внутри секции они проверяются ещё раз, без ожидания. Без нужного выбора ждать незачем: ниже будет ошибка.
        var plan = await Plan(projectId, field, changed, command.Choice, ct);
        if (plan.Problems == null)
            await TaskRewrites.WaitForLocks(locks, plan.Affected, ct);

        return await writes.Exclusive(projectId, async () =>
        {
            // Поле перечитано под блокировкой записи: между проверкой версии и записью его никто не изменил.
            if ((await fields.GetById(projectId, id, ct))?.Version != field.Version)
                throw Versioning.Modified(Subject(field));
            if (name != field.Name)
                await EnsureNameFree(projectId, name, id, ct);

            // Условие колонки хранит значение в виде прежнего типа и перечисления: с другим типом оно потеряло бы смысл. От «одного значения» или
            // «нескольких» зависит, исключают ли друг друга условия колонок с общим статусом (Level=a и Level=b вместе возможны только у списка).
            if (changed.Type != field.Type || changed.EnumId != field.EnumId || changed.Multiple != field.Multiple)
            {
                var usages = new Usages(Subject(field));
                await AddBoardColumns(projectId, id, usages, ct);
                usages.ThrowIfAny("changed to another type, enum or multiplicity (remove its conditions from the columns first)");
            }

            await ApplyValues(projectId, field, changed, command.Choice, ct);

            var version = await fields.Update(changed, expected, ct) ?? throw Versioning.Modified(Subject(field));
            return changed with { Version = version };
        }, ct);
    }

    /// <summary>Что изменится у задач: затронутые задачи, как их менять и, если без выбора пользователя не обойтись, описание проблем (иначе null).</summary>
    private sealed record ValuesPlan(TaskItem[] Affected, string? Problems, Func<TaskItem, TaskItem?> Change);

    private async Task<ValuesPlan> Plan(Guid projectId, FieldDefinition field, FieldDefinition changed, FieldChangeChoice? choice, CancellationToken ct)
    {
        var oldEnum = field.EnumId is { } oldId ? await enums.GetById(projectId, oldId, ct) : null;
        var newEnum = changed.EnumId is { } newId ? await enums.GetById(projectId, newId, ct) : null;
        if (changed.EnumId != null && newEnum == null)
            throw new TaskerValidationException($"EnumId: enum not found in the project: {changed.EnumId}");

        var conversion = FieldConversion.Create(field, oldEnum, changed, newEnum, choice);
        if (!conversion.ChangesValues)
            return new ValuesPlan([], null, _ => null);

        var typeFields = (await types.GetAll(projectId, ct)).ToDictionary(x => x.Id, x => x.Fields.Select(f => f.FieldId).ToHashSet());
        bool Holds(TaskField f) => f.Own == null && f.FieldId == field.Id && f.Values.Count > 0;

        TaskItem? Change(TaskItem task)
        {
            if (!task.Fields.Any(Holds))
                return null;

            var entries = new List<TaskField>();
            var differs = false;
            foreach (var entry in task.Fields)
            {
                if (!Holds(entry))
                {
                    entries.Add(entry);
                    continue;
                }

                var values = conversion.Convert(entry.Values).Values;
                differs |= !values.SequenceEqual(entry.Values);
                // Поле типа без значения в задаче не записывается (см. TaskField): пустое — убираем; дополнительное остаётся.
                if (values.Count > 0 || !(typeFields.TryGetValue(task.TypeId, out var own) && own.Contains(field.Id)))
                    entries.Add(entry with { Values = values });
            }
            return differs ? task with { Fields = entries } : null;
        }

        var holders = (await tasks.GetAll(projectId, new TaskFilter { FieldIds = [field.Id] }, ct))
            .Where(x => x.Fields.Any(Holds)).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArray();

        var results = holders.Select(x => conversion.Convert(x.Fields.First(Holds).Values)).ToArray();
        var problems = new List<string>();
        var bad = results.Count(x => x.Unconvertible.Count > 0);
        if (bad > 0 && choice?.ClearUnconvertible != true)
        {
            var examples = results.SelectMany(x => x.Unconvertible).Distinct().Take(5).Select(x => $"'{x}'");
            problems.Add($"{bad} task(s) have values that do not fit the new definition (e.g. {string.Join(", ", examples)}): " +
                         "choose to clear such values (the others are converted)" +
                         (changed.Type == FieldType.Enum ? " or map them to values of the enum" : ""));
        }
        var several = results.Count(x => x.Several);
        if (several > 0 && choice?.Several == null)
            problems.Add($"{several} task(s) have several values, but the field will hold one: choose to keep the first value or to clear them");

        return new ValuesPlan(
            holders.Where(x => Change(x) != null).ToArray(),
            problems.Count == 0 ? null : $"{Subject(field)} cannot be changed: {string.Join("; ", problems)}",
            Change);
    }

    private async Task ApplyValues(Guid projectId, FieldDefinition field, FieldDefinition changed, FieldChangeChoice? choice, CancellationToken ct)
    {
        var plan = await Plan(projectId, field, changed, choice, ct);
        if (plan.Problems != null)
            throw new TaskerConflictException(plan.Problems);

        // Массовая правка по общему правилу блокировок: сначала ждём все затрагиваемые задачи, потом пишем.
        await TaskRewrites.ModifyAll(tasks, time, locks, plan.Affected, plan.Change, CascadeAttempts, ct);
    }

    /// <summary>
    /// Нельзя удалить поле, на которое есть ссылки: его используют типы задач (подключённое поле), задачи (значения или добавленное
    /// поле) и колонки досок (условия по полю). Проверка и удаление — в одной секции записи, чтобы между ними поле не подключили к типу.
    /// </summary>
    /// <returns>false — поля нет.</returns>
    public async Task<bool> Delete(Guid projectId, Guid id, string? version, CancellationToken ct = default)
    {
        var field = await fields.GetById(projectId, id, ct);
        if (field == null)
            return false;
        await locks.EnsureWritable(Locks.LockedEntity.Field, field.Id, Subject(field), ct);
        var expected = Versioning.Check(field.Version, version, Subject(field));

        return await writes.Exclusive(projectId, async () =>
        {
            var usages = new Usages(Subject(field));
            foreach (var type in await types.GetAll(projectId, ct))
            {
                if (type.Fields.Any(x => x.FieldId == id))
                    usages.Add($"task type '{type.Name}'");
            }
            usages.AddTasks(await tasks.Count(projectId, new TaskFilter { FieldIds = [id] }, ct));
            await AddBoardColumns(projectId, id, usages, ct);
            usages.ThrowIfAny("deleted");

            if (!await fields.Delete(projectId, id, expected, ct))
                throw Versioning.Modified(Subject(field));
            await locks.Forget(Locks.LockedEntity.Field, field.Id, ct);
            return true;
        }, ct);
    }

    /// <summary>Колонки досок, у которых есть условие по этому полю.</summary>
    private async Task AddBoardColumns(Guid projectId, Guid fieldId, Usages usages, CancellationToken ct)
    {
        foreach (var board in await boards.GetAll(projectId, ct))
        foreach (var column in board.Columns)
        {
            if (column.FieldConditions.Any(x => x.FieldId == fieldId))
                usages.AddBoardColumn(board, column);
        }
    }

    private async Task EnsureNameFree(Guid projectId, string name, Guid? exceptId, CancellationToken ct)
    {
        if ((await fields.GetAll(projectId, ct)).Any(x => x.Id != exceptId && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new TaskerConflictException($"Field '{name}' already exists in the project");
    }

    private static string Subject(FieldDefinition field) => $"Field '{field.Name}'";
}
