using HotChocolate.Types;
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
