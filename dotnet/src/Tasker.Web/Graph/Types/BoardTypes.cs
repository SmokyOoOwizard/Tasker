using HotChocolate.Types;
using Tasker.Core.Boards;

namespace Tasker.Web.Graph.Types;

internal sealed class BoardType : ObjectType<Board>
{
    protected override void Configure(IObjectTypeDescriptor<Board> descriptor)
    {
        descriptor.Name("Board");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.ProjectId);
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.StatusSetIds);
        descriptor.Field(x => x.Columns);
        descriptor.Field(x => x.Version);
    }
}

internal sealed record DropStatus(Guid StatusSetId, Guid StatusId);

internal sealed class BoardColumnType : ObjectType<BoardColumn>
{
    protected override void Configure(IObjectTypeDescriptor<BoardColumn> descriptor)
    {
        descriptor.Name("BoardColumn");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.StatusIds);
        descriptor.Field(x => x.Query);
        descriptor.Field(x => x.DropStatuses)
            .Type<NonNullType<ListType<NonNullType<DropStatusType>>>>()
            .Resolve(x => x.Parent<BoardColumn>().DropStatuses.Select(drop => new DropStatus(drop.Key, drop.Value)).ToArray());
    }
}

internal sealed class DropStatusType : ObjectType<DropStatus>
{
    protected override void Configure(IObjectTypeDescriptor<DropStatus> descriptor)
    {
        descriptor.Name("DropStatus");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.StatusSetId);
        descriptor.Field(x => x.StatusId);
    }
}
