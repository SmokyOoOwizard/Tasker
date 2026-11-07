using HotChocolate.Types;
using Tasker.Core.Comments;

namespace Tasker.Web.Graph.Types;

internal sealed class TaskCommentViewType : ObjectType<TaskCommentView>
{
    protected override void Configure(IObjectTypeDescriptor<TaskCommentView> descriptor)
    {
        descriptor.Name("TaskComment");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.TaskId);
        descriptor.Field(x => x.ParentId);
        descriptor.Field(x => x.Depth);
        descriptor.Field(x => x.Author);
        descriptor.Field(x => x.Body);
        descriptor.Field(x => x.CreatedAt);
        descriptor.Field(x => x.EditedAt);
        descriptor.Field(x => x.Deleted);
        descriptor.Field(x => x.Mine);
        descriptor.Field(x => x.Version);
    }
}

internal sealed class CommentAuthorType : ObjectType<CommentAuthor>
{
    protected override void Configure(IObjectTypeDescriptor<CommentAuthor> descriptor)
    {
        descriptor.Name("CommentAuthor");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(x => x.Id);
        descriptor.Field(x => x.Name);
        descriptor.Field(x => x.Kind);
    }
}
