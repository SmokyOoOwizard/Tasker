namespace Tasker.Core.Tasks.Query;

public abstract record TaskPredicate
{
    public abstract bool Matches(TaskItem task);
}

public sealed record TaskAllOf(TaskPredicate[] Parts) : TaskPredicate
{
    public override bool Matches(TaskItem task) => Parts.All(x => x.Matches(task));
}

public sealed record TaskAnyOf(TaskPredicate[] Parts) : TaskPredicate
{
    public override bool Matches(TaskItem task) => Parts.Any(x => x.Matches(task));
}

public sealed record TaskNot(TaskPredicate Inner) : TaskPredicate
{
    public override bool Matches(TaskItem task) => !Inner.Matches(task);
}

public sealed record TaskStatusIn(Guid[] Ids) : TaskPredicate
{
    public override bool Matches(TaskItem task) => Ids.Contains(task.StatusId);
}

public sealed record TaskTypeIn(Guid[] Ids) : TaskPredicate
{
    public override bool Matches(TaskItem task) => Ids.Contains(task.TypeId);
}

public sealed record TaskInSeries(Guid[] Ids) : TaskPredicate
{
    public override bool Matches(TaskItem task) => task.SeriesNumbers.Any(x => Ids.Contains(x.SeriesId));
}

public sealed record TaskInOtherSeries(Guid[] Ids) : TaskPredicate
{
    public override bool Matches(TaskItem task) => task.SeriesNumbers.Any(x => !Ids.Contains(x.SeriesId));
}

public sealed record TaskTitleContains(string Folded) : TaskPredicate
{
    public override bool Matches(TaskItem task) => TaskSortNames.Fold(task.Title).Contains(Folded, StringComparison.Ordinal);
}

public sealed record TaskTitleEquals(string Folded) : TaskPredicate
{
    public override bool Matches(TaskItem task) => TaskSortNames.Fold(task.Title) == Folded;
}

public sealed record TaskFieldMatches(FieldCondition Condition) : TaskPredicate
{
    public override bool Matches(TaskItem task) => Condition.Matches(task);
}

public sealed record TaskFieldHasOther(FieldCondition Condition, string[] Values) : TaskPredicate
{
    public override bool Matches(TaskItem task) =>
        task.Fields.Where(Condition.Covers).SelectMany(x => x.Values).Any(x => !Values.Contains(x));
}

public sealed record TaskHasOutgoingLink(Guid[] TypeIds) : TaskPredicate
{
    public override bool Matches(TaskItem task) => task.Links.Any(x => TypeIds.Contains(x.TypeId));
}

public sealed record TaskHasIncomingLink(Guid[] TypeIds) : TaskPredicate
{
    public override bool Matches(TaskItem task) =>
        throw new NotSupportedException("Incoming links are not stored in the task: this condition is checked by the storage");
}

public enum TaskTimeProperty { Created, Updated }

public sealed record TaskTimeRange(TaskTimeProperty Property, long? FromTicks, long? BeforeTicks) : TaskPredicate
{
    public override bool Matches(TaskItem task)
    {
        var ticks = (Property == TaskTimeProperty.Created ? task.CreatedAt : task.UpdatedAt).UtcTicks;
        return (FromTicks == null || ticks >= FromTicks) && (BeforeTicks == null || ticks < BeforeTicks);
    }
}
