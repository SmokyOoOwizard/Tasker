using Tasker.Core;
using Tasker.Core.TaskSeries;
using Tasker.Core.Tasks;
using Xunit;
using static Tasker.Tests.SeriesCore.SeriesShow;

namespace Tasker.Tests.SeriesCore;

internal static class SeriesShow
{
    public static string[] Show(IEnumerable<NumberConflict> items) =>
        items.Select(x => $"{x.SeriesId}#{x.Number}:{string.Join(",", x.TaskIds)}").ToArray();

    public static string[] Show(IEnumerable<PrefixConflict> items) =>
        items.Select(x => $"{x.Prefix}:{string.Join(",", x.SeriesIds)}").ToArray();
}

public class SeriesReferenceTests
{
    [Theory]
    [InlineData("TSK-5", "TSK", 5)]
    [InlineData("tsk-12", "tsk", 12)]
    [InlineData("  TSK-5\t", "TSK", 5)]
    [InlineData("A1-007", "A1", 7)]
    [InlineData("X-2147483647", "X", int.MaxValue)]
    public void Valid_references_are_parsed_and_case_is_kept(string text, string prefix, int number)
    {
        var parsed = TaskReference.TryParse(text);

        Assert.NotNull(parsed);
        Assert.Null(parsed.Value.Id);
        Assert.Equal(prefix, parsed.Value.Prefix);
        Assert.Equal(number, parsed.Value.Number);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TSK")]
    [InlineData("TSK-")]
    [InlineData("-5")]
    [InlineData("-")]
    [InlineData("TSK-0")]
    [InlineData("TSK--5")]
    [InlineData("TSK-+5")]
    [InlineData("TSK- 5")]
    [InlineData("TSK -5")]
    [InlineData("TSK-5x")]
    [InlineData("TSK-1.5")]
    [InlineData("A-B-5")]
    [InlineData("TSK-2147483648")]
    [InlineData("T_K-5")]
    [InlineData("ТСК-5")]
    [InlineData("TSK-٥")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU-1")]
    public void Invalid_references_give_null(string? text)
    {
        Assert.Null(TaskReference.TryParse(text));
        Assert.Throws<TaskerValidationException>(() => TaskReference.Parse(text));
    }

    [Fact]
    public void Guid_is_recognised_in_any_form_and_wins_over_prefix_parsing()
    {
        var id = Guid.NewGuid();

        foreach (var text in new[] { id.ToString(), $" {id:N} ", id.ToString().ToUpperInvariant(), $"{{{id}}}" })
        {
            var parsed = TaskReference.TryParse(text);
            Assert.NotNull(parsed);
            Assert.Equal(id, parsed.Value.Id);
            Assert.Null(parsed.Value.Prefix);
            Assert.Null(parsed.Value.Number);
        }
    }

    [Fact]
    public void Twenty_char_prefix_is_accepted()
    {
        var prefix = new string('Z', 20);
        Assert.Equal(prefix, TaskReference.Parse($"{prefix}-3").Prefix);
    }

    [Theory]
    [InlineData("TSK")]
    [InlineData("tsk")]
    [InlineData("a")]
    [InlineData("A1b2")]
    [InlineData("12345678901234567890")]
    public void Valid_prefixes(string prefix)
    {
        Assert.True(SeriesPrefix.IsValid(prefix));
        Assert.Equal(prefix, SeriesPrefix.Validate(prefix));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("A-B")]
    [InlineData("A B")]
    [InlineData("A_B")]
    [InlineData("ТСК")]
    [InlineData("123456789012345678901")]
    public void Invalid_prefixes(string? prefix)
    {
        Assert.False(SeriesPrefix.IsValid(prefix));
        Assert.Throws<TaskerValidationException>(() => SeriesPrefix.Validate(prefix));
    }
}

public class SeriesServiceTests
{
    private readonly SeriesEnv _env = new();

    [Fact]
    public async Task Create_trims_name_and_stores_series_under_exclusive_scope()
    {
        var created = await _env.SeriesSvc.Create(_env.Project, new CreateSeries("  Tasks  ", "TSK"));

        Assert.Equal("Tasks", created.Name);
        Assert.Equal("TSK", created.Prefix);
        Assert.Equal(_env.Project, created.ProjectId);
        Assert.NotEqual(Versioning.New, created.Version);
        Assert.Equal(created, await _env.Series.GetById(_env.Project, created.Id));
        Assert.Equal(1, _env.Scope.Calls);
    }

    [Fact]
    public async Task Create_allows_prefixes_that_differ_only_in_case()
    {
        await _env.SeriesSvc.Create(_env.Project, new CreateSeries("Upper", "TSK"));
        await _env.SeriesSvc.Create(_env.Project, new CreateSeries("Lower", "tsk"));

        Assert.Equal(["TSK", "tsk"], (await _env.Series.GetAll(_env.Project)).Select(x => x.Prefix));
    }

    [Fact]
    public async Task Create_with_taken_prefix_conflicts_and_writes_nothing()
    {
        await _env.SeriesSvc.Create(_env.Project, new CreateSeries("First", "TSK"));
        var before = _env.Series.WriteCount;

        await Assert.ThrowsAsync<TaskerConflictException>(() => _env.SeriesSvc.Create(_env.Project, new CreateSeries("Second", "TSK")));

        Assert.Equal(before, _env.Series.WriteCount);
    }

    [Fact]
    public async Task Create_same_prefix_in_another_project_is_fine()
    {
        await _env.SeriesSvc.Create(_env.Project, new CreateSeries("First", "TSK"));
        await _env.SeriesSvc.Create(Guid.NewGuid(), new CreateSeries("Other project", "TSK"));
    }

    [Theory]
    [InlineData("", "TSK")]
    [InlineData("  ", "TSK")]
    [InlineData("Name", "")]
    [InlineData("Name", "A-B")]
    [InlineData("Name", "1234567890123456789012")]
    public async Task Create_validates_input(string name, string prefix)
    {
        await Assert.ThrowsAsync<TaskerValidationException>(() => _env.SeriesSvc.Create(_env.Project, new CreateSeries(name, prefix)));
        Assert.Equal(0, _env.Series.WriteCount);
    }

    [Fact]
    public async Task Concurrent_creates_of_one_prefix_produce_one_series()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            try
            {
                await _env.SeriesSvc.Create(_env.Project, new CreateSeries("S", "TSK"));
                return true;
            }
            catch (TaskerConflictException)
            {
                return false;
            }
        }));

        Assert.Single(results, x => x);
        Assert.Single(await _env.Series.GetAll(_env.Project));
    }

    [Fact]
    public async Task Update_renames_prefix_and_name()
    {
        var series = await _env.SeriesSvc.Create(_env.Project, new CreateSeries("Old", "OLD"));

        var updated = await _env.SeriesSvc.Update(_env.Project, series.Id, new UpdateSeries(" New ", "NEW", series.Version));

        Assert.NotNull(updated);
        Assert.Equal("New", updated.Name);
        Assert.Equal("NEW", updated.Prefix);
        Assert.NotEqual(series.Version, updated.Version);
        Assert.Equal(updated, await _env.Series.GetById(_env.Project, series.Id));
    }

    [Fact]
    public async Task Update_null_fields_are_kept()
    {
        var series = await _env.SeriesSvc.Create(_env.Project, new CreateSeries("Name", "TSK"));

        var renamed = await _env.SeriesSvc.Update(_env.Project, series.Id, new UpdateSeries("Other", null, series.Version));
        Assert.Equal(("Other", "TSK"), (renamed!.Name, renamed.Prefix));

        var reprefixed = await _env.SeriesSvc.Update(_env.Project, series.Id, new UpdateSeries(null, "ABC", renamed.Version));
        Assert.Equal(("Other", "ABC"), (reprefixed!.Name, reprefixed.Prefix));
    }

    [Fact]
    public async Task Update_prefix_taken_by_another_series_conflicts_but_own_and_case_variants_are_fine()
    {
        var a = await _env.SeriesSvc.Create(_env.Project, new CreateSeries("A", "AAA"));
        var b = await _env.SeriesSvc.Create(_env.Project, new CreateSeries("B", "BBB"));

        await Assert.ThrowsAsync<TaskerConflictException>(() => _env.SeriesSvc.Update(_env.Project, b.Id, new UpdateSeries(null, "AAA", b.Version)));

        var same = await _env.SeriesSvc.Update(_env.Project, b.Id, new UpdateSeries("B2", "BBB", b.Version));
        Assert.Equal("BBB", same!.Prefix);

        var lower = await _env.SeriesSvc.Update(_env.Project, b.Id, new UpdateSeries(null, "aaa", same.Version));
        Assert.Equal("aaa", lower!.Prefix);
        Assert.Equal("AAA", (await _env.Series.GetById(_env.Project, a.Id))!.Prefix);
    }

    [Fact]
    public async Task Update_checks_version_and_existence()
    {
        var series = await _env.SeriesSvc.Create(_env.Project, new CreateSeries("A", "AAA"));

        Assert.Null(await _env.SeriesSvc.Update(_env.Project, Guid.NewGuid(), new UpdateSeries("x", null, "any")));
        await Assert.ThrowsAsync<TaskerValidationException>(() => _env.SeriesSvc.Update(_env.Project, series.Id, new UpdateSeries("x", null, null)));
        var stale = await Assert.ThrowsAsync<TaskerConflictException>(() => _env.SeriesSvc.Update(_env.Project, series.Id, new UpdateSeries("x", null, "stale")));
        Assert.Equal(ConflictCode.Modified, stale.Code);
        await Assert.ThrowsAsync<TaskerValidationException>(() => _env.SeriesSvc.Update(_env.Project, series.Id, new UpdateSeries(" ", null, series.Version)));
        await Assert.ThrowsAsync<TaskerValidationException>(() => _env.SeriesSvc.Update(_env.Project, series.Id, new UpdateSeries(null, "A-B", series.Version)));
        Assert.Equal("A", (await _env.Series.GetById(_env.Project, series.Id))!.Name);
    }

    [Fact]
    public async Task Find_by_guid_or_exact_prefix()
    {
        var upper = _env.AddSeries("TSK");
        var lower = _env.AddSeries("tsk");

        Assert.Equal(upper.Id, (await _env.SeriesSvc.Find(_env.Project, upper.Id.ToString()))!.Id);
        Assert.Equal(upper.Id, (await _env.SeriesSvc.Find(_env.Project, " TSK "))!.Id);
        Assert.Equal(lower.Id, (await _env.SeriesSvc.Find(_env.Project, "tsk"))!.Id);
        Assert.Null(await _env.SeriesSvc.Find(_env.Project, "Tsk"));
        Assert.Null(await _env.SeriesSvc.Find(_env.Project, Guid.NewGuid().ToString()));
        Assert.Null(await _env.SeriesSvc.Find(_env.Project, "TSK-5"));
        Assert.Null(await _env.SeriesSvc.Find(_env.Project, ""));
        Assert.Null(await _env.SeriesSvc.Find(Guid.NewGuid(), upper.Id.ToString()));
    }

    [Fact]
    public async Task Delete_missing_series_returns_false_and_checks_version()
    {
        var series = await _env.SeriesSvc.Create(_env.Project, new CreateSeries("A", "AAA"));

        Assert.False(await _env.SeriesSvc.Delete(_env.Project, Guid.NewGuid(), "x"));
        await Assert.ThrowsAsync<TaskerValidationException>(() => _env.SeriesSvc.Delete(_env.Project, series.Id, null));
        var stale = await Assert.ThrowsAsync<TaskerConflictException>(() => _env.SeriesSvc.Delete(_env.Project, series.Id, "stale"));
        Assert.Equal(ConflictCode.Modified, stale.Code);
        Assert.NotNull(await _env.Series.GetById(_env.Project, series.Id));

        Assert.True(await _env.SeriesSvc.Delete(_env.Project, series.Id, series.Version));
        Assert.Null(await _env.Series.GetById(_env.Project, series.Id));
    }

    [Fact]
    public async Task Delete_removes_references_from_tasks_and_keeps_other_numbers_in_order()
    {
        var a = _env.AddSeries("AAA");
        var b = _env.AddSeries("BBB");
        var c = _env.AddSeries("CCC");
        var gone = Guid.NewGuid();
        var onlyA = _env.Seed("only A", [(a.Id, 1)]);
        var both = _env.Seed("both", [(b.Id, 4), (a.Id, 2), (c.Id, 9)]);
        var onlyB = _env.Seed("only B", [(b.Id, 1)]);
        var dangling = _env.Seed("dangling", [(gone, 3), (a.Id, 3)]);
        var none = _env.Seed("none");
        _env.Clock.Now = SeriesEnv.Start.AddDays(1);

        Assert.True(await _env.SeriesSvc.Delete(_env.Project, a.Id, a.Version));

        Assert.Empty(_env.Get(onlyA.Id).SeriesNumbers);
        Assert.Equal([new TaskSeriesNumber(b.Id, 4), new TaskSeriesNumber(c.Id, 9)], _env.Get(both.Id).SeriesNumbers);
        Assert.Equal([new TaskSeriesNumber(gone, 3)], _env.Get(dangling.Id).SeriesNumbers);
        foreach (var changed in new[] { onlyA, both, dangling })
            Assert.Equal(SeriesEnv.Start.AddDays(1), _env.Get(changed.Id).UpdatedAt);
        foreach (var untouched in new[] { onlyB, none })
            Assert.Equal(untouched, _env.Get(untouched.Id));
        Assert.Equal(0, _env.Tasks.OutsideScope);
    }

    [Fact]
    public async Task Delete_series_removes_references_from_more_than_one_page_of_tasks()
    {
        var a = _env.AddSeries("AAA");
        var b = _env.AddSeries("BBB");
        for (var i = 1; i <= 450; i++)
            _env.Seed($"t{i}", [(a.Id, i), (b.Id, i)]);

        Assert.True(await _env.SeriesSvc.Delete(_env.Project, a.Id, a.Version));

        Assert.All(_env.Tasks.All(), t => Assert.Equal([b.Id], t.SeriesNumbers.Select(x => x.SeriesId)));
        Assert.Equal(450, _env.Tasks.UpdateCount);
    }

    [Fact]
    public async Task Delete_series_deletes_the_series_first_then_cleans_tasks()
    {
        var a = _env.AddSeries("AAA");
        _env.Seed("t", [(a.Id, 1)]);
        Series? seenDuringTaskUpdate = null;
        _env.Tasks.BeforeUpdate = _ => seenDuringTaskUpdate = _env.Series.GetById(_env.Project, a.Id).Result;

        await _env.SeriesSvc.Delete(_env.Project, a.Id, a.Version);

        Assert.Equal(1, _env.Tasks.UpdateCount);
        Assert.Null(seenDuringTaskUpdate);
    }

    [Fact]
    public async Task Delete_series_retries_task_that_was_modified_concurrently()
    {
        var a = _env.AddSeries("AAA");
        var task = _env.Seed("t", [(a.Id, 1)]);
        var touched = 0;
        _env.Tasks.BeforeUpdate = t =>
        {
            if (touched++ == 0)
                _env.Tasks.Touch(task.Id, x => x with { Title = "changed elsewhere" });
        };

        Assert.True(await _env.SeriesSvc.Delete(_env.Project, a.Id, a.Version));

        var result = _env.Get(task.Id);
        Assert.Empty(result.SeriesNumbers);
        Assert.Equal("changed elsewhere", result.Title);
    }

    [Fact]
    public async Task Delete_series_gives_up_on_permanently_contended_task_after_the_series_is_gone()
    {
        var a = _env.AddSeries("AAA");
        var task = _env.Seed("t", [(a.Id, 1)]);
        _env.Tasks.BeforeUpdate = _ => _env.Tasks.Touch(task.Id, x => x);

        var ex = await Assert.ThrowsAsync<TaskerConflictException>(() => _env.SeriesSvc.Delete(_env.Project, a.Id, a.Version));

        Assert.Equal(ConflictCode.Modified, ex.Code);
        Assert.Null(await _env.Series.GetById(_env.Project, a.Id));
        // Оставшуюся недействительную ссылку уберёт cleanup.
        Assert.Equal(1, (await _env.Health.Check(_env.Project)).TasksWithInvalidSeries);
    }

    [Fact]
    public async Task Delete_series_processes_each_task_once_when_the_index_lags()
    {
        var a = _env.AddSeries("AAA");
        for (var i = 1; i <= 250; i++)
            _env.Seed($"t{i}", [(a.Id, i)]);
        _env.Tasks.LaggingIndex = true;

        Assert.True(await _env.SeriesSvc.Delete(_env.Project, a.Id, a.Version));

        Assert.All(_env.Tasks.All(), t => Assert.Empty(t.SeriesNumbers));
        Assert.Equal(250, _env.Tasks.UpdateCount);
    }

    [Fact]
    public async Task Delete_series_collects_tasks_across_pages_in_a_stable_order()
    {
        var a = _env.AddSeries("AAA");
        for (var i = 1; i <= 430; i++)
            _env.Seed($"t{i}", [(a.Id, i)], SeriesEnv.Start.AddSeconds(i % 7));
        _env.Seed("other", [(_env.AddSeries("BBB").Id, 1)]);

        Assert.True(await _env.SeriesSvc.Delete(_env.Project, a.Id, a.Version));

        Assert.Equal(430, _env.Tasks.UpdateCount);
        Assert.Equal(0, _env.Tasks.All().Count(t => t.SeriesNumbers.Any(x => x.SeriesId == a.Id)));
    }

    [Fact]
    public async Task Delete_series_continues_after_one_task_fails_and_reports_the_first_failure()
    {
        var a = _env.AddSeries("AAA");
        var tasks = Enumerable.Range(1, 5).Select(i => _env.Seed($"t{i}", [(a.Id, i)])).ToArray();
        var bad = tasks[1];
        _env.Tasks.BeforeUpdate = t =>
        {
            if (t.Id == bad.Id)
                throw new IOException("disk");
        };

        await Assert.ThrowsAsync<IOException>(() => _env.SeriesSvc.Delete(_env.Project, a.Id, a.Version));

        Assert.Null(await _env.Series.GetById(_env.Project, a.Id));
        foreach (var t in tasks)
            Assert.Equal(t.Id == bad.Id ? 1 : 0, _env.Get(t.Id).SeriesNumbers.Count);
    }

    [Fact]
    public async Task Delete_series_skips_a_task_deleted_after_collecting()
    {
        var a = _env.AddSeries("AAA");
        var first = _env.Seed("first", [(a.Id, 1)], SeriesEnv.Start);
        var second = _env.Seed("second", [(a.Id, 2)], SeriesEnv.Start.AddSeconds(1));
        // Пока правится первая задача, вторую удаляют.
        _env.Tasks.BeforeUpdate = t =>
        {
            if (t.Id == first.Id)
                _env.Tasks.Delete(_env.Project, second.Id, _env.Get(second.Id).Version).Wait();
        };

        Assert.True(await _env.SeriesSvc.Delete(_env.Project, a.Id, a.Version));

        Assert.Empty(_env.Get(first.Id).SeriesNumbers);
        Assert.DoesNotContain(_env.Tasks.All(), t => t.Id == second.Id);
        Assert.Equal(1, _env.Tasks.UpdateCount);
    }

    [Fact]
    public async Task Delete_series_removes_only_that_series_from_a_task_in_two_series()
    {
        var a = _env.AddSeries("AAA");
        var b = _env.AddSeries("BBB");
        var task = _env.Seed("t", [(a.Id, 1), (b.Id, 7)]);
        _env.Tasks.LaggingIndex = true;

        Assert.True(await _env.SeriesSvc.Delete(_env.Project, a.Id, a.Version));

        Assert.Equal([new TaskSeriesNumber(b.Id, 7)], _env.Get(task.Id).SeriesNumbers);
        Assert.Equal(1, _env.Tasks.UpdateCount);
    }
}

public class TaskServiceSeriesTests
{
    private readonly SeriesEnv _env = new();

    private static (Guid, int)[] Sn(TaskItem t) => t.SeriesNumbers.Select(x => (x.SeriesId, x.Number)).ToArray();

    [Fact]
    public async Task Create_without_series_does_not_use_scope_and_has_no_numbers()
    {
        var task = await _env.TaskSvc.Create(_env.Project, new CreateTask("Plain", null, _env.TypeId, null));
        var withEmpty = await _env.TaskSvc.Create(_env.Project, new CreateTask("Empty", null, _env.TypeId, null, []));

        Assert.Empty(task.SeriesNumbers);
        Assert.Empty(withEmpty.SeriesNumbers);
        Assert.Equal(0, _env.Scope.Calls);
    }

    [Fact]
    public async Task Create_with_one_series_numbers_from_one()
    {
        var s = _env.AddSeries("TSK");

        var first = await _env.Create("a", s.Id);
        var second = await _env.Create("b", s.Id);

        Assert.Equal([(s.Id, 1)], Sn(first));
        Assert.Equal([(s.Id, 2)], Sn(second));
        Assert.Equal(Sn(second), Sn(_env.Get(second.Id)));
        Assert.Equal(_env.Get(second.Id).Version, second.Version);
    }

    [Fact]
    public async Task Create_with_many_series_gets_a_number_in_each_in_one_exclusive_section()
    {
        var a = _env.AddSeries("AAA");
        var b = _env.AddSeries("BBB");
        var c = _env.AddSeries("CCC");
        _env.Seed("old", [(b.Id, 7)]);

        var task = await _env.Create("t", a.Id, b.Id, c.Id);

        Assert.Equal([(a.Id, 1), (b.Id, 8), (c.Id, 1)], Sn(task));
        Assert.Equal(1, _env.Scope.Calls);
        Assert.Equal(0, _env.Tasks.OutsideScope);
    }

    [Fact]
    public async Task Create_takes_max_plus_one_with_gaps_and_ignores_other_series_and_dangling_refs()
    {
        var s = _env.AddSeries("TSK");
        var other = _env.AddSeries("OTH");
        _env.Seed("one", [(s.Id, 1)]);
        _env.Seed("five", [(s.Id, 5)]);
        _env.Seed("dangling", [(Guid.NewGuid(), 99)]);
        _env.Seed("other", [(other.Id, 50)]);

        var task = await _env.Create("t", s.Id);

        Assert.Equal([(s.Id, 6)], Sn(task));
    }

    [Fact]
    public async Task Create_reuses_the_number_of_a_deleted_top_task()
    {
        var s = _env.AddSeries("TSK");
        await _env.Create("a", s.Id);
        var top = await _env.Create("b", s.Id);
        await _env.TaskSvc.Delete(_env.Project, top.Id, top.Version);

        Assert.Equal([(s.Id, 2)], Sn(await _env.Create("c", s.Id)));
    }

    [Fact]
    public async Task Create_with_missing_series_writes_nothing()
    {
        var good = _env.AddSeries("TSK");

        var ex = await Assert.ThrowsAsync<TaskerValidationException>(() => _env.Create("t", good.Id, Guid.NewGuid()));

        Assert.Contains("SeriesIds", ex.Message);
        Assert.Equal(0, _env.Tasks.WriteCount);
        Assert.Empty(_env.Tasks.All());
    }

    [Fact]
    public async Task Create_with_series_of_another_project_is_rejected()
    {
        var foreign = _env.Series.Seed(Guid.NewGuid(), "FOR");

        await Assert.ThrowsAsync<TaskerValidationException>(() => _env.Create("t", foreign.Id));
        Assert.Equal(0, _env.Tasks.WriteCount);
    }

    [Fact]
    public async Task Create_with_duplicate_series_id_writes_nothing()
    {
        var s = _env.AddSeries("TSK");

        await Assert.ThrowsAsync<TaskerValidationException>(() => _env.Create("t", s.Id, s.Id));
        Assert.Equal(0, _env.Tasks.WriteCount);
    }

    [Fact]
    public async Task Create_with_invalid_title_type_or_status_and_series_writes_nothing_and_takes_no_lock()
    {
        var s = _env.AddSeries("TSK");

        await Assert.ThrowsAsync<TaskerValidationException>(() => _env.TaskSvc.Create(_env.Project, new CreateTask(" ", null, _env.TypeId, null, [s.Id])));
        await Assert.ThrowsAsync<TaskerValidationException>(() => _env.TaskSvc.Create(_env.Project, new CreateTask("t", null, Guid.NewGuid(), null, [s.Id])));
        await Assert.ThrowsAsync<TaskerValidationException>(() => _env.TaskSvc.Create(_env.Project, new CreateTask("t", null, _env.TypeId, Guid.NewGuid(), [s.Id])));

        Assert.Equal(0, _env.Tasks.WriteCount);
        Assert.Equal(0, _env.Scope.Calls);
    }

    [Fact]
    public async Task Update_and_delete_keep_working_and_update_keeps_series_numbers()
    {
        var s = _env.AddSeries("TSK");
        var task = await _env.Create("t", s.Id);

        var updated = await _env.TaskSvc.Update(_env.Project, task.Id, new UpdateTask("New title", "d", null, null, task.Version));

        Assert.Equal([(s.Id, 1)], Sn(updated!));
        Assert.Equal([(s.Id, 1)], Sn(_env.Get(task.Id)));
        Assert.True(await _env.TaskSvc.Delete(_env.Project, task.Id, updated!.Version));
    }

    [Fact]
    public async Task Parallel_creates_get_unique_consecutive_numbers_inside_the_scope()
    {
        var a = _env.AddSeries("AAA");
        var b = _env.AddSeries("BBB");

        var created = await Task.WhenAll(Enumerable.Range(0, 60).Select(i => Task.Run(() => _env.Create($"t{i}", a.Id, b.Id))));

        Assert.Equal(Enumerable.Range(1, 60), created.Select(x => x.SeriesNumbers.Single(n => n.SeriesId == a.Id).Number).Order());
        Assert.Equal(Enumerable.Range(1, 60), created.Select(x => x.SeriesNumbers.Single(n => n.SeriesId == b.Id).Number).Order());
        Assert.Equal(1, _env.Scope.MaxConcurrent);
        Assert.Equal(0, _env.Tasks.OutsideScope);
        Assert.Empty(await _env.Tasks.GetNumberConflicts(_env.Project));
    }

    [Fact]
    public async Task Parallel_add_to_series_gets_unique_numbers()
    {
        var s = _env.AddSeries("TSK");
        var tasks = Enumerable.Range(0, 30).Select(i => _env.Seed($"t{i}")).ToArray();

        await Task.WhenAll(tasks.Select(t => Task.Run(() => _env.TaskSvc.AddToSeries(_env.Project, t.Id, s.Id, t.Version))));

        Assert.Equal(Enumerable.Range(1, 30), _env.Tasks.All().Select(x => x.SeriesNumbers.Single().Number).Order());
        Assert.Equal(0, _env.Tasks.OutsideScope);
    }

    // ---- AddToSeries ----

    [Fact]
    public async Task AddToSeries_appends_next_number_and_keeps_existing_order()
    {
        var a = _env.AddSeries("AAA");
        var b = _env.AddSeries("BBB");
        _env.Seed("x", [(b.Id, 10)]);
        var task = _env.Seed("t", [(b.Id, 3)]);
        _env.Clock.Now = SeriesEnv.Start.AddHours(5);

        var result = await _env.TaskSvc.AddToSeries(_env.Project, task.Id, a.Id, task.Version);

        Assert.Equal([(b.Id, 3), (a.Id, 1)], Sn(result!));
        Assert.Equal(SeriesEnv.Start.AddHours(5), result!.UpdatedAt);
        Assert.Equal(result, _env.Get(task.Id));
        Assert.Equal(0, _env.Tasks.OutsideScope);
        Assert.NotEqual(task.Version, result.Version);
    }

    [Fact]
    public async Task AddToSeries_when_already_member_returns_task_unchanged_without_writing()
    {
        var s = _env.AddSeries("TSK");
        var task = _env.Seed("t", [(s.Id, 4)]);

        var result = await _env.TaskSvc.AddToSeries(_env.Project, task.Id, s.Id, task.Version);

        Assert.Equal(task, result);
        Assert.Equal(0, _env.Tasks.WriteCount);
    }

    [Fact]
    public async Task AddToSeries_missing_task_or_series_gives_null_and_bad_version_is_rejected()
    {
        var s = _env.AddSeries("TSK");
        var task = _env.Seed("t");

        Assert.Null(await _env.TaskSvc.AddToSeries(_env.Project, Guid.NewGuid(), s.Id, "x"));
        Assert.Null(await _env.TaskSvc.AddToSeries(_env.Project, task.Id, Guid.NewGuid(), task.Version));
        await Assert.ThrowsAsync<TaskerValidationException>(() => _env.TaskSvc.AddToSeries(_env.Project, task.Id, s.Id, null));
        var ex = await Assert.ThrowsAsync<TaskerConflictException>(() => _env.TaskSvc.AddToSeries(_env.Project, task.Id, s.Id, "stale"));
        Assert.Equal(ConflictCode.Modified, ex.Code);
        Assert.Equal(0, _env.Tasks.WriteCount);
    }

    [Fact]
    public async Task AddToSeries_reports_modified_when_task_changes_between_check_and_write()
    {
        var s = _env.AddSeries("TSK");
        var task = _env.Seed("t");
        _env.Tasks.BeforeUpdate = _ => _env.Tasks.Touch(task.Id, x => x);

        var ex = await Assert.ThrowsAsync<TaskerConflictException>(() => _env.TaskSvc.AddToSeries(_env.Project, task.Id, s.Id, task.Version));

        Assert.Equal(ConflictCode.Modified, ex.Code);
    }

    // ---- RemoveFromSeries ----

    [Fact]
    public async Task RemoveFromSeries_removes_only_that_series_and_frees_the_number()
    {
        var a = _env.AddSeries("AAA");
        var b = _env.AddSeries("BBB");
        var c = _env.AddSeries("CCC");
        var task = _env.Seed("t", [(a.Id, 1), (b.Id, 2), (c.Id, 3)]);
        _env.Clock.Now = SeriesEnv.Start.AddHours(1);

        var result = await _env.TaskSvc.RemoveFromSeries(_env.Project, task.Id, b.Id, task.Version);

        Assert.Equal([(a.Id, 1), (c.Id, 3)], Sn(result!));
        Assert.Equal(SeriesEnv.Start.AddHours(1), result!.UpdatedAt);
        Assert.Equal(result, _env.Get(task.Id));
        Assert.Equal(0, _env.Tasks.OutsideScope);
        Assert.DoesNotContain(_env.Tasks.All().SelectMany(x => x.SeriesNumbers), x => x.SeriesId == b.Id);
    }

    [Fact]
    public async Task RemoveFromSeries_not_a_member_returns_task_as_is_missing_task_is_null_and_version_is_checked()
    {
        var s = _env.AddSeries("TSK");
        var task = _env.Seed("t");

        Assert.Equal(task, await _env.TaskSvc.RemoveFromSeries(_env.Project, task.Id, s.Id, task.Version));
        Assert.Equal(0, _env.Tasks.WriteCount);
        Assert.Null(await _env.TaskSvc.RemoveFromSeries(_env.Project, Guid.NewGuid(), s.Id, "x"));
        await Assert.ThrowsAsync<TaskerValidationException>(() => _env.TaskSvc.RemoveFromSeries(_env.Project, task.Id, s.Id, ""));
        await Assert.ThrowsAsync<TaskerConflictException>(() => _env.TaskSvc.RemoveFromSeries(_env.Project, task.Id, s.Id, "stale"));
    }

    [Fact]
    public async Task RemoveFromSeries_also_removes_a_dangling_reference()
    {
        var gone = Guid.NewGuid();
        var task = _env.Seed("t", [(gone, 2)]);

        var result = await _env.TaskSvc.RemoveFromSeries(_env.Project, task.Id, gone, task.Version);

        Assert.Empty(result!.SeriesNumbers);
    }

    // ---- RemoveFromSeriesByNumber ----

    [Fact]
    public async Task RemoveFromSeriesByNumber_removes_the_single_holder()
    {
        var a = _env.AddSeries("AAA");
        var b = _env.AddSeries("BBB");
        var t1 = _env.Seed("one", [(a.Id, 1), (b.Id, 1)]);
        _env.Seed("two", [(a.Id, 2)]);

        var result = await _env.TaskSvc.RemoveFromSeriesByNumber(_env.Project, a.Id, 1);

        Assert.Equal(t1.Id, result!.Id);
        Assert.Equal([(b.Id, 1)], Sn(result));
        Assert.Equal(result, _env.Get(t1.Id));
        Assert.Equal(0, _env.Tasks.OutsideScope);
    }

    [Fact]
    public async Task RemoveFromSeriesByNumber_without_holder_is_null()
    {
        var a = _env.AddSeries("AAA");
        _env.Seed("one", [(a.Id, 1)]);

        Assert.Null(await _env.TaskSvc.RemoveFromSeriesByNumber(_env.Project, a.Id, 2));
        Assert.Equal(0, _env.Tasks.WriteCount);
    }

    [Fact]
    public async Task RemoveFromSeriesByNumber_with_duplicates_lists_ids_and_changes_nothing()
    {
        var a = _env.AddSeries("AAA");
        var t1 = _env.Seed("one", [(a.Id, 1)]);
        var t2 = _env.Seed("two", [(a.Id, 1)]);

        var ex = await Assert.ThrowsAsync<TaskerValidationException>(() => _env.TaskSvc.RemoveFromSeriesByNumber(_env.Project, a.Id, 1));

        Assert.Contains(t1.Id.ToString(), ex.Message);
        Assert.Contains(t2.Id.ToString(), ex.Message);
        Assert.Equal(0, _env.Tasks.WriteCount);
    }

    // ---- Renumber ----

    [Fact]
    public async Task Renumber_to_free_number_keeps_position_of_other_series()
    {
        var a = _env.AddSeries("AAA");
        var b = _env.AddSeries("BBB");
        var task = _env.Seed("t", [(a.Id, 1), (b.Id, 2)]);
        _env.Clock.Now = SeriesEnv.Start.AddHours(2);

        var result = await _env.TaskSvc.Renumber(_env.Project, task.Id, a.Id, 40, task.Version);

        Assert.Equal([(a.Id, 40), (b.Id, 2)], Sn(result!));
        Assert.Equal(SeriesEnv.Start.AddHours(2), result!.UpdatedAt);
        Assert.Equal(result, _env.Get(task.Id));
        Assert.Equal(0, _env.Tasks.OutsideScope);
    }

    [Fact]
    public async Task Renumber_to_taken_number_conflicts_including_when_it_is_a_duplicate_of_own_number()
    {
        var a = _env.AddSeries("AAA");
        var t1 = _env.Seed("one", [(a.Id, 1)]);
        var t2 = _env.Seed("two", [(a.Id, 2)]);
        var t3 = _env.Seed("dup of two", [(a.Id, 2)]);

        await Assert.ThrowsAsync<TaskerConflictException>(() => _env.TaskSvc.Renumber(_env.Project, t2.Id, a.Id, 1, t2.Version));
        await Assert.ThrowsAsync<TaskerConflictException>(() => _env.TaskSvc.Renumber(_env.Project, t3.Id, a.Id, 2, t3.Version));
        Assert.Equal(0, _env.Tasks.WriteCount);
        Assert.Equal(t1, _env.Get(t1.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Renumber_to_less_than_one_conflicts(int to)
    {
        var a = _env.AddSeries("AAA");
        var task = _env.Seed("t", [(a.Id, 1)]);

        await Assert.ThrowsAsync<TaskerConflictException>(() => _env.TaskSvc.Renumber(_env.Project, task.Id, a.Id, to, task.Version));
    }

    [Fact]
    public async Task Renumber_to_own_number_is_a_noop()
    {
        var a = _env.AddSeries("AAA");
        var task = _env.Seed("t", [(a.Id, 3)]);

        Assert.Equal(task, await _env.TaskSvc.Renumber(_env.Project, task.Id, a.Id, 3, task.Version));
        Assert.Equal(0, _env.Tasks.WriteCount);
    }

    [Fact]
    public async Task Renumber_without_target_takes_max_plus_one()
    {
        var a = _env.AddSeries("AAA");
        var task = _env.Seed("t", [(a.Id, 1)]);
        _env.Seed("other", [(a.Id, 8)]);

        var result = await _env.TaskSvc.Renumber(_env.Project, task.Id, a.Id, null, task.Version);

        Assert.Equal([(a.Id, 9)], Sn(result!));
    }

    [Fact]
    public async Task Renumber_returns_null_for_missing_task_series_or_membership_and_checks_version()
    {
        var a = _env.AddSeries("AAA");
        var b = _env.AddSeries("BBB");
        var task = _env.Seed("t", [(a.Id, 1)]);

        Assert.Null(await _env.TaskSvc.Renumber(_env.Project, Guid.NewGuid(), a.Id, 5, "x"));
        Assert.Null(await _env.TaskSvc.Renumber(_env.Project, task.Id, Guid.NewGuid(), 5, task.Version));
        Assert.Null(await _env.TaskSvc.Renumber(_env.Project, task.Id, b.Id, 5, task.Version));
        await Assert.ThrowsAsync<TaskerValidationException>(() => _env.TaskSvc.Renumber(_env.Project, task.Id, a.Id, 5, null));
        var ex = await Assert.ThrowsAsync<TaskerConflictException>(() => _env.TaskSvc.Renumber(_env.Project, task.Id, a.Id, 5, "stale"));
        Assert.Equal(ConflictCode.Modified, ex.Code);
        Assert.Equal(0, _env.Tasks.WriteCount);
    }

    // ---- Resolve ----

    [Fact]
    public async Task Resolve_by_guid()
    {
        var task = _env.Seed("t");

        Assert.Equal([task], await _env.TaskSvc.Resolve(_env.Project, task.Id.ToString()));
        Assert.Empty(await _env.TaskSvc.Resolve(_env.Project, Guid.NewGuid().ToString()));
        Assert.Empty(await _env.TaskSvc.Resolve(Guid.NewGuid(), task.Id.ToString()));
    }

    [Fact]
    public async Task Resolve_by_prefix_and_number_is_case_sensitive_and_series_specific()
    {
        var upper = _env.AddSeries("TSK");
        var lower = _env.AddSeries("tsk");
        var inUpper = _env.Seed("upper", [(upper.Id, 5)]);
        var inLower = _env.Seed("lower", [(lower.Id, 5)]);

        Assert.Equal([inUpper], await _env.TaskSvc.Resolve(_env.Project, "TSK-5"));
        Assert.Equal([inLower], await _env.TaskSvc.Resolve(_env.Project, " tsk-5 "));
        Assert.Empty(await _env.TaskSvc.Resolve(_env.Project, "Tsk-5"));
        Assert.Empty(await _env.TaskSvc.Resolve(_env.Project, "TSK-6"));
        Assert.Empty(await _env.TaskSvc.Resolve(_env.Project, "NOPE-5"));
    }

    [Fact]
    public async Task Resolve_returns_all_duplicates_ordered_by_creation()
    {
        var s = _env.AddSeries("TSK");
        var late = _env.Seed("late", [(s.Id, 2)], createdAt: SeriesEnv.Start.AddDays(2));
        var early = _env.Seed("early", [(s.Id, 2)], createdAt: SeriesEnv.Start.AddDays(1));

        Assert.Equal([early.Id, late.Id], (await _env.TaskSvc.Resolve(_env.Project, "TSK-2")).Select(x => x.Id));
    }

    [Fact]
    public async Task Resolve_looks_in_all_series_sharing_a_prefix()
    {
        var s1 = _env.AddSeries("TSK");
        var s2 = _env.AddSeries("TSK");
        var t1 = _env.Seed("a", [(s1.Id, 1)]);
        var t2 = _env.Seed("b", [(s2.Id, 1)]);

        Assert.Equal(new[] { t1.Id, t2.Id }.Order(), (await _env.TaskSvc.Resolve(_env.Project, "TSK-1")).Select(x => x.Id).Order());
    }

    [Fact]
    public async Task Resolve_garbage_is_a_validation_error()
    {
        await Assert.ThrowsAsync<TaskerValidationException>(() => _env.TaskSvc.Resolve(_env.Project, "hello"));
    }
}

public class SeriesHealthTests
{
    private readonly SeriesEnv _env = new();

    [Fact]
    public async Task Clean_project_needs_no_attention()
    {
        var s = _env.AddSeries("TSK");
        _env.Seed("t", [(s.Id, 1)]);

        var health = await _env.Health.Check(_env.Project);

        Assert.False(health.NeedsAttention);
        Assert.Empty(health.NumberConflicts);
        Assert.Empty(health.PrefixConflicts);
        Assert.Equal(0, health.TasksWithInvalidSeries);
        Assert.Equal(0, health.UnreadableSeriesFiles);
    }

    [Fact]
    public async Task All_four_counters_are_reported_and_nothing_is_written()
    {
        var s = _env.AddSeries("TSK");
        var d1 = _env.AddSeries("DUP");
        var d2 = _env.AddSeries("DUP");
        var t1 = _env.Seed("a", [(s.Id, 1)]);
        var t2 = _env.Seed("b", [(s.Id, 1)]);
        _env.Seed("c", [(Guid.NewGuid(), 4)]);
        _env.Seed("d", [(s.Id, 2), (Guid.NewGuid(), 1)]);
        _env.Series.Unreadable = 0;

        var health = await _env.Health.Check(_env.Project);

        Assert.True(health.NeedsAttention);
        Assert.Equal(Show([new NumberConflict(s.Id, 1, [t1.Id, t2.Id])]), Show(health.NumberConflicts));
        Assert.Equal(Show([new PrefixConflict("DUP", new[] { d1.Id, d2.Id }.Order().ToArray())]), Show(health.PrefixConflicts));
        Assert.Equal(2, health.TasksWithInvalidSeries);
        Assert.Equal(0, health.UnreadableSeriesFiles);
        Assert.Equal(0, _env.Tasks.WriteCount);
    }

    [Fact]
    public async Task Prefix_conflicts_are_exact_and_sorted()
    {
        var upper1 = _env.AddSeries("TSK");
        var upper2 = _env.AddSeries("TSK");
        _env.AddSeries("tsk");
        var b1 = _env.AddSeries("AB");
        var b2 = _env.AddSeries("AB");
        var b3 = _env.AddSeries("AB");

        var conflicts = (await _env.Health.Check(_env.Project)).PrefixConflicts;

        Assert.Equal(["AB", "TSK"], conflicts.Select(x => x.Prefix));
        Assert.Equal(new[] { b1.Id, b2.Id, b3.Id }.Order(), conflicts[0].SeriesIds);
        Assert.Equal(new[] { upper1.Id, upper2.Id }.Order(), conflicts[1].SeriesIds);
    }

    [Fact]
    public async Task Unreadable_series_files_are_counted_and_hide_invalid_references()
    {
        _env.Seed("a", [(Guid.NewGuid(), 1)]);
        _env.Series.Unreadable = 2;

        var health = await _env.Health.Check(_env.Project);

        Assert.Equal(2, health.UnreadableSeriesFiles);
        Assert.Equal(0, health.TasksWithInvalidSeries);
        Assert.True(health.NeedsAttention);
    }
}

public class SeriesCleanupTests
{
    private readonly SeriesEnv _env = new();

    private static Guid G(int n) => new(n, 0, 0, new byte[8]);

    private static (Guid, int)[] Sn(TaskItem t) => t.SeriesNumbers.Select(x => (x.SeriesId, x.Number)).ToArray();

    [Fact]
    public async Task Nothing_to_do_changes_nothing()
    {
        var s = _env.AddSeries("TSK");
        _env.Seed("t", [(s.Id, 1)]);

        var report = await _env.Cleanup.Run(_env.Project, new CleanupOptions(ResolveConflicts: true));

        Assert.Empty(report.Changes);
        Assert.Empty(report.RemainingNumberConflicts);
        Assert.Empty(report.PrefixConflicts);
        Assert.False(report.Skipped);
        Assert.Null(report.SkipReason);
        Assert.Equal(0, _env.Tasks.WriteCount);
    }

    [Fact]
    public async Task Removes_invalid_references_keeps_valid_ones_and_describes_them()
    {
        var s = _env.AddSeries("TSK");
        var gone = Guid.NewGuid();
        var task = _env.Seed("Fix login", [(s.Id, 1), (gone, 5)]);
        _env.Clock.Now = SeriesEnv.Start.AddDays(3);

        var report = await _env.Cleanup.Run(_env.Project, new CleanupOptions());

        var change = Assert.Single(report.Changes);
        Assert.Equal(new CleanupChange(task.Id, "Fix login", CleanupChangeKind.RemovedInvalidSeries, gone, 5, null,
            "Task 'Fix login': removed invalid series reference (was #5)"), change);
        var stored = _env.Get(task.Id);
        Assert.Equal([(s.Id, 1)], Sn(stored));
        Assert.Equal(SeriesEnv.Start.AddDays(3), stored.UpdatedAt);
        Assert.Equal(1, _env.Tasks.UpdateCount);
        Assert.Equal(0, _env.Tasks.OutsideScope);
        Assert.Equal(1, _env.Scope.Calls);
    }

    [Fact]
    public async Task Several_invalid_references_on_one_task_give_one_change_each_and_one_write()
    {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var task = _env.Seed("t", [(g1, 1), (g2, 2)]);

        var report = await _env.Cleanup.Run(_env.Project, new CleanupOptions());

        Assert.Equal([(g1, 1), (g2, 2)], report.Changes.Select(x => (x.SeriesId, x.OldNumber!.Value)));
        Assert.All(report.Changes, c => Assert.Equal(CleanupChangeKind.RemovedInvalidSeries, c.Kind));
        Assert.Empty(_env.Get(task.Id).SeriesNumbers);
        Assert.Equal(1, _env.Tasks.UpdateCount);
    }

    [Fact]
    public async Task Unreadable_series_skip_everything_but_still_report_conflicts()
    {
        var s = _env.AddSeries("TSK");
        var t1 = _env.Seed("a", [(s.Id, 1), (Guid.NewGuid(), 3)], createdAt: SeriesEnv.Start.AddDays(1));
        var t2 = _env.Seed("b", [(s.Id, 1)], createdAt: SeriesEnv.Start.AddDays(2));
        _env.Series.Unreadable = 1;

        var report = await _env.Cleanup.Run(_env.Project, new CleanupOptions(ResolveConflicts: true));

        Assert.True(report.Skipped);
        Assert.False(string.IsNullOrWhiteSpace(report.SkipReason));
        Assert.Empty(report.Changes);
        Assert.Equal(Show([new NumberConflict(s.Id, 1, [t1.Id, t2.Id])]), Show(report.RemainingNumberConflicts));
        Assert.Equal(0, _env.Tasks.WriteCount);
        Assert.Equal(0, _env.Series.WriteCount);
    }

    [Fact]
    public async Task Prefix_conflicts_are_always_reported()
    {
        var a = _env.AddSeries("DUP");
        var b = _env.AddSeries("DUP");
        var expected = new PrefixConflict("DUP", new[] { a.Id, b.Id }.Order().ToArray());

        Assert.Equal(Show([expected]), Show((await _env.Cleanup.Run(_env.Project, new CleanupOptions())).PrefixConflicts));
        Assert.Equal(Show([expected]), Show((await _env.Cleanup.Run(_env.Project, new CleanupOptions(true, true))).PrefixConflicts));
        _env.Series.Unreadable = 1;
        Assert.Equal(Show([expected]), Show((await _env.Cleanup.Run(_env.Project, new CleanupOptions())).PrefixConflicts));
    }

    [Fact]
    public async Task Conflicts_are_left_alone_without_resolve_conflicts()
    {
        var s = _env.AddSeries("TSK");
        var t1 = _env.Seed("a", [(s.Id, 1)]);
        var t2 = _env.Seed("b", [(s.Id, 1)]);

        var report = await _env.Cleanup.Run(_env.Project, new CleanupOptions());

        Assert.Empty(report.Changes);
        Assert.Equal(Show([new NumberConflict(s.Id, 1, [t1.Id, t2.Id])]), Show(report.RemainingNumberConflicts));
        Assert.Equal(0, _env.Tasks.WriteCount);
    }

    [Fact]
    public async Task Two_conflicting_tasks_the_earliest_keeps_the_number()
    {
        var s = _env.AddSeries("TSK");
        var later = _env.Seed("later", [(s.Id, 3)], createdAt: SeriesEnv.Start.AddDays(2));
        var earlier = _env.Seed("earlier", [(s.Id, 3)], createdAt: SeriesEnv.Start.AddDays(1));
        _env.Seed("top", [(s.Id, 7)]);

        var report = await _env.Cleanup.Run(_env.Project, new CleanupOptions(ResolveConflicts: true));

        var change = Assert.Single(report.Changes);
        Assert.Equal(new CleanupChange(later.Id, "later", CleanupChangeKind.Renumbered, s.Id, 3, 8,
            "Task 'later': duplicate number TSK-3 changed to TSK-8"), change);
        Assert.Equal([(s.Id, 3)], Sn(_env.Get(earlier.Id)));
        Assert.Equal([(s.Id, 8)], Sn(_env.Get(later.Id)));
        Assert.Empty(report.RemainingNumberConflicts);
        Assert.Empty(await _env.Tasks.GetNumberConflicts(_env.Project));
        Assert.Equal(0, _env.Tasks.OutsideScope);
    }

    [Fact]
    public async Task Three_tasks_with_equal_created_at_are_ordered_by_guid()
    {
        var s = _env.AddSeries("TSK");
        var when = SeriesEnv.Start.AddDays(1);
        // Заводим не по порядку Guid.
        var third = _env.Seed("third", [(s.Id, 2)], when, G(3));
        var first = _env.Seed("first", [(s.Id, 2)], when, G(1));
        var second = _env.Seed("second", [(s.Id, 2)], when, G(2));
        _env.Seed("top", [(s.Id, 10)]);

        var report = await _env.Cleanup.Run(_env.Project, new CleanupOptions(ResolveConflicts: true));

        Assert.Equal([(second.Id, 2, 11), (third.Id, 2, 12)], report.Changes.Select(x => (x.TaskId, x.OldNumber!.Value, x.NewNumber!.Value)));
        Assert.Equal([(s.Id, 2)], Sn(_env.Get(first.Id)));
        Assert.Equal([(s.Id, 11)], Sn(_env.Get(second.Id)));
        Assert.Equal([(s.Id, 12)], Sn(_env.Get(third.Id)));
    }

    [Fact]
    public async Task Several_series_conflicts_invalid_references_and_a_task_in_two_groups_at_once()
    {
        var a = _env.AddSeries("AAA");
        var b = _env.AddSeries("BBB");
        var gone = Guid.NewGuid();
        var a1 = _env.Seed("a1", [(a.Id, 1), (b.Id, 1)]);
        var a2 = _env.Seed("a2", [(a.Id, 1), (b.Id, 1), (gone, 9)]);
        var a3 = _env.Seed("a3", [(a.Id, 1)]);
        var b2 = _env.Seed("b2", [(b.Id, 5)]);

        var report = await _env.Cleanup.Run(_env.Project, new CleanupOptions(ResolveConflicts: true));

        Assert.Equal(1, report.Changes.Count(x => x.Kind == CleanupChangeKind.RemovedInvalidSeries));
        Assert.Equal([(a.Id, 1)], Sn(_env.Get(a1.Id)).Where(x => x.Item1 == a.Id));
        Assert.Equal([(a.Id, 2), (b.Id, 6)], Sn(_env.Get(a2.Id)));
        Assert.Equal([(a.Id, 3)], Sn(_env.Get(a3.Id)));
        Assert.Equal([(b.Id, 5)], Sn(_env.Get(b2.Id)));
        Assert.Empty(await _env.Tasks.GetNumberConflicts(_env.Project));
        Assert.Equal(0, (await _env.Health.Check(_env.Project)).TasksWithInvalidSeries);
        Assert.Equal(3, report.Changes.Count(x => x.Kind == CleanupChangeKind.Renumbered));
    }

    [Fact]
    public async Task Duplicates_inside_a_nonexistent_series_are_not_renumbered_only_their_references_are_removed()
    {
        var gone = Guid.NewGuid();
        _env.Seed("a", [(gone, 1)]);
        _env.Seed("b", [(gone, 1)]);

        foreach (var dry in new[] { true, false })
        {
            var report = await _env.Cleanup.Run(_env.Project, new CleanupOptions(ResolveConflicts: true, DryRun: dry));

            Assert.All(report.Changes, c => Assert.Equal(CleanupChangeKind.RemovedInvalidSeries, c.Kind));
            Assert.Equal(2, report.Changes.Length);
            Assert.Empty(report.RemainingNumberConflicts);
        }
    }

    [Fact]
    public async Task Dry_run_writes_nothing_and_matches_the_real_run()
    {
        var a = _env.AddSeries("AAA");
        var b = _env.AddSeries("BBB");
        var gone = Guid.NewGuid();
        _env.Seed("x", [(a.Id, 1), (gone, 2)]);
        _env.Seed("y", [(a.Id, 1), (b.Id, 4)]);
        _env.Seed("z", [(a.Id, 1), (b.Id, 4)]);
        _env.Seed("w", [(b.Id, 4)]);
        var options = new CleanupOptions(ResolveConflicts: true);
        var before = _env.Tasks.All();

        var dry = await _env.Cleanup.Run(_env.Project, options with { DryRun = true });

        Assert.Equal(0, _env.Tasks.WriteCount);
        Assert.Equal(before, _env.Tasks.All());

        var real = await _env.Cleanup.Run(_env.Project, options);

        Assert.Equal(dry.Changes, real.Changes);
        Assert.Equal(Show(dry.RemainingNumberConflicts), Show(real.RemainingNumberConflicts));
        Assert.NotEmpty(real.Changes);
        Assert.NotEqual(before, _env.Tasks.All());
        Assert.False((await _env.Health.Check(_env.Project)).NeedsAttention);
    }

    [Fact]
    public async Task Dry_run_without_resolve_reports_conflicts_of_valid_series_only()
    {
        var s = _env.AddSeries("TSK");
        var gone = Guid.NewGuid();
        var t1 = _env.Seed("a", [(s.Id, 1), (gone, 1)]);
        var t2 = _env.Seed("b", [(s.Id, 1), (gone, 1)]);

        var dry = await _env.Cleanup.Run(_env.Project, new CleanupOptions(DryRun: true));
        var real = await _env.Cleanup.Run(_env.Project, new CleanupOptions());

        Assert.Equal(Show([new NumberConflict(s.Id, 1, [t1.Id, t2.Id])]), Show(dry.RemainingNumberConflicts));
        Assert.Equal(Show(dry.RemainingNumberConflicts), Show(real.RemainingNumberConflicts));
        Assert.Equal(dry.Changes, real.Changes);
    }

    [Fact]
    public async Task Second_run_changes_nothing()
    {
        var s = _env.AddSeries("TSK");
        _env.Seed("a", [(s.Id, 1), (Guid.NewGuid(), 4)]);
        _env.Seed("b", [(s.Id, 1)]);
        _env.Seed("c", [(s.Id, 1)]);
        var options = new CleanupOptions(ResolveConflicts: true);

        var first = await _env.Cleanup.Run(_env.Project, options);
        var snapshot = _env.Tasks.All();
        var writes = _env.Tasks.WriteCount;
        var second = await _env.Cleanup.Run(_env.Project, options);

        Assert.Equal(3, first.Changes.Length);
        Assert.Empty(second.Changes);
        Assert.Equal(writes, _env.Tasks.WriteCount);
        Assert.Equal(snapshot, _env.Tasks.All());
    }

    [Fact]
    public async Task Version_conflict_is_retried_once_after_rereading_the_task()
    {
        var gone = Guid.NewGuid();
        var task = _env.Seed("t", [(gone, 1)]);
        var touched = 0;
        _env.Tasks.BeforeUpdate = _ =>
        {
            if (touched++ == 0)
                _env.Tasks.Touch(task.Id, x => x with { Title = "t2" });
        };

        var report = await _env.Cleanup.Run(_env.Project, new CleanupOptions());

        Assert.Single(report.Changes);
        Assert.Empty(_env.Get(task.Id).SeriesNumbers);
        Assert.Equal("t2", _env.Get(task.Id).Title);
    }

    [Fact]
    public async Task Permanent_version_conflict_is_reported_as_conflict_exception()
    {
        var task = _env.Seed("t", [(Guid.NewGuid(), 1)]);
        _env.Tasks.BeforeUpdate = _ => _env.Tasks.Touch(task.Id, x => x);

        var ex = await Assert.ThrowsAsync<TaskerConflictException>(() => _env.Cleanup.Run(_env.Project, new CleanupOptions()));

        Assert.Equal(ConflictCode.Modified, ex.Code);
    }

    [Fact]
    public async Task Renumbering_conflict_retries_with_reread_task()
    {
        var s = _env.AddSeries("TSK");
        var keeper = _env.Seed("keeper", [(s.Id, 1)]);
        var other = _env.Seed("other", [(s.Id, 1)]);
        var touched = 0;
        _env.Tasks.BeforeUpdate = _ =>
        {
            if (touched++ == 0)
                _env.Tasks.Touch(other.Id, x => x with { Title = "renamed meanwhile" });
        };

        var report = await _env.Cleanup.Run(_env.Project, new CleanupOptions(ResolveConflicts: true));

        Assert.Single(report.Changes);
        Assert.Equal([(s.Id, 2)], Sn(_env.Get(other.Id)));
        Assert.Equal("renamed meanwhile", _env.Get(other.Id).Title);
        Assert.Equal([(s.Id, 1)], Sn(_env.Get(keeper.Id)));
    }
}
