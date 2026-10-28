using System.Text;
using Tasker.Core.Fields;
using Tasker.Core.Tasks;

namespace Tasker.Storage.Files.Index;

/// <summary>
/// Порядок списка задач (<see cref="TaskFilter.Sort"/>) в SQL индекса: ключ — выражение по <c>files</c> и таблицам значений, в
/// <c>ORDER BY</c> без загрузки задач. У каждого ключа задачи без значения (<c>NULL</c>) идут в конце — и при возрастании, и при убывании;
/// при равных ключах — порядок по умолчанию (<c>sort_num</c>, <c>id</c>). Идентификаторы в выражения вставляются как литералы:
/// это Guid в виде <c>D</c> и имена в нижнем регистре, они проходят через <see cref="Literal(string)"/>.
/// </summary>
internal sealed partial class WorkspaceIndex
{
    private const string DefaultOrder = "sort_num, id";

    /// <summary>Номер серии в одном ключе: место серии в старшей части, номер — в младшей (чтобы взять наименьший за один <c>min</c>).</summary>
    private const long SeriesNumberRange = 4294967296;

    private static string OrderBy(IndexQuery query)
    {
        if (query.Kind != IndexKind.Task)
            return " ORDER BY sort_text, id";
        if (query.Sort is not { Length: > 0 } keys)
            return $" ORDER BY {DefaultOrder}";

        var order = new StringBuilder(" ORDER BY ");
        foreach (var key in keys)
        {
            var expression = SortExpression(key);
            var direction = key.Descending ? "DESC" : "ASC";
            // NULL (нет значения) — в конце при любом направлении: сначала «есть ли значение», потом само значение.
            if (SortCanBeNull(key))
                order.Append($"({expression}) IS NULL, ");
            order.Append($"({expression}) {direction}, ");
        }
        return order.Append(DefaultOrder).ToString();
    }

    private static bool SortCanBeNull(TaskSortKey key) => key.Target is not (SortTarget.Title or SortTarget.Created or SortTarget.Updated);

    private static string SortExpression(TaskSortKey key) => key.Target switch
    {
        SortTarget.Title => "sort_text",
        SortTarget.Created => "sort_num",
        SortTarget.Updated => "sort_updated",
        SortTarget.Type => Case("type_id", key.TypeRanks ?? [], x => Literal(x.Id), x => x.Rank),
        SortTarget.Status => Case(null, key.StatusRanks ?? [],
            x => $"type_id = {Literal(x.TypeId)} AND status_id = {Literal(x.StatusId)}", x => x.Rank),
        SortTarget.Series =>
            $"SELECT min(({Case("s.series_id", key.SeriesRanks ?? [], x => Literal(x.Id), x => x.Rank)}) * {SeriesNumberRange} + s.number) " +
            "FROM task_series s WHERE s.path = files.path",
        SortTarget.Field => FieldExpression(key.Field!),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key.Target, null)
    };

    /// <summary>
    /// Первое значение поля задачи (в порядке записи — <c>rowid</c>): число (int, float), текст даты и логического, текст строки без регистра,
    /// у enum — место значения в перечислении. Строки поля — поле каталога или собственное поле с тем же именем и типом, как в условии фильтра.
    /// </summary>
    private static string FieldExpression(SortField field)
    {
        var column = field.Type switch
        {
            FieldType.Int or FieldType.Float => "v.number",
            FieldType.String => "lower(v.value)",
            FieldType.Enum => Case("v.value", (field.EnumValues ?? []).Select((x, i) => (Value: x, Rank: i)).ToArray(), x => Literal(x.Value), x => x.Rank),
            _ => "v.value"
        };
        var row = $"(v.own_name = {Literal(field.Key)} AND v.own_type = {(int)field.Type})";
        if (field.FieldId is { } catalog)
            row = $"(v.field_id = {Literal(catalog)} OR {row})";
        return $"SELECT {column} FROM task_field_values v WHERE v.path = files.path AND {row} ORDER BY v.rowid LIMIT 1";
    }

    /// <summary>
    /// <c>CASE</c> по таблице мест: <paramref name="subject"/> — сравниваемый столбец (<c>CASE столбец WHEN значение THEN место</c>) или null — тогда
    /// <paramref name="when"/> даёт условие целиком. Нет в таблице — <c>NULL</c> (последним).
    /// </summary>
    private static string Case<T>(string? subject, IReadOnlyCollection<T> ranks, Func<T, string> when, Func<T, int> rank)
    {
        if (ranks.Count == 0)
            return "NULL";
        var text = new StringBuilder("CASE").Append(subject == null ? "" : " " + subject);
        foreach (var item in ranks)
            text.Append($" WHEN {when(item)} THEN {rank(item)}");
        return text.Append(" END").ToString();
    }

    private static string Literal(Guid id) => Literal(id.ToString("D"));

    private static string Literal(string text) => "'" + text.Replace("'", "''") + "'";
}
