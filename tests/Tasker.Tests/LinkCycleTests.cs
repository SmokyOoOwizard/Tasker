using Tasker.Core;
using Tasker.Core.Links;
using Tasker.Core.TaskSeries;
using Tasker.Tests.SeriesCore;
using Xunit;

namespace Tasker.Tests;

/// <summary>Циклы связей (TSK-97): запрет при создании, признак типа «допускает циклы», обнаружение после слияния git. Сервисы Core на хранилищах в памяти.</summary>
public class LinkCycleTests
{
    private static async Task<LinkType> Type(SeriesEnv env, string name) => (await env.LinkTypeSvc.Find(env.Project, name))!;

    /// <summary>Как после слияния веток: связь появилась в файле задачи мимо сервиса.</summary>
    private static void AddRaw(SeriesEnv env, Guid sourceId, LinkType type, Guid targetId) =>
        env.Tasks.Touch(sourceId, t => t with { Links = [.. t.Links, new TaskLink(type.Id, targetId)] });

    [Fact]
    public async Task Default_types_forbid_cycles_only_for_blocks()
    {
        var env = new SeriesEnv();

        Assert.False((await Type(env, "Blocks")).AllowCycles);
        foreach (var name in new[] { "Duplicate", "Cloners", "Relates", "Problem/Incident" })
            Assert.True((await Type(env, name)).AllowCycles, name);
    }

    [Fact]
    public async Task A_reverse_link_closing_a_two_task_cycle_is_rejected_with_the_path_and_nothing_is_written()
    {
        var env = new SeriesEnv();
        var tsk = env.AddSeries("TSK");
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A", tsk.Id);
        var b = await env.Create("B", tsk.Id);
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        var before = env.Get(b.Id);

        var e = await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkSvc.Add(env.Project, b.Id, blocks.Id, a.Id, null));

        Assert.StartsWith("Cycle: TSK-2 → TSK-1 → TSK-2", e.Message);
        Assert.Contains("'Blocks'", e.Message);
        Assert.Equal(before.Version, env.Get(b.Id).Version);
        Assert.Empty(env.Get(b.Id).Links);
    }

    [Fact]
    public async Task A_cycle_of_three_is_rejected_and_a_chain_without_closing_is_fine()
    {
        var env = new SeriesEnv();
        var tsk = env.AddSeries("TSK");
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A", tsk.Id);
        var b = await env.Create("B", tsk.Id);
        var c = await env.Create("C", tsk.Id);
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        await env.LinkSvc.Add(env.Project, b.Id, blocks.Id, c.Id, null);
        // A → C поверх A → B → C — не цикл (ромб, а не круг).
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, c.Id, null);

        var e = await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkSvc.Add(env.Project, c.Id, blocks.Id, a.Id, null));

        // Кратчайший путь от A до C — прямая связь: «C → A → C».
        Assert.StartsWith("Cycle: TSK-3 → TSK-1 → TSK-3", e.Message);

        await env.LinkSvc.Remove(env.Project, a.Id, blocks.Id, c.Id, null);
        var e2 = await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkSvc.Add(env.Project, c.Id, blocks.Id, a.Id, null));
        Assert.StartsWith("Cycle: TSK-3 → TSK-1 → TSK-2 → TSK-3", e2.Message);
    }

    [Fact]
    public async Task The_inward_phrase_is_checked_in_the_stored_direction()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null); // A blocks B

        // «A is blocked by B» хранится как «B blocks A» — это тот же цикл.
        var (type, direction) = await env.LinkTypeSvc.Resolve(env.Project, "is blocked by");
        Assert.Equal(LinkDirection.Inward, direction);
        await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkSvc.Add(env.Project, b.Id, type.Id, a.Id, null));
    }

    [Fact]
    public async Task Links_of_other_types_do_not_close_a_cycle_of_blocks()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var duplicate = await Type(env, "Duplicate");
        var a = await env.Create("A");
        var b = await env.Create("B");
        await env.LinkSvc.Add(env.Project, a.Id, duplicate.Id, b.Id, null);

        await env.LinkSvc.Add(env.Project, b.Id, blocks.Id, a.Id, null);
        await env.LinkSvc.Add(env.Project, b.Id, duplicate.Id, a.Id, null); // «Duplicate» циклы допускает

        Assert.Equal(2, env.Get(b.Id).Links.Count);
    }

    [Fact]
    public async Task A_symmetric_type_never_forms_a_cycle_and_a_task_still_cannot_link_itself()
    {
        var env = new SeriesEnv();
        var relates = await Type(env, "Relates");
        var a = await env.Create("A");
        var b = await env.Create("B");
        var strict = await env.LinkTypeSvc.Create(env.Project, new CreateLinkType("Twins", "twins with", null, AllowCycles: false));

        await env.LinkSvc.Add(env.Project, a.Id, relates.Id, b.Id, null);
        await env.LinkSvc.Add(env.Project, b.Id, relates.Id, a.Id, null);
        await env.LinkSvc.Add(env.Project, a.Id, strict.Id, b.Id, null);
        await env.LinkSvc.Add(env.Project, b.Id, strict.Id, a.Id, null); // без направления: «циклом» не считается

        var blocks = await Type(env, "Blocks");
        var self = await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkSvc.Add(env.Project, a.Id, blocks.Id, a.Id, null));
        Assert.Contains("itself", self.Message);
    }

    [Fact]
    public async Task A_custom_type_chooses_whether_it_allows_cycles_and_it_can_be_changed()
    {
        var env = new SeriesEnv();
        var a = await env.Create("A");
        var b = await env.Create("B");

        var free = await env.LinkTypeSvc.Create(env.Project, new CreateLinkType("Feeds", "feeds", "is fed by"));
        var strict = await env.LinkTypeSvc.Create(env.Project, new CreateLinkType("Parent", "contains", "is part of", AllowCycles: false));
        Assert.True(free.AllowCycles);
        Assert.False(strict.AllowCycles);

        await env.LinkSvc.Add(env.Project, a.Id, free.Id, b.Id, null);
        await env.LinkSvc.Add(env.Project, b.Id, free.Id, a.Id, null);
        await env.LinkSvc.Add(env.Project, a.Id, strict.Id, b.Id, null);
        await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkSvc.Add(env.Project, b.Id, strict.Id, a.Id, null));

        // Разрешили — связь проходит; запретили снова — следующая такая связь отклоняется (уже созданное не трогается).
        var allowed = (await env.LinkTypeSvc.Update(env.Project, strict.Id, new UpdateLinkType(null, null, null, strict.Version, AllowCycles: true)))!;
        Assert.True(allowed.AllowCycles);
        await env.LinkSvc.Add(env.Project, b.Id, strict.Id, a.Id, null);
        var forbidden = (await env.LinkTypeSvc.Update(env.Project, strict.Id, new UpdateLinkType(null, null, null, allowed.Version, AllowCycles: false)))!;
        Assert.False(forbidden.AllowCycles);
        Assert.Equal(2, env.Get(a.Id).Links.Count(x => x.TypeId == strict.Id) + env.Get(b.Id).Links.Count(x => x.TypeId == strict.Id));

        // Правка названия признак не сбрасывает.
        Assert.False((await env.LinkTypeSvc.Update(env.Project, strict.Id, new UpdateLinkType("Parent2", null, null, forbidden.Version)))!.AllowCycles);
    }

    [Fact]
    public async Task Parallel_opposite_links_let_exactly_one_through()
    {
        for (var round = 0; round < 10; round++)
        {
            var env = new SeriesEnv();
            var blocks = await Type(env, "Blocks");
            var a = await env.Create("A");
            var b = await env.Create("B");

            var results = await Task.WhenAll(
                Attempt(env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null)),
                Attempt(env.LinkSvc.Add(env.Project, b.Id, blocks.Id, a.Id, null)));

            Assert.Equal(1, results.Count(x => x == null));
            Assert.Single(results.Where(x => x != null), x => x is TaskerValidationException);
            Assert.Equal(1, env.Get(a.Id).Links.Count + env.Get(b.Id).Links.Count);
        }

        static async Task<Exception?> Attempt(Task<Tasker.Core.Tasks.TaskItem?> add)
        {
            try
            {
                await add;
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }
    }

    [Fact]
    public async Task A_graph_too_large_to_check_is_refused_instead_of_accepted_blindly()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var head = await env.Create("Head");
        var tail = await env.Create("Tail");
        var fan = new List<Guid>();
        // Широкий веер за один уровень: LinkCycles.MaxVisited задач достижимы от цели.
        for (var i = 0; i < LinkCycles.MaxVisited + 5; i++)
            fan.Add(env.Seed("n" + i).Id);
        env.Tasks.Touch(tail.Id, t => t with { Links = fan.Select(x => new TaskLink(blocks.Id, x)).ToArray() });

        var e = await Assert.ThrowsAsync<TaskerValidationException>(() => env.LinkSvc.Add(env.Project, head.Id, blocks.Id, tail.Id, null));
        Assert.Contains("cannot verify", e.Message);
    }

    // ---- существующие циклы (после слияния git) ----

    [InProcess]
    [Fact]
    public async Task An_existing_cycle_is_reported_by_the_health_check_and_cleanup_does_not_remove_it()
    {
        var env = new SeriesEnv();
        var tsk = env.AddSeries("TSK");
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A", tsk.Id);
        var b = await env.Create("B", tsk.Id);
        var c = await env.Create("C", tsk.Id);
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        await env.LinkSvc.Add(env.Project, b.Id, blocks.Id, c.Id, null);
        Assert.Empty((await env.LinkHealthSvc.Check(env.Project)).Cycles);

        AddRaw(env, c.Id, blocks, a.Id); // ветка 1 добавила A→B→C, ветка 2 — C→A
        var versions = new[] { a.Id, b.Id, c.Id }.Select(x => env.Get(x).Version).ToArray();

        var health = await env.LinkHealthSvc.Check(env.Project);
        Assert.True(health.NeedsAttention);
        var cycle = Assert.Single(health.Cycles);
        Assert.Equal("Blocks", cycle.TypeName);
        Assert.Equal("TSK-1 → TSK-2 → TSK-3 → TSK-1", cycle.Format(new Dictionary<Guid, string> { [tsk.Id] = "TSK" }));

        var report = await env.Cleanup.Run(env.Project, new CleanupOptions());
        Assert.Empty(report.Changes);
        Assert.Single(report.LinkCycles);
        Assert.Equal(versions, new[] { a.Id, b.Id, c.Id }.Select(x => env.Get(x).Version).ToArray()); // ни одна связь не снята

        // Данные с циклом читаются: связи показываются с обеих сторон.
        var links = await env.LinkSvc.GetLinks(env.Project, a.Id);
        Assert.Equal(["blocks", "is blocked by"], links!.Select(x => x.Name).ToArray());
        Assert.Equal(2, (await env.TaskSvc.Describe(env.Project, a.Id))!.LinkViews.Count);
    }

    [Fact]
    public async Task Cycles_of_types_that_allow_them_and_two_separate_cycles_are_counted_correctly()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var duplicate = await Type(env, "Duplicate");
        var t = new[] { await env.Create("1"), await env.Create("2"), await env.Create("3"), await env.Create("4") };

        AddRaw(env, t[0].Id, blocks, t[1].Id);
        AddRaw(env, t[1].Id, blocks, t[0].Id);
        AddRaw(env, t[2].Id, blocks, t[3].Id);
        AddRaw(env, t[3].Id, blocks, t[2].Id);
        AddRaw(env, t[0].Id, duplicate, t[1].Id); // «Duplicate» допускает циклы: не в счёт
        AddRaw(env, t[1].Id, duplicate, t[0].Id);

        var cycles = (await env.LinkHealthSvc.Check(env.Project)).Cycles;

        Assert.Equal(2, cycles.Length);
        Assert.All(cycles, x => Assert.Equal(3, x.Path.Length));
        Assert.All(cycles, x => Assert.Equal(x.Path[0].Id, x.Path[^1].Id));
    }

    [Fact]
    public void Cycle_finding_handles_loops_branches_and_dangling_links_deterministically()
    {
        Guid[] n = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        LinkEdge E(int from, int to) => new(n[from], n[to]);

        // Круг 0→1→2→0 с отростком 2→3 и входом 4→0; петля 5→5; висячая цель 3 цикла не образует.
        var cycles = LinkCycles.CyclesOf([E(0, 1), E(1, 2), E(2, 0), E(2, 3), E(4, 0), E(5, 5)]).ToArray();

        Assert.Equal(2, cycles.Length);
        Assert.Contains(cycles, x => x.Length == 2 && x[0] == n[5] && x[1] == n[5]);
        var circle = Assert.Single(cycles, x => x.Length == 4);
        Assert.Equal(circle[0], circle[^1]);
        Assert.Equal(new HashSet<Guid> { n[0], n[1], n[2] }, circle.ToHashSet());
        Assert.Empty(LinkCycles.CyclesOf([E(0, 1), E(1, 2)]));
    }
}
