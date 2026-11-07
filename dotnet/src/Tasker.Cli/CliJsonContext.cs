using System.Text.Json.Serialization;
using Tasker.Core.Boards;
using Tasker.Core.Comments;
using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Core.Links;
using Tasker.Core.Locks;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;
using Tasker.Core.Users;
using Tasker.Core.Workspace;
using Tasker.Daemon;
using Tasker.Global;

namespace Tasker.Cli;

[JsonSerializable(typeof(Board))]
[JsonSerializable(typeof(ListDto<Board>))]
[JsonSerializable(typeof(FieldDefinition))]
[JsonSerializable(typeof(ListDto<FieldDefinition>))]
[JsonSerializable(typeof(FieldEnum))]
[JsonSerializable(typeof(ListDto<FieldEnum>))]
[JsonSerializable(typeof(LinkType))]
[JsonSerializable(typeof(ListDto<LinkType>))]
[JsonSerializable(typeof(TaskLinkView))]
[JsonSerializable(typeof(TaskCommentView))]
[JsonSerializable(typeof(TaskCommentView[]))]
[JsonSerializable(typeof(LockInfo))]
[JsonSerializable(typeof(Project))]
[JsonSerializable(typeof(ListDto<Project>))]
[JsonSerializable(typeof(Status))]
[JsonSerializable(typeof(ListDto<StatusListItem>))]
[JsonSerializable(typeof(StatusSet))]
[JsonSerializable(typeof(ListDto<StatusSet>))]
[JsonSerializable(typeof(Series))]
[JsonSerializable(typeof(ListDto<Series>))]
[JsonSerializable(typeof(CleanupChange))]
[JsonSerializable(typeof(PrefixConflict))]
[JsonSerializable(typeof(TaskItem))]
[JsonSerializable(typeof(TaskListItem))]
[JsonSerializable(typeof(ListDto<TaskListItem>))]
[JsonSerializable(typeof(TaskFieldView))]
[JsonSerializable(typeof(TaskType))]
[JsonSerializable(typeof(ListDto<TaskTypeListItem>))]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(ListDto<User>))]
[JsonSerializable(typeof(MigrationFile))]
[JsonSerializable(typeof(MigrationProblem))]
[JsonSerializable(typeof(MigrationRename))]
[JsonSerializable(typeof(WorkspaceProblem))]
[JsonSerializable(typeof(ControllerStatus))]
[JsonSerializable(typeof(AutostartInfo))]
[JsonSerializable(typeof(DaemonStatus))]
[JsonSerializable(typeof(StatusListItem))]
[JsonSerializable(typeof(TaskTypeListItem))]
[JsonSerializable(typeof(ProjectSeriesHealth))]
[JsonSerializable(typeof(McpSettings))]
[JsonSerializable(typeof(WorkspaceEntry))]
internal sealed partial class CliJsonContext : JsonSerializerContext
{
    public static void Install()
    {
        Tasker.Core.TaskerJson.Use(Default);
        Tasker.Core.TaskerJson.Use(DaemonJsonContext.Default);
    }
}
