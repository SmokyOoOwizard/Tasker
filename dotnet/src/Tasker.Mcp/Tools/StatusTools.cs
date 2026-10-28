using System.ComponentModel;
using ModelContextProtocol.Server;
using Tasker.Core;
using Tasker.Core.Dto;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;

namespace Tasker.Mcp.Tools;

/// <summary>Статусы, наборы статусов и типы задач: задача → тип → набор статусов → статусы.</summary>
[McpServerToolType]
public static class StatusTools
{
    private const string VersionHint =
        "`version` is the entity's current version (from the list/get call); if someone changed it since, the call fails with [modified] — re-read and retry.";

    /// <summary>Описание параметра descriptionLength для списков статусов и типов задач (у задач — <see cref="TaskTools.DescriptionHint"/>).</summary>
    private const string DescriptionLengthHint =
        "Each entry carries `description` (what the entity means and when to use it; empty - none): `descriptionLength` (default 200) is how many characters of it to return - " +
        "0 none, N the first N characters (Unicode characters, no ellipsis added), -1 the full text. `descriptionTruncated` tells that the text was cut, " +
        "`descriptionLength` of an entry is the length of its full description; repeat the call with descriptionLength -1 for the full text.";

    private const string DescriptionParamHint = "`description` is free text (Unicode, line breaks); an empty string clears it.";

    [McpServerTool(Name = "list_statuses", ReadOnly = true)]
    [Description("Lists statuses of a project (name, #RRGGBB color, description). Their order is defined by status sets. Paged: offset/limit (limit 1-200, default 50); the result has totalCount. " + DescriptionLengthHint)]
    public static Task<ListDto<StatusListItem>> ListStatuses(ProjectService projects, StatusService statuses, Guid projectId,
        int offset = 0, int limit = Page.DefaultLimit, int descriptionLength = DescriptionPreview.McpDefault, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await statuses.List(projectId, Page.Of(offset, limit), descriptionLength, ct);
        });

    [McpServerTool(Name = "create_status")]
    [Description("Creates a status. Color is #RRGGBB. `description` (optional) says what the status means and when to set it. " + DescriptionParamHint)]
    public static Task<Status> CreateStatus(ProjectService projects, StatusService statuses, Guid projectId, string name, string color, string? description = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await statuses.Create(projectId, new CreateStatus(name, color, description), ct);
        });

    [McpServerTool(Name = "update_status", Idempotent = true)]
    [Description("Changes a status name, color and/or description (null keeps the value). " + DescriptionParamHint + " " + VersionHint)]
    public static Task<Status> UpdateStatus(
        ProjectService projects, StatusService statuses, Guid projectId, Guid statusId, string version,
        string? name = null, string? color = null, string? description = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return McpCall.Found(await statuses.Update(projectId, statusId, new UpdateStatus(name, color, version, description), ct), $"Status {statusId}");
        });

    [McpServerTool(Name = "delete_status", Destructive = true)]
    [Description("Deletes a status. Fails with [in_use] listing tasks, status sets and board columns that still use it. " + VersionHint)]
    public static Task<string> DeleteStatus(ProjectService projects, StatusService statuses, Guid projectId, Guid statusId, string version, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            McpCall.Found(await statuses.Delete(projectId, statusId, version, ct), $"Status {statusId}");
            return "deleted";
        });

    [McpServerTool(Name = "list_status_sets", ReadOnly = true)]
    [Description("Lists status sets: ordered lists of statuses a task type allows. Paged: offset/limit (limit 1-200, default 50); the result has totalCount.")]
    public static Task<ListDto<StatusSet>> ListStatusSets(ProjectService projects, IStatusSetStorage sets, Guid projectId,
        int offset = 0, int limit = Page.DefaultLimit, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await sets.GetRange(projectId, Page.Of(offset, limit), ct);
        });

    [McpServerTool(Name = "create_status_set")]
    [Description("Creates a status set from status ids, in display order.")]
    public static Task<StatusSet> CreateStatusSet(ProjectService projects, StatusSetService sets, Guid projectId, string name, Guid[] statusIds, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await sets.Create(projectId, new CreateStatusSet(name, statusIds), ct);
        });

    [McpServerTool(Name = "update_status_set", Idempotent = true)]
    [Description("Renames a status set and/or replaces its statuses (full ordered list; null keeps). Removing a status still used by tasks or boards fails with [in_use]. " + VersionHint)]
    public static Task<StatusSet> UpdateStatusSet(
        ProjectService projects, StatusSetService sets, Guid projectId, Guid statusSetId, string version,
        string? name = null, Guid[]? statusIds = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return McpCall.Found(await sets.Update(projectId, statusSetId, new UpdateStatusSet(name, statusIds, version), ct), $"Status set {statusSetId}");
        });

    [McpServerTool(Name = "delete_status_set", Destructive = true)]
    [Description("Deletes a status set. Fails with [in_use] if task types or boards use it. " + VersionHint)]
    public static Task<string> DeleteStatusSet(ProjectService projects, StatusSetService sets, Guid projectId, Guid statusSetId, string version, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            McpCall.Found(await sets.Delete(projectId, statusSetId, version, ct), $"Status set {statusSetId}");
            return "deleted";
        });

    [McpServerTool(Name = "list_task_types", ReadOnly = true)]
    [Description("Lists task types (e.g. Bug, Feature) with their descriptions. Each type points to the status set its tasks use. Paged: offset/limit (limit 1-200, default 50); the result has totalCount. " + DescriptionLengthHint)]
    public static Task<ListDto<TaskTypeListItem>> ListTaskTypes(ProjectService projects, TaskTypeService types, Guid projectId,
        int offset = 0, int limit = Page.DefaultLimit, int descriptionLength = DescriptionPreview.McpDefault, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await types.List(projectId, Page.Of(offset, limit), descriptionLength, ct);
        });

    [McpServerTool(Name = "create_task_type")]
    [Description(
        "Creates a task type that uses the given status set. `description` (optional) says when to use the type. " + DescriptionParamHint + " `fields` (optional): fields of the catalog (list_fields) connected to the type, " +
        "[{fieldId, required}] in display order: every task of the type has them and cannot lose them; required ones need a value when a task is created or edited.")]
    public static Task<TaskType> CreateTaskType(
        ProjectService projects, TaskTypeService types, Guid projectId, string name, Guid statusSetId, TaskTypeField[]? fields = null, string? description = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await types.Create(projectId, new CreateTaskType(name, statusSetId, fields, description), ct);
        });

    [McpServerTool(Name = "update_task_type", Idempotent = true)]
    [Description(
        "Renames a task type, changes its description, switches its status set and/or replaces its fields (null keeps). " + DescriptionParamHint + " Switching fails with [in_use] if some tasks have statuses missing in the new set. " +
        "`fields` is the full ordered list [{fieldId, required}] of catalog fields (list_fields) every task of the type has. Adding a field or changing `required` " +
        "does not touch existing tasks (a required field is checked when a task is created or edited). Removing a field whose values some tasks hold needs a choice " +
        "in `removedFields`, one for all removed fields: clear removes the values from the tasks, keep leaves them (the field becomes an optional extra field of those tasks). " +
        "Without a choice the call fails with [in_use] giving the number of tasks. The result is the task type plus `affectedTasks`: how many tasks " +
        "lost the values (clear) or kept them as an extra field (keep); 0 when no task had values of a removed field. " + VersionHint)]
    public static Task<CascadeResult<TaskType>> UpdateTaskType(
        ProjectService projects, TaskTypeService types, Guid projectId, Guid taskTypeId, string version,
        string? name = null, Guid? statusSetId = null, TaskTypeField[]? fields = null, RemovedFieldValues? removedFields = null, string? description = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return McpCall.Found(
                await types.Update(projectId, taskTypeId, new UpdateTaskType(name, statusSetId, version, fields, removedFields, description), ct), $"Task type {taskTypeId}");
        });

    [McpServerTool(Name = "delete_task_type", Destructive = true)]
    [Description("Deletes a task type. Fails with [in_use] if it has tasks. " + VersionHint)]
    public static Task<string> DeleteTaskType(ProjectService projects, TaskTypeService types, Guid projectId, Guid taskTypeId, string version, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            McpCall.Found(await types.Delete(projectId, taskTypeId, version, ct), $"Task type {taskTypeId}");
            return "deleted";
        });
}
