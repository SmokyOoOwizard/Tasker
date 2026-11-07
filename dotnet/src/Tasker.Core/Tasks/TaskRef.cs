using Tasker.Core.TaskSeries;

namespace Tasker.Core.Tasks;

public record TaskRef(Guid Id, string Title, IReadOnlyList<TaskSeriesNumber> SeriesNumbers, Guid[] ParentIds);
