using Tasker.Core.Fields;

namespace Tasker.Core.Tasks;

/// <summary>
/// Правила полей задачи, общие для создания и правки: какие поля есть у задачи (тип + дополнительные + собственные),
/// как применить правку полей, как проверить обязательные. Работает над уже загруженными каталогом и перечислениями;
/// ничего не читает и не пишет — сервисы вызывают её под <see cref="TaskSeries.IWriteScope"/>, когда нужна согласованность.
/// </summary>
internal static class TaskFieldRules
{
    /// <summary>Поле задачи с определением: из типа, каталога или самой задачи.</summary>
    internal record Resolved(
        Guid Id, string Name, FieldType Type, bool Multiple, Guid? EnumId, bool Required, TaskFieldSource Source, TaskField? Entry)
    {
        public IReadOnlyList<string> Values => Entry?.Values ?? [];
    }

    /// <summary>
    /// Поля задачи: сначала поля типа (в порядке типа), затем дополнительные и собственные (в порядке записи в задаче).
    /// Запись о поле, которого нет в каталоге (удалено в другой ветке), пропускается — как недействительная связь.
    /// </summary>
    public static List<Resolved> Resolve(IReadOnlyList<TaskField> entries, TaskType type, IReadOnlyDictionary<Guid, FieldDefinition> catalog)
    {
        var result = new List<Resolved>();
        var typeFields = type.Fields.Select(x => x.FieldId).ToHashSet();

        foreach (var typeField in type.Fields)
        {
            if (!catalog.TryGetValue(typeField.FieldId, out var field) || result.Any(x => x.Id == field.Id))
                continue;
            var entry = entries.FirstOrDefault(x => x.FieldId == field.Id && x.Own == null);
            result.Add(new Resolved(field.Id, field.Name, field.Type, field.Multiple, field.EnumId, typeField.Required, TaskFieldSource.Type, entry));
        }

        foreach (var entry in entries)
        {
            if (entry.Own is { } own)
                result.Add(new Resolved(entry.FieldId, own.Name, own.Type, own.Multiple, own.EnumId, own.Required, TaskFieldSource.Own, entry));
            else if (!typeFields.Contains(entry.FieldId) && catalog.TryGetValue(entry.FieldId, out var field))
                result.Add(new Resolved(field.Id, field.Name, field.Type, field.Multiple, field.EnumId, false, TaskFieldSource.Extra, entry));
        }

        return result;
    }

    /// <summary>
    /// Применяет правку полей к записям задачи (<paramref name="entries"/>) и возвращает новые записи.
    /// Поля типа убрать нельзя; собственные имена не повторяют имена других полей задачи; значения проверены по типу поля.
    /// </summary>
    /// <param name="type">Тип задачи — новый, если тип меняется в той же правке.</param>
    /// <exception cref="TaskerValidationException">Правка не подходит задаче.</exception>
    public static List<TaskField> Apply(
        IReadOnlyList<TaskField> entries, TaskType type, IReadOnlyDictionary<Guid, FieldDefinition> catalog,
        IReadOnlyDictionary<Guid, FieldEnum> enums, TaskFieldChanges? changes)
    {
        var result = entries.ToList();
        if (changes == null)
            return result;

        Remove(result, type, changes.RemoveFields);
        AddFromCatalog(result, type, catalog, changes.AddFields);
        // Значения — до собственных полей: поле каталога, добавленное значением, уже учитывается в уникальности имён.
        SetValues(result, type, catalog, enums, changes.Values);
        AddOwn(result, type, catalog, enums, changes.NewOwnFields);
        return result;
    }

    /// <summary>
    /// Убирает записи без значений у полей типа: поле типа есть у задачи и так, записывать его пустым незачем.
    /// Дополнительные и собственные поля остаются, пока их не уберут явно.
    /// </summary>
    public static List<TaskField> Normalize(IEnumerable<TaskField> entries, TaskType type)
    {
        var typeFields = type.Fields.Select(x => x.FieldId).ToHashSet();
        return entries.Where(x => x.Values.Count > 0 || x.Own != null || !typeFields.Contains(x.FieldId)).ToList();
    }

    /// <summary>Обязательные поля (типа и собственные) без значения — по именам; пусто — всё заполнено.</summary>
    public static string[] MissingRequired(IReadOnlyList<TaskField> entries, TaskType type, IReadOnlyDictionary<Guid, FieldDefinition> catalog) =>
        Resolve(entries, type, catalog).Where(x => x.Required && x.Values.Count == 0).Select(x => x.Name).ToArray();

    private static void Remove(List<TaskField> entries, TaskType type, Guid[]? ids)
    {
        if (ids == null)
            return;
        EnsureDistinct(ids, "RemoveFields");

        foreach (var id in ids)
        {
            if (type.Fields.Any(x => x.FieldId == id))
                throw new TaskerValidationException($"RemoveFields: {id} is a field of the task type '{type.Name}' and cannot be removed from the task");
            if (entries.RemoveAll(x => x.FieldId == id) == 0)
                throw new TaskerValidationException($"RemoveFields: the task has no additional or own field {id}");
        }
    }

    private static void AddFromCatalog(
        List<TaskField> entries, TaskType type, IReadOnlyDictionary<Guid, FieldDefinition> catalog, Guid[]? ids)
    {
        if (ids == null)
            return;
        EnsureDistinct(ids, "AddFields");

        foreach (var id in ids)
        {
            if (!catalog.ContainsKey(id))
                throw new TaskerValidationException($"AddFields: field not found in the project: {id}");
            // Поле типа или уже добавленное — у задачи уже есть.
            if (type.Fields.Any(x => x.FieldId == id) || entries.Any(x => x.FieldId == id && x.Own == null))
                continue;
            entries.Add(new TaskField(id, []));
        }
    }

    private static void AddOwn(
        List<TaskField> entries, TaskType type, IReadOnlyDictionary<Guid, FieldDefinition> catalog,
        IReadOnlyDictionary<Guid, FieldEnum> enums, NewOwnField[]? fields)
    {
        foreach (var field in fields ?? [])
        {
            var name = Validate.FieldName(field.Name);
            if (!Enum.IsDefined(field.Type))
                throw new TaskerValidationException($"Field type: unknown '{field.Type}'");
            if (field.Type != FieldType.Enum && field.EnumId != null)
                throw new TaskerValidationException($"Field '{name}': only a field of type enum has an enum");
            if (field.Type == FieldType.Enum && field.EnumId == null)
                throw new TaskerValidationException($"Field '{name}': a field of type enum must refer to an enum of the project");
            FieldEnum? enumeration = null;
            if (field.EnumId is { } enumId && !enums.TryGetValue(enumId, out enumeration))
                throw new TaskerValidationException($"Field '{name}': enum not found in the project: {enumId}");

            if (Resolve(entries, type, catalog).Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new TaskerValidationException($"Field '{name}': the task already has a field with this name");

            var values = FieldValues.Normalize(name, field.Type, field.Multiple, enumeration, field.Values);
            entries.Add(new TaskField(Guid.NewGuid(), values, new OwnField(name, field.Type, field.Required, field.Multiple, field.EnumId)));
        }
    }

    private static void SetValues(
        List<TaskField> entries, TaskType type, IReadOnlyDictionary<Guid, FieldDefinition> catalog,
        IReadOnlyDictionary<Guid, FieldEnum> enums, TaskFieldValueInput[]? inputs)
    {
        if (inputs == null)
            return;
        EnsureDistinct(inputs.Select(x => x.FieldId).ToArray(), "Values");

        foreach (var input in inputs)
        {
            // Поле задачи (типа, дополнительное, собственное) или поле каталога, которого у задачи ещё нет: значение делает его дополнительным.
            var known = Resolve(entries, type, catalog).FirstOrDefault(x => x.Id == input.FieldId);
            (string Name, FieldType Type, bool Multiple, Guid? EnumId) field;
            if (known != null)
                field = (known.Name, known.Type, known.Multiple, known.EnumId);
            else if (catalog.TryGetValue(input.FieldId, out var added))
                field = (added.Name, added.Type, added.Multiple, added.EnumId);
            else
                throw new TaskerValidationException($"Values: the task has no field {input.FieldId} and it is not in the project's catalog");

            FieldEnum? enumeration = null;
            if (field.EnumId is { } enumId)
                enums.TryGetValue(enumId, out enumeration);
            var values = FieldValues.Normalize(field.Name, field.Type, field.Multiple, enumeration, input.Values);

            var index = entries.FindIndex(x => x.FieldId == input.FieldId);
            if (index >= 0)
                entries[index] = entries[index] with { Values = values };
            else if (values.Length > 0)
                entries.Add(new TaskField(input.FieldId, values));
        }
    }

    private static void EnsureDistinct(Guid[] ids, string argument)
    {
        if (ids.Distinct().Count() != ids.Length)
            throw new TaskerValidationException($"{argument} contains duplicates");
    }
}
