using Tasker.Core.Links;
using Tasker.Core.TaskSeries;
using Tasker.Tests.SeriesCore;
using Xunit;

namespace Tasker.Tests;

/// <summary>Чистка недействительных связей между задачами (<c>cleanup</c>) и их сводка в сверке: сервисы Core на хранилищах в памяти.</summary>
public class LinkCleanupCoreTests
{
    private static async Task<LinkType> Type(SeriesEnv env, string name) => (await env.LinkTypeSvc.Find(env.Project, name))!;

    /// <summary>Как после слияния веток: у задачи связь на то, чего нет.</summary>
    private static void AddRaw(SeriesEnv env, Guid taskId, params TaskLink[] links) =>
        env.Tasks.Touch(taskId, t => t with { Links = [.. t.Links, .. links] });

    private static CleanupChange[] Links(CleanupReport report) =>
        report.Changes.Where(x => x.Kind == CleanupChangeKind.RemovedInvalidLink).ToArray();

    [Fact]
    public async Task A_link_to_a_missing_task_is_removed_and_valid_links_stay()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        var gone = Guid.NewGuid();
        AddRaw(env, a.Id, new TaskLink(blocks.Id, gone));
        var untouched = await env.Create("Untouched");
        var untouchedVersion = env.Get(untouched.Id).Version;

        var report = await env.Cleanup.Run(env.Project, new CleanupOptions());

        Assert.Equal([new TaskLink(blocks.Id, b.Id)], env.Get(a.Id).Links);
        var change = Assert.Single(Links(report));
        Assert.Equal((a.Id, "A", blocks.Id, gone), (change.TaskId, change.TaskTitle, change.LinkTypeId, change.LinkTargetId));
        Assert.Null(change.SeriesId);
        Assert.Equal($"Task 'A': removed link 'Blocks' to a task that does not exist ({gone})", change.Description);
        Assert.Null(report.LinksSkipReason);
        Assert.Equal(untouchedVersion, env.Get(untouched.Id).Version); // чужие задачи не переписываются
    }

    [Fact]
    public async Task A_link_of_a_missing_link_type_is_removed()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        var goneType = Guid.NewGuid();
        AddRaw(env, a.Id, new TaskLink(goneType, b.Id));

        var report = await env.Cleanup.Run(env.Project, new CleanupOptions());

        Assert.Equal([new TaskLink(blocks.Id, b.Id)], env.Get(a.Id).Links);
        var change = Assert.Single(Links(report));
        Assert.Equal((goneType, b.Id), (change.LinkTypeId, change.LinkTargetId));
        Assert.Contains("link type that does not exist", change.Description);
    }

    [Fact]
    public async Task Several_bad_links_in_several_tasks_are_all_removed_in_creation_order()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var relates = await Type(env, "Relates");
        var a = await env.Create("A");
        env.Clock.Now += TimeSpan.FromMinutes(1); // порядок чистки — по времени создания задач
        var b = await env.Create("B");
        env.Clock.Now += TimeSpan.FromMinutes(1);
        var c = await env.Create("C");
        await env.LinkSvc.Add(env.Project, b.Id, relates.Id, c.Id, null);
        AddRaw(env, c.Id, new TaskLink(blocks.Id, Guid.NewGuid()), new TaskLink(Guid.NewGuid(), a.Id), new TaskLink(blocks.Id, a.Id));
        AddRaw(env, a.Id, new TaskLink(relates.Id, Guid.NewGuid()));

        var report = await env.Cleanup.Run(env.Project, new CleanupOptions());

        Assert.Equal(["A", "C", "C"], Links(report).Select(x => x.TaskTitle).ToArray()); // по порядку создания задач
        Assert.Empty(env.Get(a.Id).Links);
        Assert.Equal([new TaskLink(blocks.Id, a.Id)], env.Get(c.Id).Links);
        Assert.Equal([new TaskLink(relates.Id, c.Id)], env.Get(b.Id).Links);
    }

    [Fact]
    public async Task A_dry_run_reports_the_same_changes_and_writes_nothing()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        AddRaw(env, a.Id, new TaskLink(blocks.Id, Guid.NewGuid()));
        var before = env.Get(a.Id);
        var writes = env.Tasks.UpdateCount;

        var dry = await env.Cleanup.Run(env.Project, new CleanupOptions(DryRun: true));

        Assert.Single(Links(dry));
        Assert.Equal(before, env.Get(a.Id));
        Assert.Equal(writes, env.Tasks.UpdateCount);

        var real = await env.Cleanup.Run(env.Project, new CleanupOptions());
        Assert.Equal(Links(dry).Select(x => x.Description), Links(real).Select(x => x.Description));
        Assert.Empty(env.Get(a.Id).Links);
    }

    [Fact]
    public async Task The_cleanup_is_idempotent_and_does_nothing_when_all_links_are_valid()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        var writes = env.Tasks.UpdateCount;

        var clean = await env.Cleanup.Run(env.Project, new CleanupOptions());
        Assert.Empty(clean.Changes);
        Assert.Null(clean.LinksSkipReason);
        Assert.Equal(writes, env.Tasks.UpdateCount);

        AddRaw(env, a.Id, new TaskLink(blocks.Id, Guid.NewGuid()));
        Assert.Single(Links(await env.Cleanup.Run(env.Project, new CleanupOptions())));
        Assert.Empty((await env.Cleanup.Run(env.Project, new CleanupOptions())).Changes);
    }

    [Fact]
    public async Task Links_are_not_cleaned_while_a_task_file_is_unreadable_but_series_still_are()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var series = env.AddSeries("TSK");
        var a = await env.Create("A", series.Id);
        var gone = Guid.NewGuid();
        AddRaw(env, a.Id, new TaskLink(blocks.Id, gone));
        env.Tasks.Touch(a.Id, t => t with { SeriesNumbers = [.. t.SeriesNumbers, new TaskSeriesNumber(Guid.NewGuid(), 7)] }); // и ссылка на несуществующую серию
        env.Tasks.UnreadableFiles = 1; // конфликт слияния в файле задачи, на которую могут указывать связи

        var report = await env.Cleanup.Run(env.Project, new CleanupOptions());

        Assert.False(report.Skipped); // серии очищены как обычно
        Assert.Contains(report.Changes, x => x.Kind == CleanupChangeKind.RemovedInvalidSeries);
        Assert.Empty(Links(report));
        Assert.Contains("cannot be read", report.LinksSkipReason);
        Assert.Contains("Merge", "Merge" + report.LinksSkipReason); // причина — понятный текст
        Assert.Contains(new TaskLink(blocks.Id, gone), env.Get(a.Id).Links); // связь не стёрта
    }

    [Fact]
    public async Task Links_are_not_cleaned_while_a_link_type_file_is_unreadable()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        env.LinkTypes.UnreadableFiles = 1; // нечитаемый тип выглядел бы несуществующим

        var report = await env.Cleanup.Run(env.Project, new CleanupOptions());

        Assert.NotNull(report.LinksSkipReason);
        Assert.Empty(report.Changes);
        Assert.Equal([new TaskLink(blocks.Id, b.Id)], env.Get(a.Id).Links);
    }

    [Fact]
    public async Task Links_of_the_default_link_types_are_valid_before_the_types_are_saved()
    {
        var env = new SeriesEnv();
        var a = await env.Create("A");
        var b = await env.Create("B");
        // Типов в хранилище ещё нет — показываются типы по умолчанию; связь на один из них (например, пришла из другой ветки) действительна.
        var blocksId = DefaultLinkTypes.IdOf(env.Project, "blocks");
        AddRaw(env, a.Id, new TaskLink(blocksId, b.Id));
        Assert.Equal(0, env.LinkTypes.AddCount);

        var report = await env.Cleanup.Run(env.Project, new CleanupOptions());

        Assert.Empty(report.Changes);
        Assert.Equal([new TaskLink(blocksId, b.Id)], env.Get(a.Id).Links);
    }

    [Fact]
    public async Task Link_health_counts_tasks_with_bad_links_and_reports_unreadable_files_instead_of_guessing()
    {
        var env = new SeriesEnv();
        var blocks = await Type(env, "Blocks");
        var a = await env.Create("A");
        var b = await env.Create("B");
        var c = await env.Create("C");
        await env.LinkSvc.Add(env.Project, a.Id, blocks.Id, b.Id, null);
        Assert.Equal(new LinkHealth(0, 0), await env.LinkHealthSvc.Check(env.Project));
        Assert.False((await env.LinkHealthSvc.Check(env.Project)).NeedsAttention);

        AddRaw(env, b.Id, new TaskLink(blocks.Id, Guid.NewGuid()));
        AddRaw(env, c.Id, new TaskLink(Guid.NewGuid(), a.Id), new TaskLink(blocks.Id, Guid.NewGuid()));
        Assert.Equal(new LinkHealth(2, 0), await env.LinkHealthSvc.Check(env.Project)); // считаются задачи, а не связи

        env.Tasks.UnreadableFiles = 2;
        env.LinkTypes.UnreadableFiles = 1;
        var unclear = await env.LinkHealthSvc.Check(env.Project);
        Assert.Equal(new LinkHealth(0, 3), unclear);
        Assert.True(unclear.NeedsAttention);
    }
}

/// <summary>
/// <c>cleanup</c> и <c>sync</c> на настоящем git: слияние веток оставляет у задачи связь на удалённую задачу или тип.
/// </summary>
public class LinkCleanupCliTests : IDisposable
{
    private readonly SeriesRepo _r = new();

    public void Dispose() => _r.Dispose();

    private static Task<CliResult> Ok(Task<CliResult> run) => SeriesRepo.Ok(run);

    [Fact]
    public async Task After_a_merge_sync_reports_the_dangling_link_and_cleanup_removes_it()
    {
        await _r.Seed();
        var (source, target) = await _r.MergeDanglingTargetLink();
        var before = _r.Snapshot();

        // Связь на удалённую задачу при показе пропускается, но сверка о ней говорит.
        Assert.Empty((await Ok(_r.P("task", "links", "TSK-1"))).Data.Trim());
        var sync = await Ok(_r.Cli("sync"));
        Assert.Contains("Link problems in project 'Demo':", sync.Out);
        Assert.Contains("1 task(s) have a link to a task or link type that does not exist (run 'tasker cleanup')", sync.Out);
        Assert.DoesNotContain("Series problems", sync.Out);
        var json = (await Ok(_r.Cli("sync", "--json"))).Json["series"]!.AsArray();
        Assert.Equal(1, Assert.Single(json)!["tasksWithInvalidLinks"]!.GetValue<int>());
        Assert.Equal(before, _r.Snapshot()); // сверка ничего не пишет

        // --check и --dry-run ничего не пишут.
        var check = await _r.Cli("cleanup", "--check");
        Assert.Equal(2, check.Code);
        Assert.Contains($"removed link 'Blocks' to a task that does not exist ({target})", check.Out);
        var dry = await Ok(_r.Cli("cleanup", "--dry-run"));
        Assert.Contains("1 change(s) would be made", dry.Out);
        Assert.Equal(before, _r.Snapshot());

        var real = await Ok(_r.Cli("cleanup"));
        Assert.Contains("1 change(s) made", real.Out);
        var changed = _r.Snapshot().Where(x => before[x.Key] != x.Value).Select(x => x.Key).ToArray();
        Assert.Single(changed); // переписан только файл Base
        Assert.Contains(source.ToString("N")[..8], changed[0]);
        Assert.DoesNotContain("links:", File.ReadAllText(_r.FileOf("tasks", source)));

        Assert.Equal(0, (await _r.Cli("cleanup", "--check")).Code);
        Assert.Contains("Nothing to clean up", (await Ok(_r.Cli("cleanup"))).Out);
        Assert.Empty((await Ok(_r.Cli("sync", "--json"))).Json["series"]!.AsArray());
        Assert.DoesNotContain("Link problems", (await Ok(_r.Cli("sync"))).Out);
    }

    [Fact]
    public async Task A_link_of_a_link_type_deleted_in_another_branch_is_removed_too()
    {
        await _r.Seed();
        var source = await _r.MergeDanglingTypeLink();

        var check = await _r.Cli("cleanup", "--check");
        Assert.Equal(2, check.Code);
        Assert.Contains("removed a link of a link type that does not exist", check.Out);
        Assert.Contains("Link problems in project 'Demo':", (await Ok(_r.Cli("sync"))).Out);

        await Ok(_r.Cli("cleanup"));

        Assert.DoesNotContain("links:", File.ReadAllText(_r.FileOf("tasks", source)));
        Assert.Equal(0, (await _r.Cli("cleanup", "--check")).Code);
    }

    [Fact]
    public async Task The_json_report_describes_the_removed_links()
    {
        await _r.Seed();
        var (source, target) = await _r.MergeDanglingTargetLink();

        var json = (await Ok(_r.Cli("cleanup", "--dry-run", "--json"))).Json;

        Assert.Equal(1, json["changeCount"]!.GetValue<int>());
        var change = json["projects"]![0]!["changes"]![0]!;
        Assert.Equal("removedInvalidLink", change["kind"]!.GetValue<string>());
        Assert.Equal(source, change["taskId"]!.GetValue<Guid>());
        Assert.Equal(target, change["linkTargetId"]!.GetValue<Guid>());
        Assert.NotEqual(Guid.Empty, change["linkTypeId"]!.GetValue<Guid>());
        Assert.Null(change["seriesId"]);
        Assert.Null(json["projects"]![0]!["linksSkipReason"]);
    }

    [Fact]
    public async Task With_an_unreadable_link_type_file_links_are_not_touched_and_the_script_is_told()
    {
        await _r.Seed();
        var (source, _) = await _r.MergeDanglingTargetLink();
        // Конфликт слияния в файле типа связи: тип выглядел бы несуществующим.
        var typeFile = Directory.GetFiles(Path.Combine(_r.Folder, ".tasker"), "*.yaml", SearchOption.AllDirectories).First(x => x.Contains("link-types"));
        var typeId = File.ReadAllLines(typeFile).First(x => x.StartsWith("id: ")).Substring(4).Trim();
        File.WriteAllText(typeFile, "<<<<<<< HEAD\nname: A\n=======\nname: B\n>>>>>>> branch\n");
        var before = _r.Snapshot();

        var sync = await Ok(_r.Cli("sync"));
        Assert.Contains("Link problems in project 'Demo':", sync.Out);
        Assert.Contains("cannot be read", sync.Out);

        var check = await _r.Cli("cleanup", "--check");
        Assert.Equal(2, check.Code);
        Assert.Contains("links skipped:", check.Out);

        var real = await _r.Cli("cleanup");
        Assert.Equal(1, real.Code);
        Assert.Contains("links skipped:", real.Out);
        Assert.Contains("the links between tasks were not checked", real.Err);
        Assert.Equal(before, _r.Snapshot());
        Assert.Contains("links:", File.ReadAllText(_r.FileOf("tasks", source)));

        // Файл починили — связь убирается.
        File.WriteAllText(typeFile, "formatVersion: 2\nid: " + typeId + "\nname: Fixed\noutwardName: x\ninwardName: y\n");
        Assert.Equal(0, (await _r.Cli("cleanup")).Code);
    }
}
