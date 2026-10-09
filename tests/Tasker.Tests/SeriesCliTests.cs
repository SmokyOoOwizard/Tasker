using System.Text.Json.Nodes;
using Tasker.Core;
using Xunit;

namespace Tasker.Tests;

/// <summary>Команды <c>tasker series</c>, <c>task --series</c> и ссылки <c>TSK-5</c> — на папке с файлами и на SQLite.</summary>
[InProcess]
public class SeriesCliTests
{
    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    /// <summary>Проект Demo с типом Bug (статусы Todo, Done).</summary>
    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.InProject("Demo", "status", "create", "Todo"));
        await Ok(ws.InProject("Demo", "status", "create", "Done"));
        await Ok(ws.InProject("Demo", "status-set", "create", "Flow", "--status", "Todo", "Done"));
        await Ok(ws.InProject("Demo", "task-type", "create", "Bug", "--status-set", "Flow"));
    }

    private static Task<CliResult> P(TestWorkspace ws, params string[] args) => ws.InProject("Demo", args);

    private static async Task<Guid> Series(TestWorkspace ws, string name, string prefix) =>
        (await Ok(P(ws, "series", "create", name, "--prefix", prefix))).Id;

    private static async Task<JsonNode> NewTask(TestWorkspace ws, string title, params string[] series) =>
        (await Ok(P(ws, ["task", "create", title, "--type", "Bug", .. series.SelectMany(x => new[] { "--series", x }), "--json"]))).Json;

    private static int[] Numbers(JsonNode task, Guid seriesId) =>
        task["seriesNumbers"]!.AsArray().Where(x => x!["seriesId"]!.GetValue<Guid>() == seriesId).Select(x => x!["number"]!.GetValue<int>()).ToArray();

    // ---- series CRUD ----

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Series_can_be_created_listed_shown_updated_and_deleted(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var created = await Ok(P(ws, "series", "create", "Tasks", "--prefix", "TSK"));
        Assert.StartsWith("Created series 'Tasks' (TSK) ", created.Out);
        var id = created.Id;

        var list = await Ok(P(ws, "series", "list"));
        Assert.Equal($"{ShortId.Of(id)}  TSK  Tasks", list.Data.Trim());
        var listJson = (await Ok(P(ws, "series", "list", "--json"))).Json;
        Assert.Equal(1, listJson["totalCount"]!.GetValue<int>());
        Assert.Equal("TSK", listJson["data"]![0]!["prefix"]!.GetValue<string>());

        // Серия находится по id и по точному префиксу.
        foreach (var reference in new[] { id.ToString(), "TSK" })
        {
            var get = (await Ok(P(ws, "series", "get", reference))).Out;
            Assert.Contains("Tasks", get);
            Assert.Contains("TSK", get);
            Assert.Contains("version:", get);
        }
        var json = (await Ok(P(ws, "series", "get", "TSK", "--json"))).Json;
        Assert.Equal(id, json["id"]!.GetValue<Guid>());
        var version = json["version"]!.GetValue<string>();

        var updated = await Ok(P(ws, "series", "update", "TSK", "--name", "Work", "--prefix", "WRK"));
        Assert.StartsWith("Updated series 'Work' (WRK) ", updated.Out);
        Assert.Equal(1, (await P(ws, "series", "get", "TSK")).Code);

        var stale = await P(ws, "series", "update", "WRK", "--name", "X", "--expected-version", version);
        Assert.Equal(1, stale.Code);
        Assert.Contains("Modified by someone else", stale.Err);

        Assert.Equal(1, (await P(ws, "series", "update", "WRK")).Code);

        var deleted = await Ok(P(ws, "series", "delete", "WRK"));
        Assert.StartsWith("Deleted series 'Work'", deleted.Out);
        Assert.Equal(0, (await Ok(P(ws, "series", "list", "--json"))).Json["totalCount"]!.GetValue<int>());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Series_delete_with_an_expected_version_and_json(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var id = await Series(ws, "Tasks", "TSK");
        var version = (await Ok(P(ws, "series", "get", "TSK", "--json"))).Json["version"]!.GetValue<string>();
        await Ok(P(ws, "series", "update", "TSK", "--name", "Other"));

        Assert.Equal(1, (await P(ws, "series", "delete", "TSK", "--expected-version", version)).Code);

        var deleted = await Ok(P(ws, "series", "delete", "TSK", "--json"));
        Assert.Equal(id, deleted.Json["deleted"]!.GetValue<Guid>());
        Assert.Equal(0, deleted.Json["tasksAffected"]!.GetValue<int>());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Series_create_rejects_bad_prefixes_and_duplicates_and_tells_case_apart(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Series(ws, "Tasks", "TSK");

        foreach (var bad in new[] { "TS-K", "ТСК", "T K", "", new string('A', 21), "T_K", "TS.K" })
        {
            var result = await P(ws, "series", "create", "X", "--prefix", bad);
            Assert.Equal(1, result.Code);
            Assert.Contains("Latin letters or digits", result.Err);
        }

        var duplicate = await P(ws, "series", "create", "Again", "--prefix", "TSK");
        Assert.Equal(1, duplicate.Code);
        Assert.Contains("already used", duplicate.Err);

        // tsk — другая серия; поиск по префиксу различает регистр.
        var lower = await Series(ws, "Lower", "tsk");
        Assert.Equal(lower, (await Ok(P(ws, "series", "get", "tsk", "--json"))).Json["id"]!.GetValue<Guid>());
        Assert.Equal(1, (await P(ws, "series", "get", "Tsk")).Code);
        Assert.Contains("No series 'Tsk'", (await P(ws, "series", "get", "Tsk")).Err);

        // Переименование в занятый префикс — тоже отказ.
        Assert.Equal(1, (await P(ws, "series", "update", "tsk", "--prefix", "TSK")).Code);
        Assert.Equal(1, (await P(ws, "series", "update", "tsk", "--prefix", "T-1")).Code);
        Assert.Equal(1, (await TestWorkspace.Invoke(["series", "create", "N", "--prefix", "A1", "--project", "Nope", .. ws.Location])).Code);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Series_errors(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Series(ws, "Tasks", "TSK");

        Assert.Contains("No series 'NOPE'", (await P(ws, "series", "get", "NOPE")).Err);
        Assert.Contains("No series", (await P(ws, "series", "delete", Guid.NewGuid().ToString())).Err);
        await Ok(ws.Run("project", "create", "Other")); // с одним проектом --project необязателен
        Assert.Contains("Project is required", (await ws.Run("series", "list")).Err);
        Assert.Equal(1, (await P(ws, "series", "create", "X")).Code);
    }

    // ---- номера ----

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Tasks_get_numbers_one_two_three_and_a_removed_number_leaves_a_gap(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var tsk = await Series(ws, "Tasks", "TSK");

        var one = await Ok(P(ws, "task", "create", "One", "--type", "Bug", "--series", "TSK"));
        Assert.EndsWith("(TSK-1)", one.Out.Trim());
        Assert.StartsWith("Created task 'One' ", one.Out);
        var two = await NewTask(ws, "Two", "TSK");
        var three = await NewTask(ws, "Three", tsk.ToString());
        Assert.Equal(new[] { 2 }, Numbers(two, tsk));
        Assert.Equal(new[] { 3 }, Numbers(three, tsk));

        var removed = await Ok(P(ws, "series", "remove-task", "TSK", "2"));
        Assert.Contains("(was TSK-2)", removed.Out);

        // Второй номер свободен, но следующая задача получает максимум + 1.
        Assert.Equal(new[] { 4 }, Numbers(await NewTask(ws, "Four", "TSK"), tsk));
        // Задача осталась, но без номера: в списке без ссылки.
        var list = (await Ok(P(ws, "task", "list"))).Data;
        Assert.Contains("  Todo  Bug  Two", list);
        Assert.DoesNotContain("TSK-2", list);
        Assert.Contains("TSK-1".PadRight(ShortId.Length) + "  Todo  Bug  One", list);

        var missing = await P(ws, "series", "remove-task", "TSK", "2");
        Assert.Equal(1, missing.Code);
        Assert.Contains("No task TSK-2", missing.Err);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task A_task_can_be_in_several_series_with_a_number_in_each(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var tsk = await Series(ws, "Tasks", "TSK");
        var prj = await Series(ws, "Projects", "PRJ");
        await NewTask(ws, "In PRJ only", "PRJ");

        var task = await NewTask(ws, "Both", "TSK", "PRJ");
        Assert.Equal(new[] { 1 }, Numbers(task, tsk));
        Assert.Equal(new[] { 2 }, Numbers(task, prj));

        var created = await Ok(P(ws, "task", "create", "Again", "--type", "Bug", "--series", "PRJ", "--series", "TSK"));
        Assert.EndsWith("(PRJ-3, TSK-2)", created.Out.Trim());

        var id = task["id"]!.GetValue<Guid>();
        var text = (await Ok(P(ws, "task", "get", "PRJ-2"))).Out;
        Assert.Contains($"id:", text);
        Assert.Contains("series:", text);
        Assert.Contains("PRJ-2, TSK-1", text);
        Assert.Equal(id, (await Ok(P(ws, "task", "get", "TSK-1", "--json"))).Json["id"]!.GetValue<Guid>());

        // Одна серия дважды — отказ.
        var twice = await P(ws, "task", "create", "X", "--type", "Bug", "--series", "TSK", "--series", "TSK");
        Assert.Equal(1, twice.Code);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Task_get_update_delete_work_by_reference_and_report_unknown_ones(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Series(ws, "Tasks", "TSK");
        await Series(ws, "Lower", "tsk");
        var task = await NewTask(ws, "Fix login", "TSK");
        var other = await NewTask(ws, "Other", "tsk");

        Assert.Contains("Fix login", (await Ok(P(ws, "task", "get", "TSK-1"))).Out);
        Assert.Contains("Other", (await Ok(P(ws, "task", "get", "tsk-1"))).Out);
        Assert.Contains("Updated task 'Renamed'", (await Ok(P(ws, "task", "update", "TSK-1", "--title", "Renamed"))).Out);
        Assert.Equal("Renamed", (await Ok(P(ws, "task", "get", task["id"]!.GetValue<string>(), "--json"))).Json["title"]!.GetValue<string>());

        var none = await P(ws, "task", "get", "TSK-9");
        Assert.Equal(1, none.Code);
        Assert.Contains("No task 'TSK-9'", none.Err);
        Assert.Contains("No task", (await P(ws, "task", "get", "XYZ-1")).Err);
        Assert.Contains("Task is given by id", (await P(ws, "task", "get", "not a ref")).Err);
        Assert.Contains("Task is given by id", (await P(ws, "task", "get", "TSK-0")).Err);

        Assert.StartsWith("Deleted task 'Other'", (await Ok(P(ws, "task", "delete", "tsk-1"))).Out);
        Assert.Equal(1, (await P(ws, "task", "get", "tsk-1")).Code);
        _ = other;
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Task_list_text_shows_reference_then_current_status_then_title_without_id(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Series(ws, "Tasks", "TSK");
        await Series(ws, "Projects", "PRJ");
        var one = await NewTask(ws, "One", "TSK");
        var two = await NewTask(ws, "Two", "TSK", "PRJ");
        var loose = await NewTask(ws, "Loose");
        await Ok(P(ws, "task", "update", "TSK-1", "--status", "Done"));

        var text = (await Ok(P(ws, "task", "list"))).Data;
        var lines = text.Trim().Split('\n');
        Assert.Equal(3, lines.Length);
        // Статус после ссылки, затем тип и название; состояние берётся текущее (после update).
        Assert.Equal("TSK-1".PadRight(11) + "  Done  Bug  One", lines[0]);
        // Задача в двух сериях: все ссылки, как и раньше.
        Assert.Equal("PRJ-1,TSK-2".PadRight(11) + "  Todo  Bug  Two", lines[1]);
        // Задача без серии: короткий id остаётся единственной ручкой.
        Assert.Equal($"{ShortId.Of(loose["id"]!.GetValue<Guid>())}".PadRight(11) + "  Todo  Bug  Loose", lines[2]);

        // Guid задач с серией в тексте не показывается.
        Assert.DoesNotContain(one["id"]!.GetValue<string>(), text);
        Assert.DoesNotContain(two["id"]!.GetValue<string>(), text);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Task_list_json_still_has_ids_and_status_ids(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Series(ws, "Tasks", "TSK");
        var one = await NewTask(ws, "One", "TSK");

        var item = (await Ok(P(ws, "task", "list", "--json"))).Json["data"]![0]!;
        Assert.Equal(one["id"]!.GetValue<Guid>(), item["id"]!.GetValue<Guid>());
        Assert.Equal(one["statusId"]!.GetValue<Guid>(), item["statusId"]!.GetValue<Guid>());
        Assert.NotNull(item["seriesNumbers"]);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Task_list_filters_by_series_and_shows_references(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Series(ws, "Tasks", "TSK");
        await Series(ws, "Projects", "PRJ");
        var a = await NewTask(ws, "A", "TSK");
        await NewTask(ws, "B", "PRJ");
        var c = await NewTask(ws, "C", "TSK", "PRJ");
        await NewTask(ws, "D");

        var tsk = await Ok(P(ws, "task", "list", "--series", "TSK"));
        var lines = tsk.Data.Trim().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("TSK-1        Todo  Bug  A", lines[0]);
        Assert.Equal("PRJ-2,TSK-2  Todo  Bug  C", lines[1]);

        var json = (await Ok(P(ws, "task", "list", "--series", "PRJ", "--json"))).Json;
        Assert.Equal(["B", "C"], json["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray());
        Assert.NotNull(json["data"]![0]!["seriesNumbers"]);

        // Задача без серии: ссылки нет, поэтому вместо неё короткий id (другой ручки для get/update/delete у неё нет).
        var all = (await Ok(P(ws, "task", "list"))).Data.Trim().Split('\n');
        Assert.Matches(@"^[0-9a-f]{8} +Todo  Bug  D$", all[3]);

        Assert.Contains("No series", (await P(ws, "task", "list", "--series", "NOPE")).Err);
        Assert.Equal(1, (await P(ws, "task", "create", "T", "--type", "Bug", "--series", "NOPE")).Code);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Add_task_and_renumber_task(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var tsk = await Series(ws, "Tasks", "TSK");
        await Series(ws, "Projects", "PRJ");
        var first = await NewTask(ws, "First", "TSK");
        var plain = await NewTask(ws, "Plain");
        var plainId = plain["id"]!.GetValue<string>();

        var added = await Ok(P(ws, "series", "add-task", "TSK", plainId));
        Assert.Contains("is TSK-2", added.Out);
        // Повторное включение ничего не меняет.
        Assert.Contains("is TSK-2", (await Ok(P(ws, "series", "add-task", "TSK", "TSK-2"))).Out);
        Assert.Contains("is PRJ-1", (await Ok(P(ws, "series", "add-task", "PRJ", "TSK-1"))).Out);
        Assert.Equal(new[] { 1 }, Numbers((await Ok(P(ws, "series", "add-task", "TSK", "TSK-1", "--json"))).Json, tsk));

        // Без --to — следующий свободный (максимум + 1).
        Assert.Contains("is TSK-3", (await Ok(P(ws, "series", "renumber-task", "TSK", "TSK-1"))).Out);
        Assert.Contains("is TSK-10", (await Ok(P(ws, "series", "renumber-task", "TSK", plainId, "--to", "10"))).Out);
        Assert.Equal(1, (await P(ws, "series", "renumber-task", "TSK", plainId, "--to", "3")).Code);
        Assert.Contains("already taken", (await P(ws, "series", "renumber-task", "TSK", plainId, "--to", "3")).Err);
        Assert.Equal(1, (await P(ws, "series", "renumber-task", "TSK", plainId, "--to", "0")).Code);
        Assert.Equal(first["id"]!.GetValue<Guid>(), (await Ok(P(ws, "task", "get", "TSK-3", "--json"))).Json["id"]!.GetValue<Guid>());

        // Ошибки: нет серии, нет задачи, задача не в серии.
        Assert.Contains("No series 'NOPE'", (await P(ws, "series", "add-task", "NOPE", plainId)).Err);
        Assert.Contains("No task 'TSK-99'", (await P(ws, "series", "add-task", "TSK", "TSK-99")).Err);
        var plain2 = (await NewTask(ws, "Plain2"))["id"]!.GetValue<string>();
        Assert.Contains("is not in series TSK", (await P(ws, "series", "renumber-task", "TSK", plain2)).Err);
        Assert.Contains("No series", (await P(ws, "series", "remove-task", "NOPE", "1")).Err);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Deleting_a_series_with_tasks_removes_it_from_them_and_keeps_other_series(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var tsk = await Series(ws, "Tasks", "TSK");
        var prj = await Series(ws, "Projects", "PRJ");
        var both = (await NewTask(ws, "Both", "TSK", "PRJ"))["id"]!.GetValue<string>();
        await NewTask(ws, "Only TSK", "TSK");
        await NewTask(ws, "Only PRJ", "PRJ");

        var deleted = await Ok(P(ws, "series", "delete", "TSK"));
        Assert.Contains("Removed the series from 2 task(s)", deleted.Out);

        var task = (await Ok(P(ws, "task", "get", both, "--json"))).Json;
        Assert.Empty(Numbers(task, tsk));
        Assert.Equal(new[] { 1 }, Numbers(task, prj));
        Assert.Equal(1, (await Ok(P(ws, "task", "list", "--series", "PRJ", "--json"))).Json["data"]!.AsArray().Count(x => x!["title"]!.GetValue<string>() == "Both"));
        Assert.Equal(1, (await P(ws, "task", "get", "TSK-1")).Code);
        // Все задачи на месте.
        Assert.Equal(3, (await Ok(P(ws, "task", "list", "--json"))).Json["totalCount"]!.GetValue<int>());
        // Префикс освободился.
        await Series(ws, "New", "TSK");
        Assert.Contains("TSK-1", (await Ok(P(ws, "task", "create", "Fresh", "--type", "Bug", "--series", "TSK"))).Out);
    }

    [Fact]
    public async Task Duplicate_numbers_show_all_tasks_and_refuse_changes()
    {
        // Дубликат в БД невозможен (уникальный индекс), поэтому только файлы: дубликат правим в YAML, как после слияния веток.
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        await Series(ws, "Tasks", "TSK");
        var first = await NewTask(ws, "First", "TSK");
        var second = await NewTask(ws, "Second", "TSK");
        DuplicateNumber(ws, second["id"]!.GetValue<Guid>(), 1);

        var text = (await Ok(P(ws, "task", "get", "TSK-1"))).Out;
        Assert.Contains("First", text);
        Assert.Contains("Second", text);
        Assert.Contains($"conflict: TSK-1 is also used by {second["id"]}", text);
        Assert.Contains($"conflict: TSK-1 is also used by {first["id"]}", text);

        var json = (await Ok(P(ws, "task", "get", "TSK-1", "--json"))).Json;
        Assert.Equal(2, json.AsArray().Count);

        foreach (var command in new[]
        {
            new[] { "task", "update", "TSK-1", "--title", "X" },
            ["task", "delete", "TSK-1"],
            ["series", "add-task", "TSK", "TSK-1"],
            ["series", "renumber-task", "TSK", "TSK-1"],
            ["series", "remove-task", "TSK", "1"]
        })
        {
            var result = await P(ws, command);
            Assert.Equal(1, result.Code);
            Assert.Contains(first["id"]!.GetValue<string>(), result.Err);
            Assert.Contains(second["id"]!.GetValue<string>(), result.Err);
        }

        // По id — можно; так дубликат решают вручную.
        Assert.Contains("is TSK-2", (await Ok(P(ws, "series", "renumber-task", "TSK", second["id"]!.GetValue<string>()))).Out);
        Assert.Contains("First", (await Ok(P(ws, "task", "get", "TSK-1"))).Out);
    }

    private static void DuplicateNumber(TestWorkspace ws, Guid taskId, int number)
    {
        var file = FileFinder.Find(ws.Root, "tasks", taskId);
        var lines = File.ReadAllLines(file).Select(x => x.StartsWith("  number:") ? $"  number: {number}" : x).ToArray();
        File.WriteAllLines(file, lines);
    }

    [Fact]
    public async Task An_invalid_series_reference_is_marked_in_task_get()
    {
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        var series = await Series(ws, "Tasks", "TSK");
        var task = await NewTask(ws, "Orphan", "TSK");
        // Серию удалили в другой ветке: файла серии нет, а задача на неё ссылается.
        File.Delete(FileFinder.Find(ws.Root, "series", series));

        var text = (await Ok(P(ws, "task", "get", task["id"]!.GetValue<string>()))).Out;
        Assert.Contains($"(invalid series {series}) #1", text);
        Assert.Contains($"(invalid series {series}) #1  Todo  Bug  Orphan", (await Ok(P(ws, "task", "list"))).Out);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Help_of_the_new_commands_works(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        foreach (var command in new[]
        {
            new[] { "series" }, ["series", "create"], ["series", "list"], ["series", "get"], ["series", "update"], ["series", "delete"],
            ["series", "add-task"], ["series", "remove-task"], ["series", "renumber-task"], ["cleanup"]
        })
        {
            var result = await TestWorkspace.Invoke([.. command, "-h"]);
            Assert.Equal(0, result.Code);
            Assert.Contains("Usage", result.Out);
        }

        Assert.Contains("series", (await TestWorkspace.Invoke(["--help"])).Out);
        Assert.Contains("cleanup", (await TestWorkspace.Invoke(["--help"])).Out);
    }

    // ---- несколько процессов ----

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Parallel_processes_get_unique_consecutive_numbers(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Series(ws, "Tasks", "TSK");

        const int count = 6;
        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(i =>
            ws.RunProcess("task", "create", $"Task {i}", "--type", "Bug", "--series", "TSK", "-p", "Demo")));
        foreach (var result in results)
            Assert.True(result.Code == 0, result.Err);

        var numbers = results.Select(x => int.Parse(x.Out.Trim().Split('(')[^1].TrimEnd(')').Split('-')[1])).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, count), numbers);

        var listed = (await Ok(P(ws, "task", "list", "--series", "TSK", "--json"))).Json["data"]!.AsArray();
        Assert.Equal(count, listed.Count);
        Assert.Equal(Enumerable.Range(1, count), listed.Select(x => x!["seriesNumbers"]![0]!["number"]!.GetValue<int>()).Order());
    }
}
