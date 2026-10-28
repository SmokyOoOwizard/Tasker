using System.ComponentModel;
using ModelContextProtocol.Server;
using Tasker.Core.Dto;
using Tasker.Core.Projects;
using Tasker.Core.Tasks;

namespace Tasker.Mcp.Tools;

[McpServerToolType]
public static class TaskTools
{
    /// <summary>Как записывается id задачи в аргументах инструментов (`taskId`).</summary>
    public const string TaskIdHint =
        "`taskId` is the task's id (Guid) or its short id: the first 8 or more hex characters of the Guid (as the console lists it), if they match one task only.";

    private const string FieldsHint =
        "`fields` is an object, every part optional (applied in this order): " +
        "removeFields [fieldId] - remove extra and own fields with their values (fields of the task type cannot be removed); " +
        "addFields [fieldId] - add catalog fields without values; " +
        "values [{fieldId, values: [text]}] - set values (an empty list clears them; a catalog field the task does not have yet is added as extra); " +
        "newOwnFields [{name, type, values?, required?, multiple?, enumId?}] - fields defined in this task only. " +
        "fieldId is a field of the task type, an extra field, or an own field (its id is in `fieldViews`). " +
        "Values are text: int, float (decimal point), bool (true/false), date (yyyy-MM-dd), enum (value id or name); several only for a `multiple` field. " +
        "`fieldViews` of get_task shows the task's fields, list_fields the catalog.";

    /// <summary>Описание параметра descriptionLength для всех инструментов, которые отдают задачи списком.</summary>
    public const string DescriptionHint =
        "Each task in the list carries a preview of its description: `descriptionLength` (default 200) is how many characters of it to return - " +
        "0 none, N the first N characters (Unicode characters, never cut in the middle of an emoji or accented letter, no ellipsis added), -1 the full text. " +
        "`descriptionTruncated` tells that the text was cut, `descriptionLength` of a task is the length of its full description, `linksCount` the number of its links (outgoing and incoming), `parentIds` the ids of its parents (tasks that include it by a hierarchical link type such as Parent/Child) and `childCount` the number of its child tasks (> 0: an epic). The list is always flat, the hierarchy is in these two fields. " +
        "Get the full text with get_task or by repeating the call with descriptionLength -1.";

    /// <summary>Описание параметра sort для инструментов, которые отдают задачи списком.</summary>
    public const string SortHint =
        "`sort` (optional) orders the tasks: keys separated by commas, '-' before a key for descending order, e.g. \"status,-updated,Estimate\". " +
        "Keys: status (position in the status set of the task's type; tasks of different sets: position, then set and status name), type (type name), title (ignoring case), " +
        "created, updated, series (number in a series: TSK-9 before TSK-12) or a field name (catalog field or own field of tasks - same naming rules as `field`; " +
        "int/float by value, date by date, bool false before true, enum by the order of its values, string ignoring case; a multiple field by its first value). " +
        "Tasks without a value go last in both directions; equal keys keep the creation order, so pages never overlap. totalCount and filters are not affected. An unknown key is an error listing the valid ones.";

    /// <summary>Описание фильтров по типу, статусу и серии для инструментов, которые отдают задачи списком.</summary>
    public const string FilterHint =
        "Filters: typeIds / statusIds / seriesIds (arrays; the single typeId / statusId / seriesId are the same and are merged with them). " +
        "Values of one parameter are alternatives (OR: statusIds [A, B] - tasks in A or B); different parameters (types, statuses, series, field) must all hold (AND). " +
        "A status the task's type does not have simply matches nothing.";

    /// <summary>Единичный параметр и массив одного фильтра вместе; null, если не задано ни то, ни другое.</summary>
    internal static IEnumerable<Guid>? Merge(Guid? single, Guid[]? many) =>
        single == null ? many : (many ?? []).Append(single.Value);

    [McpServerTool(Name = "list_tasks", ReadOnly = true)]
    [Description("Lists tasks of a project in creation order (or in the order of `sort`), optionally filtered by task types, statuses, series (tasks that have a number in it) and/or fields (catalog fields and own fields of tasks; field: [\"Estimate>=3\", \"Level!=Low\", \"Due:set\"], all must hold; Name is letters and digits only; operators = != (any type; != also takes tasks without a value; for a multiple field = means contains the value and != means none equals it; enum value by name or id) and > >= < <= (int, float, date only); Name:set / Name:unset - has a value or not; Name:attached / Name:detached - the field is connected to the task (its type has it or it was added) or not (for an own field: the task has it). A name found in the catalog is read by the catalog field's type and also matches own fields of that name and type (other types are skipped); a name only own fields have is read by their type, several types - an error). Paged: offset/limit (limit 1-200, default 50). " + FilterHint + " " + SortHint + " " + DescriptionHint)]
    public static Task<ListDto<TaskListItem>> ListTasks(
        ProjectService projects, TaskService tasks, Guid projectId,
        Guid? typeId = null, Guid? statusId = null, Guid? seriesId = null, Guid[]? typeIds = null, Guid[]? statusIds = null, Guid[]? seriesIds = null,
        string[]? field = null, int offset = 0, int limit = Page.DefaultLimit, int descriptionLength = DescriptionPreview.McpDefault, string? sort = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var filter = TaskFilter.Of(Merge(typeId, typeIds), Merge(statusId, statusIds), Merge(seriesId, seriesIds));
            return await tasks.List(projectId, filter, field, Page.Of(offset, limit), descriptionLength, ct, sort);
        });

    [McpServerTool(Name = "get_task", ReadOnly = true)]
    [Description(
        "Gets a task by id (" + TaskIdHint + "), including its current `version` needed to update or delete it. `fieldViews` lists the task's fields with definitions: " +
        "fieldId, name, type, multiple, required, source (type: field of its task type, extra: field added from the catalog, own: defined in the task), " +
        "values (enum: value ids) and texts (enum: value names). `fields` holds only the stored values. `linkViews` lists the task's links from both sides (the ones it starts and the ones pointing at it, same shape as get_task_links: typeId, typeName, direction outward|inward, name from this task's side such as 'blocks' / 'is blocked by', task {id, title, statusId, seriesNumbers}); `linkCount` is the total (linkViews holds at most 100, use get_task_links for all). `links` holds only the stored outgoing links (typeId, targetId); list_tasks has no linkViews.")]
    public static Task<TaskDetails> GetTask(ProjectService projects, TaskService tasks, Guid projectId, string taskId, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return McpCall.Found(await tasks.Describe(projectId, await McpCall.TaskId(tasks, projectId, taskId, ct), ct), $"Task {taskId}");
        });

    [McpServerTool(Name = "create_task")]
    [Description("Creates a task. statusId must be in the status set of the task type; omit it to use the set's first status. " +
        "seriesIds (optional): ids of series the new task joins, it gets the next number in each (see the result's seriesNumbers; reference it as PREFIX-number). " +
        "fields (optional): values and extra fields, see " + FieldsHint + " Required fields of the task type must get values, otherwise the call fails with [invalid].")]
    public static Task<TaskDetails> CreateTask(
        ProjectService projects, TaskService tasks, Guid projectId, string title, Guid typeId,
        string? description = null, Guid? statusId = null, Guid[]? seriesIds = null, TaskFieldChanges? fields = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await tasks.Describe(projectId, await tasks.Create(projectId, new CreateTask(title, description, typeId, statusId, seriesIds, fields), ct), ct);
        });

    [McpServerTool(Name = "update_task", Idempotent = true)]
    [Description(
        "Changes a task: title, description (\"\" clears it), type and/or status; null keeps a field. " +
        "The status must be in the status set of the (new) type. " +
        "fields (optional) changes the task's fields, see " + FieldsHint + " Changing the type keeps the old type's fields that have values as extra fields; " +
        "required fields of the new type must get values in the same call. Required fields are checked when the title, description, type or fields change, not on a status-only change. " +
        "`version` is the task's current version (from get_task); if someone changed the task since, the call fails with [modified] — re-read and retry. " + TaskIdHint)]
    public static Task<TaskDetails> UpdateTask(
        ProjectService projects, TaskService tasks, Guid projectId, string taskId, string version,
        string? title = null, string? description = null, Guid? typeId = null, Guid? statusId = null, TaskFieldChanges? fields = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var updated = McpCall.Found(
                await tasks.Update(projectId, await McpCall.TaskId(tasks, projectId, taskId, ct), new UpdateTask(title, description, typeId, statusId, version, fields), ct), $"Task {taskId}");
            return await tasks.Describe(projectId, updated, ct);
        });

    [McpServerTool(Name = "delete_task", Destructive = true)]
    [Description("Deletes a task. `version` is the task's current version (from get_task). " + TaskIdHint)]
    public static Task<string> DeleteTask(ProjectService projects, TaskService tasks, Guid projectId, string taskId, string version, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            McpCall.Found(await tasks.Delete(projectId, await McpCall.TaskId(tasks, projectId, taskId, ct), version, ct), $"Task {taskId}");
            return "deleted";
        });
}
