using HotChocolate.Types;
using Tasker.Core.Links;

namespace Tasker.Web.Graph.Types;

internal sealed class LinkTypeType : ObjectType<LinkType>
{
    protected override void Configure(IObjectTypeDescriptor<LinkType> descriptor)
    {
        descriptor.Name("LinkType");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.ProjectId);
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.OutwardName);
        descriptor.Field(x => x.InwardName);
        descriptor.Field(x => x.Version);
        descriptor.Field(x => x.AllowCycles);
        descriptor.Field(x => x.Hierarchical);
        descriptor.Field(x => x.IsSymmetric);
    }
}

internal sealed class TaskLinkType : ObjectType<TaskLink>
{
    protected override void Configure(IObjectTypeDescriptor<TaskLink> descriptor)
    {
        descriptor.Name("TaskLink");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.TypeId);
        descriptor.Field(x => x.TargetId);
    }
}

internal sealed class TaskLinkViewType : ObjectType<TaskLinkView>
{
    protected override void Configure(IObjectTypeDescriptor<TaskLinkView> descriptor)
    {
        descriptor.Name("TaskLinkView");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.TypeId);
        descriptor.Field(x => x.TypeName);
        descriptor.Field(x => x.Direction);
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.Task);
    }
}

internal sealed class LinkedTaskType : ObjectType<LinkedTask>
{
    protected override void Configure(IObjectTypeDescriptor<LinkedTask> descriptor)
    {
        descriptor.Name("LinkedTask");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.Title);
        descriptor.Field(x => x.StatusId);
        descriptor.Field(x => x.SeriesNumbers);
    }
}
