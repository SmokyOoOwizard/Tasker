using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Autofac;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core;
using Tasker.Core.Comments;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.Users;
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Storage.Files;
using Tasker.Web;
using Xunit;

namespace Tasker.Tests;

public abstract class CommentContract : IAsyncLifetime
{
    private sealed class AsUser(Guid id) : ICurrentUser
    {
        public Guid? Id => id;
    }

    protected sealed record Setup(Guid Project, Guid Task, Guid Other, Guid Human, Guid Agent);

    public abstract Task InitializeAsync();
    public abstract Task DisposeAsync();

    protected abstract Task<T> Run<T>(Func<ILifetimeScope, Task<T>> action);

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    protected Task<Setup> NewSetup() => Run(async scope =>
    {
        var project = Guid.NewGuid();
        var status = Guid.NewGuid();
        var set = Guid.NewGuid();
        var type = Guid.NewGuid();
        await scope.Resolve<IProjectStorage>().Add(new Project { Id = project, Name = "P" + project.ToString("N")[..6], CreatedAt = Start, Version = "" });
        await scope.Resolve<IStatusStorage>().Add(new Status { Id = status, ProjectId = project, Name = "Open", Color = "#000000", Version = "" });
        await scope.Resolve<IStatusSetStorage>().Add(new StatusSet { Id = set, ProjectId = project, Name = "S", StatusIds = [status], Version = "" });
        await scope.Resolve<ITaskTypeStorage>().Add(new TaskType { Id = type, ProjectId = project, Name = "T", StatusSetId = set, Version = "" });
        var tasks = new List<Guid>();
        foreach (var title in new[] { "A", "B" })
        {
            var id = Guid.NewGuid();
            tasks.Add(id);
            await scope.Resolve<ITaskStorage>().Add(new TaskItem { Id = id, ProjectId = project, Title = title, TypeId = type, StatusId = status, CreatedAt = Start, UpdatedAt = Start, Version = "" });
        }

        var human = Guid.NewGuid();
        var agent = Guid.NewGuid();
        await scope.Resolve<IUserStorage>().Add(new User { Id = human, Username = "human" + human.ToString("N")[..6], IsAdmin = false, CreatedAt = Start, Version = "" }, null);
        await scope.Resolve<IUserStorage>().Add(new User { Id = agent, Username = "agent" + agent.ToString("N")[..6], IsAdmin = false, Kind = UserKind.Agent, CreatedAt = Start, Version = "" }, null);
        return new Setup(project, tasks[0], tasks[1], human, agent);
    });

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = Start;

        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }

    private readonly Clock _clock = new();

    protected TaskCommentService Service(ILifetimeScope scope, Guid user) => new(
        scope.Resolve<ITaskCommentStorage>(), scope.Resolve<ITaskStorage>(), scope.Resolve<IUserStorage>(), new AsUser(user), _clock,
        new Tasker.Core.Agents.AgentGuard(new AsUser(user), scope.Resolve<IUserStorage>(), new UserOptions(false)));

    protected Task<TaskCommentView> Add(Setup s, Guid user, string body, Guid? parent = null) =>
        Run(async scope => (await Service(scope, user).Add(s.Project, s.Task, new AddTaskComment(body, parent)))!);

    protected Task<TaskCommentView[]> List(Setup s, Guid user) =>
        Run(async scope => (await Service(scope, user).List(s.Project, s.Task))!);

    [Fact]
    public async Task Replies_form_a_tree_in_reading_order_at_any_depth()
    {
        var s = await NewSetup();
        var first = await Add(s, s.Human, "first");
        var second = await Add(s, s.Human, "second");
        var parent = first.Id;
        for (var i = 1; i <= 12; i++)
            parent = (await Add(s, i % 2 == 0 ? s.Human : s.Agent, "level " + i, parent)).Id;
        var sibling = await Add(s, s.Human, "sibling", first.Id);

        var tree = await List(s, s.Human);

        Assert.Equal(15, tree.Length);
        Assert.Equal("first", tree[0].Body);
        Assert.Equal(Enumerable.Range(1, 12).Select(i => "level " + i), tree.Skip(1).Take(12).Select(x => x.Body));
        Assert.Equal(Enumerable.Range(1, 12), tree.Skip(1).Take(12).Select(x => x.Depth));
        Assert.Equal(sibling.Id, tree[13].Id);
        Assert.Equal(1, tree[13].Depth);
        Assert.Equal(second.Id, tree[14].Id);
        Assert.Equal(0, tree[14].Depth);
    }

    [Fact]
    public async Task The_author_is_seen_with_time_and_kind()
    {
        var s = await NewSetup();
        var person = await Add(s, s.Human, "from a person");
        var agent = await Add(s, s.Agent, "from an agent", person.Id);

        var tree = await List(s, s.Human);

        Assert.Equal(UserKind.Human, tree[0].Author!.Kind);
        Assert.True(tree[0].Mine);
        Assert.Equal(UserKind.Agent, tree[1].Author!.Kind);
        Assert.Equal(agent.Author!.Id, s.Agent);
        Assert.False(tree[1].Mine);
        Assert.True(tree[1].CreatedAt > tree[0].CreatedAt);
    }

    [Fact]
    public async Task Only_the_author_changes_a_comment_and_a_stale_version_is_refused()
    {
        var s = await NewSetup();
        var comment = await Add(s, s.Human, "text");

        await Assert.ThrowsAsync<TaskerForbiddenException>(() => Run<object?>(async scope =>
            await Service(scope, s.Agent).Update(s.Project, s.Task, comment.Id, new UpdateTaskComment("hack", comment.Version))));
        await Assert.ThrowsAsync<TaskerForbiddenException>(() => Run<object?>(async scope =>
            await Service(scope, s.Agent).Delete(s.Project, s.Task, comment.Id, comment.Version)));

        var edited = await Run(async scope => (await Service(scope, s.Human).Update(s.Project, s.Task, comment.Id, new UpdateTaskComment("changed", comment.Version)))!);
        Assert.Equal("changed", edited.Body);
        Assert.NotNull(edited.EditedAt);
        Assert.NotEqual(comment.Version, edited.Version);

        await Assert.ThrowsAsync<TaskerConflictException>(() => Run<object?>(async scope =>
            await Service(scope, s.Human).Update(s.Project, s.Task, comment.Id, new UpdateTaskComment("again", comment.Version))));
    }

    [Fact]
    public async Task A_deleted_comment_with_replies_stays_as_a_mark_and_the_branch_is_whole()
    {
        var s = await NewSetup();
        var root = await Add(s, s.Human, "root");
        var middle = await Add(s, s.Human, "secret", root.Id);
        var reply = await Add(s, s.Agent, "reply", middle.Id);

        Assert.True(await Run(scope => Service(scope, s.Human).Delete(s.Project, s.Task, middle.Id, middle.Version)));
        var tree = await List(s, s.Human);

        Assert.Equal([root.Id, middle.Id, reply.Id], tree.Select(x => x.Id));
        Assert.True(tree[1].Deleted);
        Assert.Equal("", tree[1].Body);
        Assert.Null(tree[1].Author);
        Assert.False(tree[1].Mine);
        Assert.Equal(2, tree[2].Depth);

        var refused = await Assert.ThrowsAsync<TaskerValidationException>(() => Add(s, s.Human, "late", middle.Id));
        Assert.Equal("comment.parentDeleted", refused.Error.Code);
    }

    [Fact]
    public async Task A_deleted_comment_without_live_replies_disappears()
    {
        var s = await NewSetup();
        var root = await Add(s, s.Human, "root");
        var middle = await Add(s, s.Human, "middle", root.Id);
        var leaf = await Add(s, s.Human, "leaf", middle.Id);

        Assert.True(await Run(scope => Service(scope, s.Human).Delete(s.Project, s.Task, middle.Id, middle.Version)));
        Assert.Equal(3, (await List(s, s.Human)).Length);

        Assert.True(await Run(scope => Service(scope, s.Human).Delete(s.Project, s.Task, leaf.Id, leaf.Version)));

        Assert.Equal([root.Id], (await List(s, s.Human)).Select(x => x.Id));
    }

    [Fact]
    public async Task Bad_input_is_refused()
    {
        var s = await NewSetup();
        var other = await Run(async scope => (await Service(scope, s.Human).Add(s.Project, s.Other, new AddTaskComment("in another task")))!);

        var blank = await Assert.ThrowsAsync<TaskerValidationException>(() => Add(s, s.Human, "   "));
        Assert.Equal("validation.required", blank.Error.Code);
        var foreign = await Assert.ThrowsAsync<TaskerValidationException>(() => Add(s, s.Human, "text", other.Id));
        Assert.Equal("comment.parentNotFound", foreign.Error.Code);
        Assert.Null(await Run(scope => Service(scope, s.Human).Add(s.Project, Guid.NewGuid(), new AddTaskComment("text"))));
        Assert.Null(await Run(scope => Service(scope, s.Human).List(s.Project, Guid.NewGuid())));
    }

    [Fact]
    public async Task Removing_the_comments_of_a_task_leaves_other_tasks_alone()
    {
        var s = await NewSetup();
        await Add(s, s.Human, "goes away");
        var kept = await Run(async scope => (await Service(scope, s.Human).Add(s.Project, s.Other, new AddTaskComment("stays")))!);

        Assert.Equal(1, await Run(scope => scope.Resolve<ITaskCommentStorage>().DeleteByTask(s.Project, s.Task)));

        Assert.Empty(await List(s, s.Human));
        Assert.Equal([kept.Id], (await Run(async scope => (await Service(scope, s.Human).List(s.Project, s.Other))!)).Select(x => x.Id));
    }
}

public sealed class FilesCommentTests : CommentContract
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private IContainer _container = null!;

    public override Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var builder = new ContainerBuilder();
        builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterModule(new FileStorageModule(_root));
        _container = builder.Build();
        return Task.CompletedTask;
    }

    public override async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    protected override Task<T> Run<T>(Func<ILifetimeScope, Task<T>> action) => action(_container);

    private string[] CommentFiles() => Directory.Exists(Path.Combine(_root, ".tasker"))
        ? Directory.GetFiles(Path.Combine(_root, ".tasker"), "*.yaml", SearchOption.AllDirectories).Where(x => x.Contains($"{Path.DirectorySeparatorChar}comments{Path.DirectorySeparatorChar}")).ToArray()
        : [];

    [Fact]
    public async Task Every_comment_is_a_file_of_its_own_and_the_task_file_does_not_change()
    {
        var s = await NewSetup();
        var before = await Run(scope => scope.Resolve<ITaskStorage>().GetById(s.Project, s.Task));
        var first = await Add(s, s.Human, "First line\nSecond line");
        var reply = await Add(s, s.Agent, "Reply", first.Id);

        var files = CommentFiles();
        Assert.Equal(2, files.Length);
        var name = Path.GetFileName(files.Single(x => x.EndsWith(first.Id.ToString("N")[..8] + ".yaml")));
        Assert.Equal($"{s.Task.ToString("N")[..8]}-{first.Id.ToString("N")[..8]}.yaml", name);

        var text = await File.ReadAllTextAsync(files.Single(x => x.EndsWith(reply.Id.ToString("N")[..8] + ".yaml")));
        Assert.StartsWith("formatVersion: 13\n", text.ReplaceLineEndings("\n"));
        Assert.Contains($"parentId: {first.Id}", text);
        Assert.Contains("authorKind: agent", text);
        Assert.EndsWith("body: Reply\n", text.ReplaceLineEndings("\n"));

        var after = await Run(scope => scope.Resolve<ITaskStorage>().GetById(s.Project, s.Task));
        Assert.Equal(before!.Version, after!.Version);
    }

    [Fact]
    public async Task A_deleted_comment_keeps_no_text_in_its_file()
    {
        var s = await NewSetup();
        var comment = await Add(s, s.Human, "secret");
        await Add(s, s.Human, "reply", comment.Id);

        await Run(scope => Service(scope, s.Human).Delete(s.Project, s.Task, comment.Id, comment.Version));

        var text = await File.ReadAllTextAsync(CommentFiles().Single(x => x.EndsWith(comment.Id.ToString("N")[..8] + ".yaml")));
        Assert.DoesNotContain("secret", text);
        Assert.Contains("deleted: true", text);
    }

    [Fact]
    public async Task A_reply_whose_parent_file_is_gone_stays_visible_under_a_mark()
    {
        var s = await NewSetup();
        var parent = await Add(s, s.Human, "parent");
        var reply = await Add(s, s.Human, "reply", parent.Id);
        File.Delete(CommentFiles().Single(x => x.EndsWith(parent.Id.ToString("N")[..8] + ".yaml")));
        await Run(async scope =>
        {
            await scope.Resolve<Tasker.Core.Workspace.IWorkspaceIndex>().Rescan();
            return 0;
        });

        var tree = await List(s, s.Human);

        Assert.Equal([parent.Id, reply.Id], tree.Select(x => x.Id));
        Assert.True(tree[0].Deleted);
        Assert.Equal("reply", tree[1].Body);
    }
}

public sealed class DbCommentTests : CommentContract
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private IContainer _container = null!;

    private string DbFile => Path.Combine(_root, "tasker.db");

    public override async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _container = Open();
        await _container.Resolve<IStorageLifecycle>().Start(default);
    }

    private IContainer Open()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule(new DbStorageModule(new DbConfigs { SqliteFile = DbFile }));
        return builder.Build();
    }

    public override async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    protected override async Task<T> Run<T>(Func<ILifetimeScope, Task<T>> action)
    {
        await using var scope = _container.BeginLifetimeScope();
        return await action(scope);
    }

    [Fact]
    public async Task Deleting_a_task_row_deletes_its_comments()
    {
        var s = await NewSetup();
        await Add(s, s.Human, "goes away");
        var task = await Run(scope => scope.Resolve<ITaskStorage>().GetById(s.Project, s.Task));

        await Run(scope => scope.Resolve<ITaskStorage>().Delete(s.Project, s.Task, task!.Version));

        Assert.Empty(await Run(scope => scope.Resolve<ITaskCommentStorage>().GetAll(s.Project)));
    }

    [Fact]
    public async Task A_database_created_before_comments_gets_the_table_on_start()
    {
        var s = await NewSetup();
        await Add(s, s.Human, "old");
        await _container.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={DbFile}"))
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE task_comments";
            await drop.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        _container = Open();
        await _container.Resolve<IStorageLifecycle>().Start(default);

        Assert.Empty(await List(s, s.Human));
        var comment = await Add(s, s.Human, "new");
        Assert.Equal([comment.Id], (await List(s, s.Human)).Select(x => x.Id));
    }
}

public class CommentConsoleTests
{
    [Fact]
    public void The_native_console_has_json_metadata_for_comments()
    {
        Assert.NotNull(Tasker.Cli.CliJsonContext.Default.GetTypeInfo(typeof(TaskCommentView)));
        Assert.NotNull(Tasker.Cli.CliJsonContext.Default.GetTypeInfo(typeof(TaskCommentView[])));
    }

    private static Task<CliResult> Ok(Task<CliResult> run) => SeriesRepo.Ok(run);

    private static async Task<(string First, string Reply)> TwoComments(Func<string[], Task<CliResult>> p)
    {
        var first = (await Ok(p(["task", "comment", "add", "TSK-1", "Первый", "--json"]))).Json["id"]!.GetValue<string>();
        var reply = (await Ok(p(["task", "comment", "add", "TSK-1", "Ответ", "--reply-to", first[..8], "--json"]))).Json["id"]!.GetValue<string>();
        return (first, reply);
    }

    [Fact]
    public async Task The_console_shows_the_tree_and_adds_changes_and_deletes_comments()
    {
        using var repo = new SeriesRepo();
        await repo.Seed();

        var empty = await Ok(repo.P("task", "comment", "list", "TSK-1"));
        Assert.Equal("No comments", empty.Out.Trim());

        var (first, reply) = await TwoComments(args => repo.P(args));
        var list = (await Ok(repo.P("task", "comment", "list", "TSK-1"))).Out.ReplaceLineEndings("\n");
        Assert.Contains("  Первый\n", list);
        Assert.Contains("\n  " + reply[..8], list);
        Assert.Contains("\n    Ответ", list);

        await Ok(repo.P("task", "comment", "edit", "TSK-1", reply[..8], "Изменён"));
        var json = (await Ok(repo.P("task", "comment", "list", "TSK-1", "--json"))).Json.AsArray();
        Assert.Equal(first, json[1]!["parentId"]!.GetValue<string>());
        Assert.Equal(1, json[1]!["depth"]!.GetValue<int>());
        Assert.Equal("Изменён", json[1]!["body"]!.GetValue<string>());
        Assert.Equal("human", json[1]!["author"]!["kind"]!.GetValue<string>());
        Assert.True(json[1]!["mine"]!.GetValue<bool>());

        await Ok(repo.P("task", "comment", "delete", "TSK-1", first[..8]));
        var after = (await Ok(repo.P("task", "comment", "list", "TSK-1"))).Out;
        Assert.Contains("(deleted)", after);
        Assert.DoesNotContain("Первый", after);
        Assert.Contains("Изменён", after);
    }

    [Fact]
    public async Task Comments_from_two_git_branches_merge_without_a_conflict()
    {
        using var repo = new SeriesRepo();
        await repo.Seed();
        var root = (await Ok(repo.P("task", "comment", "add", "TSK-1", "Общий", "--json"))).Json["id"]!.GetValue<string>();
        repo.Repo.Commit("root comment");

        await repo.Branch("a", () => Ok(repo.P("task", "comment", "add", "TSK-1", "Из ветки А", "--reply-to", root)));
        await repo.Branch("b", () => Ok(repo.P("task", "comment", "add", "TSK-1", "Из ветки Б", "--reply-to", root)));
        repo.Merge("a", "b");

        var tree = (await Ok(repo.P("task", "comment", "list", "TSK-1", "--json"))).Json.AsArray();
        Assert.Equal(3, tree.Count);
        Assert.Contains(tree, x => x!["body"]!.GetValue<string>() == "Из ветки А" && x["depth"]!.GetValue<int>() == 1);
        Assert.Contains(tree, x => x!["body"]!.GetValue<string>() == "Из ветки Б" && x["depth"]!.GetValue<int>() == 1);
        Assert.Empty(repo.Repo.Git("diff", "--name-only", "--diff-filter=U").Trim());
    }

    [Fact]
    public async Task A_reply_merged_with_the_deletion_of_its_parent_keeps_the_branch()
    {
        using var repo = new SeriesRepo();
        await repo.Seed();
        var root = (await Ok(repo.P("task", "comment", "add", "TSK-1", "Родитель", "--json"))).Json["id"]!.GetValue<string>();
        repo.Repo.Commit("root comment");

        await repo.Branch("delete", () => Ok(repo.P("task", "comment", "delete", "TSK-1", root)));
        await repo.Branch("reply", () => Ok(repo.P("task", "comment", "add", "TSK-1", "Ответ", "--reply-to", root)));
        repo.Merge("delete", "reply");

        var tree = (await Ok(repo.P("task", "comment", "list", "TSK-1", "--json"))).Json.AsArray();
        Assert.Equal(2, tree.Count);
        Assert.True(tree[0]!["deleted"]!.GetValue<bool>());
        Assert.Equal("Ответ", tree[1]!["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task Deleting_a_task_deletes_its_comments_and_cleanup_removes_the_ones_left_by_a_merge()
    {
        using var repo = new SeriesRepo();
        await repo.Seed();
        await repo.NewTask("Вторая", "TSK");
        await repo.NewTask("Третья", "TSK");
        await Ok(repo.P("task", "comment", "add", "TSK-2", "Комментарий"));
        await Ok(repo.P("task", "delete", "TSK-2"));
        var comments = Directory.GetFiles(Path.Combine(repo.Folder, ".tasker"), "*.yaml", SearchOption.AllDirectories)
            .Where(x => x.Contains($"{Path.DirectorySeparatorChar}comments{Path.DirectorySeparatorChar}"));
        Assert.Empty(comments);
        repo.Repo.Commit("two tasks");

        await repo.Branch("del", () => Ok(repo.P("task", "delete", "TSK-3")));
        await repo.Branch("note", () => Ok(repo.P("task", "comment", "add", "TSK-3", "Поздний")));
        repo.Merge("del", "note");

        var report = (await Ok(repo.P("cleanup"))).Out;
        Assert.Contains("its task no longer exists", report);
        comments = Directory.GetFiles(Path.Combine(repo.Folder, ".tasker"), "*.yaml", SearchOption.AllDirectories)
            .Where(x => x.Contains($"{Path.DirectorySeparatorChar}comments{Path.DirectorySeparatorChar}"));
        Assert.Empty(comments);
    }
}

public class CommentApiTests
{
    private const string Read = "query($p: UUID!, $t: UUID!) { project(id: $p) { taskComments(id: $t) { id parentId depth body deleted mine version editedAt author { name kind } } } }";

    private static async Task<JsonArray> Tree(ApiHost api, string task)
    {
        using var http = new HttpClient();
        var (_, body) = await GraphReads.Query(http, api.BaseUrl, Read, new { p = api.ProjectId, t = task });
        Assert.Null(body!["errors"]);
        return body["data"]!["project"]!["taskComments"]!.AsArray();
    }

    [Fact]
    public async Task Rest_changes_and_graphql_reads_comments_of_the_local_person()
    {
        await using var api = await ApiHost.Start("files");
        var task = (await api.NewTask("Задача"))["id"]!.GetValue<string>();
        var path = api.P($"/tasks/{task}/comments");

        var (created, first) = await api.Post(path, new { body = "Первый" });
        Assert.Equal(HttpStatusCode.OK, created);
        var firstId = first!["id"]!.GetValue<string>();
        var (_, reply) = await api.Post(path, new { body = "Ответ", parentId = firstId });

        var tree = await Tree(api, task);
        Assert.Equal(2, tree.Count);
        Assert.Equal("human", tree[0]!["author"]!["kind"]!.GetValue<string>().ToLowerInvariant());
        Assert.True(tree[0]!["mine"]!.GetValue<bool>());
        Assert.Equal(1, tree[1]!["depth"]!.GetValue<int>());

        var (patched, edited) = await api.Patch($"{path}/{reply!["id"]}", new { body = "Правка", version = tree[1]!["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, patched);
        Assert.NotNull(edited!["editedAt"]);

        var (stale, conflict) = await api.Patch($"{path}/{reply["id"]}", new { body = "Снова", version = tree[1]!["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.Conflict, stale);
        Assert.Equal("modified", conflict!["code"]!.GetValue<string>().Split('.').Last());

        var (blank, _) = await api.Post(path, new { body = " " });
        Assert.Equal(HttpStatusCode.BadRequest, blank);

        var (deleted, _) = await api.Delete($"{path}/{firstId}?version={Uri.EscapeDataString(tree[0]!["version"]!.GetValue<string>())}");
        Assert.Equal(HttpStatusCode.NoContent, deleted);
        tree = await Tree(api, task);
        Assert.True(tree[0]!["deleted"]!.GetValue<bool>());
        Assert.Null(tree[0]!["author"]);
        Assert.Equal("Правка", tree[1]!["body"]!.GetValue<string>());

        var (gone, _) = await api.Post(api.P($"/tasks/{Guid.NewGuid()}/comments"), new { body = "text" });
        Assert.Equal(HttpStatusCode.NotFound, gone);
    }

    [Fact]
    public async Task A_change_of_a_comment_reaches_subscribers_as_a_comment_event()
    {
        await using var api = await ApiHost.Start("files");
        var task = (await api.NewTask("Задача"))["id"]!.GetValue<string>();
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Post, api.BaseUrl + "/graphql")
        {
            Content = JsonContent.Create(new { query = "subscription { workspaceChanges { entity projectId id } }" })
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal(": connected", await reader.ReadLineAsync());

        var (_, comment) = await api.Post(api.P($"/tasks/{task}/comments"), new { body = "Живой" });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line == null)
                Assert.Fail("The stream ended");
            if (line!.StartsWith("data:", StringComparison.Ordinal) && line.Contains("\"comment\"", StringComparison.Ordinal))
            {
                Assert.Contains(comment!["id"]!.GetValue<string>(), line);
                return;
            }
        }
    }

    [Fact]
    public async Task An_agent_reads_the_tree_and_replies_to_a_comment_and_is_shown_as_the_author()
    {
        using var daemon = new DaemonFixture();
        var folder = await daemon.Workspace("a", "Alpha");
        var status = await daemon.Start();
        var key = status.Workspaces.Single().Key!;
        var projectId = JsonNode.Parse(await daemon.CallTool(key, "list_projects"))!["data"]![0]!["id"]!.GetValue<string>();

        var statusId = JsonNode.Parse(await daemon.CallTool(key, "create_status", new { projectId, name = "Todo", color = "#112233" }))!["id"]!.GetValue<string>();
        var setId = JsonNode.Parse(await daemon.CallTool(key, "create_status_set", new { projectId, name = "Set", statusIds = new[] { statusId } }))!["id"]!.GetValue<string>();
        var typeId = JsonNode.Parse(await daemon.CallTool(key, "create_task_type", new { projectId, name = "Bug", statusSetId = setId }))!["id"]!.GetValue<string>();
        var taskId = JsonNode.Parse(await daemon.CallTool(key, "create_task", new { projectId, title = "Задача", typeId }))!["id"]!.GetValue<string>();

        var human = await TaskerProcess.Run("task", "comment", "add", taskId, "Вопрос человека", "--json", "-w", folder, "-p", projectId);
        Assert.True(human.Code == 0, human.Err);
        var question = human.Json["id"]!.GetValue<string>();

        var tree = JsonNode.Parse(await daemon.CallTool(key, "get_task_comments", new { projectId, taskId }))!["comments"]!.AsArray();
        Assert.Equal("Вопрос человека", tree.Single()!["body"]!.GetValue<string>());
        Assert.False(tree.Single()!["mine"]!.GetValue<bool>());

        var answer = JsonNode.Parse(await daemon.CallTool(key, "add_task_comment", new { projectId, taskId, body = "Ответ агента", parentId = question[..8] }))!;
        Assert.Equal("agent", answer["author"]!["kind"]!.GetValue<string>());
        Assert.Equal(1, answer["depth"]!.GetValue<int>());
        Assert.Equal(question, answer["parentId"]!.GetValue<string>());

        var (refused, text) = await daemon.CallToolResult(key, "update_task_comment", new { projectId, taskId, commentId = question, body = "x", version = tree.Single()!["version"]!.GetValue<string>() });
        Assert.True(refused);
        Assert.StartsWith("[forbidden]", text);

        var edited = JsonNode.Parse(await daemon.CallTool(key, "update_task_comment", new { projectId, taskId, commentId = answer["id"]!.GetValue<string>(), body = "Уточнение", version = answer["version"]!.GetValue<string>() }))!;
        Assert.Equal("Уточнение", edited["body"]!.GetValue<string>());

        var console = await TaskerProcess.Run("task", "comment", "list", taskId, "-w", folder, "-p", projectId);
        Assert.Contains("[agent]", console.Out);
        Assert.Contains("Уточнение", console.Out);

        Assert.Equal("deleted", await daemon.CallTool(key, "delete_task_comment", new { projectId, taskId, commentId = edited["id"]!.GetValue<string>(), version = edited["version"]!.GetValue<string>() }));
    }
}

public class CommentServerTests : IAsyncLifetime
{
    private sealed class EmptyModule : Module;

    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var builder = TaskerWebApp.CreateBuilder<EmptyModule>([], TaskerMode.Server);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapTasker();
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Send(HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token != null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body != null)
            request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();

    [Fact]
    public async Task On_the_server_comments_live_in_the_database_and_only_the_author_changes_them()
    {
        var owner = (await Send(HttpMethod.Post, "/api/auth/register", null, new { username = "owner", email = "owner@example.com", password = "Passw0rd!123" })).Body!["accessToken"]!.GetValue<string>();
        await Send(HttpMethod.Post, "/api/users", owner, new { username = "guest", email = "guest@example.com", password = "Passw0rd!123" });
        var guest = (await Send(HttpMethod.Post, "/api/auth/login", null, new { login = "guest", password = "Passw0rd!123" })).Body!["accessToken"]!.GetValue<string>();
        var guestId = (await GraphReads.Query(_http, new Uri(_http.BaseAddress!, "/api").ToString(), "{ me { id } }", null, guest)).Body!["data"]!["me"]!["id"]!.GetValue<string>();

        var project = (await Send(HttpMethod.Post, "/api/projects", owner, new { name = "P" })).Body!;
        var p = $"/api/projects/{Id(project)}";
        await Send(HttpMethod.Post, p + "/members", owner, new { userId = guestId });
        var status = (await Send(HttpMethod.Post, p + "/statuses", owner, new { name = "Todo", color = "#112233" })).Body!;
        var set = (await Send(HttpMethod.Post, p + "/status-sets", owner, new { name = "Set", statusIds = new[] { Id(status) } })).Body!;
        var type = (await Send(HttpMethod.Post, p + "/task-types", owner, new { name = "Task", statusSetId = Id(set) })).Body!;
        var task = (await Send(HttpMethod.Post, p + "/tasks", owner, new { title = "A", typeId = Id(type) })).Body!;
        var comments = $"{p}/tasks/{Id(task)}/comments";

        var first = (await Send(HttpMethod.Post, comments, owner, new { body = "От владельца" })).Body!;
        var reply = (await Send(HttpMethod.Post, comments, guest, new { body = "От гостя", parentId = Id(first) })).Body!;
        Assert.Equal("guest", reply["author"]!["name"]!.GetValue<string>());

        var (forbidden, _) = await Send(HttpMethod.Patch, $"{comments}/{Id(first)}", guest, new { body = "x", version = first["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden);

        var (deleted, _) = await Send(HttpMethod.Delete, $"{comments}/{Id(first)}?version={first["version"]!.GetValue<string>()}", owner);
        Assert.Equal(HttpStatusCode.NoContent, deleted);
        var read = (await GraphReads.Query(_http, new Uri(_http.BaseAddress!, "/api").ToString(),
            "query($p: UUID!, $t: UUID!) { project(id: $p) { taskComments(id: $t) { deleted body depth mine } } }",
            new { p = Id(project), t = Id(task) }, guest)).Body!["data"]!["project"]!["taskComments"]!.AsArray();
        Assert.True(read[0]!["deleted"]!.GetValue<bool>());
        Assert.Equal("От гостя", read[1]!["body"]!.GetValue<string>());
        Assert.True(read[1]!["mine"]!.GetValue<bool>());

        var taskNow = (await Send(HttpMethod.Post, p + "/tasks", owner, new { title = "B", typeId = Id(type) })).Body!;
        await Send(HttpMethod.Post, $"{p}/tasks/{Id(taskNow)}/comments", owner, new { body = "Уйдёт" });
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"{p}/tasks/{Id(taskNow)}?version={taskNow["version"]!.GetValue<string>()}", owner)).Status);
        var after = await GraphReads.Query(_http, new Uri(_http.BaseAddress!, "/api").ToString(),
            "query($p: UUID!, $t: UUID!) { project(id: $p) { taskComments(id: $t) { id } } }", new { p = Id(project), t = Id(taskNow) }, owner);
        Assert.Null(after.Body!["data"]!["project"]!["taskComments"]);
    }
}
