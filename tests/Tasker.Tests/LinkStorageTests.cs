using Autofac;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core;
using Tasker.Core.Dto;
using Tasker.Core.Links;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Tests.SeriesCore;
using Tasker.Core.Tasks;
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Storage.Files;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Связи и типы связей в хранилищах — одно поведение для файлов и БД. Каждый вызов <see cref="Run"/> получает свой скоуп
/// (в БД — свой запрос и контекст), как отдельный запрос сервера.
/// </summary>
public abstract class LinkStorageContract : IAsyncLifetime
{
    public abstract Task InitializeAsync();
    public abstract Task DisposeAsync();

    protected abstract Task<T> Run<T>(Func<ILifetimeScope, Task<T>> action);

    private sealed record Setup(Guid Project, Guid TypeId, Guid StatusId);

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private Task<Setup> NewProject() => Run(async scope =>
    {
        var project = Guid.NewGuid();
        var status = Guid.NewGuid();
        var set = Guid.NewGuid();
        var type = Guid.NewGuid();
        await scope.Resolve<IProjectStorage>().Add(new Project { Id = project, Name = "P" + project.ToString("N")[..6], CreatedAt = Start, Version = "" });
        await scope.Resolve<IStatusStorage>().Add(new Status { Id = status, ProjectId = project, Name = "Open", Color = "#000000", Version = "" });
        await scope.Resolve<IStatusSetStorage>().Add(new StatusSet { Id = set, ProjectId = project, Name = "S", StatusIds = [status], Version = "" });
        await scope.Resolve<ITaskTypeStorage>().Add(new TaskType { Id = type, ProjectId = project, Name = "T", StatusSetId = set, Version = "" });
        return new Setup(project, type, status);
    });

    protected static LinkType NewLinkType(Guid project, string name, string outward = "blocks", string inward = "is blocked by") =>
        new() { Id = Guid.NewGuid(), ProjectId = project, Name = name, OutwardName = outward, InwardName = inward, Version = "" };

    private static TaskItem NewTask(Setup s, string title, int minute, params TaskLink[] links) => new()
    {
        Id = Guid.NewGuid(), ProjectId = s.Project, Title = title, TypeId = s.TypeId, StatusId = s.StatusId, Links = links,
        CreatedAt = Start.AddMinutes(minute), UpdatedAt = Start.AddMinutes(minute), Version = ""
    };

    private Task<LinkType> AddType(Guid project, string name, string outward = "blocks", string inward = "is blocked by") => Run(async scope =>
    {
        var type = NewLinkType(project, name, outward, inward);
        return type with { Version = await scope.Resolve<ILinkTypeStorage>().Add(type) };
    });

    private Task<TaskItem> AddTask(TaskItem task) => Run(async scope =>
        task with { Version = await scope.Resolve<ITaskStorage>().Add(task) });

    private Task<TaskItem?> Get(Setup s, Guid id) => Run(scope => scope.Resolve<ITaskStorage>().GetById(s.Project, id));

    // ---- циклы (TSK-97) ----

    [Fact]
    public async Task The_allow_cycles_flag_of_a_link_type_is_stored_and_updated()
    {
        var s = await NewProject();
        var strict = await Run(async scope =>
        {
            var type = NewLinkType(s.Project, "Strict") with { AllowCycles = false };
            return type with { Version = await scope.Resolve<ILinkTypeStorage>().Add(type) };
        });
        var free = await Run(async scope =>
        {
            var type = NewLinkType(s.Project, "Free") with { AllowCycles = true };
            return type with { Version = await scope.Resolve<ILinkTypeStorage>().Add(type) };
        });

        var read = (await Run(x => x.Resolve<ILinkTypeStorage>().GetAll(s.Project))).ToDictionary(x => x.Name);
        Assert.False(read["Strict"].AllowCycles);
        Assert.True(read["Free"].AllowCycles);

        var version = await Run(x => x.Resolve<ILinkTypeStorage>().Update(strict with { AllowCycles = true }, strict.Version));
        Assert.NotNull(version);
        Assert.True((await Run(x => x.Resolve<ILinkTypeStorage>().GetById(s.Project, strict.Id)))!.AllowCycles);
        Assert.True((await Run(x => x.Resolve<ILinkTypeStorage>().GetRange(s.Project, new Page(0, 10)))).Data.Single(x => x.Name == "Strict").AllowCycles);
    }

    [Fact]
    public async Task Link_targets_and_edges_of_a_type_are_read_by_type_and_project()
    {
        var s = await NewProject();
        var other = await NewProject();
        var blocks = await AddType(s.Project, "Blocks");
        var relates = await AddType(s.Project, "Relates", "relates to", "relates to");
        var otherBlocks = await AddType(other.Project, "Blocks");
        var c = await AddTask(NewTask(s, "C", 3));
        var d = await AddTask(NewTask(s, "D", 4));
        var b = await AddTask(NewTask(s, "B", 2, new TaskLink(blocks.Id, c.Id), new TaskLink(blocks.Id, d.Id), new TaskLink(relates.Id, c.Id)));
        var a = await AddTask(NewTask(s, "A", 1, new TaskLink(blocks.Id, b.Id)));
        var foreign = await AddTask(NewTask(other, "Foreign", 5, new TaskLink(otherBlocks.Id, b.Id)));

        var targets = await Run(x => x.Resolve<ITaskStorage>().GetLinkTargets(s.Project, blocks.Id, [a.Id, b.Id, c.Id, foreign.Id]));
        Assert.Equal([a.Id, b.Id], targets.Keys.OrderBy(x => x == a.Id ? 0 : 1).ToArray());
        Assert.Equal([b.Id], targets[a.Id]);
        Assert.Equal(new[] { c.Id, d.Id }.OrderBy(x => x).ToArray(), targets[b.Id].OrderBy(x => x).ToArray());
        Assert.Empty(await Run(x => x.Resolve<ITaskStorage>().GetLinkTargets(s.Project, blocks.Id, [])));

        var edges = await Run(x => x.Resolve<ITaskStorage>().GetLinkEdges(s.Project, blocks.Id));
        Assert.Equal(3, edges.Length);
        Assert.Contains(new LinkEdge(a.Id, b.Id), edges);
        Assert.Contains(new LinkEdge(b.Id, d.Id), edges);
        Assert.Single(await Run(x => x.Resolve<ITaskStorage>().GetLinkEdges(s.Project, relates.Id)));
        Assert.Single(await Run(x => x.Resolve<ITaskStorage>().GetLinkEdges(other.Project, otherBlocks.Id)));
    }

    [Fact]
    public async Task The_hierarchical_flag_is_stored_and_ids_come_in_list_order_restricted_by_the_ids_filter()
    {
        var s = await NewProject();
        var epic = await Run(async scope =>
        {
            var type = NewLinkType(s.Project, "Parent/Child", "includes", "is part of") with { AllowCycles = false, Hierarchical = true };
            return type with { Version = await scope.Resolve<ILinkTypeStorage>().Add(type) };
        });
        var plain = await AddType(s.Project, "Plain");
        Assert.True((await Run(x => x.Resolve<ILinkTypeStorage>().GetById(s.Project, epic.Id)))!.Hierarchical);
        Assert.False((await Run(x => x.Resolve<ILinkTypeStorage>().GetById(s.Project, plain.Id)))!.Hierarchical);
        var updated = await Run(x => x.Resolve<ILinkTypeStorage>().Update(plain with { Hierarchical = true }, plain.Version));
        Assert.NotNull(updated);
        Assert.True((await Run(x => x.Resolve<ILinkTypeStorage>().GetAll(s.Project))).Single(x => x.Name == "Plain").Hierarchical);

        var other = await NewProject();
        var a = await AddTask(NewTask(s, "Charlie", 1));
        var b = await AddTask(NewTask(s, "Alpha", 2));
        var c = await AddTask(NewTask(s, "Bravo", 3));
        await AddTask(NewTask(other, "Foreign", 4));

        Assert.Equal([a.Id, b.Id, c.Id], await Run(x => x.Resolve<ITaskStorage>().GetIds(s.Project, null)));
        Assert.Equal([b.Id, c.Id, a.Id], await Run(x => x.Resolve<ITaskStorage>().GetIds(s.Project, new TaskFilter
        {
            Sort = [new TaskSortKey(SortTarget.Title, false, "title")]
        })));
        Assert.Equal([a.Id, c.Id], await Run(x => x.Resolve<ITaskStorage>().GetIds(s.Project, new TaskFilter { Ids = [a.Id, c.Id, Guid.NewGuid()] })));
        Assert.Equal([b.Id], (await Run(x => x.Resolve<ITaskStorage>().GetAll(s.Project, new TaskFilter { Ids = [b.Id] }))).Select(x => x.Id).ToArray());
        Assert.Empty(await Run(x => x.Resolve<ITaskStorage>().GetIds(s.Project, new TaskFilter { Ids = [] })));
    }

    private Task<T> Linking<T>(Func<TaskLinkService, LinkTypeService, Task<T>> action) => Run(async scope =>
    {
        var tasks = scope.Resolve<ITaskStorage>();
        var locks = new Tasker.Core.Locks.EditLockService(scope.Resolve<Tasker.Core.Locks.IEditLockStorage>(), new FakeEditor(), TimeProvider.System);
        var types = new LinkTypeService(scope.Resolve<ILinkTypeStorage>(), tasks, locks);
        var links = new TaskLinkService(tasks, types, TimeProvider.System, locks, scope.Resolve<Tasker.Core.TaskSeries.ISeriesStorage>(),
            scope.Resolve<Tasker.Core.TaskSeries.IWriteScope>());
        return await action(links, types);
    });

    [Fact]
    public async Task A_link_closing_a_cycle_is_rejected_by_the_service_over_the_real_storage()
    {
        var s = await NewProject();
        var blocks = await Linking(async (_, types) => (await types.Find(s.Project, "Blocks"))!);
        var a = await AddTask(NewTask(s, "A", 1));
        var b = await AddTask(NewTask(s, "B", 2));
        var c = await AddTask(NewTask(s, "C", 3));

        await Linking((links, _) => links.Add(s.Project, a.Id, blocks.Id, b.Id, null));
        await Linking((links, _) => links.Add(s.Project, b.Id, blocks.Id, c.Id, null));
        var direct = await Assert.ThrowsAsync<TaskerValidationException>(() => Linking((links, _) => links.Add(s.Project, b.Id, blocks.Id, a.Id, null)));
        var long3 = await Assert.ThrowsAsync<TaskerValidationException>(() => Linking((links, _) => links.Add(s.Project, c.Id, blocks.Id, a.Id, null)));

        Assert.StartsWith("Cycle: ", direct.Message);
        Assert.Contains("→", long3.Message);
        Assert.Empty((await Get(s, c.Id))!.Links);
        Assert.Empty((await Get(s, b.Id))!.Links.Where(x => x.TargetId == a.Id));
    }

    [Fact]
    public async Task Two_opposite_links_created_at_the_same_time_let_exactly_one_through()
    {
        var s = await NewProject();
        var blocks = await Linking(async (_, types) => (await types.Find(s.Project, "Blocks"))!);

        for (var round = 0; round < 6; round++)
        {
            var a = await AddTask(NewTask(s, "A" + round, 1));
            var b = await AddTask(NewTask(s, "B" + round, 2));
            var start = new TaskCompletionSource();

            async Task<Exception?> Attempt(Guid from, Guid to)
            {
                await start.Task;
                try
                {
                    await Linking((links, _) => links.Add(s.Project, from, blocks.Id, to, null));
                    return null;
                }
                catch (Exception e)
                {
                    return e;
                }
            }

            var both = new[] { Task.Run(() => Attempt(a.Id, b.Id)), Task.Run(() => Attempt(b.Id, a.Id)) };
            start.SetResult();
            var results = await Task.WhenAll(both);

            Assert.Equal(1, results.Count(x => x == null));
            Assert.Single(results.Where(x => x is TaskerValidationException));
            var stored = (await Get(s, a.Id))!.Links.Count + (await Get(s, b.Id))!.Links.Count;
            Assert.Equal(1, stored);
        }
    }

    // ---- типы связей ----

    [Fact]
    public async Task Link_types_can_be_added_listed_by_name_updated_and_deleted_with_versions()
    {
        var s = await NewProject();
        var zeta = await AddType(s.Project, "Zeta", "z-out", "z-in");
        var alpha = await AddType(s.Project, "Alpha", "a-out", "a-in");

        var all = await Run(x => x.Resolve<ILinkTypeStorage>().GetAll(s.Project));
        Assert.Equal(["Alpha", "Zeta"], all.Select(x => x.Name).ToArray());
        Assert.Equal(("a-out", "a-in"), (all[0].OutwardName, all[0].InwardName));
        Assert.False(string.IsNullOrEmpty(all[0].Version));

        var page = await Run(x => x.Resolve<ILinkTypeStorage>().GetRange(s.Project, new Page(1, 1)));
        Assert.Equal((2, 1), (page.TotalCount, page.Data.Length));
        Assert.Equal("Zeta", page.Data[0].Name);

        // Чужой проект типов не видит.
        var other = await NewProject();
        Assert.Empty(await Run(x => x.Resolve<ILinkTypeStorage>().GetAll(other.Project)));
        Assert.Null(await Run(x => x.Resolve<ILinkTypeStorage>().GetById(other.Project, alpha.Id)));

        var version = await Run(x => x.Resolve<ILinkTypeStorage>().Update(alpha with { Name = "Alpha2", InwardName = "a-in2" }, alpha.Version));
        Assert.NotNull(version);
        Assert.NotEqual(alpha.Version, version);
        var reread = await Run(x => x.Resolve<ILinkTypeStorage>().GetById(s.Project, alpha.Id));
        Assert.Equal(("Alpha2", "a-out", "a-in2"), (reread!.Name, reread.OutwardName, reread.InwardName));

        // Устаревшая версия и чужая версия не проходят; запись не меняется.
        Assert.Null(await Run(x => x.Resolve<ILinkTypeStorage>().Update(alpha with { Name = "Lost" }, alpha.Version)));
        Assert.False(await Run(x => x.Resolve<ILinkTypeStorage>().Delete(s.Project, alpha.Id, alpha.Version)));
        Assert.True(await Run(x => x.Resolve<ILinkTypeStorage>().Delete(s.Project, alpha.Id, version!)));
        Assert.Null(await Run(x => x.Resolve<ILinkTypeStorage>().GetById(s.Project, alpha.Id)));
        Assert.Single(await Run(x => x.Resolve<ILinkTypeStorage>().GetAll(s.Project)));
        Assert.NotNull(zeta);
    }

    // ---- связи в задачах ----

    [Fact]
    public async Task Links_of_a_task_are_stored_and_read_back()
    {
        var s = await NewProject();
        var blocks = await AddType(s.Project, "Blocks");
        var relates = await AddType(s.Project, "Relates", "relates to", "relates to");
        var target1 = await AddTask(NewTask(s, "Target 1", 1));
        var target2 = await AddTask(NewTask(s, "Target 2", 2));

        var source = await AddTask(NewTask(s, "Source", 3, new TaskLink(blocks.Id, target1.Id), new TaskLink(relates.Id, target2.Id)));

        var read = (await Get(s, source.Id))!;
        Assert.Equal(2, read.Links.Count);
        Assert.Contains(new TaskLink(blocks.Id, target1.Id), read.Links);
        Assert.Contains(new TaskLink(relates.Id, target2.Id), read.Links);
        Assert.Empty((await Get(s, target1.Id))!.Links);

        // И в списке: связи видны без отдельного чтения.
        var listed = (await Run(x => x.Resolve<ITaskStorage>().GetRange(s.Project, null, new Page(0, 50)))).Data.Single(x => x.Id == source.Id);
        Assert.Equal(2, listed.Links.Count);
    }

    [Fact]
    public async Task Updating_a_task_replaces_its_links_and_an_empty_list_clears_them()
    {
        var s = await NewProject();
        var blocks = await AddType(s.Project, "Blocks");
        var dup = await AddType(s.Project, "Duplicate", "duplicates", "is duplicated by");
        var a = await AddTask(NewTask(s, "A", 1));
        var b = await AddTask(NewTask(s, "B", 2));
        var source = await AddTask(NewTask(s, "Source", 3, new TaskLink(blocks.Id, a.Id)));

        var v2 = await Run(x => x.Resolve<ITaskStorage>().Update(source with { Links = [new TaskLink(dup.Id, b.Id), new TaskLink(blocks.Id, b.Id)] }, source.Version));
        Assert.NotNull(v2);
        var read = (await Get(s, source.Id))!;
        Assert.Equal(2, read.Links.Count);
        Assert.DoesNotContain(new TaskLink(blocks.Id, a.Id), read.Links);

        var v3 = await Run(x => x.Resolve<ITaskStorage>().Update(read with { Links = [] }, v2!));
        Assert.NotNull(v3);
        Assert.Empty((await Get(s, source.Id))!.Links);
        Assert.Empty(await Run(x => x.Resolve<ITaskStorage>().GetLinkedTo(s.Project, b.Id)));
    }

    [Fact]
    public async Task Tasks_linking_to_a_target_are_found_in_creation_order_and_only_in_the_same_project()
    {
        var s = await NewProject();
        var other = await NewProject();
        var blocks = await AddType(s.Project, "Blocks");
        var otherBlocks = await AddType(other.Project, "Blocks");
        var target = await AddTask(NewTask(s, "Target", 1));
        var late = await AddTask(NewTask(s, "Late", 9, new TaskLink(blocks.Id, target.Id)));
        var early = await AddTask(NewTask(s, "Early", 4, new TaskLink(blocks.Id, target.Id)));
        await AddTask(NewTask(s, "Unrelated", 5));
        // Задача другого проекта с тем же id цели (искусственно): в этот проект не попадает.
        await AddTask(NewTask(other, "Foreign", 6, new TaskLink(otherBlocks.Id, target.Id)));

        var found = await Run(x => x.Resolve<ITaskStorage>().GetLinkedTo(s.Project, target.Id));

        Assert.Equal([early.Id, late.Id], found.Select(x => x.Id).ToArray());
        Assert.Empty(await Run(x => x.Resolve<ITaskStorage>().GetLinkedTo(s.Project, Guid.NewGuid())));
        Assert.Single(await Run(x => x.Resolve<ITaskStorage>().GetLinkedTo(other.Project, target.Id)));
    }

    [Fact]
    public async Task The_filter_by_link_type_counts_and_lists_tasks_with_such_links()
    {
        var s = await NewProject();
        var blocks = await AddType(s.Project, "Blocks");
        var relates = await AddType(s.Project, "Relates", "relates to", "relates to");
        var target = await AddTask(NewTask(s, "Target", 1));
        var withBlocks = await AddTask(NewTask(s, "WithBlocks", 2, new TaskLink(blocks.Id, target.Id)));
        await AddTask(NewTask(s, "WithRelates", 3, new TaskLink(relates.Id, target.Id)));
        await AddTask(NewTask(s, "NoLinks", 4));

        Task<int> Count(params Guid[] ids) => Run(x => x.Resolve<ITaskStorage>().Count(s.Project, new TaskFilter { LinkTypeIds = ids }));

        Assert.Equal(1, await Count(blocks.Id));
        Assert.Equal(1, await Count(relates.Id));
        Assert.Equal(2, await Count(blocks.Id, relates.Id));
        Assert.Equal(0, await Count());
        var page = await Run(x => x.Resolve<ITaskStorage>().GetRange(s.Project, new TaskFilter { LinkTypeIds = [blocks.Id] }, new Page(0, 50)));
        Assert.Equal([withBlocks.Id], page.Data.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task Deleting_a_source_task_removes_its_links_from_the_reverse_lookup()
    {
        var s = await NewProject();
        var blocks = await AddType(s.Project, "Blocks");
        var target = await AddTask(NewTask(s, "Target", 1));
        var source = await AddTask(NewTask(s, "Source", 2, new TaskLink(blocks.Id, target.Id)));
        Assert.Single(await Run(x => x.Resolve<ITaskStorage>().GetLinkedTo(s.Project, target.Id)));

        Assert.True(await Run(x => x.Resolve<ITaskStorage>().Delete(s.Project, source.Id, source.Version)));

        Assert.Empty(await Run(x => x.Resolve<ITaskStorage>().GetLinkedTo(s.Project, target.Id)));
        Assert.Equal(0, await Run(x => x.Resolve<ITaskStorage>().Count(s.Project, new TaskFilter { LinkTypeIds = [blocks.Id] })));
    }

    [Fact]
    public async Task Tasks_with_links_to_missing_tasks_or_types_are_found_in_creation_order_and_per_project()
    {
        var s = await NewProject();
        var other = await NewProject();
        var good = await AddType(s.Project, "Good");
        var unknown = await AddType(s.Project, "Unknown"); // тип есть, но среди «известных» его нет — связи этого типа недействительны
        var target = await AddTask(NewTask(s, "Target", 1));
        await AddTask(NewTask(s, "Fine", 2, new TaskLink(good.Id, target.Id)));
        var badType = await AddTask(NewTask(s, "BadType", 3, new TaskLink(unknown.Id, target.Id)));
        var both = await AddTask(NewTask(s, "Both", 4, new TaskLink(good.Id, target.Id), new TaskLink(good.Id, Guid.NewGuid())));
        var noTarget = await AddTask(NewTask(s, "NoTarget", 5, new TaskLink(good.Id, Guid.NewGuid())));
        // Цель в другом проекте — для этого проекта её нет. Задача с мёртвой связью в другом проекте сюда не попадает.
        var foreignTarget = await AddTask(NewTask(other, "Foreign target", 6));
        var cross = await AddTask(NewTask(s, "Cross", 7, new TaskLink(good.Id, foreignTarget.Id)));
        await AddTask(NewTask(other, "Foreign dangling", 8, new TaskLink(good.Id, Guid.NewGuid())));
        await AddTask(NewTask(s, "NoLinks", 9));

        var found = await Run(x => x.Resolve<ITaskStorage>().GetWithInvalidLinks(s.Project, [good.Id]));

        Assert.Equal([badType.Id, both.Id, noTarget.Id, cross.Id], found.Select(x => x.Id).ToArray());
        Assert.Equal(2, found[1].Links.Count); // задача возвращается целиком, с действительной связью тоже

        // Ни один тип не известен — недействительна каждая связь; нет связей — нет и задачи в выборке.
        var none = await Run(x => x.Resolve<ITaskStorage>().GetWithInvalidLinks(s.Project, []));
        Assert.Equal(5, none.Length);
        Assert.Empty(await Run(x => x.Resolve<ITaskStorage>().GetWithInvalidLinks(Guid.NewGuid(), [good.Id])));
    }

    [Fact]
    public async Task A_link_becomes_invalid_when_its_target_is_deleted_and_valid_again_when_it_is_restored()
    {
        var s = await NewProject();
        var type = await AddType(s.Project, "Blocks");
        var target = await AddTask(NewTask(s, "Target", 1));
        var source = await AddTask(NewTask(s, "Source", 2, new TaskLink(type.Id, target.Id)));
        Assert.Empty(await Run(x => x.Resolve<ITaskStorage>().GetWithInvalidLinks(s.Project, [type.Id])));

        Assert.True(await Run(x => x.Resolve<ITaskStorage>().Delete(s.Project, target.Id, target.Version)));
        Assert.Equal([source.Id], (await Run(x => x.Resolve<ITaskStorage>().GetWithInvalidLinks(s.Project, [type.Id]))).Select(x => x.Id).ToArray());

        await AddTask(target with { Version = "" });
        Assert.Empty(await Run(x => x.Resolve<ITaskStorage>().GetWithInvalidLinks(s.Project, [type.Id])));
    }

    [Fact]
    public async Task The_same_link_listed_twice_is_stored_once()
    {
        var s = await NewProject();
        var blocks = await AddType(s.Project, "Blocks");
        var target = await AddTask(NewTask(s, "Target", 1));
        var link = new TaskLink(blocks.Id, target.Id);

        var source = await AddTask(NewTask(s, "Source", 2, link, link));

        Assert.Single((await Get(s, source.Id))!.Links);
        Assert.Single(await Run(x => x.Resolve<ITaskStorage>().GetLinkedTo(s.Project, target.Id)));
    }
}

public sealed class FilesLinkStorageTests : LinkStorageContract
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private IContainer _container = null!;

    public override Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _container = Open();
        return Task.CompletedTask;
    }

    private IContainer Open()
    {
        var builder = new ContainerBuilder();
        builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterModule(new FileStorageModule(_root));
        return builder.Build();
    }

    public override async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    protected override Task<T> Run<T>(Func<ILifetimeScope, Task<T>> action) => action(_container);

    [Fact]
    public async Task The_task_file_lists_links_by_type_and_task_and_a_fresh_process_reads_them_from_the_file()
    {
        var project = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var target = Guid.NewGuid();
        var source = Guid.NewGuid();
        var projectDir = Path.Combine(_root, ".tasker", "projects", project.ToString());
        Directory.CreateDirectory(Path.Combine(projectDir, "tasks"));
        Directory.CreateDirectory(Path.Combine(projectDir, "link-types"));

        // Файлы, написанные руками (или пришедшие с git pull): ни один процесс о них ещё не знает.
        await File.WriteAllTextAsync(Path.Combine(projectDir, "link-types", typeId + ".yaml"),
            $"id: {typeId}\nname: Blocks\noutwardName: blocks\ninwardName: is blocked by\n");
        foreach (var (id, title) in new[] { (target, "Target"), (source, "Source") })
        {
            var links = id == source ? $"links:\n- typeId: {typeId}\n  taskId: {target}\n" : "";
            await File.WriteAllTextAsync(Path.Combine(projectDir, "tasks", id + ".yaml"),
                $"id: {id}\ntitle: {title}\ntypeId: {Guid.NewGuid()}\nstatusId: {Guid.NewGuid()}\ncreatedAt: 2026-01-01T10:00:00.0000000+00:00\nupdatedAt: 2026-01-01T10:00:00.0000000+00:00\n{links}");
        }

        await using var fresh = Open();
        var linked = await fresh.Resolve<ITaskStorage>().GetLinkedTo(project, target);
        var type = await fresh.Resolve<ILinkTypeStorage>().GetById(project, typeId);

        Assert.Equal([source], linked.Select(x => x.Id).ToArray());
        Assert.Equal([new TaskLink(typeId, target)], linked[0].Links);
        Assert.Equal("is blocked by", type!.InwardName);
    }

    [Fact]
    public async Task Unreadable_task_and_link_type_files_are_counted_per_project()
    {
        var project = Guid.NewGuid();
        var otherProject = Guid.NewGuid();
        foreach (var (p, task, type) in new[] { (project, "<<<<<<< HEAD\n", "id: [oops\n"), (otherProject, "<<<<<<< HEAD\n", "<<<<<<< HEAD\n") })
        {
            var dir = Path.Combine(_root, ".tasker", "projects", p.ToString());
            Directory.CreateDirectory(Path.Combine(dir, "tasks"));
            Directory.CreateDirectory(Path.Combine(dir, "link-types"));
            await File.WriteAllTextAsync(Path.Combine(dir, "tasks", Guid.NewGuid() + ".yaml"), task);
            await File.WriteAllTextAsync(Path.Combine(dir, "link-types", Guid.NewGuid() + ".yaml"), type);
        }
        await File.WriteAllTextAsync(Path.Combine(_root, ".tasker", "projects", project.ToString(), "tasks", "named-0123abcd.yaml"), "<<<<<<< HEAD\n");
        await _container.Resolve<Tasker.Storage.Files.Index.WorkspaceIndex>().Rescan();

        Assert.Equal(2, await _container.Resolve<ITaskStorage>().CountUnreadable(project));
        Assert.Equal(1, await _container.Resolve<ILinkTypeStorage>().CountUnreadable(project));
        Assert.Equal(1, await _container.Resolve<ITaskStorage>().CountUnreadable(otherProject));
        Assert.Equal(0, await _container.Resolve<ITaskStorage>().CountUnreadable(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_link_type_file_without_the_flag_gets_the_default_and_a_written_one_has_it()
    {
        var project = Guid.NewGuid();
        var blocksId = DefaultLinkTypes.IdOf(project, "blocks");
        var customId = Guid.NewGuid();
        var projectDir = Path.Combine(_root, ".tasker", "projects", project.ToString());
        Directory.CreateDirectory(Path.Combine(projectDir, "link-types"));
        Directory.CreateDirectory(Path.Combine(projectDir, "tasks"));

        // Файлы формата 6: признака нет — «Blocks» по умолчанию циклы запрещает, остальные, как и раньше, допускают.
        await File.WriteAllTextAsync(Path.Combine(projectDir, "link-types", blocksId + ".yaml"),
            $"formatVersion: 6\nid: {blocksId}\nname: Blocks\noutwardName: blocks\ninwardName: is blocked by\n");
        await File.WriteAllTextAsync(Path.Combine(projectDir, "link-types", customId + ".yaml"),
            $"formatVersion: 6\nid: {customId}\nname: Custom\noutwardName: feeds\ninwardName: is fed by\n");

        await using var fresh = Open();
        var types = (await fresh.Resolve<ILinkTypeStorage>().GetAll(project)).ToDictionary(x => x.Name);
        Assert.False(types["Blocks"].AllowCycles);
        Assert.True(types["Custom"].AllowCycles);

        // Перезапись пишет признак явно (формат 7).
        var custom = types["Custom"];
        await fresh.Resolve<ILinkTypeStorage>().Update(custom with { AllowCycles = false }, custom.Version);
        var text = await File.ReadAllTextAsync(FileFinder.Find(_root, "link-types", customId));
        Assert.Contains("allowCycles: false", text);
        Assert.Contains("formatVersion: 9", text);
        Assert.Contains("hierarchical: false", text);
        Assert.False((await fresh.Resolve<ILinkTypeStorage>().GetById(project, customId))!.AllowCycles);
    }

    [Fact]
    public async Task Files_with_a_cycle_made_by_two_branches_are_read_and_the_health_check_reports_the_cycle()
    {
        var project = Guid.NewGuid();
        var blocksId = DefaultLinkTypes.IdOf(project, "blocks");
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var projectDir = Path.Combine(_root, ".tasker", "projects", project.ToString());
        Directory.CreateDirectory(Path.Combine(projectDir, "link-types"));
        Directory.CreateDirectory(Path.Combine(projectDir, "tasks"));
        await File.WriteAllTextAsync(Path.Combine(projectDir, "link-types", blocksId + ".yaml"),
            $"id: {blocksId}\nname: Blocks\noutwardName: blocks\ninwardName: is blocked by\n");
        // Каждая ветка добавила свою связь: файлы разные, git конфликта не видит.
        foreach (var (id, title, other) in new[] { (a, "A", b), (b, "B", a) })
            await File.WriteAllTextAsync(Path.Combine(projectDir, "tasks", id + ".yaml"),
                $"id: {id}\ntitle: {title}\ntypeId: {Guid.NewGuid()}\nstatusId: {Guid.NewGuid()}\ncreatedAt: 2026-01-01T10:00:00.0000000+00:00\nupdatedAt: 2026-01-01T10:00:00.0000000+00:00\nlinks:\n- typeId: {blocksId}\n  taskId: {other}\n");

        await using var fresh = Open();
        var health = await new LinkHealthService(fresh.Resolve<ITaskStorage>(), fresh.Resolve<ILinkTypeStorage>()).Check(project);

        Assert.True(health.NeedsAttention);
        var cycle = Assert.Single(health.Cycles);
        Assert.Equal(3, cycle.Path.Length);
        Assert.Equal(cycle.Path[0].Id, cycle.Path[^1].Id);
        Assert.Equal(["A", "B"], cycle.Path.Take(2).Select(x => x.Title).OrderBy(x => x).ToArray());
        Assert.Equal(0, health.TasksWithInvalidLinks);
        // Задачи читаются как обычно.
        Assert.Equal(2, (await fresh.Resolve<ITaskStorage>().GetLinkedTo(project, a)).Length + (await fresh.Resolve<ITaskStorage>().GetLinkedTo(project, b)).Length);
    }

    [Fact]
    public async Task A_written_task_has_a_links_section_only_when_it_has_links()
    {
        var project = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var target = Guid.NewGuid();
        var task = new TaskItem
        {
            Id = Guid.NewGuid(), ProjectId = project, Title = "With link", TypeId = Guid.NewGuid(), StatusId = Guid.NewGuid(),
            Links = [new TaskLink(typeId, target)], CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = ""
        };
        var plain = task with { Id = Guid.NewGuid(), Title = "Plain", Links = [] };

        await _container.Resolve<ITaskStorage>().Add(task);
        await _container.Resolve<ITaskStorage>().Add(plain);

        var withLinks = await File.ReadAllTextAsync(FileFinder.Find(_root, "tasks", task.Id));
        Assert.Contains("links:", withLinks);
        Assert.Contains($"typeId: {typeId}", withLinks);
        Assert.Contains($"taskId: {target}", withLinks);
        Assert.DoesNotContain("links:", await File.ReadAllTextAsync(FileFinder.Find(_root, "tasks", plain.Id)));
    }
}

public sealed class DbLinkStorageTests : LinkStorageContract
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private IContainer _container = null!;

    public override async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var builder = new ContainerBuilder();
        builder.RegisterModule(new DbStorageModule(new DbConfigs { SqliteFile = Path.Combine(_root, "tasker.db") }));
        _container = builder.Build();
        await _container.Resolve<IStorageLifecycle>().Start(default);
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
    public async Task The_database_has_no_unreadable_files()
    {
        await using var scope = _container.BeginLifetimeScope();

        Assert.Equal(0, await scope.Resolve<ITaskStorage>().CountUnreadable(Guid.NewGuid()));
        Assert.Equal(0, await scope.Resolve<ILinkTypeStorage>().CountUnreadable(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_link_type_that_has_links_cannot_be_deleted_by_the_database()
    {
        var project = Guid.NewGuid();
        var status = Guid.NewGuid();
        var set = Guid.NewGuid();
        var taskType = Guid.NewGuid();
        var linkType = NewLinkType(project, "Blocks");
        var target = Guid.NewGuid();
        var source = Guid.NewGuid();

        await using var scope = _container.BeginLifetimeScope();
        await scope.Resolve<IProjectStorage>().Add(new Project { Id = project, Name = "P", CreatedAt = DateTimeOffset.UtcNow, Version = "" });
        await scope.Resolve<IStatusStorage>().Add(new Status { Id = status, ProjectId = project, Name = "Open", Color = "#000000", Version = "" });
        await scope.Resolve<IStatusSetStorage>().Add(new StatusSet { Id = set, ProjectId = project, Name = "S", StatusIds = [status], Version = "" });
        await scope.Resolve<ITaskTypeStorage>().Add(new TaskType { Id = taskType, ProjectId = project, Name = "T", StatusSetId = set, Version = "" });
        var typeVersion = await scope.Resolve<ILinkTypeStorage>().Add(linkType);
        var tasks = scope.Resolve<ITaskStorage>();
        TaskItem Make(Guid id, params TaskLink[] links) => new()
        {
            Id = id, ProjectId = project, Title = "T", TypeId = taskType, StatusId = status, Links = links,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Version = ""
        };
        await tasks.Add(Make(target));
        await tasks.Add(Make(source, new TaskLink(linkType.Id, target)));

        // Сервис проверяет «тип используется» сам; а база — последняя линия защиты, если проверку обошли.
        await Assert.ThrowsAsync<SqliteException>(() => scope.Resolve<ILinkTypeStorage>().Delete(project, linkType.Id, typeVersion));
    }
}
