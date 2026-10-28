using System.Globalization;
using Tasker.Core.Fields;

namespace Tasker.Core.Tasks;

/// <summary>Что проверяет условие по полю каталога (<see cref="FieldCondition"/>).</summary>
public enum FieldOperator
{
    /// <summary>Среди значений поля есть заданное.</summary>
    Equal,

    /// <summary>Ни одно значение поля не равно заданному: подходят и задачи без значения (дополнение к <see cref="Equal"/>).</summary>
    NotEqual,

    /// <summary>Среди значений поля есть большее заданного (int, float, date).</summary>
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,

    /// <summary>У поля есть хотя бы одно значение.</summary>
    Set,

    /// <summary>У поля нет значения (в том числе поле задаче не подключено).</summary>
    Unset,

    /// <summary>Поле подключено к задаче: входит в её тип или добавлено ей (даже без значения).</summary>
    Attached,

    /// <summary>Поле к задаче не подключено.</summary>
    Detached
}

/// <summary>
/// Условие по полю: полю каталога и собственным полям задач с тем же именем и тем же типом (<paramref name="Name"/>, <paramref name="Type"/>).
/// Собственное поле у каждой задачи своё (id внутри задачи), поэтому оно находится не по id, а по имени (без учёта регистра, ключ — <see cref="FieldNames.Key"/>)
/// и типу; собственные поля с тем же именем, но другого типа условию не подходят. Какие значения нужны оператору, зависит от него:
/// <list type="bullet">
/// <item><see cref="FieldOperator.Equal"/>, <see cref="FieldOperator.NotEqual"/> — <paramref name="Value"/> (канонический текст, <see cref="Fields.FieldValues"/>);</item>
/// <item>сравнения — <paramref name="Value"/> и у int и float ещё <paramref name="Number"/> (то же значение числом: сравнение идёт по числовому столбцу
/// хранилища, у date — по самому тексту <c>yyyy-MM-dd</c>, он сортируется как текст);</item>
/// <item><see cref="FieldOperator.Attached"/>, <see cref="FieldOperator.Detached"/> — <paramref name="TypeIds"/>: типы задач, в которых есть поле каталога
/// (определяются при разборе условия, поэтому смена типа задачи и правка полей типа учитываются сразу; запись о поле в самой задаче хранилище смотрит само).
/// Собственное поле «подключено», если оно у задачи есть.</item>
/// </list>
/// У поля с несколькими значениями «хотя бы одно значение подходит» для равенства и сравнений; <see cref="FieldOperator.NotEqual"/> —
/// «ни одно не равно». Условия строит <see cref="TaskService.ParseFieldFilters"/>.
/// </summary>
/// <param name="Name">Имя поля, как его ввёл клиент (регистр не важен).</param>
/// <param name="Type">Тип: у поля каталога — его тип, иначе — тип собственных полей, по которому разобрано значение.</param>
/// <param name="FieldId">Поле каталога с этим именем; null — в каталоге такого поля нет, условие только про собственные поля задач.</param>
public record FieldCondition(
    string Name, FieldType Type, FieldOperator Operator, string? Value = null, double? Number = null, Guid? FieldId = null, Guid[]? TypeIds = null)
{
    /// <summary>Ключ имени в хранилищах: <see cref="FieldNames.Key"/>.</summary>
    public string Key => FieldNames.Key(Name);

    /// <summary>Относится ли запись поля в задаче к этому условию: то же поле каталога или собственное поле с тем же именем и типом.</summary>
    public bool Covers(TaskField entry) =>
        entry.Own == null
            ? FieldId != null && entry.FieldId == FieldId
            : entry.Own.Type == Type && FieldNames.Key(entry.Own.Name) == Key;

    /// <summary>Подходит ли задача (для проверок в памяти; хранилища выполняют то же запросом).</summary>
    public bool Matches(TaskItem task)
    {
        var entries = task.Fields.Where(Covers).ToArray();
        var values = entries.SelectMany(x => x.Values).ToArray();
        return Operator switch
        {
            FieldOperator.Equal => values.Contains(Value!),
            FieldOperator.NotEqual => !values.Contains(Value!),
            FieldOperator.Greater => values.Any(x => Holds(x, c => c > 0)),
            FieldOperator.GreaterOrEqual => values.Any(x => Holds(x, c => c >= 0)),
            FieldOperator.Less => values.Any(x => Holds(x, c => c < 0)),
            FieldOperator.LessOrEqual => values.Any(x => Holds(x, c => c <= 0)),
            FieldOperator.Set => values.Length > 0,
            FieldOperator.Unset => values.Length == 0,
            FieldOperator.Attached => entries.Length > 0 || (TypeIds?.Contains(task.TypeId) ?? false),
            FieldOperator.Detached => entries.Length == 0 && !(TypeIds?.Contains(task.TypeId) ?? false),
            _ => throw new ArgumentOutOfRangeException(nameof(Operator), Operator, null)
        };
    }

    /// <summary>Значение задачи не меньше/больше заданного: по числу (int, float) или по тексту <c>yyyy-MM-dd</c> (date).</summary>
    private bool Holds(string value, Func<int, bool> sign)
    {
        if (Number is not { } number)
            return sign(string.CompareOrdinal(value, Value));
        // Значение, которое не число, ничему не больше и не меньше.
        return FieldNumbers.Parse(value) is { } own && sign(own.CompareTo(number));
    }
}

/// <summary>Имена полей в хранилищах и условиях: без учёта регистра.</summary>
public static class FieldNames
{
    /// <summary>Ключ имени (нижний регистр), по которому хранилища находят собственные поля задач.</summary>
    public static string Key(string name) => name.ToLowerInvariant();
}

/// <summary>Числовое чтение канонического значения поля — то, что хранилища кладут в числовой столбец.</summary>
public static class FieldNumbers
{
    /// <summary>Значение как число (<c>double</c>; у int точно до 2^53); null — не число. Значения int и float в каноническом виде всегда числа.</summary>
    public static double? Parse(string canonical) =>
        double.TryParse(canonical, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
}

/// <summary>
/// Фильтр задач. null — без ограничения по полю; пустой массив — ни одна задача не подходит.
/// </summary>
public class TaskFilter
{
    public Guid[]? TypeIds { get; init; }
    public Guid[]? StatusIds { get; init; }

    /// <summary>
    /// Фильтр по значениям из параметров интерфейсов (REST, MCP): значения одного параметра — «ИЛИ», разные параметры — «И».
    /// Не заданный или пустой параметр не ограничивает (в отличие от пустого массива в самом фильтре); повторы убираются; ничего не задано — null.
    /// </summary>
    public static TaskFilter? Of(IEnumerable<Guid>? typeIds, IEnumerable<Guid>? statusIds, IEnumerable<Guid>? seriesIds)
    {
        static Guid[]? Distinct(IEnumerable<Guid>? ids) => ids?.Distinct().ToArray() is { Length: > 0 } found ? found : null;
        var types = Distinct(typeIds);
        var statuses = Distinct(statusIds);
        var series = Distinct(seriesIds);
        return types == null && statuses == null && series == null ? null : new TaskFilter { TypeIds = types, StatusIds = statuses, SeriesIds = series };
    }

    /// <summary>Задачи, у которых есть номер хотя бы в одной из этих серий.</summary>
    public Guid[]? SeriesIds { get; init; }

    /// <summary>
    /// Только задачи с этими id (пересечение с остальными условиями): догрузить показанные задачи иерархии (<see cref="TaskService.ListTree"/>)
    /// теми же условиями, что и список. Не отбирает «по смыслу» — клиенты это условие не задают.
    /// </summary>
    public Guid[]? Ids { get; init; }

    /// <summary>Задачи, у которых есть исходящая связь хотя бы одного из этих типов (<see cref="Links.LinkType"/>).</summary>
    public Guid[]? LinkTypeIds { get; init; }

    /// <summary>
    /// Задачи, у которых записано хотя бы одно из этих полей (<see cref="TaskField.FieldId"/>): со значениями или дополнительное.
    /// Поле типа без значения в задаче не записано и под фильтр не подходит.
    /// </summary>
    public Guid[]? FieldIds { get; init; }

    /// <summary>Задачи, у которых есть собственное поле с одним из этих перечислений (<see cref="OwnField.EnumId"/>).</summary>
    public Guid[]? EnumIds { get; init; }

    /// <summary>
    /// Все условия по полям (И): значения, сравнения, наличие значения и подключение поля (<see cref="FieldCondition"/>).
    /// Из текста клиента условия строит <see cref="TaskService.ParseFieldFilters"/>.
    /// </summary>
    public FieldCondition[]? FieldValues { get; init; }

    /// <summary>
    /// Порядок списка: ключи по очереди (<see cref="TaskSortKey"/>), при равных — как без него (по времени создания, затем по id).
    /// null — порядок по умолчанию. Условием отбора не является: <see cref="Matches"/> его не смотрит; страницы и подсчёты не меняются.
    /// Из текста клиента ключи строит <see cref="TaskService.ParseSort"/>.
    /// </summary>
    public TaskSortKey[]? Sort { get; init; }

    public bool Matches(TaskItem task) =>
        (Ids == null || Ids.Contains(task.Id)) &&
        (TypeIds == null || TypeIds.Contains(task.TypeId)) &&
        (StatusIds == null || StatusIds.Contains(task.StatusId)) &&
        (SeriesIds == null || task.SeriesNumbers.Any(x => SeriesIds.Contains(x.SeriesId))) &&
        (LinkTypeIds == null || task.Links.Any(x => LinkTypeIds.Contains(x.TypeId))) &&
        (FieldIds == null || task.Fields.Any(x => FieldIds.Contains(x.FieldId))) &&
        (EnumIds == null || task.Fields.Any(x => x.Own?.EnumId is { } enumId && EnumIds.Contains(enumId))) &&
        (FieldValues == null || FieldValues.All(c => c.Matches(task)));
}
