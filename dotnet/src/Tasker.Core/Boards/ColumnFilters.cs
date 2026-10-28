using Tasker.Core.Fields;
using Tasker.Core.Tasks;

namespace Tasker.Core.Boards;

/// <summary>
/// Условия колонки по полям (<see cref="BoardColumn.FieldConditions"/>): проверка, перевод в условия фильтра задач, запись текстом
/// и проверка «колонки не пересекаются».
/// </summary>
internal static class ColumnFilters
{
    private static bool IsOrdered(FieldOperator op) =>
        op is FieldOperator.Greater or FieldOperator.GreaterOrEqual or FieldOperator.Less or FieldOperator.LessOrEqual;

    /// <summary>У условия есть значение для сравнения (то есть оператор записывается как «имя, знак, значение»).</summary>
    private static bool HasOperand(FieldOperator op) => op is FieldOperator.Equal or FieldOperator.NotEqual || IsOrdered(op);

    /// <summary>Условию нужно хотя бы одно значение у поля: пустое или не подключённое поле ему не подходит.</summary>
    private static bool RequiresValue(FieldOperator op) => op is FieldOperator.Equal or FieldOperator.Set || IsOrdered(op);

    /// <summary>
    /// Условие колонки как условие фильтра задач. То, что при разборе текста считается по каталогу (типы, в которых есть поле, — для
    /// «подключено»; число — для сравнений), считается здесь заново при каждом чтении: колонка видит правки типов задач сразу.
    /// </summary>
    public static FieldCondition[] ToConditions(
        IEnumerable<ColumnFieldFilter> filters, IReadOnlyCollection<TaskType> types, IReadOnlyDictionary<Guid, FieldDefinition> fields) =>
        filters.Select(f =>
        {
            // Поле условия — поле каталога (хранится по id); имя и тип берутся из каталога при каждом чтении, переименование колонку не ломает.
            var field = fields[f.FieldId];
            return f.Operator switch
            {
                FieldOperator.Attached or FieldOperator.Detached => new FieldCondition(
                    field.Name, field.Type, f.Operator, FieldId: f.FieldId,
                    TypeIds: types.Where(t => t.Fields.Any(x => x.FieldId == f.FieldId)).Select(t => t.Id).ToArray()),
                _ when IsOrdered(f.Operator) => new FieldCondition(field.Name, field.Type, f.Operator, f.Value, FieldNumbers.Parse(f.Value!), f.FieldId),
                _ => new FieldCondition(field.Name, field.Type, f.Operator, f.Value, FieldId: f.FieldId)
            };
        }).ToArray();

    /// <summary>Условие в виде, понятном клиенту (<c>Имя=значение</c>, <c>Имя&gt;=3</c>, <c>Имя:set</c>): тот же текст читает разбор условий <c>--field</c>.</summary>
    public static string Text(ColumnFieldFilter filter, IReadOnlyDictionary<Guid, FieldDefinition> fields, IReadOnlyDictionary<Guid, FieldEnum> enums)
    {
        if (!fields.TryGetValue(filter.FieldId, out var field))
            return $"{filter.FieldId}{Symbol(filter.Operator)}{filter.Value}";
        var value = filter.Value == null ? null
            : FieldValues.Texts(field.Type, field.EnumId is { } enumId ? enums.GetValueOrDefault(enumId) : null, [filter.Value])[0];
        return field.Name + Symbol(filter.Operator) + value;
    }

    private static string Symbol(FieldOperator op) => op switch
    {
        FieldOperator.Equal => "=",
        FieldOperator.NotEqual => "!=",
        FieldOperator.Greater => ">",
        FieldOperator.GreaterOrEqual => ">=",
        FieldOperator.Less => "<",
        FieldOperator.LessOrEqual => "<=",
        FieldOperator.Set => ":set",
        FieldOperator.Unset => ":unset",
        FieldOperator.Attached => ":attached",
        FieldOperator.Detached => ":detached",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null)
    };

    /// <summary>
    /// Проверка условия против каталога: поле есть, оператор подходит его типу, значение есть там, где нужно, и разбирается по типу
    /// (у enum — id значения перечисления). Условия, пришедшие из хранилища, проверяются тем же правилом, что введённые.
    /// </summary>
    /// <exception cref="TaskerValidationException">Условие не подходит каталогу.</exception>
    public static void Validate(
        ColumnFieldFilter filter, IReadOnlyDictionary<Guid, FieldDefinition> fields, IReadOnlyDictionary<Guid, FieldEnum> enums, string where)
    {
        if (!Enum.IsDefined(filter.Operator))
            throw new TaskerValidationException($"{where}: unknown operator '{filter.Operator}'");
        if (!fields.TryGetValue(filter.FieldId, out var field))
            throw new TaskerValidationException($"{where}: field {filter.FieldId} not found in the project's catalog");

        if (!HasOperand(filter.Operator))
        {
            if (filter.Value != null)
                throw new TaskerValidationException($"{where}: '{Symbol(filter.Operator)}' on field '{field.Name}' takes no value");
            return;
        }

        if (filter.Value == null)
            throw new TaskerValidationException($"{where}: '{Symbol(filter.Operator)}' on field '{field.Name}' needs a value");
        if (IsOrdered(filter.Operator) && field.Type is not (FieldType.Int or FieldType.Float or FieldType.Date))
            throw new TaskerValidationException(
                $"{where}: '{Symbol(filter.Operator)}' does not apply to field '{field.Name}' of type {field.Type.ToString().ToLowerInvariant()} " +
                "(only int, float and date can be compared; use = or !=)");

        var enumeration = field.EnumId is { } enumId ? enums.GetValueOrDefault(enumId) : null;
        string canonical;
        try
        {
            canonical = FieldValues.Normalize(field.Name, field.Type, false, enumeration, [filter.Value])[0];
        }
        catch (TaskerValidationException e)
        {
            throw new TaskerValidationException($"{where}: {e.Message}");
        }
        // Хранится только каноническая запись (у enum — id значения): по ней переименование значения колонку не ломает.
        if (canonical != filter.Value)
            throw new TaskerValidationException($"{where}: value '{filter.Value}' of field '{field.Name}' is not in canonical form ('{canonical}')");
    }

    /// <summary>
    /// Заведомо ли ни одна задача не попадёт в обе колонки: хотя бы одна пара их условий по одному полю не выполняется вместе.
    /// Проверка осторожная — «да» только когда это следует из самих условий (для поля с несколькими значениями — только из условий,
    /// верных при любом наборе значений); пересечение, которое не доказано, считается пересечением.
    /// </summary>
    public static bool AreExclusive(BoardColumn a, BoardColumn b, IReadOnlyDictionary<Guid, FieldDefinition> fields) =>
        a.FieldConditions.Any(x => b.FieldConditions.Any(y =>
            x.FieldId == y.FieldId && fields.TryGetValue(x.FieldId, out var field) && (OneWay(x, y, field) || OneWay(y, x, field))));

    /// <summary>
    /// Первая пара колонок, у которых есть общий статус и условия которых не исключают друг друга (<see cref="AreExclusive"/>): задача показывалась бы
    /// в обеих. null — доска корректна.
    /// </summary>
    public static (BoardColumn First, BoardColumn Second, Guid Status, int SecondIndex)? FindOverlap(
        IReadOnlyList<BoardColumn> columns, IReadOnlyDictionary<Guid, FieldDefinition> fields)
    {
        for (var j = 1; j < columns.Count; j++)
        {
            for (var i = 0; i < j; i++)
            {
                var shared = columns[i].StatusIds.Intersect(columns[j].StatusIds).ToArray();
                if (shared.Length > 0 && !AreExclusive(columns[i], columns[j], fields))
                    return (columns[i], columns[j], shared[0], j);
            }
        }
        return null;
    }

    private static bool OneWay(ColumnFieldFilter x, ColumnFieldFilter y, FieldDefinition field)
    {
        // Нет значений (поле пусто или не подключено) — несовместимо с условием, которому нужно значение; «не подключено» — с «подключено».
        if (x.Operator is FieldOperator.Unset or FieldOperator.Detached && RequiresValue(y.Operator))
            return true;
        if (x.Operator == FieldOperator.Detached && y.Operator == FieldOperator.Attached)
            return true;
        if (x.Operator == FieldOperator.Equal && y.Operator == FieldOperator.NotEqual && x.Value == y.Value)
            return true;

        // Дальше — только поле с одним значением и условия «равно» и сравнения: у списка значений два условия могут выполняться разными значениями.
        if (field.Multiple || !(x.Operator == FieldOperator.Equal || IsOrdered(x.Operator)) || !(y.Operator == FieldOperator.Equal || IsOrdered(y.Operator)))
            return false;

        var numeric = field.Type is FieldType.Int or FieldType.Float;
        if (!numeric && field.Type != FieldType.Date)
            return x.Operator == FieldOperator.Equal && y.Operator == FieldOperator.Equal && x.Value != y.Value;

        // Каждое условие — отрезок на оси значений; пустое пересечение отрезков — колонки не пересекаются.
        int Compare(string left, string right) =>
            numeric ? FieldNumbers.Parse(left)!.Value.CompareTo(FieldNumbers.Parse(right)!.Value) : string.CompareOrdinal(left, right);
        var (lowX, highX) = Bounds(x);
        var (lowY, highY) = Bounds(y);
        var low = Tighter(lowX, lowY, Compare, higher: true);
        var high = Tighter(highX, highY, Compare, higher: false);
        if (low == null || high == null)
            return false;
        var order = Compare(low.Value.Value, high.Value.Value);
        return order > 0 || order == 0 && (low.Value.Open || high.Value.Open);
    }

    private readonly record struct Bound(string Value, bool Open);

    private static (Bound? Low, Bound? High) Bounds(ColumnFieldFilter f) => f.Operator switch
    {
        FieldOperator.Equal => (new Bound(f.Value!, false), new Bound(f.Value!, false)),
        FieldOperator.Greater => (new Bound(f.Value!, true), null),
        FieldOperator.GreaterOrEqual => (new Bound(f.Value!, false), null),
        FieldOperator.Less => (null, new Bound(f.Value!, true)),
        _ => (null, new Bound(f.Value!, false))
    };

    /// <summary>Из двух границ одной стороны — более тугая (нижняя — большая, верхняя — меньшая; при равенстве — открытая).</summary>
    private static Bound? Tighter(Bound? a, Bound? b, Func<string, string, int> compare, bool higher)
    {
        if (a == null || b == null)
            return a ?? b;
        var order = compare(a.Value.Value, b.Value.Value);
        if (order == 0)
            return a.Value.Open ? a : b;
        return (order > 0) == higher ? a : b;
    }
}
