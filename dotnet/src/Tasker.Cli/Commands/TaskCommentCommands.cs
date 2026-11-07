using System.CommandLine;
using System.Text;
using Tasker.Cli.Completion;
using Tasker.Core.Comments;
using Tasker.Core.Users;

namespace Tasker.Cli.Commands;

internal static class TaskCommentCommands
{
    public static Command Build(GlobalOptions g, Argument<string> task, Func<Context, Guid, string, Task<Guid>> findTask)
    {
        var body = new Argument<string>("text") { Description = "Comment text (Markdown)" }.NoSuggestions();
        var comment = new Argument<string>("comment") { Description = "Comment: id or its first 8 or more characters, as 'task comment list' shows" }.NoSuggestions();
        var replyTo = new Option<string?>("--reply-to") { Description = "Reply to this comment: id or its first 8 or more characters" }.NoSuggestions();
        var expected = Kit.ExpectedVersion();

        return Kit.Group("comment", "Comments of a task: a tree of replies from people and agents",
            Kit.Leaf(g, "list", "Shows the comment tree of a task", c => c.Arguments.Add(task), async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var taskId = await findTask(ctx, projectId, parse.GetRequiredValue(task));
                var views = await ctx.Get<TaskCommentService>().List(projectId, taskId, ctx.Ct) ?? [];
                ctx.Print(views, Tree(views));
            }),
            Kit.Leaf(g, "add", "Adds a comment to a task or, with --reply-to, a reply to a comment", c =>
            {
                c.Arguments.Add(task);
                c.Arguments.Add(body);
                c.Options.Add(replyTo);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var taskId = await findTask(ctx, projectId, parse.GetRequiredValue(task));
                var service = ctx.Get<TaskCommentService>();
                Guid? parent = parse.GetValue(replyTo) is { } reference ? await Find(ctx, service, projectId, taskId, reference) : null;
                var view = await service.Add(projectId, taskId, new AddTaskComment(parse.GetRequiredValue(body), parent), ctx.Ct);
                ctx.Print(view!, $"Added comment {view!.Id}");
            }),
            Kit.Leaf(g, "edit", "Changes the text of your own comment", c =>
            {
                c.Arguments.Add(task);
                c.Arguments.Add(comment);
                c.Arguments.Add(body);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var taskId = await findTask(ctx, projectId, parse.GetRequiredValue(task));
                var service = ctx.Get<TaskCommentService>();
                var id = await Find(ctx, service, projectId, taskId, parse.GetRequiredValue(comment));
                var version = parse.GetValue(expected) ?? await Current(ctx, service, projectId, taskId, id);
                var view = await service.Update(projectId, taskId, id, new UpdateTaskComment(parse.GetRequiredValue(body), version), ctx.Ct)
                    ?? throw new CliException($"Comment not found: {parse.GetRequiredValue(comment)}");
                ctx.Print(view, $"Updated comment {view.Id}");
            }),
            Kit.Leaf(g, "delete", "Deletes your own comment; its replies stay, a mark is shown in its place", c =>
            {
                c.Arguments.Add(task);
                c.Arguments.Add(comment);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var taskId = await findTask(ctx, projectId, parse.GetRequiredValue(task));
                var service = ctx.Get<TaskCommentService>();
                var id = await Find(ctx, service, projectId, taskId, parse.GetRequiredValue(comment));
                var version = parse.GetValue(expected) ?? await Current(ctx, service, projectId, taskId, id);
                if (!await service.Delete(projectId, taskId, id, version, ctx.Ct))
                    throw new CliException($"Comment not found: {parse.GetRequiredValue(comment)}");
                ctx.Print(new { deleted = id }, $"Deleted comment {id}");
            }));
    }

    public static string Tree(IReadOnlyList<TaskCommentView> views)
    {
        if (views.Count == 0)
            return "No comments";

        var text = new StringBuilder();
        foreach (var view in views)
        {
            var indent = new string(' ', view.Depth * 2);
            if (text.Length > 0)
                text.Append('\n');
            if (view.Deleted)
            {
                text.Append(indent).Append(Tasker.Core.ShortId.Of(view.Id)).Append("  (deleted)");
                continue;
            }

            text.Append(indent).Append(Tasker.Core.ShortId.Of(view.Id)).Append("  ").Append(view.Author!.Name);
            if (view.Author.Kind == UserKind.Agent)
                text.Append(" [agent]");
            text.Append("  ").Append(view.CreatedAt.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
            if (view.EditedAt != null)
                text.Append("  (edited)");
            foreach (var line in view.Body.Replace("\r\n", "\n").Split('\n'))
                text.Append('\n').Append(indent).Append("  ").Append(line);
        }
        return text.ToString();
    }

    private static async Task<Guid> Find(Context ctx, TaskCommentService service, Guid projectId, Guid taskId, string reference) =>
        await service.ResolveId(projectId, taskId, reference, ctx.Ct) ?? throw new CliException($"Comment not found: {reference}");

    private static async Task<string> Current(Context ctx, TaskCommentService service, Guid projectId, Guid taskId, Guid id) =>
        (await service.List(projectId, taskId, ctx.Ct))?.FirstOrDefault(x => x.Id == id)?.Version
        ?? throw new CliException($"Comment not found: {id}");
}
