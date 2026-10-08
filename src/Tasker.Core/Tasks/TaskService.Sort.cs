using Tasker.Core.Fields;

namespace Tasker.Core.Tasks;

public partial class TaskService
{
    /// <summary>
    /// Ключи упорядочивания из текста клиента: <c>status,-updated,Estimate</c> — ключи через запятую, <c>-</c> перед ключом — по убыванию.
    /// Ключи: <c>status</c> (позиция статуса в наборе типа задачи; у задач с разными наборами — позиция, затем название набора и статуса),
    /// <c>type</c> (название типа), <c>title</c> (без учёта регистра, Unicode), <c>created</c>, <c>updated</c>, <c>series</c> (номер в серии: серия с меньшим префиксом,
    /// внутри неё по числу — <c>TSK-9</c> раньше <c>TSK-12</c>) и имя поля — поля каталога или собственного поля задач (так же, как у фильтра по полю:
    /// у собственных полей без поля в каталоге один тип — по нему, несколько — ошибка). Поля сравниваются по типу: int и float — по числу, date — по дате,
    /// bool — false раньше true, enum — по порядку значений в перечислении, string — без учёта регистра. У поля с несколькими значениями — по первому
    /// (в порядке хранения), и при убывании тоже по первому. Задачи без значения — всегда в конце. Встроенное слово главнее поля с таким же именем.
    /// </summary>
    /// <returns>null — ключей нет (пустой текст).</returns>
    /// <exception cref="TaskerValidationException">Пустой ключ, ключ повторён, неизвестный ключ (в ошибке — допустимые), у собственных полей несколько типов.</exception>
    public async Task<TaskSortKey[]?> ParseSort(Guid projectId, string? sort, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sort))
            return null;

        IReadOnlyCollection<FieldDefinition>? catalog = null;
        var seen = new HashSet<string>();
        var result = new List<TaskSortKey>();
        foreach (var raw in sort.Split(','))
        {
            var text = raw.Trim();
            var descending = text.StartsWith('-');
            var name = (descending ? text[1..] : text).Trim();
            if (name.Length == 0)
                throw new TaskerValidationException($"Sort '{sort}': an empty key (expected keys separated by commas, '-' before a key for descending order)");
            if (!seen.Add(FieldNames.Key(name)))
                throw new TaskerValidationException($"Sort '{sort}': key '{name}' is listed twice");

            if (TaskSortNames.Builtin.TryGetValue(name, out var target))
            {
                var canonical = TaskSortNames.BuiltinNames.First(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                result.Add(await BuiltinKey(projectId, target, descending, canonical, ct));
                continue;
            }

            catalog ??= await fields.GetAll(projectId, ct);
            result.Add(await FieldKey(projectId, sort, name, descending, catalog, ct));
        }
        return [.. result];
    }

    private async Task<TaskSortKey> BuiltinKey(Guid projectId, SortTarget target, bool descending, string name, CancellationToken ct)
    {
        var key = new TaskSortKey(target, descending, name);
        switch (target)
        {
            case SortTarget.Type:
                var allTypes = await types.GetAll(projectId, ct);
                return key with { TypeRanks = Ranks(allTypes.Select(x => (x.Id, (object)TaskSortNames.Fold(x.Name)))) };

            case SortTarget.Series:
                var allSeries = await seriesStorage.GetAll(projectId, ct);
                // Префикс сравнивается точно, как в БД и индексе (TSK и tsk — разные серии).
                return key with { SeriesRanks = Ranks(allSeries.Select(x => (x.Id, (object)x.Prefix)), StringComparer.Ordinal) };

            case SortTarget.Status:
                return key with { StatusRanks = await StatusRanks(projectId, ct) };

            default:
                return key;
        }
    }

    /// <summary>Места (0, 1, 2…) по возрастанию значений; равные значения делят место.</summary>
    private static IdRank[] Ranks(IEnumerable<(Guid Id, object Value)> items, IComparer<object>? comparer = null)
    {
        comparer ??= Comparer<object>.Create((a, b) => string.CompareOrdinal((string)a, (string)b));
        var ordered = items.OrderBy(x => x.Value, comparer).ThenBy(x => x.Id).ToArray();
        var result = new List<IdRank>();
        var rank = -1;
        object? previous = null;
        foreach (var item in ordered)
        {
            if (previous == null || comparer.Compare(previous, item.Value) != 0)
                rank++;
            previous = item.Value;
            result.Add(new IdRank(item.Id, rank));
        }
        return [.. result];
    }

    private static IdRank[] Ranks(IEnumerable<(Guid Id, object Value)> items, StringComparer comparer) =>
        Ranks(items, Comparer<object>.Create((a, b) => comparer.Compare((string)a, (string)b)));

    /// <summary>
    /// Место статуса у задач каждого типа: позиция статуса в наборе типа, затем название набора и статуса (без учёта регистра), затем id.
    /// Типы с одним набором получают одинаковые места.
    /// </summary>
    private async Task<StatusRank[]> StatusRanks(Guid projectId, CancellationToken ct)
    {
        var allTypes = await types.GetAll(projectId, ct);
        var allSets = (await sets.GetAll(projectId, ct)).ToDictionary(x => x.Id);
        var names = (await statuses.GetAll(projectId, ct)).ToDictionary(x => x.Id, x => x.Name);

        var entries = new List<(Guid TypeId, Guid StatusId, int Position, string SetName, string StatusName, Guid SetId)>();
        foreach (var type in allTypes)
        {
            if (!allSets.TryGetValue(type.StatusSetId, out var set))
                continue;
            for (var position = 0; position < set.StatusIds.Length; position++)
            {
                var statusId = set.StatusIds[position];
                entries.Add((type.Id, statusId, position, TaskSortNames.Fold(set.Name), TaskSortNames.Fold(names.GetValueOrDefault(statusId, "")), set.Id));
            }
        }

        var ordered = entries
            .OrderBy(x => x.Position)
            .ThenBy(x => x.SetName, StringComparer.Ordinal)
            .ThenBy(x => x.StatusName, StringComparer.Ordinal)
            .ThenBy(x => x.SetId)
            .ThenBy(x => x.StatusId)
            .ThenBy(x => x.TypeId)
            .ToArray();

        var result = new List<StatusRank>();
        var rank = -1;
        Guid? previousSet = null;
        Guid? previousStatus = null;
        foreach (var entry in ordered)
        {
            // Одно место у статуса одного набора; типы на этом же наборе делят его.
            if (previousSet != entry.SetId || previousStatus != entry.StatusId)
                rank++;
            (previousSet, previousStatus) = (entry.SetId, entry.StatusId);
            result.Add(new StatusRank(entry.TypeId, entry.StatusId, rank));
        }
        return [.. result];
    }

    private async Task<TaskSortKey> FieldKey(
        Guid projectId, string sort, string name, bool descending, IReadOnlyCollection<FieldDefinition> catalog, CancellationToken ct)
    {
        SortField field;
        Guid? enumId;
        if (catalog.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is { } found)
        {
            field = new SortField(found.Name, found.Type, found.Id);
            enumId = found.EnumId;
        }
        else
        {
            var kinds = await tasks.GetOwnFieldKinds(projectId, FieldNames.Key(name), ct);
            switch (kinds.Length)
            {
                case 0:
                    var names = catalog.Select(x => x.Name).Order(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
                    throw new TaskerValidationException(
                        $"Sort '{sort}': unknown key '{name}'. Keys: {string.Join(", ", TaskSortNames.BuiltinNames)}, " +
                        $"or the name of a field ({(names.Length > 0 ? "catalog: " + string.Join(", ", names) + "; " : "")}or an own field of tasks); " +
                        "put '-' before a key for descending order, separate keys with commas");
                case 1:
                    field = new SortField(name, kinds[0].Type, null);
                    enumId = kinds[0].EnumId;
                    break;
                default:
                    throw new TaskerValidationException(
                        $"Sort '{sort}': tasks have own fields '{name}' of several types ({string.Join(", ", await KindNames(projectId, kinds, ct))}) " +
                        "and the catalog has no such field, so it is unclear which one to sort by");
            }
        }

        if (field.Type == FieldType.Enum)
        {
            var enumeration = enumId is { } id ? await enums.GetById(projectId, id, ct) : null;
            field = field with { EnumValues = enumeration?.Values.Select(x => x.Id.ToString("D")).ToArray() ?? [] };
        }
        return new TaskSortKey(SortTarget.Field, descending, field.Name) { Field = field };
    }
}
