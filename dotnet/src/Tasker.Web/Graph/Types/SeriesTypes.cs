using HotChocolate.Types;
using Tasker.Core.Links;
using Tasker.Core.TaskSeries;

namespace Tasker.Web.Graph.Types;

internal sealed class SeriesType : ObjectType<Series>
{
    protected override void Configure(IObjectTypeDescriptor<Series> descriptor)
    {
        descriptor.Name("Series");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.ProjectId);
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.Prefix);
        descriptor.Field(x => x.Version);
    }
}

internal sealed class TaskSeriesNumberType : ObjectType<TaskSeriesNumber>
{
    protected override void Configure(IObjectTypeDescriptor<TaskSeriesNumber> descriptor)
    {
        descriptor.Name("TaskSeriesNumber");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.SeriesId);
        descriptor.Field(x => x.Number);
    }
}

internal sealed class SeriesHealthType : ObjectType<SeriesHealth>
{
    protected override void Configure(IObjectTypeDescriptor<SeriesHealth> descriptor)
    {
        descriptor.Name("SeriesHealth");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.NumberConflicts);
        descriptor.Field(x => x.PrefixConflicts);
        descriptor.Field(x => x.TasksWithInvalidSeries);
        descriptor.Field(x => x.UnreadableSeriesFiles);
        descriptor.Field(x => x.NeedsAttention);
    }
}

internal sealed class NumberConflictType : ObjectType<NumberConflict>
{
    protected override void Configure(IObjectTypeDescriptor<NumberConflict> descriptor)
    {
        descriptor.Name("NumberConflict");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.SeriesId);
        descriptor.Field(x => x.Number);
        descriptor.Field(x => x.TaskIds);
    }
}

internal sealed class PrefixConflictType : ObjectType<PrefixConflict>
{
    protected override void Configure(IObjectTypeDescriptor<PrefixConflict> descriptor)
    {
        descriptor.Name("PrefixConflict");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Prefix);
        descriptor.Field(x => x.SeriesIds);
    }
}

internal sealed class ConflictPlanType : ObjectType<ConflictPlan>
{
    protected override void Configure(IObjectTypeDescriptor<ConflictPlan> descriptor)
    {
        descriptor.Name("ConflictPlan");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.SeriesId);
        descriptor.Field(x => x.Token);
        descriptor.Field(x => x.Steps);
    }
}

internal sealed class ConflictPlanStepType : ObjectType<ConflictPlanStep>
{
    protected override void Configure(IObjectTypeDescriptor<ConflictPlanStep> descriptor)
    {
        descriptor.Name("ConflictPlanStep");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.TaskId);
        descriptor.Field(x => x.TaskTitle);
        descriptor.Field(x => x.OldNumber);
        descriptor.Field(x => x.NewNumber);
        descriptor.Field(x => x.KeeperId);
        descriptor.Field(x => x.KeeperTitle);
    }
}

internal sealed class LinkHealthType : ObjectType<LinkHealth>
{
    protected override void Configure(IObjectTypeDescriptor<LinkHealth> descriptor)
    {
        descriptor.Name("LinkHealth");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.TasksWithInvalidLinks);
        descriptor.Field(x => x.UnreadableFiles);
        descriptor.Field(x => x.Cycles);
        descriptor.Field(x => x.NeedsAttention);
    }
}

internal sealed class LinkCycleType : ObjectType<LinkCycle>
{
    protected override void Configure(IObjectTypeDescriptor<LinkCycle> descriptor)
    {
        descriptor.Name("LinkCycle");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.TypeId);
        descriptor.Field(x => x.TypeName);
        descriptor.Field(x => x.Path);
    }
}

internal sealed class LinkCycleTaskType : ObjectType<LinkCycleTask>
{
    protected override void Configure(IObjectTypeDescriptor<LinkCycleTask> descriptor)
    {
        descriptor.Name("LinkCycleTask");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.Title);
        descriptor.Field(x => x.SeriesNumbers);
    }
}
