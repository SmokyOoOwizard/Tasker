using System.Linq.Expressions;
using System.Reflection;
using Tasker.Core.Fields;
using Tasker.Core.Tasks;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

/// <summary>
/// Порядок списка задач (<see cref="TaskFilter.Sort"/>) запросом к БД: ключ — выражение в <c>ORDER BY</c> (подзапросы к значениям полей и номерам серий,
/// <c>CASE</c> по таблице мест, которую раскладывает ядро), без загрузки задач. У каждого ключа, который может отсутствовать, сначала идёт признак
/// «значения нет» — такие задачи в конце и при возрастании, и при убывании. При равных ключах — порядок по умолчанию (создание, затем id).
/// </summary>
internal static class TaskSorting
{
    /// <summary>Строка-ключ подзапроса: значение поля задачи или номер в серии в виде, пригодном для сравнения.</summary>
    internal class SortValue
    {
        public Guid TaskId { get; set; }
        public Guid FieldId { get; set; }
        public int Position { get; set; }
        public string? Text { get; set; }
        public double? Number { get; set; }
        public int? Rank { get; set; }
    }

    /// <summary>Номер серии в одном числе: место серии в старшей части, номер — в младшей (наименьшее число — серия с меньшим местом и её номер).</summary>
    private const double SeriesNumberRange = 4294967296;

    public static IOrderedQueryable<TaskDbModel> Apply(AppDbContext context, IQueryable<TaskDbModel> source, TaskSortKey[]? keys)
    {
        IOrderedQueryable<TaskDbModel>? ordered = null;
        foreach (var key in keys ?? [])
        {
            var desc = key.Descending;
            switch (key.Target)
            {
                case SortTarget.Title:
                    ordered = Then(source, ordered, x => x.Title.ToLower(), desc);
                    break;
                case SortTarget.Created:
                    ordered = Then(source, ordered, x => x.CreatedAt, desc);
                    break;
                case SortTarget.Updated:
                    ordered = Then(source, ordered, x => x.UpdatedAt, desc);
                    break;
                case SortTarget.Type:
                    ordered = ThenNullLast(source, ordered, TypeRank(key.TypeRanks ?? []), desc);
                    break;
                case SortTarget.Status:
                    ordered = ThenNullLast(source, ordered, StatusRank(key.StatusRanks ?? []), desc);
                    break;
                case SortTarget.Series:
                {
                    var numbers = context.TaskSeriesNumbers.Select(SeriesProjection(key.SeriesRanks ?? []));
                    ordered = ThenNullLast(source, ordered, x => numbers.Where(r => r.TaskId == x.Id).Min(r => r.Number), desc);
                    break;
                }
                case SortTarget.Field:
                    ordered = Field(context, source, ordered, key.Field!, desc);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(keys), key.Target, null);
            }
        }

        return ordered == null
            ? source.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            : ordered.ThenBy(x => x.CreatedAt).ThenBy(x => x.Id);
    }

    /// <summary>Первое значение поля задачи (в порядке записи): число, текст или место в перечислении — как у ключа этого типа.</summary>
    private static IOrderedQueryable<TaskDbModel> Field(
        AppDbContext context, IQueryable<TaskDbModel> source, IOrderedQueryable<TaskDbModel>? ordered, SortField field, bool desc)
    {
        var (key, ownType) = (field.Key, (int)field.Type);
        var rows = field.FieldId is { } catalogId
            ? context.TaskFieldValues.Where(v => v.FieldId == catalogId || (v.OwnKey == key && v.OwnType == ownType))
            : context.TaskFieldValues.Where(v => v.OwnKey == key && v.OwnType == ownType);
        var values = rows.Select(ValueProjection(field));

        switch (field.Type)
        {
            case FieldType.Int or FieldType.Float:
                return ThenNullLast(source, ordered,
                    x => values.Where(r => r.TaskId == x.Id).OrderBy(r => r.Position).ThenBy(r => r.FieldId).Select(r => r.Number).FirstOrDefault(), desc);
            case FieldType.Enum:
                return ThenNullLast(source, ordered,
                    x => values.Where(r => r.TaskId == x.Id).OrderBy(r => r.Position).ThenBy(r => r.FieldId).Select(r => r.Rank).FirstOrDefault(), desc);
            default:
                return ThenNullLast(source, ordered,
                    x => values.Where(r => r.TaskId == x.Id).OrderBy(r => r.Position).ThenBy(r => r.FieldId).Select(r => r.Text).FirstOrDefault(), desc);
        }
    }

    // ---- упорядочивание ----

    private static IOrderedQueryable<TaskDbModel> Then<TKey>(
        IQueryable<TaskDbModel> source, IOrderedQueryable<TaskDbModel>? ordered, Expression<Func<TaskDbModel, TKey>> key, bool desc) =>
        ordered == null
            ? desc ? source.OrderByDescending(key) : source.OrderBy(key)
            : desc ? ordered.ThenByDescending(key) : ordered.ThenBy(key);

    /// <summary>Ключ, которого у задачи может не быть: сначала «ключа нет» (false раньше true), потом сам ключ.</summary>
    private static IOrderedQueryable<TaskDbModel> ThenNullLast<TKey>(
        IQueryable<TaskDbModel> source, IOrderedQueryable<TaskDbModel>? ordered, Expression<Func<TaskDbModel, TKey>> key, bool desc)
    {
        var missing = Expression.Lambda<Func<TaskDbModel, bool>>(Expression.Equal(key.Body, Expression.Constant(null, typeof(TKey))), key.Parameters);
        return Then(source, Then(source, ordered, missing, false), key, desc);
    }

    // ---- таблицы мест ----

    private static Expression<Func<TaskDbModel, int?>> TypeRank(IdRank[] ranks)
    {
        var x = Expression.Parameter(typeof(TaskDbModel), "x");
        var typeId = Expression.Property(x, nameof(TaskDbModel.TypeId));
        return Expression.Lambda<Func<TaskDbModel, int?>>(Chain(ranks, r => Expression.Equal(typeId, Expression.Constant(r.Id)), r => r.Rank), x);
    }

    private static Expression<Func<TaskDbModel, int?>> StatusRank(StatusRank[] ranks)
    {
        var x = Expression.Parameter(typeof(TaskDbModel), "x");
        var typeId = Expression.Property(x, nameof(TaskDbModel.TypeId));
        var statusId = Expression.Property(x, nameof(TaskDbModel.StatusId));
        return Expression.Lambda<Func<TaskDbModel, int?>>(
            Chain(ranks, r => Expression.AndAlso(
                Expression.Equal(typeId, Expression.Constant(r.TypeId)), Expression.Equal(statusId, Expression.Constant(r.StatusId))), r => r.Rank), x);
    }

    /// <summary><c>CASE WHEN условие THEN место … ELSE NULL END</c> по таблице мест.</summary>
    private static Expression Chain<T>(IEnumerable<T> ranks, Func<T, Expression> test, Func<T, int> rank)
    {
        Expression body = Expression.Constant(null, typeof(int?));
        foreach (var item in ranks.Reverse())
            body = Expression.Condition(test(item), Expression.Constant((int?)rank(item), typeof(int?)), body);
        return body;
    }

    private static Expression<Func<TaskSeriesNumberDbModel, SortValue>> SeriesProjection(IdRank[] ranks)
    {
        var n = Expression.Parameter(typeof(TaskSeriesNumberDbModel), "n");
        var rank = Chain(ranks, r => Expression.Equal(Expression.Property(n, nameof(TaskSeriesNumberDbModel.SeriesId)), Expression.Constant(r.Id)), r => r.Rank);
        // место * 2^32 + номер: null, если серии нет в таблице мест.
        var number = Expression.Add(
            Expression.Multiply(Expression.Convert(rank, typeof(double?)), Expression.Constant((double?)SeriesNumberRange, typeof(double?))),
            Expression.Convert(Expression.Property(n, nameof(TaskSeriesNumberDbModel.Number)), typeof(double?)));
        return Expression.Lambda<Func<TaskSeriesNumberDbModel, SortValue>>(
            Init(Expression.Property(n, nameof(TaskSeriesNumberDbModel.TaskId)), Expression.Constant(Guid.Empty), Expression.Constant(0), null, number, null), n);
    }

    private static Expression<Func<TaskFieldValueDbModel, SortValue>> ValueProjection(SortField field)
    {
        var v = Expression.Parameter(typeof(TaskFieldValueDbModel), "v");
        Expression value = Expression.Property(v, nameof(TaskFieldValueDbModel.Value));
        Expression? text = null, number = null, rank = null;
        switch (field.Type)
        {
            case FieldType.Int or FieldType.Float:
                number = Expression.Property(v, nameof(TaskFieldValueDbModel.Number));
                break;
            case FieldType.String:
                text = Expression.Call(value, typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!);
                break;
            case FieldType.Enum:
                rank = Chain((field.EnumValues ?? []).Select((x, i) => (Value: x, Rank: i)), r => Expression.Equal(value, Expression.Constant(r.Value)), r => r.Rank);
                break;
            default:
                text = value;
                break;
        }
        return Expression.Lambda<Func<TaskFieldValueDbModel, SortValue>>(
            Init(Expression.Property(v, nameof(TaskFieldValueDbModel.TaskId)), Expression.Property(v, nameof(TaskFieldValueDbModel.FieldId)),
                Expression.Property(v, nameof(TaskFieldValueDbModel.Position)), text, number, rank), v);
    }

    private static MemberInitExpression Init(Expression taskId, Expression fieldId, Expression position, Expression? text, Expression? number, Expression? rank)
    {
        MemberBinding Bind(string name, Expression? value, Type type) =>
            Expression.Bind(typeof(SortValue).GetProperty(name)!, value ?? Expression.Constant(null, type));
        return Expression.MemberInit(
            Expression.New(typeof(SortValue)),
            Bind(nameof(SortValue.TaskId), taskId, typeof(Guid)),
            Bind(nameof(SortValue.FieldId), fieldId, typeof(Guid)),
            Bind(nameof(SortValue.Position), position, typeof(int)),
            Bind(nameof(SortValue.Text), text, typeof(string)),
            Bind(nameof(SortValue.Number), number, typeof(double?)),
            Bind(nameof(SortValue.Rank), rank, typeof(int?)));
    }
}
