using Tasker.Core;
using Tasker.Core.Links;
using Tasker.Core.Locks;
using Tasker.Core.Tasks;
using Tasker.Tests.SeriesCore;
using Xunit;

namespace Tasker.Tests;

/// <summary>Связи между задачами и их типы в сервисах Core (хранилища в памяти).</summary>
public class LinkCoreTests
{
    private static async Task<LinkType> Type(SeriesEnv env, string name) =>
        (await env.LinkTypeSvc.Find(env.Project, name))!;

    // ---- типы по умолчанию ----

    [Fact]
    public async Task A_project_shows_the_jira_link_types_without_writing_and_saves_them_on_the_first_write()
    {
        var env = new SeriesEnv();

        var types = await env.LinkTypeSvc.GetAll(env.Project);
        var again = await env.LinkTypeSvc.GetAll(env.Project);
        var page = await env.LinkTypeSvc.GetRange(env.Project, new Tasker.Core.Dto.Page(0, 50));
        var one = await env.LinkTypeSvc.GetById(env.Project, types[0].Id);

        Assert.Equal(["Blocks", "Cloners", "Duplicate", "Parent/Child", "Problem/Incident", "Relates"], types.Select(x => x.Name).ToArray());
        Assert.Equal(0, env.LinkTypes.AddCount); // чтение ничего не записало
        Assert.Equal(types.Select(x => x.Id), again.Select(x => x.Id));
        Assert.Equal(6, page.TotalCount);
        Assert.Equal(types[0].Id, one!.Id);
        Assert.All(types, x => Assert.Equal(LinkTypeService.DefaultVersion, x.Version));

        // Записи типов и связей сохраняют типы по умолчанию — один раз.
        await env.LinkTypeSvc.Create(env.Project, new CreateLinkType("Mine", "x", "y"));
        Assert.Equal(7, env.LinkTypes.AddCount);
        await env.LinkTypeSvc.Create(env.Project, new CreateLinkType("Mine2", "x", "y"));
        Assert.Equal(8, env.LinkTypes.AddCount);
        Assert.Equal(types.Select(x => x.Id), (await env.LinkTypeSvc.GetAll(env.Project)).Where(x => x.Name != "Mine" && x.Name != "Mine2").Select(x => x.Id));

        var blocks = types.Single(x => x.Name == "Blocks");
        Assert.Equal(("blocks", "is blocked by"), (blocks.OutwardName, blocks.InwardName));
        Assert.False(blocks.IsSymmetric);
        Assert.True(types.Single(x => x.Name == "Relates").IsSymmetric);
        Assert.Equal(("causes", "is caused by"), (types.Single(x => x.Name == "Problem/Incident").OutwardName, types.Single(x => x.Name == "Problem/Incident").InwardName));
        Assert.Equal(("clones", "is cloned by"), (types.Single(x => x.Name == "Cloners").OutwardName, types.Single(x => x.Name == "Cloners").InwardName));
        Assert.Equal(("duplicates", "is duplicated by"), (types.Single(x => x.Name == "Duplicate").OutwardName, types.Single(x => x.Name == "Duplicate").InwardName));
    }

    [Fact]
    public void Default_type_ids_are_stable_per_project_and_differ_between_projects()
    {
        var project = Guid.NewGuid();

        Assert.Equal(DefaultLinkTypes.IdOf(project, "blocks"), DefaultLinkTypes.IdOf(project, "blocks"));
        Assert.NotEqual(DefaultLinkTypes.IdOf(project, "blocks"), DefaultLinkTypes.IdOf(project, "relates"));
        Assert.NotEqual(DefaultLinkTypes.IdOf(project, "blocks"), DefaultLinkTypes.IdOf(Guid.NewGuid(), "blocks"));
        // Два клона одной папки, в которых типы появились независимо, получают одни и те же id (и файлы без конфликта слияния).
        Assert.Equal(6, DefaultLinkTypes.All.Select(x => DefaultLinkTypes.IdOf(project, x.Key)).Distinct().Count());
    }

    [Fact]
    public async Task A_default_type_read_before_it_was_saved_has_a_stale_version_and_the_client_must_reread()
    {
        var env = new SeriesEnv();
        var shown = await Type(env, "Blocks"); // виртуальный: версия «default»

        var stale = await Assert.ThrowsAsync<TaskerConflictException>(() =>
            env.LinkTypeSvc.Update(env.Project, shown.Id, new UpdateLinkType("Blockers", null, null, shown.Version)));

        Assert.Equal(ConflictCode.Modified, stale.Code);
        var saved = await Type(env, "Blocks"); // запись сохранила типы: теперь настоящая версия, тот же id
        Assert.Equal(shown.Id, saved.Id);
        Assert.NotEqual(LinkTypeService.DefaultVersion, saved.Version);
        var renamed = await env.LinkTypeSvc.Update(env.Project, saved.Id, new UpdateLinkType("Blockers", null, null, saved.Version));
        Assert.Equal("Blockers", renamed!.Name);
    }

    [Fact]
    public async Task Defaults_are_not_recreated_while_the_project_has_its_own_types()
    {
        var env = new SeriesEnv();
        await env.LinkTypeSvc.EnsureDefaults(env.Project);
        var all = await env.LinkTypeSvc.GetAll(env.Project);
        foreach (var type in all.Where(x => x.Name != "Blocks"))
            Assert.True(await env.LinkTypeSvc.Delete(env.Project, type.Id, type.Version));

        var left = await env.LinkTypeSvc.GetAll(env.Project);

        // Parent/Child — единственный иерархический тип: пока в проекте нет другого, он остаётся (см. LinkTypeService).
        Assert.Equal(["Blocks", "Parent/Child"], left.Select(x => x.Name).ToArray());

        await env.LinkTypeSvc.Create(env.Project, new CreateLinkType("Epic link", "has story", "belongs to epic", Hierarchical: true));
        var parent = left.Single(x => x.Name == "Parent/Child");
        var saved = (await env.LinkTypeSvc.Find(env.Project, "Parent/Child"))!;
        Assert.True(await env.LinkTypeSvc.Delete(env.Project, saved.Id, saved.Version));
        Assert.Equal(["Blocks", "Epic link"], (await env.LinkTypeSvc.GetAll(env.Project)).Select(x => x.Name).ToArray());
        Assert.True(parent.Hierarchical);
    }

    // ---- связи: обе стороны ----

    [Fact]
    public async Task A_link_is_stored_on_the_source_and_each_side_sees_its_own_name()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("Fix login");
        var b = await env.Create("Release");

        var updated = await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);

        Assert.Equal([new TaskLink(blocks.Id, b.Id)], updated!.Links);
        Assert.NotEqual(a.Version, updated.Version); // источник изменился
        Assert.Equal(b.Version, env.Get(b.Id).Version); // цель — нет
        Assert.Empty(env.Get(b.Id).Links);

        var fromA = (await env.LinkSvc.GetLinks(env.Project, a.Id))!;
        var only = Assert.Single(fromA);
        Assert.Equal((LinkDirection.Outward, "blocks", "Blocks"), (only.Direction, only.Name, only.TypeName));
        Assert.Equal(b.Id, only.Task.Id);
        Assert.Equal("Release", only.Task.Title);

        var fromB = (await env.LinkSvc.GetLinks(env.Project, b.Id))!;
        var inward = Assert.Single(fromB);
        Assert.Equal((LinkDirection.Inward, "is blocked by"), (inward.Direction, inward.Name));
        Assert.Equal(a.Id, inward.Task.Id);
    }

    [Fact]
    public async Task Adding_the_same_link_twice_changes_nothing_the_second_time()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");

        var first = await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        var writes = env.Tasks.UpdateCount;
        var second = await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);

        Assert.Single(second!.Links);
        Assert.Equal(first!.Version, second.Version);
        Assert.Equal(writes, env.Tasks.UpdateCount);
    }

    [Fact]
    public async Task One_pair_can_have_links_of_different_types_and_both_directions()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Duplicate"); // «Blocks» циклов не допускает (TSK-97), у «Duplicate» они разрешены
        var relates = await Type(env, "Relates");
        var a = await env.Create("A");
        var b = await env.Create("B");

        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        await env.LinkSvc.Add(env.Project, b.Id, blocks.Id, a.Id, null); // и наоборот — другая связь
        await env.LinkSvc.Add(env.Project, a.Id, relates.Id, b.Id, null);

        var views = (await env.LinkSvc.GetLinks(env.Project, a.Id))!;
        Assert.Equal(["duplicates", "is duplicated by", "relates to"], views.Select(x => x.Name).ToArray());
    }

    [Fact]
    public async Task Links_are_shown_by_type_then_outward_before_inward_then_title()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var dup = await Type(env, "Duplicate");
        var a = await env.Create("A");
        var z = await env.Create("Zeta");
        var b = await env.Create("Beta");
        var c = await env.Create("Gamma");
        await env.LinkSvc.Add(env.Project, a.Id, dup.Id, z.Id, null);
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, z.Id, null);
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        await env.LinkSvc.Add(env.Project, c.Id, blocks.Id, a.Id, null);

        var views = (await env.LinkSvc.GetLinks(env.Project, a.Id))!;

        Assert.Equal(
            ["Blocks/blocks/Beta", "Blocks/blocks/Zeta", "Blocks/is blocked by/Gamma", "Duplicate/duplicates/Zeta"],
            views.Select(x => $"{x.TypeName}/{x.Name}/{x.Task.Title}").ToArray());
    }

    // ---- симметричные типы ----

    [Fact]
    public async Task A_symmetric_link_looks_the_same_from_both_sides_and_is_not_added_twice()
    {
        var env = new SeriesEnv();
        var relates = await Type(env, "Relates");
        var a = await env.Create("A");
        var b = await env.Create("B");

        await env.LinkSvc.Add(env.Project, a.Id, relates.Id, b.Id, null);
        var reverse = await env.LinkSvc.Add(env.Project, b.Id, relates.Id, a.Id, null); // уже есть с другой стороны

        Assert.Empty(reverse!.Links);
        Assert.Equal(["relates to"], (await env.LinkSvc.GetLinks(env.Project, a.Id))!.Select(x => x.Name).ToArray());
        Assert.Equal(["relates to"], (await env.LinkSvc.GetLinks(env.Project, b.Id))!.Select(x => x.Name).ToArray());
    }

    [Fact]
    public async Task Removing_a_symmetric_link_from_either_side_removes_it_everywhere()
    {
        var env = new SeriesEnv();
        var relates = await Type(env, "Relates");
        var a = await env.Create("A");
        var b = await env.Create("B");
        await env.LinkSvc.Add(env.Project, a.Id, relates.Id, b.Id, null);

        // Убираем со стороны B, хотя хранится связь в A.
        await env.LinkSvc.Remove(env.Project, b.Id, relates.Id, a.Id, null);

        Assert.Empty((await env.LinkSvc.GetLinks(env.Project, a.Id))!);
        Assert.Empty((await env.LinkSvc.GetLinks(env.Project, b.Id))!);
    }

    // ---- проверки ----

    [Fact]
    public async Task Invalid_links_are_rejected_with_a_reason()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");

        var self = await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkSvc.Add(env.Project, a.Id, blocks.Id, a.Id, null));
        Assert.Contains("itself", self.Message);

        var unknownTarget = await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkSvc.Add(env.Project, a.Id, blocks.Id, Guid.NewGuid(), null));
        Assert.StartsWith("TargetId", unknownTarget.Message);

        var b = await env.Create("B");
        var unknownType = await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkSvc.Add(env.Project, a.Id, Guid.NewGuid(), b.Id, null));
        Assert.StartsWith("TypeId", unknownType.Message);

        Assert.Null(await env.LinkSvc.Add(env.Project, Guid.NewGuid(), blocks.Id, b.Id, null)); // нет задачи-источника
        Assert.Null(await env.LinkSvc.GetLinks(env.Project, Guid.NewGuid()));
        Assert.Empty(env.Get(a.Id).Links);
    }

    [Fact]
    public async Task A_task_of_another_project_cannot_be_the_target()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var foreign = env.Tasks.Seed(new TaskItem
        {
            Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Title = "Foreign", TypeId = env.TypeId, StatusId = env.StatusId,
            CreatedAt = SeriesEnv.Start, UpdatedAt = SeriesEnv.Start, Version = ""
        });

        await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkSvc.Add(env.Project, a.Id, blocks.Id, foreign.Id, null));
    }

    // ---- версии ----

    [Fact]
    public async Task With_a_version_a_stale_one_is_rejected_and_without_one_a_race_is_retried()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");
        var c = await env.Create("C");

        // Версия указана и устарела (задачу уже изменили) — [modified].
        env.Tasks.Touch(a.Id, t => t with { Title = "A renamed" });
        var stale = await Assert.ThrowsAsync<TaskerConflictException>(() => env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, a.Version));
        Assert.Equal(ConflictCode.Modified, stale.Code);
        Assert.Empty(env.Get(a.Id).Links);

        // Версия не указана, а задачу меняют прямо во время записи — операция перечитывает и повторяет.
        var raced = 0;
        env.Tasks.BeforeUpdate = _ =>
        {
            if (raced++ == 0)
                env.Tasks.Touch(a.Id, t => t with { Title = "A again" });
        };
        var updated = await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, c.Id, null);

        Assert.Equal([new TaskLink(blocks.Id, c.Id)], updated!.Links);
        Assert.Equal("A again", env.Get(a.Id).Title); // чужая правка не потеряна
    }

    [Fact]
    public async Task With_the_current_version_the_link_is_added_and_the_task_gets_a_new_version()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");

        var updated = await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, a.Version);

        Assert.NotEqual(a.Version, updated!.Version);
        Assert.Equal(updated.Version, env.Get(a.Id).Version);
        Assert.Equal(env.Clock.Now, updated.UpdatedAt);
    }

    // ---- удаление ----

    [Fact]
    public async Task Removing_a_link_is_idempotent()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);

        var removed = await env.LinkSvc.Remove(env.Project, a.Id, blocks.Id, b.Id, null);
        var again = await env.LinkSvc.Remove(env.Project, a.Id, blocks.Id, b.Id, null);

        Assert.Empty(removed!.Links);
        Assert.Empty(again!.Links);
        Assert.Null(await env.LinkSvc.Remove(env.Project, Guid.NewGuid(), blocks.Id, b.Id, null));
    }

    [Fact]
    public async Task Deleting_a_task_removes_the_links_that_pointed_to_it()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var relates = await Type(env, "Relates");
        var a = await env.Create("A");
        var b = await env.Create("B");
        var c = await env.Create("C");
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        await env.LinkSvc.Add(env.Project, c.Id, relates.Id, b.Id, null);
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, c.Id, null);

        Assert.True(await env.TaskSvc.Delete(env.Project, b.Id, env.Get(b.Id).Version));

        Assert.Equal([new TaskLink(blocks.Id, c.Id)], env.Get(a.Id).Links); // остальная связь A цела
        Assert.Empty(env.Get(c.Id).Links);
        Assert.Single((await env.LinkSvc.GetLinks(env.Project, c.Id))!); // осталась только «A blocks C»
    }

    [Fact]
    public async Task Deleting_a_source_task_takes_its_links_with_it()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);

        await env.TaskSvc.Delete(env.Project, a.Id, env.Get(a.Id).Version);

        Assert.Empty((await env.LinkSvc.GetLinks(env.Project, b.Id))!);
    }

    [Fact]
    public async Task Links_to_a_missing_task_or_type_are_skipped_when_shown()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        // Как после слияния веток git: ссылка на задачу и на тип, которых уже нет.
        env.Tasks.Touch(a.Id, t => t with { Links = [.. t.Links, new TaskLink(blocks.Id, Guid.NewGuid()), new TaskLink(Guid.NewGuid(), b.Id)] });

        var views = (await env.LinkSvc.GetLinks(env.Project, a.Id))!;

        Assert.Single(views);
        Assert.Equal(b.Id, views[0].Task.Id);
    }

    // ---- блокировка на время правки ----

    [Fact]
    public async Task A_task_someone_else_edits_cannot_get_a_link_but_can_be_a_target()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");
        await env.Locks.Acquire(LockedEntity.Task, a.Id, "Task");

        env.Editor.Holder = FakeEditor.Anna;
        await Assert.ThrowsAsync<TaskerLockedException>(() => env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null));
        await Assert.ThrowsAsync<TaskerLockedException>(() => env.LinkSvc.Remove(env.Project, a.Id, blocks.Id, b.Id, null));

        // Цель связи не меняется, поэтому её блокировка не мешает.
        var linked = await env.LinkSvc.Add(env.Project, b.Id, blocks.Id, a.Id, null);
        Assert.Single(linked!.Links);
    }

    // ---- типы связей ----

    [Fact]
    public async Task Link_types_can_be_created_renamed_and_deleted_when_unused()
    {
        var env = new SeriesEnv();

        var depends = await env.LinkTypeSvc.Create(env.Project, new CreateLinkType("Depends", "depends on", "is a dependency of"));
        Assert.Equal(("depends on", "is a dependency of"), (depends.OutwardName, depends.InwardName));

        var renamed = await env.LinkTypeSvc.Update(env.Project, depends.Id, new UpdateLinkType("Dependency", null, "is needed by", depends.Version));
        Assert.Equal(("Dependency", "depends on", "is needed by"), (renamed!.Name, renamed.OutwardName, renamed.InwardName));

        Assert.True(await env.LinkTypeSvc.Delete(env.Project, depends.Id, renamed.Version));
        Assert.Null(await env.LinkTypeSvc.Find(env.Project, "Dependency"));
    }

    [Fact]
    public async Task A_type_without_inward_name_is_symmetric_and_names_must_be_unique_and_valid()
    {
        var env = new SeriesEnv();

        var same = await env.LinkTypeSvc.Create(env.Project, new CreateLinkType("Pair", "goes with", null));
        Assert.True(same.IsSymmetric);
        Assert.Equal("goes with", same.InwardName);

        await Assert.ThrowsAsync<TaskerConflictException>(() => env.LinkTypeSvc.Create(env.Project, new CreateLinkType("pair", "x", "y")));
        await Assert.ThrowsAsync<TaskerConflictException>(() => env.LinkTypeSvc.Create(env.Project, new CreateLinkType("blocks", "x", "y"))); // как у встроенного
        await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkTypeSvc.Create(env.Project, new CreateLinkType(" ", "x", "y")));
        await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkTypeSvc.Create(env.Project, new CreateLinkType("N", "", "y")));
        await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkTypeSvc.Create(env.Project, new CreateLinkType("N", "x", new string('y', 101))));

        // Версия обязательна и сверяется.
        var type = await Type(env, "Blocks");
        await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkTypeSvc.Update(env.Project, type.Id, new UpdateLinkType("B2", null, null, null)));
        await Assert.ThrowsAsync<TaskerConflictException>(() => env.LinkTypeSvc.Update(env.Project, type.Id, new UpdateLinkType("B2", null, null, "999")));
        Assert.Null(await env.LinkTypeSvc.Update(env.Project, Guid.NewGuid(), new UpdateLinkType("B2", null, null, "1")));
    }

    [Fact]
    public async Task A_type_with_links_cannot_be_deleted_until_they_are_removed()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        blocks = await Type(env, "Blocks"); // связь сохранила типы по умолчанию: у Blocks теперь настоящая версия

        var error = await Assert.ThrowsAsync<TaskerConflictException>(() => env.LinkTypeSvc.Delete(env.Project, blocks.Id, blocks.Version));
        Assert.Equal(ConflictCode.InUse, error.Code);
        Assert.Contains("1 task(s)", error.Message);

        await env.LinkSvc.Remove(env.Project, a.Id, blocks.Id, b.Id, null);
        Assert.True(await env.LinkTypeSvc.Delete(env.Project, blocks.Id, blocks.Version));
        Assert.False(await env.LinkTypeSvc.Delete(env.Project, blocks.Id, blocks.Version));
    }

    [Fact]
    public async Task A_type_someone_else_edits_cannot_be_changed_or_deleted()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        await env.Locks.Acquire(LockedEntity.LinkType, blocks.Id, "Link type");

        env.Editor.Holder = FakeEditor.Anna;
        await Assert.ThrowsAsync<TaskerLockedException>(() => env.LinkTypeSvc.Update(env.Project, blocks.Id, new UpdateLinkType("B2", null, null, blocks.Version)));
        await Assert.ThrowsAsync<TaskerLockedException>(() => env.LinkTypeSvc.Delete(env.Project, blocks.Id, blocks.Version));
    }

    // ---- разбор того, что написал человек ----

    [Theory]
    [InlineData("Blocks", "Blocks", LinkDirection.Outward)]
    [InlineData("blocks", "Blocks", LinkDirection.Outward)]
    [InlineData("  BLOCKS ", "Blocks", LinkDirection.Outward)]
    [InlineData("is blocked by", "Blocks", LinkDirection.Inward)]
    [InlineData("Is Duplicated By", "Duplicate", LinkDirection.Inward)]
    [InlineData("duplicates", "Duplicate", LinkDirection.Outward)]
    [InlineData("relates to", "Relates", LinkDirection.Outward)] // у симметричной — всегда исходящая
    [InlineData("Problem/Incident", "Problem/Incident", LinkDirection.Outward)]
    [InlineData("is caused by", "Problem/Incident", LinkDirection.Inward)]
    public async Task A_phrase_finds_the_type_and_the_side(string phrase, string expectedType, LinkDirection expectedDirection)
    {
        var env = new SeriesEnv();

        var (type, direction) = await env.LinkTypeSvc.Resolve(env.Project, phrase);

        Assert.Equal((expectedType, expectedDirection), (type.Name, direction));
    }

    [Fact]
    public async Task A_phrase_by_id_is_outward_and_unknown_or_ambiguous_phrases_are_rejected()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");

        Assert.Equal((blocks.Id, LinkDirection.Outward), await Pair(env, blocks.Id.ToString()));

        var unknown = await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkTypeSvc.Resolve(env.Project, "frobnicates"));
        Assert.Contains("is blocked by", unknown.Message); // подсказка со списком доступных
        await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkTypeSvc.Resolve(env.Project, " "));

        // Два типа с одним названием стороны — не угадываем.
        await env.LinkTypeSvc.Create(env.Project, new CreateLinkType("Mine", "blocks", "is stopped by"));
        var ambiguous = await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkTypeSvc.Resolve(env.Project, "blocks"));
        Assert.Contains("several link types", ambiguous.Message);

        static async Task<(Guid, LinkDirection)> Pair(SeriesEnv env, string phrase)
        {
            var (type, direction) = await env.LinkTypeSvc.Resolve(env.Project, phrase);
            return (type.Id, direction);
        }
    }

    [Fact]
    public async Task The_task_filter_matches_tasks_with_a_link_of_the_given_types()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var relates = await Type(env, "Relates");
        var a = await env.Create("A");
        var b = await env.Create("B");
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);

        Assert.Equal(1, await env.Tasks.Count(env.Project, new TaskFilter { LinkTypeIds = [blocks.Id] }));
        Assert.Equal(0, await env.Tasks.Count(env.Project, new TaskFilter { LinkTypeIds = [relates.Id] }));
        Assert.Equal(1, await env.Tasks.Count(env.Project, new TaskFilter { LinkTypeIds = [relates.Id, blocks.Id] }));
        Assert.Equal(0, await env.Tasks.Count(env.Project, new TaskFilter { LinkTypeIds = [] }));
        Assert.Equal(2, await env.Tasks.Count(env.Project, new TaskFilter()));
    }

    // ---- связи в представлении задачи (TaskDetails) ----

    [Fact]
    public async Task Task_details_show_links_from_both_sides_and_skip_invalid_ones()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");
        var c = await env.Create("C");
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        await env.LinkSvc.Add(env.Project, b.Id, blocks.Id, c.Id, null);
        env.Tasks.Touch(a.Id, t => t with { Links = [.. t.Links, new TaskLink(blocks.Id, Guid.NewGuid()), new TaskLink(Guid.NewGuid(), b.Id)] });

        var details = (await env.TaskSvc.Describe(env.Project, b.Id))!;

        Assert.Equal(2, details.LinkCount);
        Assert.Equal([(LinkDirection.Outward, "blocks", c.Id), (LinkDirection.Inward, "is blocked by", a.Id)],
            details.LinkViews.Select(x => (x.Direction, x.Name, x.Task.Id)).ToArray());
        Assert.Equal([new TaskLink(blocks.Id, c.Id)], details.Links); // сырое хранимое — только исходящие
        // Задача без связей: пусто и ноль.
        var d = await env.Create("D");
        var alone = await env.TaskSvc.Describe(env.Project, d.Id);
        Assert.Empty(alone!.LinkViews);
        Assert.Equal(0, alone.LinkCount);
    }

    [Fact]
    public async Task Task_details_cap_long_link_lists_but_report_the_total()
    {
        var env = new SeriesEnv();
        var relates = await Type(env, "Relates");
        var hub = await env.Create("Hub");
        var total = TaskLinkService.MaxViewed + 5;
        for (var i = 0; i < total; i++)
        {
            var other = await env.Create($"Other {i:000}");
            await env.LinkSvc.Add(env.Project, other.Id, relates.Id, hub.Id, null);
        }

        var details = (await env.TaskSvc.Describe(env.Project, hub.Id))!;

        Assert.Equal(total, details.LinkCount);
        Assert.Equal(TaskLinkService.MaxViewed, details.LinkViews.Count);
        Assert.Equal(total, (await env.LinkSvc.GetLinks(env.Project, hub.Id))!.Length); // get_task_links отдаёт все
    }
}
