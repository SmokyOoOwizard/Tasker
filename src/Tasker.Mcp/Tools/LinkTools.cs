using System.ComponentModel;
using ModelContextProtocol.Server;
using Tasker.Core.Dto;
using Tasker.Core.Links;
using Tasker.Core.Projects;
using Tasker.Core.Tasks;

namespace Tasker.Mcp.Tools;

/// <summary>
/// Связи между задачами одного проекта («A блокирует B», «A дублирует B», «A связана с B») и настраиваемые типы связей.
/// У типа два названия — для каждой стороны: «blocks» и «is blocked by». Связи по умолчанию — как в Jira.
/// </summary>
[McpServerToolType]
public static class LinkTools
{
    private const string VersionHint =
        "`version` is the link type's current version (from the list call); if someone changed it since, the call fails with [modified] — re-read and retry.";

    private const string PhraseHint =
        "`link` is a link type name or one of its two side names, case-insensitive: 'Blocks' or 'blocks' means the task blocks the other one; " +
        "'is blocked by' means the task is blocked by the other one. Call list_link_types to see them.";

    /// <summary>Связи задачи. Объект, а не голый массив: у инструмента MCP корень результата — объект.</summary>
    public record TaskLinks(TaskLinkView[] Links);

    [McpServerTool(Name = "list_link_types", ReadOnly = true)]
    [Description(
        "Lists link types of a project (Blocks, Duplicate, Cloners, Relates, Problem/Incident by default, plus your own). Each has `outwardName` " +
        "(the side the link starts from, e.g. 'blocks'), `inwardName` (the side it points to, e.g. 'is blocked by') and `allowCycles` " +
        "(false: a link closing a cycle of this type is rejected; Blocks by default) and `hierarchical` (Parent/Child by default: 'includes' / 'is part of'; " +
        "the source task is the parent - an epic - and the target its child; list_tasks reports `parentIds` and `childCount` from such links, and the console lists them as a tree). " +
        "Paged: offset/limit (limit 1-200, default 50); the result has totalCount.")]
    public static Task<ListDto<LinkType>> ListLinkTypes(
        ProjectService projects, LinkTypeService types, Guid projectId,
        int offset = 0, int limit = Page.DefaultLimit, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await types.GetRange(projectId, Page.Of(offset, limit), ct);
        });

    [McpServerTool(Name = "create_link_type")]
    [Description(
        "Creates a link type. `outwardName` is how the link reads for the task it starts from ('depends on'), `inwardName` for the task it points to " +
        "('is a dependency of'); omit `inwardName` for a link without direction ('relates to'). The name must be unique in the project. " +
        "`allowCycles` (default true): false makes link_tasks reject a link that closes a cycle of this type (A blocks B, B blocks A) with [invalid] and the cycle path. " +
        "`hierarchical` (default false): true makes the type a parent/child one - a task may have several parents, cycles are always rejected (allowCycles true is an error) " +
        "and the two names must differ.")]
    public static Task<LinkType> CreateLinkType(
        ProjectService projects, LinkTypeService types, Guid projectId, string name, string outwardName, string? inwardName = null, bool? allowCycles = null,
        bool? hierarchical = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await types.Create(projectId, new CreateLinkType(name, outwardName, inwardName, allowCycles, hierarchical), ct);
        });

    [McpServerTool(Name = "update_link_type", Idempotent = true)]
    [Description(
        "Changes a link type name, its two side names, `allowCycles` and/or `hierarchical` (null keeps the value; allowCycles false makes link_tasks reject a link that closes a cycle " +
        "of this type, existing links are not touched; hierarchical true makes it a parent/child type, which forbids cycles and needs two different side names). " + VersionHint)]
    public static Task<LinkType> UpdateLinkType(
        ProjectService projects, LinkTypeService types, Guid projectId, Guid linkTypeId, string version,
        string? name = null, string? outwardName = null, string? inwardName = null, bool? allowCycles = null, bool? hierarchical = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return McpCall.Found(await types.Update(projectId, linkTypeId, new UpdateLinkType(name, outwardName, inwardName, version, allowCycles, hierarchical), ct), $"Link type {linkTypeId}");
        });

    [McpServerTool(Name = "delete_link_type", Destructive = true)]
    [Description("Deletes a link type. Fails with [in_use] while tasks still have links of this type: remove them first. " + VersionHint)]
    public static Task<string> DeleteLinkType(
        ProjectService projects, LinkTypeService types, Guid projectId, Guid linkTypeId, string version, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            McpCall.Found(await types.Delete(projectId, linkTypeId, version, ct), $"Link type {linkTypeId}");
            return "deleted";
        });

    [McpServerTool(Name = "get_task_links", ReadOnly = true)]
    [Description(
        "Shows all links of a task, both the ones it starts and the ones pointing at it, each named from this task's side: " +
        "'blocks' for the task that blocks and 'is blocked by' for the blocked one. Every link has the other task (id, title, statusId). " + TaskTools.TaskIdHint)]
    public static Task<TaskLinks> GetTaskLinks(ProjectService projects, TaskService tasks, TaskLinkService links, Guid projectId, string taskId, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return new TaskLinks(McpCall.Found(await links.GetLinks(projectId, await McpCall.TaskId(tasks, projectId, taskId, ct), ct), $"Task {taskId}"));
        });

    [McpServerTool(Name = "link_tasks", Idempotent = true)]
    [Description(
        "Links two tasks of the same project: 'task <link> otherTask', e.g. task 'blocks' otherTask or task 'is blocked by' otherTask. " +
        "Adding a link that already exists changes nothing. A link that would close a cycle ('A blocks B' while B already blocks A, directly or through a chain) is rejected with [invalid] and the cycle path " +
        "unless the link type allows cycles (Blocks does not). Returns all links of `taskId`. `taskId` and `otherTaskId` are task ids, full or short (the first 8 or more hex characters). " + PhraseHint)]
    public static Task<TaskLinks> LinkTasks(
        ProjectService projects, LinkTypeService types, TaskService tasks, TaskLinkService links, Guid projectId, string taskId, string link, string otherTaskId, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var (task, other) = (await McpCall.TaskId(tasks, projectId, taskId, ct), await McpCall.TaskId(tasks, projectId, otherTaskId, ct));
            var (type, direction) = await types.Resolve(projectId, link, ct);
            // «A is blocked by B» хранится как «B blocks A»: источник — тот, от кого связь исходит.
            var (source, target) = direction == LinkDirection.Outward ? (task, other) : (other, task);
            McpCall.Found(await links.Add(projectId, source, type.Id, target, null, ct), $"Task {source}");
            return new TaskLinks((await links.GetLinks(projectId, task, ct))!);
        });

    [McpServerTool(Name = "unlink_tasks", Idempotent = true, Destructive = true)]
    [Description(
        "Removes the link 'task <link> otherTask' (the same phrases as link_tasks). A link that does not exist is not an error. Returns all links of `taskId`. `taskId` and `otherTaskId` are task ids, full or short (the first 8 or more hex characters). " + PhraseHint)]
    public static Task<TaskLinks> UnlinkTasks(
        ProjectService projects, LinkTypeService types, TaskService tasks, TaskLinkService links, Guid projectId, string taskId, string link, string otherTaskId, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var (task, other) = (await McpCall.TaskId(tasks, projectId, taskId, ct), await McpCall.TaskId(tasks, projectId, otherTaskId, ct));
            var (type, direction) = await types.Resolve(projectId, link, ct);
            var (source, target) = direction == LinkDirection.Outward ? (task, other) : (other, task);
            McpCall.Found(await links.Remove(projectId, source, type.Id, target, null, ct), $"Task {source}");
            return new TaskLinks(McpCall.Found(await links.GetLinks(projectId, task, ct), $"Task {taskId}"));
        });
}
