namespace Tasker.Core.TaskSeries;

/// <summary>Номер задачи в серии. У задачи по одному на каждую серию, в которой она состоит.</summary>
public record TaskSeriesNumber(Guid SeriesId, int Number);

/// <summary>Несколько задач с одним номером в серии — после слияния веток git. Задачи отсортированы по <c>CreatedAt</c>, затем по Guid.</summary>
public record NumberConflict(Guid SeriesId, int Number, Guid[] TaskIds);

/// <summary>Несколько серий проекта с одним префиксом — после слияния веток git. Серии отсортированы по Guid.</summary>
public record PrefixConflict(string Prefix, Guid[] SeriesIds);
