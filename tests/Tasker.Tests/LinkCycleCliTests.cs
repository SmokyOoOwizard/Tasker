using Tasker.Tests.SeriesCore;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Циклы связей (TSK-97) снаружи: консоль на настоящем git. Две ветки независимо добавляют «A blocks B» и «B blocks A» — файлы разные,
/// git конфликта не видит, а после слияния цикл есть.
/// </summary>
[InProcess]
public class LinkCycleCliTests : IDisposable
{
    private readonly SeriesRepo _r = new();

    public void Dispose() => _r.Dispose();

    private static Task<CliResult> Ok(Task<CliResult> run) => SeriesRepo.Ok(run);

    /// <summary>Типы связей по умолчанию сохраняются в файлы первой связью; её тут же убираем и коммитим общее состояние.</summary>
    private async Task SaveLinkTypes()
    {
        await Ok(_r.P("task", "link", "TSK-1", "relates to", "TSK-2"));
        await Ok(_r.P("task", "unlink", "TSK-1", "relates to", "TSK-2"));
        _r.Repo.Commit("link types");
    }

    /// <summary>Base (TSK-1) и Other (TSK-2); типы связей по умолчанию сохранены в файлы и закоммичены.</summary>
    private async Task SeedTwo()
    {
        await _r.Seed();
        await Ok(_r.P("task", "create", "Other", "--type", "Bug", "--series", "TSK"));
        await SaveLinkTypes();
    }

    private async Task MergeOpposite()
    {
        await SeedTwo();
        await _r.Branch("one", async () => await Ok(_r.P("task", "link", "TSK-1", "blocks", "TSK-2")));
        await _r.Branch("two", async () => await Ok(_r.P("task", "link", "TSK-2", "blocks", "TSK-1")));
        _r.Merge("one", "two");
    }

    [Fact]
    public async Task The_console_rejects_a_link_that_closes_a_cycle_and_the_type_can_allow_it()
    {
        await SeedTwo();
        await Ok(_r.P("task", "link", "TSK-1", "blocks", "TSK-2"));

        var direct = await _r.P("task", "link", "TSK-2", "blocks", "TSK-1");
        Assert.Equal(1, direct.Code);
        Assert.Contains("Cycle: TSK-2 → TSK-1 → TSK-2", direct.Err);
        // «is blocked by» — та же связь с другой стороны.
        var inward = await _r.P("task", "link", "TSK-1", "is blocked by", "TSK-2");
        Assert.Equal(1, inward.Code);
        Assert.Contains("Cycle: TSK-2 → TSK-1 → TSK-2", inward.Err);

        var blocks = (await Ok(_r.P("link-type", "get", "Blocks", "--json"))).Json;
        Assert.False(blocks["allowCycles"]!.GetValue<bool>());
        Assert.Contains("allowCycles", (await Ok(_r.P("link-type", "get", "Blocks"))).Out);

        await Ok(_r.P("link-type", "update", "Blocks", "--allow-cycles", "true"));
        await Ok(_r.P("task", "link", "TSK-2", "blocks", "TSK-1"));
        Assert.Equal(2, (await Ok(_r.P("task", "links", "TSK-1"))).Data.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task A_custom_link_type_takes_allow_cycles_on_create_and_update()
    {
        await SeedTwo();

        await Ok(_r.P("link-type", "create", "Parent", "--outward", "contains", "--inward", "is part of", "--allow-cycles", "false"));
        Assert.False((await Ok(_r.P("link-type", "get", "Parent", "--json"))).Json["allowCycles"]!.GetValue<bool>());
        await Ok(_r.P("link-type", "create", "Feeds", "--outward", "feeds", "--inward", "is fed by"));
        Assert.True((await Ok(_r.P("link-type", "get", "Feeds", "--json"))).Json["allowCycles"]!.GetValue<bool>());

        await Ok(_r.P("task", "link", "TSK-1", "contains", "TSK-2"));
        Assert.Equal(1, (await _r.P("task", "link", "TSK-2", "contains", "TSK-1")).Code);
        await Ok(_r.P("link-type", "update", "Parent", "--allow-cycles", "true"));
        await Ok(_r.P("task", "link", "TSK-2", "contains", "TSK-1"));
        // Без единого изменения update по-прежнему отказывает.
        Assert.Equal(1, (await _r.P("link-type", "update", "Parent")).Code);
    }

    [Fact]
    public async Task After_a_merge_sync_and_cleanup_check_report_the_cycle_and_cleanup_leaves_the_links()
    {
        await MergeOpposite();
        var before = _r.Snapshot();

        // Данные с циклом читаются.
        Assert.Contains("TSK-2", (await Ok(_r.P("task", "links", "TSK-1"))).Out);
        Assert.Contains("is blocked by", (await Ok(_r.P("task", "get", "TSK-1"))).Out);

        var sync = await Ok(_r.Cli("sync"));
        Assert.Contains("Link problems in project 'Demo':", sync.Out);
        Assert.Contains("Link cycle: TSK-1 → TSK-2 → TSK-1 (link type Blocks)", sync.Out);
        Assert.Contains("tasker task unlink", sync.Out);
        var json = Assert.Single((await Ok(_r.Cli("sync", "--json"))).Json["series"]!.AsArray())!;
        Assert.Equal("TSK-1 → TSK-2 → TSK-1", json["linkCycles"]![0]!["path"]!.GetValue<string>());
        Assert.Equal(before, _r.Snapshot());

        var check = await _r.Cli("cleanup", "--check");
        Assert.Equal(2, check.Code);
        Assert.Contains("link cycle: TSK-1 → TSK-2 → TSK-1 (link type Blocks)", check.Out);
        Assert.Contains("tasker task unlink", check.Out);

        // cleanup связи сам не удаляет: ничего не изменилось, цикл остаётся на виду.
        var real = await Ok(_r.Cli("cleanup"));
        Assert.Contains("link cycle:", real.Out);
        Assert.DoesNotContain("change(s)", real.Out);
        Assert.Equal(before, _r.Snapshot());
        var cleanupJson = (await Ok(_r.Cli("cleanup", "--dry-run", "--json"))).Json;
        Assert.Equal(0, cleanupJson["changeCount"]!.GetValue<int>());
        Assert.Equal("TSK-1 → TSK-2 → TSK-1", cleanupJson["projects"]![0]!["linkCycles"]![0]!["path"]!.GetValue<string>());

        // Человек снимает одну из связей — цикла нет.
        await Ok(_r.P("task", "unlink", "TSK-2", "blocks", "TSK-1"));
        Assert.Equal(0, (await _r.Cli("cleanup", "--check")).Code);
        Assert.DoesNotContain("Link problems", (await Ok(_r.Cli("sync"))).Out);
        Assert.Empty((await Ok(_r.Cli("sync", "--json"))).Json["series"]!.AsArray());
    }

    [Fact]
    public async Task A_cycle_of_three_made_by_three_branches_is_found()
    {
        await _r.Seed();
        await Ok(_r.P("task", "create", "Second", "--type", "Bug", "--series", "TSK"));
        await Ok(_r.P("task", "create", "Third", "--type", "Bug", "--series", "TSK"));
        await SaveLinkTypes();
        await _r.Branch("one", async () => await Ok(_r.P("task", "link", "TSK-1", "blocks", "TSK-2")));
        await _r.Branch("two", async () => await Ok(_r.P("task", "link", "TSK-2", "blocks", "TSK-3")));
        await _r.Branch("three", async () => await Ok(_r.P("task", "link", "TSK-3", "blocks", "TSK-1")));
        _r.Merge("one", "two", "three");

        var check = await _r.Cli("cleanup", "--check");

        Assert.Equal(2, check.Code);
        Assert.Contains("link cycle: TSK-1 → TSK-2 → TSK-3 → TSK-1 (link type Blocks)", check.Out);
    }
}
