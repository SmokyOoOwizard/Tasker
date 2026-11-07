using System.ComponentModel;
using ModelContextProtocol.Server;
using Tasker.Core.Comments;
using Tasker.Core.Projects;
using Tasker.Core.Tasks;

namespace Tasker.Mcp.Tools;

[McpServerToolType]
public static class CommentTools
{
    private const string CommentIdHint =
        "`commentId` is the comment's id, full or short (the first 8 or more hex characters), from get_task_comments.";

    public record TaskComments(TaskCommentView[] Comments);

    [McpServerTool(Name = "get_task_comments", ReadOnly = true)]
    [Description(
        "Reads the comment tree of a task, in reading order: every reply follows its parent. Each comment has `id`, `parentId` (null for a top-level comment), " +
        "`depth` (0 for top level), `author` {id, name, kind: human|agent}, `body` (Markdown), `createdAt`, `editedAt`, `mine` and `version`. " +
        "A deleted comment that still has replies is present with `deleted` true, no author and an empty body. " + TaskTools.TaskIdHint)]
    public static Task<TaskComments> GetTaskComments(
        ProjectService projects, TaskService tasks, TaskCommentService comments, Guid projectId, string taskId, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var task = await McpCall.TaskId(tasks, projectId, taskId, ct);
            return new TaskComments(McpCall.Found(await comments.List(projectId, task, ct), $"Task {taskId}"));
        });

    [McpServerTool(Name = "add_task_comment")]
    [Description(
        "Adds a comment to a task, or a reply when `parentId` is given: reply to any comment at any depth, except a deleted one. `body` is Markdown. " +
        "You become the author automatically and are shown as an agent. Returns the new comment. `parentId` is a comment id, full or short. " + TaskTools.TaskIdHint)]
    public static Task<TaskCommentView> AddTaskComment(
        ProjectService projects, TaskService tasks, TaskCommentService comments, Guid projectId, string taskId, string body, string? parentId = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var task = await McpCall.TaskId(tasks, projectId, taskId, ct);
            var parent = parentId == null ? (Guid?)null : await CommentId(comments, projectId, task, parentId, ct);
            return McpCall.Found(await comments.Add(projectId, task, new AddTaskComment(body, parent), ct), $"Task {taskId}");
        });

    [McpServerTool(Name = "update_task_comment", Idempotent = true)]
    [Description(
        "Changes the text of your own comment; someone else's fails with [forbidden]. `version` is the comment's current version (from get_task_comments); " +
        "if it changed since, the call fails with [modified]: re-read and retry. " + CommentIdHint + " " + TaskTools.TaskIdHint)]
    public static Task<TaskCommentView> UpdateTaskComment(
        ProjectService projects, TaskService tasks, TaskCommentService comments, Guid projectId, string taskId, string commentId, string body, string version, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var task = await McpCall.TaskId(tasks, projectId, taskId, ct);
            var id = await CommentId(comments, projectId, task, commentId, ct);
            return McpCall.Found(await comments.Update(projectId, task, id, new UpdateTaskComment(body, version), ct), $"Comment {commentId}");
        });

    [McpServerTool(Name = "delete_task_comment", Destructive = true)]
    [Description(
        "Deletes your own comment; someone else's fails with [forbidden]. Its replies stay: if it has any, a mark is shown in its place. " +
        "`version` as in update_task_comment. " + CommentIdHint + " " + TaskTools.TaskIdHint)]
    public static Task<string> DeleteTaskComment(
        ProjectService projects, TaskService tasks, TaskCommentService comments, Guid projectId, string taskId, string commentId, string version, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var task = await McpCall.TaskId(tasks, projectId, taskId, ct);
            var id = await CommentId(comments, projectId, task, commentId, ct);
            McpCall.Found(await comments.Delete(projectId, task, id, version, ct), $"Comment {commentId}");
            return "deleted";
        });

    private static async Task<Guid> CommentId(TaskCommentService comments, Guid projectId, Guid taskId, string reference, CancellationToken ct) =>
        await comments.ResolveId(projectId, taskId, reference, ct) ?? throw new ModelContextProtocol.McpException($"[not_found] Comment {reference} not found");
}
