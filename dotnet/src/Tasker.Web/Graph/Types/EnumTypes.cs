using HotChocolate.Execution.Configuration;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Core.Fields;
using Tasker.Core.Links;
using Tasker.Core.Locks;
using Tasker.Core.Tasks;
using Tasker.Core.Users;
using Tasker.Daemon;

namespace Tasker.Web.Graph.Types;

internal sealed class NamedEnumType<T> : EnumType<T> where T : struct, Enum
{
    protected override void Configure(IEnumTypeDescriptor<T> descriptor) => descriptor.Name(typeof(T).Name);
}

internal static class TaskerTypes
{
    public static IRequestExecutorBuilder AddTaskerTypes(this IRequestExecutorBuilder builder) =>
        builder
            .AddType<ProjectType>()
            .AddType<TaskCommentViewType>()
            .AddType<CommentAuthorType>()
            .AddType<ProjectStatsType>()
            .AddType<RegistrationType>()
            .AddType<StatusType>()
            .AddType<StatusListItemType>()
            .AddType<StatusSetType>()
            .AddType<TaskTypeType>()
            .AddType<TaskTypeListItemType>()
            .AddType<TaskTypeFieldType>()
            .AddType<FieldDefinitionType>()
            .AddType<FieldEnumType>()
            .AddType<FieldEnumValueType>()
            .AddType<TaskFieldType>()
            .AddType<OwnFieldType>()
            .AddType<TaskFieldViewType>()
            .AddType<LinkTypeType>()
            .AddType<TaskLinkType>()
            .AddType<TaskLinkViewType>()
            .AddType<TaskSearchHitType>()
            .AddType<TaskSearchSnippetType>()
            .AddType<TaskSearchRangeType>()
            .AddType<LinkedTaskType>()
            .AddType<TaskItemType>()
            .AddType<TaskListItemType>()
            .AddType<TaskTreeItemType>()
            .AddType<TaskTreeListType>()
            .AddType<TaskDetailsType>()
            .AddType<SeriesType>()
            .AddType<TaskSeriesNumberType>()
            .AddType<SeriesHealthType>()
            .AddType<LinkHealthType>()
            .AddType<LinkCycleType>()
            .AddType<LinkCycleTaskType>()
            .AddType<NumberConflictType>()
            .AddType<PrefixConflictType>()
            .AddType<ConflictPlanType>()
            .AddType<ConflictPlanStepType>()
            .AddType<BoardType>()
            .AddType<BoardColumnType>()
            .AddType<DropStatusType>()
            .AddType<ColumnFieldFilterType>()
            .AddType<UserType>()
            .AddType<AgentTokenType>()
            .AddType<LockInfoType>()
            .AddType<WorkspaceProblemType>()
            .AddType<WorkspaceMcpType>()
            .AddType<WorkspaceChangeType>()
            .AddType<PageType<Tasker.Core.Projects.Project>>()
            .AddType<PageType<Tasker.Core.Statuses.StatusListItem>>()
            .AddType<PageType<Tasker.Core.Statuses.StatusSet>>()
            .AddType<PageType<TaskTypeListItem>>()
            .AddType<PageType<FieldDefinition>>()
            .AddType<PageType<FieldEnum>>()
            .AddType<PageType<LinkType>>()
            .AddType<PageType<TaskListItem>>()
            .AddType<PageType<Tasker.Core.TaskSeries.Series>>()
            .AddType<PageType<Tasker.Core.Boards.Board>>()
            .AddType<PageType<LockInfo>>()
            .AddType<PageType<User>>()
            .AddType<PageType<Tasker.Core.Agents.AgentToken>>()
            .AddType<PageType<Tasker.Core.Workspace.WorkspaceProblem>>()
            .AddType<NamedEnumType<FieldType>>()
            .AddType<NamedEnumType<FieldOperator>>()
            .AddType<NamedEnumType<TaskFieldSource>>()
            .AddType<NamedEnumType<LinkDirection>>()
            .AddType<NamedEnumType<LockedEntity>>()
            .AddType<NamedEnumType<UserKind>>()
            .AddType<NamedEnumType<DaemonState>>()
            .AddType<NamedEnumType<WorkspaceState>>();
}
