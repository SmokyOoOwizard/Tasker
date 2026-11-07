using Tasker.Core.Tasks;
using Tasker.Core.Users;

namespace Tasker.Core.Comments;

public class TaskCommentService(
    ITaskCommentStorage comments,
    ITaskStorage tasks,
    IUserStorage users,
    ICurrentUser currentUser,
    TimeProvider time,
    LocalHuman? human = null)
{
    public async Task<TaskCommentView[]?> List(Guid projectId, Guid taskId, CancellationToken ct = default)
    {
        if (await tasks.GetById(projectId, taskId, ct) == null)
            return null;

        return await Views(projectId, taskId, currentUser.Id ?? (human == null ? null : await human.Find(users, ct)), ct);
    }

    public async Task<TaskCommentView?> Add(Guid projectId, Guid taskId, AddTaskComment command, CancellationToken ct = default)
    {
        if (await tasks.GetById(projectId, taskId, ct) == null)
            return null;

        var body = Body(command.Body);
        if (command.ParentId is { } parentId)
        {
            var parent = await comments.GetById(projectId, parentId, ct);
            if (parent == null || parent.TaskId != taskId)
                throw TaskerErrors.CommentParentNotFound(parentId);
            if (parent.Deleted)
                throw TaskerErrors.CommentParentDeleted(parentId);
        }

        var author = await Writer(ct);
        var comment = new TaskComment
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            TaskId = taskId,
            ParentId = command.ParentId,
            AuthorId = author.Id,
            AuthorName = author.Username,
            AuthorKind = author.Kind,
            Body = body,
            CreatedAt = time.GetUtcNow(),
            Version = Versioning.New
        };
        await comments.Add(comment, ct);
        return await View(projectId, taskId, comment.Id, author.Id, ct);
    }

    public async Task<TaskCommentView?> Update(Guid projectId, Guid taskId, Guid id, UpdateTaskComment command, CancellationToken ct = default)
    {
        var comment = await comments.GetById(projectId, id, ct);
        if (comment == null || comment.TaskId != taskId || comment.Deleted)
            return null;

        var author = await Own(comment, ct);
        var body = Body(command.Body);
        var expected = Versioning.Check(comment.Version, command.Version, Subject(comment));
        if (body != comment.Body)
        {
            var written = await comments.Update(comment with { Body = body, EditedAt = time.GetUtcNow() }, expected, ct);
            if (written == null)
                throw Versioning.Modified(Subject(comment));
        }
        return await View(projectId, taskId, id, author, ct);
    }

    public async Task<bool> Delete(Guid projectId, Guid taskId, Guid id, string? version, CancellationToken ct = default)
    {
        var comment = await comments.GetById(projectId, id, ct);
        if (comment == null || comment.TaskId != taskId || comment.Deleted)
            return false;

        await Own(comment, ct);
        var expected = Versioning.Check(comment.Version, version, Subject(comment));
        var written = await comments.Update(comment with { Body = "", Deleted = true, EditedAt = time.GetUtcNow() }, expected, ct);
        if (written == null)
            throw Versioning.Modified(Subject(comment));
        return true;
    }

    public async Task<Guid?> ResolveId(Guid projectId, Guid taskId, string reference, CancellationToken ct = default)
    {
        var text = reference.Trim();
        if (Guid.TryParse(text, out var full))
            return full;

        var key = ShortId.TryKey(text);
        if (key == null)
            throw TaskerErrors.Invalid($"Comment id is not valid: '{reference}'");

        var found = (await comments.GetByTask(projectId, taskId, ct)).Where(x => ShortId.Matches(x.Id, key)).ToArray();
        return found.Length switch
        {
            0 => null,
            1 => found[0].Id,
            _ => throw TaskerErrors.Invalid($"Comment id '{reference}' matches several comments: {string.Join(", ", found.Select(x => x.Id))}")
        };
    }

    private static string Body(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw TaskerErrors.Required(ErrorField.CommentBody);
        return value.Trim();
    }

    private static ErrorSubject Subject(TaskComment comment) => ErrorSubject.Comment(ShortId.Of(comment.Id));

    private async Task<User> Writer(CancellationToken ct)
    {
        var id = currentUser.Id ?? (human == null ? null : await human.Ensure(users, ct));
        if (id == null || await users.GetById(id.Value, ct) is not { } user)
            throw TaskerErrors.SignInRequired();
        return user;
    }

    private async Task<Guid> Own(TaskComment comment, CancellationToken ct)
    {
        var author = await Writer(ct);
        if (author.Id != comment.AuthorId)
            throw TaskerErrors.CommentNotAuthor();
        return author.Id;
    }

    private async Task<TaskCommentView?> View(Guid projectId, Guid taskId, Guid id, Guid? me, CancellationToken ct) =>
        (await Views(projectId, taskId, me, ct)).FirstOrDefault(x => x.Id == id);

    private async Task<TaskCommentView[]> Views(Guid projectId, Guid taskId, Guid? me, CancellationToken ct)
    {
        var all = await comments.GetByTask(projectId, taskId, ct);
        var known = new Dictionary<Guid, User?>();
        foreach (var authorId in all.Where(x => !x.Deleted).Select(x => x.AuthorId).Distinct())
            known[authorId] = await users.GetById(authorId, ct);

        return CommentTree.Build(all, taskId).Select(x =>
        {
            var author = x.Deleted
                ? null
                : known.GetValueOrDefault(x.Comment.AuthorId) is { } user
                    ? new CommentAuthor(user.Id, user.Username, user.Kind)
                    : new CommentAuthor(x.Comment.AuthorId, x.Comment.AuthorName, x.Comment.AuthorKind);
            return new TaskCommentView(
                x.Comment.Id, taskId, x.ParentId, x.Depth, author,
                x.Deleted ? "" : x.Comment.Body,
                x.Comment.CreatedAt,
                x.Deleted ? null : x.Comment.EditedAt,
                x.Deleted,
                !x.Deleted && me == x.Comment.AuthorId,
                x.Comment.Version);
        }).ToArray();
    }
}

internal static class CommentTree
{
    public sealed record Node(TaskComment Comment, Guid? ParentId, int Depth, bool Deleted);

    public static IReadOnlyList<Node> Build(IReadOnlyList<TaskComment> comments, Guid taskId)
    {
        var byId = comments.ToDictionary(x => x.Id);
        foreach (var missing in comments.Where(x => x.ParentId is { } p && !byId.ContainsKey(p)).Select(x => x.ParentId!.Value).Distinct().ToArray())
        {
            var first = comments.Where(x => x.ParentId == missing).Min(x => x.CreatedAt);
            byId[missing] = new TaskComment
            {
                Id = missing,
                ProjectId = comments[0].ProjectId,
                TaskId = taskId,
                AuthorId = Guid.Empty,
                AuthorName = "",
                CreatedAt = first,
                Deleted = true,
                Version = Versioning.New
            };
        }

        var children = byId.Values
            .Where(x => x.ParentId != null)
            .GroupBy(x => x.ParentId!.Value)
            .ToDictionary(x => x.Key, x => Ordered(x).ToArray());

        var result = new List<Node>();
        foreach (var root in Ordered(byId.Values.Where(x => x.ParentId == null)))
            Walk(root, 0, children, result, []);
        return result;
    }

    private static IEnumerable<TaskComment> Ordered(IEnumerable<TaskComment> items) =>
        items.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id);

    private static bool Walk(TaskComment comment, int depth, Dictionary<Guid, TaskComment[]> children, List<Node> result, HashSet<Guid> path)
    {
        if (!path.Add(comment.Id))
            return false;

        var at = result.Count;
        result.Add(new Node(comment, comment.ParentId, depth, comment.Deleted));
        var live = !comment.Deleted;
        foreach (var child in children.GetValueOrDefault(comment.Id) ?? [])
            live |= Walk(child, depth + 1, children, result, path);
        path.Remove(comment.Id);

        if (!live)
            result.RemoveRange(at, result.Count - at);
        return live;
    }
}
