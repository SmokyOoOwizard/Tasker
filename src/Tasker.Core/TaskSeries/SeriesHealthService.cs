namespace Tasker.Core.TaskSeries;

/// <summary>Состояние серий проекта — что нужно чистить и исправлять.</summary>
/// <param name="TasksWithInvalidSeries">Сколько задач ссылаются на серию, которой нет (всегда 0, если есть нечитаемые файлы серий: тогда не понять).</param>
/// <param name="UnreadableSeriesFiles">Сколько файлов серий не удалось прочитать.</param>
public record SeriesHealth(
    NumberConflict[] NumberConflicts,
    PrefixConflict[] PrefixConflicts,
    int TasksWithInvalidSeries,
    int UnreadableSeriesFiles)
{
    public bool NeedsAttention =>
        NumberConflicts.Length > 0 || PrefixConflicts.Length > 0 || TasksWithInvalidSeries > 0 || UnreadableSeriesFiles > 0;
}

/// <summary>Только чтение: ничего не меняет. Им пользуются сверка (<c>tasker sync</c>) и <c>cleanup --check</c>.</summary>
public class SeriesHealthService(Tasks.ITaskStorage tasks, ISeriesStorage series)
{
    public async Task<SeriesHealth> Check(Guid projectId, CancellationToken ct = default)
    {
        var numberConflicts = await tasks.GetNumberConflicts(projectId, ct);
        var all = await series.GetAll(projectId, ct);
        var unreadable = await series.CountUnreadable(projectId, ct);

        // Нечитаемая серия выглядела бы несуществующей: тогда ссылки на неё не считаем недействительными.
        var invalid = unreadable > 0
            ? 0
            : (await tasks.GetWithSeriesNotIn(projectId, all.Select(x => x.Id).ToArray(), ct)).Length;

        return new SeriesHealth(numberConflicts, FindPrefixConflicts(all), invalid, unreadable);
    }

    /// <summary>Серии с одним префиксом (точное совпадение): группы по префиксу (ordinal), id по возрастанию.</summary>
    internal static PrefixConflict[] FindPrefixConflicts(IEnumerable<Series> all) => all
        .GroupBy(x => x.Prefix, StringComparer.Ordinal)
        .Where(g => g.Count() > 1)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => new PrefixConflict(g.Key, g.Select(x => x.Id).OrderBy(x => x.ToString("D"), StringComparer.Ordinal).ToArray()))
        .ToArray();
}
