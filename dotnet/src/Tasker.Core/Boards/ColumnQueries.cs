using Tasker.Core.Fields;
using Tasker.Core.Tasks;
using Tasker.Core.Tasks.Query;

namespace Tasker.Core.Boards;

internal static class ColumnQueries
{
    private readonly record struct Literal(Guid FieldId, FieldOperator Operator, string? Value);

    private readonly record struct Bound(string Value, bool Open);

    public static (BoardColumn First, BoardColumn Second, Guid Status, int SecondIndex)? FindOverlap(
        IReadOnlyList<BoardColumn> columns, IReadOnlyList<TaskPredicate?> predicates, IReadOnlyDictionary<Guid, FieldDefinition> fields)
    {
        for (var j = 1; j < columns.Count; j++)
        {
            for (var i = 0; i < j; i++)
            {
                var shared = columns[i].StatusIds.Intersect(columns[j].StatusIds).ToArray();
                if (shared.Length > 0 && !AreExclusive(predicates[i], predicates[j], fields))
                    return (columns[i], columns[j], shared[0], j);
            }
        }
        return null;
    }

    public static bool AreExclusive(TaskPredicate? a, TaskPredicate? b, IReadOnlyDictionary<Guid, FieldDefinition> fields)
    {
        if (a == null || b == null)
            return false;
        if (a is TaskAnyOf anyA)
            return anyA.Parts.All(x => AreExclusive(x, b, fields));
        if (b is TaskAnyOf anyB)
            return anyB.Parts.All(x => AreExclusive(a, x, fields));
        if (a is TaskAllOf allA)
            return allA.Parts.Any(x => AreExclusive(x, b, fields));
        if (b is TaskAllOf allB)
            return allB.Parts.Any(x => AreExclusive(a, x, fields));
        return Contradict(a, b, fields) || Contradict(b, a, fields);
    }

    private static bool Contradict(TaskPredicate x, TaskPredicate y, IReadOnlyDictionary<Guid, FieldDefinition> fields)
    {
        if (Ids(x) is var (kindX, idsX, negatedX) && Ids(y) is var (kindY, idsY, negatedY) && kindX == kindY)
        {
            if (!negatedX && !negatedY)
                return !idsX.Intersect(idsY).Any();
            if (!negatedX && negatedY)
                return idsX.All(idsY.Contains);
            return false;
        }

        return ToLiteral(x) is { } left && ToLiteral(y) is { } right && left.FieldId == right.FieldId
            && fields.TryGetValue(left.FieldId, out var field) && OneWay(left, right, field);
    }

    private static (Type Kind, Guid[] Ids, bool Negated)? Ids(TaskPredicate predicate) => predicate switch
    {
        TaskStatusIn s => (typeof(TaskStatusIn), s.Ids, false),
        TaskTypeIn t => (typeof(TaskTypeIn), t.Ids, false),
        TaskNot { Inner: TaskStatusIn s } => (typeof(TaskStatusIn), s.Ids, true),
        TaskNot { Inner: TaskTypeIn t } => (typeof(TaskTypeIn), t.Ids, true),
        _ => null
    };

    private static Literal? ToLiteral(TaskPredicate predicate) => predicate switch
    {
        TaskFieldMatches { Condition: { FieldId: { } id } c } => new Literal(id, c.Operator, c.Value),
        TaskNot { Inner: TaskFieldMatches { Condition: { FieldId: { } id } c } } => Negate(c.Operator) is { } op ? new Literal(id, op, c.Value) : null,
        _ => null
    };

    private static FieldOperator? Negate(FieldOperator op) => op switch
    {
        FieldOperator.Equal => FieldOperator.NotEqual,
        FieldOperator.Set => FieldOperator.Unset,
        FieldOperator.Unset => FieldOperator.Set,
        FieldOperator.Attached => FieldOperator.Detached,
        FieldOperator.Detached => FieldOperator.Attached,
        _ => null
    };

    private static bool IsOrdered(FieldOperator op) =>
        op is FieldOperator.Greater or FieldOperator.GreaterOrEqual or FieldOperator.Less or FieldOperator.LessOrEqual;

    private static bool RequiresValue(FieldOperator op) => op is FieldOperator.Equal or FieldOperator.Set || IsOrdered(op);

    private static bool OneWay(Literal x, Literal y, FieldDefinition field)
    {
        if (x.Operator is FieldOperator.Unset or FieldOperator.Detached && RequiresValue(y.Operator))
            return true;
        if (x.Operator == FieldOperator.Detached && y.Operator == FieldOperator.Attached)
            return true;
        if (x.Operator == FieldOperator.Equal && y.Operator == FieldOperator.NotEqual && x.Value == y.Value)
            return true;

        if (field.Multiple || !(x.Operator == FieldOperator.Equal || IsOrdered(x.Operator)) || !(y.Operator == FieldOperator.Equal || IsOrdered(y.Operator)))
            return false;
        if (x.Value == null || y.Value == null)
            return false;

        var numeric = field.Type is FieldType.Int or FieldType.Float;
        if (!numeric && field.Type != FieldType.Date)
            return x.Operator == FieldOperator.Equal && y.Operator == FieldOperator.Equal && x.Value != y.Value;

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

    private static (Bound? Low, Bound? High) Bounds(Literal f) => f.Operator switch
    {
        FieldOperator.Equal => (new Bound(f.Value!, false), new Bound(f.Value!, false)),
        FieldOperator.Greater => (new Bound(f.Value!, true), null),
        FieldOperator.GreaterOrEqual => (new Bound(f.Value!, false), null),
        FieldOperator.Less => (null, new Bound(f.Value!, true)),
        _ => (null, new Bound(f.Value!, false))
    };

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
