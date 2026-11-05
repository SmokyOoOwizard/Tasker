using HotChocolate.Types;
using Tasker.Core.Statuses;

namespace Tasker.Web.Graph.Types;

internal sealed class StatusType : ObjectType<Status>
{
    protected override void Configure(IObjectTypeDescriptor<Status> descriptor)
    {
        descriptor.Name("Status");
        descriptor.BindFieldsExplicitly();
        Describe(descriptor);
    }

    public static void Describe<T>(IObjectTypeDescriptor<T> descriptor) where T : Status
    {
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.ProjectId);
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.Color);
        descriptor.Field(x => x.Description);
        descriptor.Field(x => x.Icon);
        descriptor.Field(x => x.Version);
    }
}

internal sealed class StatusListItemType : ObjectType<StatusListItem>
{
    protected override void Configure(IObjectTypeDescriptor<StatusListItem> descriptor)
    {
        descriptor.Name("StatusListItem");
        descriptor.BindFieldsExplicitly();
        StatusType.Describe(descriptor);
        descriptor.Field(x => x.DescriptionTruncated);
        descriptor.Field(x => x.DescriptionLength);
    }
}

internal sealed class StatusSetType : ObjectType<StatusSet>
{
    protected override void Configure(IObjectTypeDescriptor<StatusSet> descriptor)
    {
        descriptor.Name("StatusSet");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.ProjectId);
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.StatusIds);
        descriptor.Field(x => x.Version);
    }
}
