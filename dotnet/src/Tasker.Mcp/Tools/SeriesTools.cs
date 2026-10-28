using System.ComponentModel;
using ModelContextProtocol.Server;
using Tasker.Core.Dto;
using Tasker.Core.Projects;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;

namespace Tasker.Mcp.Tools;

/// <summary>Серии задач: нумерация «ПРЕФИКС-номер» (TSK-5). Список серий, номера у задач и поиск по ссылке.</summary>
[McpServerToolType]
public static class SeriesTools
{
    private const string VersionHint =
        "`version` is the entity's current version (from the list/get call); if someone changed it since, the call fails with [modified] — re-read and retry.";

    private const string SeriesHint = "`series` is a series id (Guid) or its exact prefix (case matters: TSK and tsk are different series).";

    /// <summary>Результат поиска по ссылке. Объект, а не голый массив: у инструмента MCP корень результата — объект.</summary>
    public record TaskMatches(TaskListItem[] Tasks);

    [McpServerTool(Name = "list_series", ReadOnly = true)]
    [Description(
        "Lists task series of a project ordered by prefix. A series numbers tasks 1, 2, 3... so they can be referenced as PREFIX-number (e.g. TSK-5). " +
        "Paged: offset/limit (limit 1-200, default 50); the result has totalCount.")]
    public static Task<ListDto<Series>> ListSeries(
        ProjectService projects, ISeriesStorage series, Guid projectId,
        int offset = 0, int limit = Page.DefaultLimit, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await series.GetRange(projectId, Page.Of(offset, limit), ct);
        });

    [McpServerTool(Name = "get_series", ReadOnly = true)]
    [Description("Gets a series by id or by prefix, including its current `version` needed to update or delete it. " + SeriesHint)]
    public static Task<Series> GetSeries(ProjectService projects, SeriesService seriesService, Guid projectId, string series, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return McpCall.Found(await seriesService.Find(projectId, series, ct), $"Series '{series}'");
        });

    [McpServerTool(Name = "create_series")]
    [Description(
        "Creates a task series. prefix: 1-20 Latin letters or digits, case-sensitive, unique in the project (fails with [in_use] if taken, [invalid] if malformed). " +
        "Creating a series does not number any tasks: add them with add_task_to_series or the `seriesIds` parameter of create_task.")]
    public static Task<Series> CreateSeries(ProjectService projects, SeriesService seriesService, Guid projectId, string name, string prefix, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await seriesService.Create(projectId, new CreateSeries(name, prefix), ct);
        });

    [McpServerTool(Name = "update_series", Idempotent = true)]
    [Description(
        "Changes a series name and/or prefix (null keeps the value). A new prefix renames the series: task numbers stay, " +
        "but references written elsewhere with the old prefix (commit messages, chats) stop working. " + VersionHint)]
    public static Task<Series> UpdateSeries(
        ProjectService projects, SeriesService seriesService, Guid projectId, Guid seriesId, string version,
        string? name = null, string? prefix = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return McpCall.Found(await seriesService.Update(projectId, seriesId, new UpdateSeries(name, prefix, version), ct), $"Series {seriesId}");
        });

    [McpServerTool(Name = "delete_series", Destructive = true)]
    [Description(
        "Deletes a series. A series that has tasks can be deleted: their numbers in it are removed from the tasks (the tasks themselves stay). " +
        "Numbers cannot be restored afterwards. " + VersionHint)]
    public static Task<string> DeleteSeries(ProjectService projects, SeriesService seriesService, Guid projectId, Guid seriesId, string version, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            McpCall.Found(await seriesService.Delete(projectId, seriesId, version, ct), $"Series {seriesId}");
            return "deleted";
        });

    [McpServerTool(Name = "series_health", ReadOnly = true)]
    [Description(
        "Read-only check of the project's series: number conflicts (several tasks with the same number in a series, usually after a git merge), " +
        "prefix conflicts (several series with one prefix — rename one with update_series), tasks referencing series that do not exist, and unreadable series files. " +
        "Fixing invalid references and number conflicts is done with `tasker cleanup` on the command line or renumber_task.")]
    public static Task<SeriesHealth> SeriesHealthCheck(ProjectService projects, SeriesHealthService health, Guid projectId, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await health.Check(projectId, ct);
        });

    [McpServerTool(Name = "add_task_to_series")]
    [Description(
        "Puts a task into a series: it gets the next number (the highest number in the series + 1; the first is 1). " +
        "A task can be in several series, with a number in each. Already in the series: returns the task unchanged. " +
        SeriesHint + " " +
        "`version` is the task's current version (from get_task). " + TaskTools.TaskIdHint)]
    public static Task<TaskItem> AddTaskToSeries(
        ProjectService projects, SeriesService seriesService, TaskService tasks, Guid projectId, string series, string taskId, string version, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var found = McpCall.Found(await seriesService.Find(projectId, series, ct), $"Series '{series}'");
            return McpCall.Found(await tasks.AddToSeries(projectId, await McpCall.TaskId(tasks, projectId, taskId, ct), found.Id, version, ct), $"Task {taskId}");
        });

    [McpServerTool(Name = "remove_task_from_series")]
    [Description(
        "Takes a task out of a series; its number there becomes free and may be given to another task later. Not in the series: returns the task unchanged. " +
        SeriesHint + " `version` is the task's current version (from get_task). " + TaskTools.TaskIdHint)]
    public static Task<TaskItem> RemoveTaskFromSeries(
        ProjectService projects, SeriesService seriesService, TaskService tasks, Guid projectId, string series, string taskId, string version, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var found = McpCall.Found(await seriesService.Find(projectId, series, ct), $"Series '{series}'");
            return McpCall.Found(await tasks.RemoveFromSeries(projectId, await McpCall.TaskId(tasks, projectId, taskId, ct), found.Id, version, ct), $"Task {taskId}");
        });

    [McpServerTool(Name = "renumber_task", Idempotent = true)]
    [Description(
        "Changes the number of a task in a series: `to` is the wanted number (must be free, otherwise [in_use]); omit it to take the next free number (highest + 1). " +
        "Use it to resolve a number conflict reported by series_health. The task must already be in the series ([not_found] otherwise). " +
        SeriesHint + " `version` is the task's current version (from get_task). " + TaskTools.TaskIdHint)]
    public static Task<TaskItem> RenumberTask(
        ProjectService projects, SeriesService seriesService, TaskService tasks, Guid projectId, string series, string taskId, string version,
        int? to = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var found = McpCall.Found(await seriesService.Find(projectId, series, ct), $"Series '{series}'");
            return McpCall.Found(await tasks.Renumber(projectId, await McpCall.TaskId(tasks, projectId, taskId, ct), found.Id, to, version, ct), $"Task {taskId} in series '{series}'");
        });

    [McpServerTool(Name = "find_tasks_by_reference", ReadOnly = true)]
    [Description(
        "Finds tasks by a reference: a task id (Guid, or its first 8 or more hex characters) or PREFIX-number such as TSK-5 (prefix is case-sensitive). " +
        "Returns all matches: normally one, several if the number is duplicated in the series (see series_health), none if nothing matches ([invalid] if the text is neither an id nor a reference). " + TaskTools.DescriptionHint)]
    public static Task<TaskMatches> FindTasksByReference(ProjectService projects, TaskService tasks, Guid projectId, string reference, int descriptionLength = DescriptionPreview.McpDefault, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return new TaskMatches(await tasks.Preview(projectId, await tasks.Resolve(projectId, reference, ct, allowIdPrefix: true), descriptionLength, ct));
        });
}
