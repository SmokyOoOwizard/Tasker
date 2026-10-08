using System.Text.Json.Nodes;
using Tasker.Core;
using Tasker.Core.Links;
using Tasker.Core.Tasks;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// TSK-109: иерархия в <c>task list</c> — эпик и дочерние задачи с отступом, несколько родителей, фильтры, порядок, страницы верхнего уровня,
/// <c>--flat</c> и плоский <c>--json</c> с <c>parentIds</c>/<c>childCount</c>. Консоль на обоих хранилищах (папка и SQLite), REST, стандартный тип без миграции.
/// </summary>
public class TaskHierarchyTests
{
    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    /// <summary>Строка дерева: уровень (отступ / 4), ссылка и признак повтора.</summary>
    private record Row(int Depth, string Handle, bool Repeated, string Line);

    private static Row[] Tree(CliResult result) => result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Skip(result.Found == null ? 0 : 1)
        .Select(line =>
        {
            var indent = line.Length - line.TrimStart().Length;
            Assert.Equal(0, indent % 4);
            var handle = line.TrimStart().Split(' ')[0];
            return new Row(indent / 4, handle, line.TrimStart().StartsWith(handle + " (+)"), line);
        })
        .ToArray();

    private static string Shape(CliResult result) =>
        string.Join(" ", Tree(result).Select(x => $"{new string('>', x.Depth)}{x.Handle}{(x.Repeated ? "+" : "")}"));

    /// <summary>Проект Demo: статусы Todo и Done, тип Bug, серия TSK. Задачи создаёт <see cref="Create"/>.</summary>
    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("status", "create", "Todo"));
        await Ok(ws.Run("status", "create", "Done"));
        await Ok(ws.Run("status-set", "create", "Flow", "--status", "Todo", "Done"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(ws.Run("series", "create", "Tasks", "--prefix", "TSK"));
    }

    private static Task<CliResult> Create(TestWorkspace ws, string title, string? status = null, params string[] parents)
    {
        var args = new List<string> { "task", "create", title, "--type", "Bug", "--series", "TSK" };
        if (status != null)
            args.AddRange(["--status", status]);
        foreach (var parent in parents)
            args.AddRange(["--parent", parent]);
        return Ok(ws.Run([.. args]));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task An_epic_is_followed_by_its_children_indented_and_columns_stay_aligned(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Create(ws, "Epic");                       // TSK-1
        await Create(ws, "First", null, "TSK-1");       // TSK-2
        await Create(ws, "Second", null, "TSK-1");      // TSK-3
        await Create(ws, "Solo");                       // TSK-4

        var list = await Ok(ws.Run("task", "list"));

        Assert.Equal("Found 4", list.Found);
        Assert.Equal("TSK-1 >TSK-2 >TSK-3 TSK-4", Shape(list));
        // Отступ входит в первую колонку: статус, тип и заголовок начинаются в одном столбце у всех строк.
        var columns = Tree(list).Select(x => x.Line.IndexOf("Todo", StringComparison.Ordinal)).Distinct().ToArray();
        Assert.Single(columns);
        Assert.StartsWith("    TSK-2", Tree(list)[1].Line);
        Assert.EndsWith("Todo  Bug  First", Tree(list)[1].Line);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Children_nest_three_levels_deep(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Create(ws, "Epic");                    // 1
        await Create(ws, "Story", null, "TSK-1");    // 2
        await Create(ws, "Sub", null, "TSK-2");      // 3
        await Create(ws, "Subsub", null, "TSK-3");   // 4

        Assert.Equal("TSK-1 >TSK-2 >>TSK-3 >>>TSK-4", Shape(await Ok(ws.Run("task", "list"))));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task A_task_with_two_parents_is_shown_under_each_with_its_subtree_and_counted_once(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Create(ws, "Epic one");                    // 1
        await Create(ws, "Epic two");                    // 2
        await Create(ws, "Shared", null, "TSK-1", "TSK-2"); // 3
        await Create(ws, "Part", null, "TSK-3");         // 4

        var list = await Ok(ws.Run("task", "list"));

        // Повтор помечен «(+)», поддерево повторяется вместе с ним; число — уникальные задачи.
        Assert.Equal("Found 4", list.Found);
        Assert.Equal("TSK-1 >TSK-3 >>TSK-4 TSK-2 >TSK-3+ >>TSK-4+", Shape(list));
        Assert.Contains("TSK-3 (+)", Tree(list)[4].Line);
        Assert.DoesNotContain("(+)", Tree(list)[1].Line);

        var ids = (await Ok(ws.Run("task", "list", "--json"))).Json["data"]!.AsArray();
        var shared = ids.Single(x => x!["title"]!.GetValue<string>() == "Shared")!;
        Assert.Equal(2, shared["parentIds"]!.AsArray().Count);
        Assert.Equal(1, shared["childCount"]!.GetValue<int>());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Filters_act_on_every_task_separately(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Create(ws, "Epic todo");                       // 1: Todo
        await Create(ws, "Child done", "Done", "TSK-1");     // 2: Done, ребёнок Todo-эпика
        await Create(ws, "Child todo", null, "TSK-1");       // 3
        await Create(ws, "Epic done", "Done");               // 4: Done
        await Create(ws, "Child of done", null, "TSK-4");    // 5: Todo, эпик не подходит
        await Create(ws, "Two parents", null, "TSK-1", "TSK-4"); // 6

        // Todo: ребёнок виден под своим подошедшим эпиком; ребёнок «Done»-эпика (TSK-5) — на верхнем уровне; у TSK-6 есть подошедший родитель TSK-1.
        Assert.Equal("TSK-1 >TSK-3 >TSK-6 TSK-5", Shape(await Ok(ws.Run("task", "list", "--status", "Todo"))));
        // Done: ребёнок TSK-2 — на верхнем уровне (его эпик не подходит), Done-эпик TSK-4 без не подошедших детей.
        Assert.Equal("TSK-2 TSK-4", Shape(await Ok(ws.Run("task", "list", "--status", "Done"))));
        // «Found» — задачи после фильтра.
        Assert.Equal("Found 4", (await Ok(ws.Run("task", "list", "--status", "Todo"))).Found);
        // Нет вложенности в результате (оба на верхнем уровне) — итог страницы прежней формы.
        Assert.Equal("Found 2, shown 1-1 (use --offset/--limit)", (await Ok(ws.Run("task", "list", "--status", "Done", "--limit", "1"))).Found);
        // Родитель в результате, ребёнок нет — ребёнок просто не показывается.
        Assert.Equal("TSK-4", Shape(await Ok(ws.Run("task", "list", "--status", "Done", "--type", "Bug", "--series", "TSK", "--sort", "-created", "--limit", "1"))));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task A_child_without_a_matching_parent_goes_to_the_top_level_and_a_task_with_no_matching_parent_among_several_too(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Create(ws, "Epic A", "Done");                 // 1
        await Create(ws, "Epic B", "Done");                 // 2
        await Create(ws, "Shared", null, "TSK-1", "TSK-2"); // 3: оба родителя Done, сам Todo

        Assert.Equal("TSK-3", Shape(await Ok(ws.Run("task", "list", "--status", "Todo"))));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Sort_orders_the_top_level_and_the_children_of_each_parent(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Create(ws, "Alpha");                  // 1
        await Create(ws, "Beta");                   // 2
        await Create(ws, "Zed child", null, "TSK-1");   // 3
        await Create(ws, "Abc child", null, "TSK-1");   // 4
        await Create(ws, "Mid child", null, "TSK-2");   // 5

        Assert.Equal("TSK-1 >TSK-3 >TSK-4 TSK-2 >TSK-5", Shape(await Ok(ws.Run("task", "list"))));
        Assert.Equal("TSK-1 >TSK-4 >TSK-3 TSK-2 >TSK-5", Shape(await Ok(ws.Run("task", "list", "--sort", "title"))));
        Assert.Equal("TSK-2 >TSK-5 TSK-1 >TSK-3 >TSK-4", Shape(await Ok(ws.Run("task", "list", "--sort", "-title"))));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Pages_count_top_level_tasks_and_never_split_a_subtree(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        for (var i = 1; i <= 3; i++)
            await Create(ws, $"Epic {i}");                     // 1, 2, 3
        await Create(ws, "Child of 1", null, "TSK-1");          // 4
        await Create(ws, "Child of 1 too", null, "TSK-1");      // 5
        await Create(ws, "Child of 3", null, "TSK-3");          // 6

        var first = await Ok(ws.Run("task", "list", "--limit", "1"));
        Assert.Equal("Found 6, shown top-level 1-1 of 3 (use --offset/--limit)", first.Found);
        Assert.Equal("TSK-1 >TSK-4 >TSK-5", Shape(first));

        var second = await Ok(ws.Run("task", "list", "--offset", "1", "--limit", "1"));
        Assert.Equal("Found 6, shown top-level 2-2 of 3 (use --offset/--limit)", second.Found);
        Assert.Equal("TSK-2", Shape(second));

        var last = await Ok(ws.Run("task", "list", "--offset", "2", "--limit", "5"));
        Assert.Equal("Found 6, shown top-level 3-3 of 3 (use --offset/--limit)", last.Found);
        Assert.Equal("TSK-3 >TSK-6", Shape(last));

        var beyond = await Ok(ws.Run("task", "list", "--offset", "9"));
        Assert.Equal("Found 6, shown top-level none of 3 (use --offset/--limit)", beyond.Out.Trim());

        var all = await Ok(ws.Run("task", "list", "--all"));
        Assert.Equal("Found 6", all.Found);
        Assert.Equal("TSK-1 >TSK-4 >TSK-5 TSK-2 TSK-3 >TSK-6", Shape(all));

        // --flat страниц верхнего уровня не знает: обычный итог.
        Assert.Equal("Found 6, shown 1-2 (use --offset/--limit)", (await Ok(ws.Run("task", "list", "--flat", "--limit", "2"))).Found);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Flat_and_json_keep_the_flat_list_with_parentIds_and_childCount(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Create(ws, "Epic");                   // 1
        await Create(ws, "Child", null, "TSK-1");   // 2
        await Create(ws, "Other");                  // 3

        var flat = await Ok(ws.Run("task", "list", "--flat"));
        Assert.Equal("TSK-1 TSK-2 TSK-3", Shape(flat));
        Assert.Equal("Found 3", flat.Found);

        var json = (await Ok(ws.Run("task", "list", "--json"))).Json;
        var data = json["data"]!.AsArray();
        Assert.Equal(3, json["totalCount"]!.GetValue<int>());
        Assert.Equal(["Epic", "Child", "Other"], data.Select(x => x!["title"]!.GetValue<string>()).ToArray());
        JsonNode Of(string title) => data.Single(x => x!["title"]!.GetValue<string>() == title)!;
        var epicId = Of("Epic")["id"]!.GetValue<string>();
        Assert.Equal(1, Of("Epic")["childCount"]!.GetValue<int>());
        Assert.Empty(Of("Epic")["parentIds"]!.AsArray());
        Assert.Equal([epicId], Of("Child")["parentIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Equal(0, Of("Child")["childCount"]!.GetValue<int>());
        Assert.Equal(0, Of("Other")["childCount"]!.GetValue<int>());
        Assert.Null(Of("Epic")["depth"]);

        // Доски дерево не строят.
        await Ok(ws.Run("board", "create", "Main", "--status-set", "Flow", "--column", "Open=Todo"));
        var column = await Ok(ws.Run("board", "tasks", "Main", "Open"));
        Assert.Equal("TSK-1 TSK-2 TSK-3", Shape(column));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Long_titles_are_cut_by_width_with_the_indent_counted_and_the_found_line_is_not_cut(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Create(ws, "Epic");
        await Create(ws, "A very long title of a child task that does not fit", null, "TSK-1");

        var narrow = await Ok(ws.Run("task", "list", "--width", "30"));
        var lines = narrow.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Found 2", lines[0]);
        Assert.All(lines.Skip(1), x => Assert.True(Tasker.Cli.Table.Width(x) <= 30, x));
        Assert.EndsWith("…", lines[2]);
        Assert.StartsWith("    TSK-2", lines[2]);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Parents_can_be_added_and_removed_with_update_and_cycles_are_rejected(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Create(ws, "Epic one");   // 1
        await Create(ws, "Epic two");   // 2
        await Create(ws, "Task");       // 3

        await Ok(ws.Run("task", "update", "TSK-3", "--add-parent", "TSK-1", "TSK-2"));
        Assert.Equal("TSK-1 >TSK-3 TSK-2 >TSK-3+", Shape(await Ok(ws.Run("task", "list"))));

        await Ok(ws.Run("task", "update", "TSK-3", "--remove-parent", "TSK-2", "--title", "Renamed"));
        Assert.Equal("TSK-1 >TSK-3 TSK-2", Shape(await Ok(ws.Run("task", "list"))));
        Assert.Contains("Renamed", (await Ok(ws.Run("task", "list"))).Out);

        // Связь, замыкающая цикл по родителям, отклоняется.
        var cycle = await ws.Run("task", "link", "TSK-3", "includes", "TSK-1");
        Assert.Equal(1, cycle.Code);
        Assert.Contains("Cycle", cycle.Err);
        var direct = await ws.Run("task", "update", "TSK-1", "--add-parent", "TSK-3");
        Assert.Equal(1, direct.Code);
        Assert.Contains("Cycle", direct.Err);
        Assert.Equal(1, (await ws.Run("task", "update", "TSK-1", "--add-parent", "TSK-1")).Code);

        // `task get` видит связь с обеих сторон.
        Assert.Contains("is part of", (await Ok(ws.Run("task", "get", "TSK-3"))).Out);
        Assert.Contains("includes", (await Ok(ws.Run("task", "get", "TSK-1"))).Out);

        // Неверная ссылка на родителя ничего не создаёт.
        var before = (await Ok(ws.Run("task", "list", "--flat"))).Found;
        Assert.Equal(1, (await ws.Run("task", "create", "Ghost", "--type", "Bug", "--parent", "TSK-99")).Code);
        Assert.Equal(before, (await Ok(ws.Run("task", "list", "--flat"))).Found);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task A_cycle_across_two_hierarchical_types_is_rejected_too(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(ws.Run("link-type", "create", "Epic link", "--outward", "has story", "--inward", "belongs to epic", "--hierarchical", "true"));
        await Create(ws, "A");
        await Create(ws, "B");
        await Ok(ws.Run("task", "link", "TSK-1", "includes", "TSK-2"));

        var result = await ws.Run("task", "link", "TSK-2", "has story", "TSK-1");
        Assert.Equal(1, result.Code);
        Assert.Contains("Cycle", result.Err);

        // Несколько иерархических типов: без --parent-type команда не угадывает.
        var ambiguous = await ws.Run("task", "update", "TSK-2", "--add-parent", "TSK-1");
        Assert.Equal(1, ambiguous.Code);
        Assert.Contains("--parent-type", ambiguous.Err);
        await Ok(ws.Run("task", "update", "TSK-2", "--remove-parent", "TSK-1", "--parent-type", "Parent/Child"));
        await Ok(ws.Run("task", "update", "TSK-2", "--add-parent", "TSK-1", "--parent-type", "Epic link"));
        Assert.Equal("TSK-1 >TSK-2", Shape(await Ok(ws.Run("task", "list"))));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Hierarchical_link_types_forbid_cycles_and_need_two_names(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var parent = (await Ok(ws.Run("link-type", "get", "Parent/Child", "--json"))).Json;
        Assert.True(parent["hierarchical"]!.GetValue<bool>());
        Assert.False(parent["allowCycles"]!.GetValue<bool>());
        Assert.Equal("includes", parent["outwardName"]!.GetValue<string>());
        Assert.Equal("is part of", parent["inwardName"]!.GetValue<string>());
        Assert.False((await Ok(ws.Run("link-type", "get", "Blocks", "--json"))).Json["hierarchical"]!.GetValue<bool>());

        var explicitCycles = await ws.Run("link-type", "create", "Wrong", "--outward", "has", "--inward", "of", "--hierarchical", "true", "--allow-cycles", "true");
        Assert.Equal(1, explicitCycles.Code);
        var oneName = await ws.Run("link-type", "create", "Wrong", "--outward", "has", "--hierarchical", "true");
        Assert.Equal(1, oneName.Code);

        // Без явного allowCycles иерархический тип циклов не допускает.
        var created = (await Ok(ws.Run("link-type", "create", "Epic link", "--outward", "has story", "--inward", "belongs to epic", "--hierarchical", "true", "--json"))).Json;
        Assert.True(created["hierarchical"]!.GetValue<bool>());
        Assert.False(created["allowCycles"]!.GetValue<bool>());

        // Обычный тип можно сделать иерархическим и обратно; иерархическому нельзя разрешить циклы.
        await Ok(ws.Run("link-type", "create", "Plain", "--outward", "a", "--inward", "b"));
        var made = (await Ok(ws.Run("link-type", "update", "Plain", "--hierarchical", "true", "--json"))).Json;
        Assert.True(made["hierarchical"]!.GetValue<bool>());
        Assert.False(made["allowCycles"]!.GetValue<bool>());
        Assert.Equal(1, (await ws.Run("link-type", "update", "Plain", "--allow-cycles", "true")).Code);
        Assert.False((await Ok(ws.Run("link-type", "update", "Plain", "--hierarchical", "false", "--json"))).Json["hierarchical"]!.GetValue<bool>());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task The_standard_type_is_there_without_migration_and_comes_back_only_while_no_hierarchical_type_exists(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // Проект с уже сохранёнными типами без Parent/Child (как созданный до иерархии): удаляем его после сохранения набора.
        await Create(ws, "A");
        await Create(ws, "B");
        await Ok(ws.Run("task", "link", "TSK-1", "relates to", "TSK-2")); // типы по умолчанию сохраняются
        await Ok(ws.Run("link-type", "delete", "Parent/Child"));

        // Иерархического типа в проекте нет — Parent/Child снова виден (и снова сохраняется первой записью).
        var listed = (await Ok(ws.Run("link-type", "list", "--json"))).Json["data"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("Parent/Child", listed);
        Assert.Equal("default", (await Ok(ws.Run("link-type", "get", "Parent/Child", "--json"))).Json["version"]!.GetValue<string>());
        await Ok(ws.Run("task", "update", "TSK-2", "--add-parent", "TSK-1"));
        Assert.Equal("TSK-1 >TSK-2", Shape(await Ok(ws.Run("task", "list"))));
        Assert.NotEqual("default", (await Ok(ws.Run("link-type", "get", "Parent/Child", "--json"))).Json["version"]!.GetValue<string>());

        // Свой иерархический тип заменяет стандартный: удалённый Parent/Child не возвращается.
        await Ok(ws.Run("task", "update", "TSK-2", "--remove-parent", "TSK-1"));
        await Ok(ws.Run("link-type", "create", "Epic link", "--outward", "has story", "--inward", "belongs to epic", "--hierarchical", "true"));
        await Ok(ws.Run("link-type", "delete", "Parent/Child"));
        var after = (await Ok(ws.Run("link-type", "list", "--json"))).Json["data"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray();
        Assert.DoesNotContain("Parent/Child", after);
        Assert.Contains("Epic link", after);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task A_deep_chain_stops_at_the_depth_limit_and_every_task_is_still_found(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Create(ws, "Level 0");
        for (var i = 1; i < TaskHierarchy.MaxDepth + 2; i++)
            await Create(ws, $"Level {i}", null, $"TSK-{i}");

        var list = await Ok(ws.Run("task", "list"));

        Assert.Equal($"Found {TaskHierarchy.MaxDepth + 2}", list.Found);
        Assert.Equal(TaskHierarchy.MaxDepth, Tree(list).Length);
        Assert.Equal(TaskHierarchy.MaxDepth - 1, Tree(list)[^1].Depth);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Hundreds_of_tasks_are_listed_by_pages_of_top_level_tasks(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        for (var i = 1; i <= 30; i++)
            await Create(ws, $"Epic {i:00}");                 // TSK-1…30
        for (var i = 1; i <= 30; i++)
            await Create(ws, $"Child {i:00}", null, $"TSK-{i}"); // TSK-31…60

        var page = await Ok(ws.Run("task", "list", "--limit", "10", "--offset", "10"));
        Assert.Equal("Found 60, shown top-level 11-20 of 30 (use --offset/--limit)", page.Found);
        Assert.Equal(20, Tree(page).Length);
        Assert.Equal(10, Tree(page).Count(x => x.Depth == 0));
        Assert.Equal("TSK-11", Tree(page)[0].Handle);
        Assert.Equal("TSK-41", Tree(page)[1].Handle);
    }

    // ---- ядро: порядок верхнего уровня и защита от циклов ----

    [Fact]
    public void A_cycle_of_parents_cannot_hide_tasks_and_does_not_hang_the_walk()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        // Слияние веток дало цикл A → B → C → A, и C тоже входит в D.
        var hierarchy = new TaskHierarchy([new LinkEdge(a, b), new LinkEdge(b, c), new LinkEdge(c, a)]);
        var ordered = new[] { a, b, c };

        var top = hierarchy.TopLevel(ordered);

        Assert.Equal([a], top); // из цикла берётся первая по порядку списка, остальные достижимы от неё
        var rows = hierarchy.Rows(top, ordered.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i));
        Assert.Equal([a, b, c], rows.Select(x => x.Id).ToArray());
        Assert.All(rows, x => Assert.False(x.Repeated));
    }

    [Fact]
    public void A_diamond_repeats_the_shared_subtree_and_marks_the_repeats()
    {
        var (root, left, right, shared, leaf) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var hierarchy = new TaskHierarchy([
            new LinkEdge(root, left), new LinkEdge(root, right), new LinkEdge(left, shared), new LinkEdge(right, shared), new LinkEdge(shared, leaf)]);
        var ordered = new[] { root, left, right, shared, leaf };
        var position = ordered.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

        var rows = hierarchy.Rows(hierarchy.TopLevel(ordered), position);

        Assert.Equal([(root, 0, false), (left, 1, false), (shared, 2, false), (leaf, 3, false), (right, 1, false), (shared, 2, true), (leaf, 3, true)],
            rows.Select(x => (x.Id, x.Depth, x.Repeated)).ToArray());
        Assert.Equal(new[] { left, right }.Order(), hierarchy.ParentsOf(shared).Order());
        Assert.Equal(1, hierarchy.ChildCount(shared));
    }

    // ---- REST ----

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Rest_list_is_flat_with_parentIds_and_childCount_and_flat_false_gives_the_tree(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        async Task<string> Make(string title) => (await api.Post(api.P("/tasks"), new { title, typeId = api.TypeId })).Body!["id"]!.GetValue<string>();

        var epic = await Make("Epic");
        var child = await Make("Child");
        var other = await Make("Other");
        var types = (await api.Get(api.P("/link-types"))).Body!["data"]!.AsArray();
        var parentType = types.Single(x => x!["name"]!.GetValue<string>() == "Parent/Child")!;
        Assert.True(parentType["hierarchical"]!.GetValue<bool>());
        var link = await api.Post(api.P($"/tasks/{epic}/links"), new { typeId = parentType["id"]!.GetValue<string>(), targetId = child });
        Assert.Equal(System.Net.HttpStatusCode.OK, link.Status);

        var flat = (await api.Get(api.P("/tasks"))).Body!;
        var items = flat["data"]!.AsArray();
        Assert.Equal(3, flat["totalCount"]!.GetValue<int>());
        Assert.Null(flat["topLevelCount"]);
        Assert.Equal(1, items.Single(x => x!["id"]!.GetValue<string>() == epic)!["childCount"]!.GetValue<int>());
        Assert.Equal([epic], items.Single(x => x!["id"]!.GetValue<string>() == child)!["parentIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());

        var tree = (await api.Get(api.P("/tasks?flat=false&limit=1"))).Body!;
        Assert.Equal(3, tree["totalCount"]!.GetValue<int>());
        Assert.Equal(2, tree["topLevelCount"]!.GetValue<int>());
        var rows = tree["data"]!.AsArray();
        Assert.Equal([epic, child], rows.Select(x => x!["id"]!.GetValue<string>()).ToArray());
        Assert.Equal([0, 1], rows.Select(x => x!["depth"]!.GetValue<int>()).ToArray());
        Assert.All(rows, x => Assert.False(x!["repeated"]!.GetValue<bool>()));
        Assert.DoesNotContain(other, rows.Select(x => x!["id"]!.GetValue<string>()));

        var second = (await api.Get(api.P("/tasks?flat=false&limit=1&offset=1"))).Body!;
        Assert.Equal([other], second["data"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).ToArray());

        // Создать иерархический тип с циклами нельзя (400).
        var bad = await api.Post(api.P("/link-types"), new { name = "Bad", outwardName = "has", inwardName = "of", allowCycles = true, hierarchical = true });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.Status);
    }

    // ---- файловый формат ----

    [Fact]
    public async Task A_link_type_file_of_format_8_is_read_as_an_ordinary_type_and_written_as_format_9()
    {
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        await Ok(ws.Run("link-type", "create", "Needs", "--outward", "needs", "--inward", "is needed by"));
        var file = Directory.GetFiles(ws.Root, "needs-*.yaml", SearchOption.AllDirectories).Single();
        var text = (await File.ReadAllTextAsync(file)).ReplaceLineEndings("\n");
        Assert.StartsWith("formatVersion: 9\n", text);
        Assert.Contains("hierarchical: false", text);

        // Файл, записанный до признака: версия 8 и без ключа.
        var old = string.Join("\n", text.Split('\n').Where(x => !x.StartsWith("hierarchical:"))).Replace("formatVersion: 9", "formatVersion: 8");
        await File.WriteAllTextAsync(file, old);

        var read = (await Ok(ws.Run("link-type", "get", "Needs", "--json"))).Json;
        Assert.False(read["hierarchical"]!.GetValue<bool>());
        Assert.True(read["allowCycles"]!.GetValue<bool>());

        await Ok(ws.Run("link-type", "update", "Needs", "--hierarchical", "true"));
        var written = (await File.ReadAllTextAsync(Directory.GetFiles(ws.Root, "needs-*.yaml", SearchOption.AllDirectories).Single())).ReplaceLineEndings("\n");
        Assert.StartsWith("formatVersion: 9\n", written);
        Assert.Contains("hierarchical: true", written);
        Assert.Contains("allowCycles: false", written);
    }
}
