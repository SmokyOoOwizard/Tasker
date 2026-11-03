using System.Text.Json.Serialization;

namespace Tasker.Mcp;

[JsonSerializable(typeof(global::System.Guid))]
[JsonSerializable(typeof(global::System.Guid[]))]
[JsonSerializable(typeof(global::System.Int32))]
[JsonSerializable(typeof(global::System.Nullable<global::System.Boolean>))]
[JsonSerializable(typeof(global::System.Nullable<global::System.Guid>))]
[JsonSerializable(typeof(global::System.Nullable<global::System.Int32>))]
[JsonSerializable(typeof(global::System.Nullable<global::Tasker.Core.Fields.FieldType>))]
[JsonSerializable(typeof(global::System.Nullable<global::Tasker.Core.Fields.SeveralValues>))]
[JsonSerializable(typeof(global::System.Nullable<global::Tasker.Core.Tasks.RemovedFieldValues>))]
[JsonSerializable(typeof(global::System.Nullable<global::Tasker.Mcp.Tools.FieldTools.RemovedValuesChoice>))]
[JsonSerializable(typeof(global::System.String))]
[JsonSerializable(typeof(global::System.String[]))]
[JsonSerializable(typeof(global::Tasker.Core.Boards.Board))]
[JsonSerializable(typeof(global::Tasker.Core.Boards.BoardColumnInput[]))]
[JsonSerializable(typeof(global::Tasker.Core.CascadeResult<global::Tasker.Core.Fields.FieldEnum>))]
[JsonSerializable(typeof(global::Tasker.Core.CascadeResult<global::Tasker.Core.Tasks.TaskType>))]
[JsonSerializable(typeof(global::Tasker.Core.Dto.ListDto<global::Tasker.Core.Boards.Board>))]
[JsonSerializable(typeof(global::Tasker.Core.Dto.ListDto<global::Tasker.Core.Fields.FieldDefinition>))]
[JsonSerializable(typeof(global::Tasker.Core.Dto.ListDto<global::Tasker.Core.Fields.FieldEnum>))]
[JsonSerializable(typeof(global::Tasker.Core.Dto.ListDto<global::Tasker.Core.Links.LinkType>))]
[JsonSerializable(typeof(global::Tasker.Core.Dto.ListDto<global::Tasker.Core.Projects.Project>))]
[JsonSerializable(typeof(global::Tasker.Core.Dto.ListDto<global::Tasker.Core.Statuses.StatusListItem>))]
[JsonSerializable(typeof(global::Tasker.Core.Dto.ListDto<global::Tasker.Core.Statuses.StatusSet>))]
[JsonSerializable(typeof(global::Tasker.Core.Dto.ListDto<global::Tasker.Core.Tasks.TaskListItem>))]
[JsonSerializable(typeof(global::Tasker.Core.Dto.ListDto<global::Tasker.Core.Tasks.TaskTypeListItem>))]
[JsonSerializable(typeof(global::Tasker.Core.Dto.ListDto<global::Tasker.Core.TaskSeries.Series>))]
[JsonSerializable(typeof(global::Tasker.Core.Dto.ListDto<global::Tasker.Core.Users.User>))]
[JsonSerializable(typeof(global::Tasker.Core.Dto.ListDto<global::Tasker.Core.Workspace.WorkspaceProblem>))]
[JsonSerializable(typeof(global::Tasker.Core.Fields.FieldDefinition))]
[JsonSerializable(typeof(global::Tasker.Core.Fields.FieldEnum))]
[JsonSerializable(typeof(global::Tasker.Core.Fields.FieldEnumValueInput[]))]
[JsonSerializable(typeof(global::Tasker.Core.Fields.FieldType))]
[JsonSerializable(typeof(global::Tasker.Core.Fields.FieldValueMapping[]))]
[JsonSerializable(typeof(global::Tasker.Core.Links.LinkType))]
[JsonSerializable(typeof(global::Tasker.Core.Locks.LockInfo))]
[JsonSerializable(typeof(global::Tasker.Core.Projects.Project))]
[JsonSerializable(typeof(global::Tasker.Core.Statuses.Status))]
[JsonSerializable(typeof(global::Tasker.Core.Statuses.StatusSet))]
[JsonSerializable(typeof(global::Tasker.Core.Tasks.TaskDetails))]
[JsonSerializable(typeof(global::Tasker.Core.Tasks.TaskFieldChanges))]
[JsonSerializable(typeof(global::Tasker.Core.Tasks.TaskItem))]
[JsonSerializable(typeof(global::Tasker.Core.Tasks.TaskType))]
[JsonSerializable(typeof(global::Tasker.Core.Tasks.TaskTypeField[]))]
[JsonSerializable(typeof(global::Tasker.Core.TaskSeries.Series))]
[JsonSerializable(typeof(global::Tasker.Core.TaskSeries.SeriesHealth))]
[JsonSerializable(typeof(global::Tasker.Core.Users.User))]
[JsonSerializable(typeof(global::Tasker.Mcp.Tools.BoardTools.BoardView))]
[JsonSerializable(typeof(global::Tasker.Mcp.Tools.LinkTools.TaskLinks))]
[JsonSerializable(typeof(global::Tasker.Mcp.Tools.LockTools.LockResult))]
[JsonSerializable(typeof(global::Tasker.Mcp.Tools.SeriesTools.TaskMatches))]
[JsonSerializable(typeof(global::Tasker.Mcp.Tools.WorkspaceTools.WorkspaceList))]
public sealed partial class McpJsonContext : JsonSerializerContext
{
    public static void Install()
    {
        Tasker.Core.TaskerJson.GeneratedResolvers.Add(Default);
        Tasker.Core.TaskerJson.Options.TypeInfoResolverChain.Insert(0, Default);
    }
}
