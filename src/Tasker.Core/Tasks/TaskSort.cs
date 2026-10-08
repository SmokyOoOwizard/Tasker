using Tasker.Core.Fields;

namespace Tasker.Core.Tasks;

/// <summary>По чему упорядочиваются задачи (<see cref="TaskSortKey"/>).</summary>
public enum SortTarget
{
    /// <summary>Позиция статуса в наборе статусов типа задачи (<see cref="TaskSortKey.StatusRanks"/>).</summary>
    Status,

    /// <summary>Название типа задачи, без учёта регистра (<see cref="TaskSortKey.TypeRanks"/>).</summary>
    Type,

    /// <summary>Заголовок, без учёта регистра (Unicode).</summary>
    Title,
    Created,
    Updated,

    /// <summary>Номер в серии: серия с меньшим префиксом, внутри неё — по числу (<see cref="TaskSortKey.SeriesRanks"/>).</summary>
    Series,

    /// <summary>Поле каталога или собственное поле задач (<see cref="TaskSortKey.Field"/>).</summary>
    Field
}

/// <summary>Место значения в порядке: чем меньше <paramref name="Rank"/>, тем раньше. Равные значения (у разных id) имеют одинаковое место.</summary>
public record IdRank(Guid Id, int Rank);

/// <summary>Место статуса у задач типа <paramref name="TypeId"/>: порядок статуса зависит от набора типа, а у разных наборов он свой.</summary>
public record StatusRank(Guid TypeId, Guid StatusId, int Rank);

/// <summary>Поле, по которому упорядочиваются задачи: поле каталога и собственные поля с тем же именем и типом (как у <see cref="FieldCondition"/>).</summary>
/// <param name="FieldId">Поле каталога с этим именем; null — в каталоге такого поля нет, ключ только про собственные поля задач.</param>
/// <param name="EnumValues">У enum — значения перечисления (канонический текст, Guid в виде <c>D</c>) в порядке перечисления; иначе null.</param>
public record SortField(string Name, FieldType Type, Guid? FieldId, string[]? EnumValues = null)
{
    /// <summary>Ключ имени в хранилищах: <see cref="FieldNames.Key"/>.</summary>
    public string Key => FieldNames.Key(Name);

    /// <summary>Относится ли запись поля в задаче к ключу: то же поле каталога или собственное поле с тем же именем и типом.</summary>
    public bool Covers(TaskField entry) =>
        entry.Own == null
            ? FieldId != null && entry.FieldId == FieldId
            : entry.Own.Type == Type && FieldNames.Key(entry.Own.Name) == Key;
}

/// <summary>
/// Ключ упорядочивания списка задач (<see cref="TaskFilter.Sort"/>); из текста клиента его строит <see cref="TaskService.ParseSort"/>.
/// Порядок «места» (статус, тип, серия, значение enum) хранилища сами не знают, поэтому ядро заранее раскладывает его в таблицы мест
/// (<see cref="StatusRanks"/>, <see cref="TypeRanks"/>, <see cref="SeriesRanks"/>, <see cref="SortField.EnumValues"/>), а хранилище только сравнивает по ним
/// запросом. Задачи без значения ключа — всегда в конце, и при возрастании, и при убывании.
/// </summary>
public record TaskSortKey(SortTarget Target, bool Descending, string Name)
{
    public SortField? Field { get; init; }
    public StatusRank[]? StatusRanks { get; init; }
    public IdRank[]? TypeRanks { get; init; }
    public IdRank[]? SeriesRanks { get; init; }
}

/// <summary>Короткие записи ключей (<c>status,-updated,Estimate</c>) и общий текст ошибок.</summary>
public static class TaskSortNames
{
    /// <summary>Встроенные ключи (имя в тексте, без учёта регистра).</summary>
    public static readonly IReadOnlyDictionary<string, SortTarget> Builtin = new Dictionary<string, SortTarget>(StringComparer.OrdinalIgnoreCase)
    {
        ["status"] = SortTarget.Status,
        ["type"] = SortTarget.Type,
        ["title"] = SortTarget.Title,
        ["created"] = SortTarget.Created,
        ["updated"] = SortTarget.Updated,
        ["series"] = SortTarget.Series
    };

    /// <summary>Встроенные ключи в порядке показа.</summary>
    public static readonly string[] BuiltinNames = ["status", "type", "title", "created", "updated", "series"];

    /// <summary>Строка для сравнения без учёта регистра по Unicode: нижний регистр, порядок ординальный (одинаковый во всех хранилищах).</summary>
    public static string Fold(string text) => text.ToLowerInvariant();
}
