using Autofac;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core;
using Tasker.Core.Agents;
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
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Storage.Files;
using Xunit;

namespace Tasker.Tests;

public abstract class AgentRightsContract : IAsyncLifetime
{
    private sealed class AsUser(Guid? id) : ICurrentUser
    {
        public Guid? Id => id;
    }

    protected sealed record Setup(Guid Project, Guid Type, Guid Status, Guid Set, Guid Task, Guid Other, Guid Human);

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    public abstract Task InitializeAsync();
    public abstract Task DisposeAsync();

    protected abstract bool Server { get; }

    protected abstract Task<T> Run<T>(Func<ILifetimeScope, Task<T>> action);

    protected abstract Task<Guid> NewAgent(Setup s, AgentRights? rights);

    protected CoreServices As(ILifetimeScope scope, Guid? user) => new(
        new StorageSet(
            scope.Resolve<IProjectStorage>(), scope.Resolve<ITaskStorage>(), scope.Resolve<IStatusStorage>(), scope.Resolve<IStatusSetStorage>(),
            scope.Resolve<ITaskTypeStorage>(), scope.Resolve<ISeriesStorage>(), scope.Resolve<IWriteScope>(), scope.Resolve<ILinkTypeStorage>(),
            scope.Resolve<IFieldStorage>(), scope.Resolve<IFieldEnumStorage>(), scope.Resolve<IBoardStorage>(), scope.Resolve<IUserStorage>(),
            scope.Resolve<IEditLockStorage>(), scope.Resolve<ITaskCommentStorage>(), scope.ResolveOptional<IAgentTokenStorage>()),
        new AsUser(user), new OpenProjectAccess(), new UserOptions(Server),
        members: scope.ResolveOptional<IProjectMemberStorage>());

    protected Task<T> With<T>(Guid? user, Func<CoreServices, Task<T>> action) => Run(scope => action(As(scope, user)));

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
            await scope.Resolve<ITaskStorage>().Add(new TaskItem
            {
                Id = id, ProjectId = project, Title = title, TypeId = type, StatusId = status, CreatedAt = Start, UpdatedAt = Start, Version = ""
            });
        }

        var human = Guid.NewGuid();
        await scope.Resolve<IUserStorage>().Add(new User
        {
            Id = human, Username = "human" + human.ToString("N")[..6], IsAdmin = false, CreatedAt = Start, Version = "",
            Email = Server ? $"h{human:N}@example.com" : null
        }, null);
        if (scope.ResolveOptional<IProjectMemberStorage>() is { } members)
            await members.Add(project, human);
        return new Setup(project, type, status, set, tasks[0], tasks[1], human);
    });

    protected static async Task Denied(string right, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<TaskerForbiddenException>(action);
        Assert.Equal("agent.rightMissing", error.Code);
        Assert.Equal(right, error.Error.Params["right"]);
        Assert.Contains($"'{right}'", error.Message);
    }

    private static Task<TaskItem> CreateTask(CoreServices x, Setup s, string title = "new") =>
        x.Tasks.Create(s.Project, new CreateTask(title, null, s.Type, null));

    private async Task<TaskItem> Load(Setup s, Guid id) =>
        (await Run(scope => scope.Resolve<ITaskStorage>().GetById(s.Project, id)))!;

    private static Guid LinkType(Setup s) => LinkTypeService.Defaults(s.Project).First(x => !x.Hierarchical).Id;

    [Fact]
    public async Task A_read_only_agent_reads_but_every_change_is_refused()
    {
        var s = await NewSetup();
        var agent = await NewAgent(s, AgentRights.ReadOnly);

        Assert.NotNull(await With(agent, x => x.Tasks.Describe(s.Project, s.Task)));
        Assert.NotNull(await With(agent, x => x.Projects.GetById(s.Project)));

        var task = await Load(s, s.Task);
        await Denied("editTasks", () => With(agent, x => CreateTask(x, s)));
        await Denied("editTasks", () => With(agent, x => x.Tasks.Update(s.Project, s.Task, new UpdateTask("changed", null, null, null, task.Version))));
        await Denied("editTasks", () => With(agent, x => x.TaskComments.Add(s.Project, s.Task, new AddTaskComment("hi"))));
        await Denied("editTasks", () => With(agent, x => x.TaskLinks.Add(s.Project, s.Task, LinkType(s), s.Other, task.Version)));
        await Denied("editTasks", () => With(agent, x => x.EntityLocks.Acquire(s.Project, LockedEntity.Task, s.Task)));
        await Denied("deleteTasks", () => With(agent, x => x.Tasks.Delete(s.Project, s.Task, task.Version)));
        await Denied("configureProject", () => With(agent, x => x.Statuses.Create(s.Project, new CreateStatus("Done", "#112233"))));
        await Denied("configureProject", () => With(agent, x => x.LinkTypes.Create(s.Project, new CreateLinkType("Dup", "duplicates", "is duplicated by"))));
        await Denied("configureProject", () => With(agent, x => x.Fields.Create(s.Project, new CreateField("Estimate", FieldType.Int, null, null))));
        await Denied("configureProject", () => With(agent, x => x.Boards.Create(s.Project, new CreateBoard("B", [s.Set], []))));
        await Denied("manageSeries", () => With(agent, x => x.Series.Create(s.Project, new CreateSeries("Main", "MN"))));

        Assert.Equal("A", (await Load(s, s.Task)).Title);
        Assert.Equal(2, (await Run(scope => scope.Resolve<ITaskStorage>().GetAll(s.Project, new TaskFilter()))).Length);
    }

    [Fact]
    public async Task A_tasks_only_agent_changes_tasks_but_not_statuses_boards_or_series()
    {
        var s = await NewSetup();
        var agent = await NewAgent(s, AgentRights.ReadOnly with { EditTasks = true });

        var created = await With(agent, x => CreateTask(x, s));
        var updated = await With(agent, x => x.Tasks.Update(s.Project, created.Id, new UpdateTask("renamed", null, null, null, created.Version)));
        Assert.Equal("renamed", updated!.Title);
        var linked = await With(agent, x => x.TaskLinks.Add(s.Project, created.Id, LinkType(s), s.Task, updated.Version));
        Assert.NotNull(linked);
        var comment = await With(agent, x => x.TaskComments.Add(s.Project, created.Id, new AddTaskComment("note")));
        Assert.NotNull(comment);

        await Denied("deleteTasks", () => With(agent, x => x.Tasks.Delete(s.Project, created.Id, linked!.Version)));
        await Denied("deleteTasks", () => With(agent, x => x.TaskComments.Delete(s.Project, created.Id, comment!.Id, comment.Version)));
        await Denied("configureProject", () => With(agent, x => x.Statuses.Create(s.Project, new CreateStatus("Done", "#112233"))));
        var status = (await Run(scope => scope.Resolve<IStatusStorage>().GetById(s.Project, s.Status)))!;
        await Denied("configureProject", () => With(agent, x => x.Statuses.Delete(s.Project, s.Status, status.Version)));
        await Denied("configureProject", () => With(agent, x => x.Boards.Create(s.Project, new CreateBoard("B", [s.Set], []))));
        var type = (await Run(scope => scope.Resolve<ITaskTypeStorage>().GetById(s.Project, s.Type)))!;
        await Denied("configureProject", () => With(agent, x => x.TaskTypes.Delete(s.Project, s.Type, type.Version)));
        await Denied("manageSeries", () => With(agent, x => x.Series.Create(s.Project, new CreateSeries("Main", "MN"))));
    }

    [Fact]
    public async Task The_delete_right_lets_an_agent_delete_tasks_and_comments()
    {
        var s = await NewSetup();
        var agent = await NewAgent(s, AgentRights.ReadOnly with { EditTasks = true, DeleteTasks = true });

        var comment = await With(agent, x => x.TaskComments.Add(s.Project, s.Task, new AddTaskComment("note")));
        Assert.True(await With(agent, x => x.TaskComments.Delete(s.Project, s.Task, comment!.Id, comment.Version)));
        var task = await Load(s, s.Other);
        Assert.True(await With(agent, x => x.Tasks.Delete(s.Project, s.Other, task.Version)));
        await Denied("configureProject", () => With(agent, x => x.Statuses.Create(s.Project, new CreateStatus("Done", "#112233"))));
    }

    [Fact]
    public async Task The_project_right_configures_statuses_types_and_boards_without_editing_tasks()
    {
        var s = await NewSetup();
        var agent = await NewAgent(s, AgentRights.ReadOnly with { ConfigureProject = true });

        var status = await With(agent, x => x.Statuses.Create(s.Project, new CreateStatus("Done", "#112233")));
        var set = await With(agent, x => x.StatusSets.Create(s.Project, new CreateStatusSet("Flow", [status.Id])));
        var board = await With(agent, x => x.Boards.Create(s.Project, new CreateBoard("Board", [set.Id], [new BoardColumnInput(null, "Done", [status.Id], null)])));
        Assert.True(await With(agent, x => x.Boards.Delete(s.Project, board.Id, board.Version)));
        var field = await With(agent, x => x.Fields.Create(s.Project, new CreateField("Estimate", FieldType.Int, null, null)));
        Assert.True(await With(agent, x => x.Fields.Delete(s.Project, field.Id, field.Version)));

        await Denied("editTasks", () => With(agent, x => CreateTask(x, s)));
        await Denied("manageSeries", () => With(agent, x => x.Series.Create(s.Project, new CreateSeries("Main", "MN"))));
    }

    [Fact]
    public async Task The_series_right_manages_series_and_numbers()
    {
        var s = await NewSetup();
        var agent = await NewAgent(s, AgentRights.ReadOnly with { ManageSeries = true });

        var series = await With(agent, x => x.Series.Create(s.Project, new CreateSeries("Main", "MN")));
        var task = await Load(s, s.Task);
        await Denied("editTasks", () => With(agent, x => x.Tasks.AddToSeries(s.Project, s.Task, series.Id, task.Version)));
        var added = await With(s.Human, x => x.Tasks.AddToSeries(s.Project, s.Task, series.Id, task.Version));
        var renumbered = await With(agent, x => x.Tasks.Renumber(s.Project, s.Task, series.Id, 7, added!.Version));
        Assert.Equal(7, renumbered!.SeriesNumbers.Single().Number);
        Assert.True(await With(agent, x => x.Series.Delete(s.Project, series.Id, series.Version)));
    }

    [Fact]
    public async Task An_agent_without_saved_rights_works_as_before()
    {
        var s = await NewSetup();
        var agent = await NewAgent(s, null);

        var created = await With(agent, x => CreateTask(x, s));
        var status = await With(agent, x => x.Statuses.Create(s.Project, new CreateStatus("Done", "#112233")));
        Assert.True(await With(agent, x => x.Statuses.Delete(s.Project, status.Id, status.Version)));
        await With(agent, x => x.Series.Create(s.Project, new CreateSeries("Main", "MN")));
        Assert.True(await With(agent, x => x.Tasks.Delete(s.Project, created.Id, created.Version)));
        Assert.Equal(AgentRights.Full, await With(agent, x => x.Guard.RightsIn(s.Project)));
    }

    [Fact]
    public async Task People_are_not_limited_and_only_people_create_projects()
    {
        var s = await NewSetup();
        var agent = await NewAgent(s, AgentRights.ReadOnly);

        await With(s.Human, x => CreateTask(x, s));
        var status = await With(s.Human, x => x.Statuses.Create(s.Project, new CreateStatus("Done", "#112233")));
        Assert.True(await With(s.Human, x => x.Statuses.Delete(s.Project, status.Id, status.Version)));
        Assert.Null(await With(s.Human, x => x.Guard.RightsIn(s.Project)));

        var refused = await Assert.ThrowsAsync<TaskerForbiddenException>(() => With(agent, x => x.Projects.Create(new CreateProject("Agents"))));
        Assert.Equal("user.personOnly", refused.Code);
        var full = await NewAgent(s, null);
        Assert.Equal("user.personOnly", (await Assert.ThrowsAsync<TaskerForbiddenException>(() => With(full, x => x.Projects.Create(new CreateProject("Agents2"))))).Code);
        Assert.NotNull(await With(s.Human, x => x.Projects.Create(new CreateProject("People"))));
    }

    [Fact]
    public async Task The_agent_sees_its_rights_in_the_project()
    {
        var s = await NewSetup();
        var rights = AgentRights.ReadOnly with { EditTasks = true, ManageSeries = true };
        var agent = await NewAgent(s, rights);

        Assert.Equal(rights, await With(agent, x => x.Guard.RightsIn(s.Project)));
    }
}

public sealed class FilesAgentRightsTests : AgentRightsContract
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private IContainer _container = null!;

    protected override bool Server => false;

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

    protected override async Task<Guid> NewAgent(Setup s, AgentRights? rights)
    {
        var agent = await With(null, x => x.Agents.Create(new CreateAgent("bot" + Guid.NewGuid().ToString("N")[..6])));
        if (rights != null)
            await With(null, x => x.Agents.Update(agent.Id, new UpdateAgent(null, agent.Version, rights)));
        return agent.Id;
    }

    private string AgentFile(Guid id) => Path.Combine(_root, ".tasker", "users", id + ".yaml");

    [Fact]
    public async Task Rights_are_a_line_in_the_agent_file_and_full_access_writes_nothing()
    {
        var s = await NewSetup();
        var full = await NewAgent(s, null);
        Assert.DoesNotContain("rights", await File.ReadAllTextAsync(AgentFile(full)));

        var limited = await NewAgent(s, AgentRights.ReadOnly with { EditTasks = true, ManageSeries = true });
        var text = (await File.ReadAllTextAsync(AgentFile(limited))).ReplaceLineEndings("\n");
        Assert.Contains("rights:\n- editTasks\n- manageSeries\n", text);

        var readOnly = await NewAgent(s, AgentRights.ReadOnly);
        Assert.Contains("rights: []", await File.ReadAllTextAsync(AgentFile(readOnly)));

        var reread = await Run(scope => scope.Resolve<IUserStorage>().GetById(limited));
        Assert.Equal(AgentRights.ReadOnly with { EditTasks = true, ManageSeries = true }, reread!.Rights);
    }

    [Fact]
    public async Task Rights_edited_in_the_file_apply_and_unknown_names_are_ignored()
    {
        var s = await NewSetup();
        var agent = await NewAgent(s, null);
        var file = AgentFile(agent);
        var text = (await File.ReadAllTextAsync(file)).ReplaceLineEndings("\n");
        await File.WriteAllTextAsync(file, text.Replace("kind: agent\n", "kind: agent\nrights:\n- deleteTasks\n- flyToTheMoon\n"));
        await Run(async scope =>
        {
            await scope.Resolve<Tasker.Core.Workspace.IWorkspaceIndex>().Rescan();
            return 0;
        });

        Assert.Equal(AgentRights.ReadOnly with { DeleteTasks = true }, await With(agent, x => x.Guard.RightsIn(s.Project)));
        await Denied("editTasks", () => With(agent, x => x.Tasks.Create(s.Project, new CreateTask("x", null, s.Type, null))));
    }

    [Fact]
    public async Task An_agent_cannot_change_its_own_rights()
    {
        var s = await NewSetup();
        var agent = await NewAgent(s, AgentRights.ReadOnly);
        var stored = (await Run(scope => scope.Resolve<IUserStorage>().GetById(agent)))!;

        var refused = await Assert.ThrowsAsync<TaskerForbiddenException>(() =>
            With(agent, x => x.Agents.Update(agent, new UpdateAgent(null, stored.Version, AgentRights.Full))));
        Assert.Equal("user.personOnly", refused.Code);
    }
}

public sealed class DbAgentRightsTests : AgentRightsContract
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private IContainer _container = null!;

    private string DbFile => Path.Combine(_root, "tasker.db");

    protected override bool Server => true;

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

    private ProjectMemberService Members(ILifetimeScope scope, Guid user)
    {
        var core = As(scope, user);
        return new ProjectMemberService(scope.Resolve<IProjectMemberStorage>(), scope.Resolve<IUserStorage>(), core.Users);
    }

    protected override async Task<Guid> NewAgent(Setup s, AgentRights? rights)
    {
        var agent = await With(s.Human, x => x.Agents.Create(new CreateAgent("bot" + Guid.NewGuid().ToString("N")[..6])));
        await Run(scope => Members(scope, s.Human).Add(s.Project, new AddProjectMember(agent.Id, rights)));
        return agent.Id;
    }

    [Fact]
    public async Task Rights_are_per_project_and_a_person_changes_them_later()
    {
        var s = await NewSetup();
        var agent = await NewAgent(s, AgentRights.ReadOnly);
        var second = await With(s.Human, x => x.Projects.Create(new CreateProject("Second")));
        await Run(scope => Members(scope, s.Human).Add(second.Id, new AddProjectMember(agent)));

        Assert.Equal(AgentRights.ReadOnly, await With(agent, x => x.Guard.RightsIn(s.Project)));
        Assert.Equal(AgentRights.Full, await With(agent, x => x.Guard.RightsIn(second.Id)));
        var listed = await Run(scope => Members(scope, s.Human).GetRange(s.Project, Page.Of(0, 50)));
        Assert.Equal(AgentRights.ReadOnly, listed.Data.Single(x => x.Id == agent).Rights);
        Assert.Null(listed.Data.Single(x => x.Id == s.Human).Rights);

        var changed = await Run(scope => Members(scope, s.Human).Update(s.Project, agent, new UpdateProjectMember(AgentRights.ReadOnly with { EditTasks = true })));
        Assert.True(changed!.Rights!.EditTasks);
        await With(agent, x => x.Tasks.Create(s.Project, new CreateTask("by agent", null, s.Type, null)));

        var whoami = await With(agent, async x => await x.Guard.ProjectRights((await x.Guard.CurrentAgent())!));
        Assert.Equal(2, whoami!.Length);
        Assert.Equal(AgentRights.ReadOnly with { EditTasks = true }, whoami.Single(x => x.ProjectId == s.Project).Rights);
    }

    [Fact]
    public async Task Only_a_person_changes_member_rights_and_only_of_agents()
    {
        var s = await NewSetup();
        var agent = await NewAgent(s, AgentRights.ReadOnly);

        Assert.Equal("user.personOnly", (await Assert.ThrowsAsync<TaskerForbiddenException>(() =>
            Run(scope => Members(scope, agent).Update(s.Project, agent, new UpdateProjectMember(AgentRights.Full))))).Code);
        Assert.Equal("agent.rightsAgentOnly", (await Assert.ThrowsAsync<TaskerValidationException>(() =>
            Run(scope => Members(scope, s.Human).Update(s.Project, s.Human, new UpdateProjectMember(AgentRights.Full))))).Code);
        Assert.Null(await Run(scope => Members(scope, s.Human).Update(s.Project, Guid.NewGuid(), new UpdateProjectMember(AgentRights.Full))));

        var stored = (await Run(scope => scope.Resolve<IUserStorage>().GetById(agent)))!;
        Assert.Equal("agent.rightsOnMembership", (await Assert.ThrowsAsync<TaskerValidationException>(() =>
            With(s.Human, x => x.Agents.Update(agent, new UpdateAgent(null, stored.Version, AgentRights.Full))))).Code);
    }

    [Fact]
    public async Task An_agent_outside_the_project_has_no_rights_there()
    {
        var s = await NewSetup();
        var agent = await NewAgent(s, null);
        var other = await With(s.Human, x => x.Projects.Create(new CreateProject("Other")));

        Assert.Equal(AgentRights.ReadOnly, await With(agent, x => x.Guard.RightsIn(other.Id)));
    }

    [Fact]
    public async Task A_database_created_before_rights_gets_the_column_on_start_and_members_keep_full_access()
    {
        var s = await NewSetup();
        var agent = await NewAgent(s, null);
        await _container.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={DbFile}"))
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = "ALTER TABLE project_members DROP COLUMN \"AgentRights\"";
            await drop.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        _container = Open();
        await _container.Resolve<IStorageLifecycle>().Start(default);

        Assert.Equal(AgentRights.Full, await With(agent, x => x.Guard.RightsIn(s.Project)));
        await With(agent, x => x.Tasks.Create(s.Project, new CreateTask("after upgrade", null, s.Type, null)));
    }
}
