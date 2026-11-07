using HotChocolate.Types;
using Tasker.Core.Tasks.Search;

namespace Tasker.Web.Graph.Types;

internal sealed class TaskSearchHitType : ObjectType<TaskSearchHit>
{
    protected override void Configure(IObjectTypeDescriptor<TaskSearchHit> descriptor)
    {
        descriptor.Name("TaskSearchHit");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Task).Type<NonNullType<TaskItemType>>();
        descriptor.Field(x => x.Field);
        descriptor.Field(x => x.Match);
        descriptor.Field(x => x.SwitchedLayout);
        descriptor.Field(x => x.Score);
        descriptor.Field(x => x.Snippet).Type<TaskSearchSnippetType>();
    }
}

internal sealed class TaskSearchSnippetType : ObjectType<TaskSearchSnippet>
{
    protected override void Configure(IObjectTypeDescriptor<TaskSearchSnippet> descriptor)
    {
        descriptor.Name("TaskSearchSnippet");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Text);
        descriptor.Field(x => x.Ranges).Type<NonNullType<ListType<NonNullType<TaskSearchRangeType>>>>();
    }
}

internal sealed class TaskSearchRangeType : ObjectType<TaskSearchRange>
{
    protected override void Configure(IObjectTypeDescriptor<TaskSearchRange> descriptor)
    {
        descriptor.Name("TaskSearchRange");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Start);
        descriptor.Field(x => x.Length);
    }
}
