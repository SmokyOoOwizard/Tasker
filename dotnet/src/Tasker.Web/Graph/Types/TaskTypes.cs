using HotChocolate.Types;
using Tasker.Core.Tasks;

namespace Tasker.Web.Graph.Types;

internal sealed class TaskTypeType : ObjectType<TaskType>
{
    protected override void Configure(IObjectTypeDescriptor<TaskType> descriptor)
    {
        descriptor.Name("TaskType");
        descriptor.BindFieldsExplicitly();
        Describe(descriptor);
    }

    public static void Describe<T>(IObjectTypeDescriptor<T> descriptor) where T : TaskType
    {
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.ProjectId);
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.Description);
        descriptor.Field(x => x.StatusSetId);
        descriptor.Field(x => x.Fields);
        descriptor.Field(x => x.Version);
    }
}

internal sealed class TaskTypeListItemType : ObjectType<TaskTypeListItem>
{
    protected override void Configure(IObjectTypeDescriptor<TaskTypeListItem> descriptor)
    {
        descriptor.Name("TaskTypeListItem");
        descriptor.BindFieldsExplicitly();
        TaskTypeType.Describe(descriptor);
        descriptor.Field(x => x.DescriptionTruncated);
        descriptor.Field(x => x.DescriptionLength);
    }
}

internal sealed class TaskTypeFieldType : ObjectType<TaskTypeField>
{
    protected override void Configure(IObjectTypeDescriptor<TaskTypeField> descriptor)
    {
        descriptor.Name("TaskTypeField");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.FieldId);
        descriptor.Field(x => x.Required);
    }
}

internal sealed class TaskItemType : ObjectType<TaskItem>
{
    protected override void Configure(IObjectTypeDescriptor<TaskItem> descriptor)
    {
        descriptor.Name("TaskItem");
        descriptor.BindFieldsExplicitly();
        Describe(descriptor);
    }

    public static void Describe<T>(IObjectTypeDescriptor<T> descriptor) where T : TaskItem
    {
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.ProjectId);
        descriptor.Field(x => x.Title);
        descriptor.Field(x => x.Description);
        descriptor.Field(x => x.TypeId);
        descriptor.Field(x => x.StatusId);
        descriptor.Field(x => x.SeriesNumbers);
        descriptor.Field(x => x.Links);
        descriptor.Field(x => x.Fields);
        descriptor.Field(x => x.CreatedAt);
        descriptor.Field(x => x.UpdatedAt);
        descriptor.Field(x => x.Version);
    }
}

internal sealed class TaskListItemType : ObjectType<TaskListItem>
{
    protected override void Configure(IObjectTypeDescriptor<TaskListItem> descriptor)
    {
        descriptor.Name("TaskListItem");
        descriptor.BindFieldsExplicitly();
        Describe(descriptor);
    }

    public static void Describe<T>(IObjectTypeDescriptor<T> descriptor) where T : TaskListItem
    {
        TaskItemType.Describe(descriptor);
        descriptor.Field(x => x.DescriptionTruncated);
        descriptor.Field(x => x.DescriptionLength);
        descriptor.Field(x => x.LinksCount);
        descriptor.Field(x => x.ParentIds);
        descriptor.Field(x => x.ChildCount);
    }
}

internal sealed class TaskTreeItemType : ObjectType<TaskTreeItem>
{
    protected override void Configure(IObjectTypeDescriptor<TaskTreeItem> descriptor)
    {
        descriptor.Name("TaskTreeItem");
        descriptor.BindFieldsExplicitly();
        TaskListItemType.Describe(descriptor);
        descriptor.Field(x => x.Depth);
        descriptor.Field(x => x.Repeated);
        descriptor.Field(x => x.TreeChildCount);
    }
}

internal sealed class TaskTreeListType : ObjectType<TaskTreeList>
{
    protected override void Configure(IObjectTypeDescriptor<TaskTreeList> descriptor)
    {
        descriptor.Name("TaskTreeList");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.TotalCount);
        descriptor.Field(x => x.Offset);
        descriptor.Field(x => x.Limit);
        descriptor.Field(x => x.Data);
        descriptor.Field(x => x.TopLevelCount);
    }
}

internal sealed class TaskDetailsType : ObjectType<TaskDetails>
{
    protected override void Configure(IObjectTypeDescriptor<TaskDetails> descriptor)
    {
        descriptor.Name("TaskDetails");
        descriptor.BindFieldsExplicitly();
        TaskItemType.Describe(descriptor);
        descriptor.Field(x => x.FieldViews);
        descriptor.Field(x => x.LinkViews);
        descriptor.Field(x => x.LinkCount);
    }
}

internal sealed class TaskRefType : ObjectType<TaskRef>
{
    protected override void Configure(IObjectTypeDescriptor<TaskRef> descriptor)
    {
        descriptor.Name("TaskRef");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.Title);
        descriptor.Field(x => x.SeriesNumbers);
        descriptor.Field(x => x.ParentIds);
    }
}
