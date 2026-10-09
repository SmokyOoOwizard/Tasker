using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Порядок списков задач (TSK-98): <c>--sort</c> консоли, <c>?sort=</c> REST, <c>sort</c> MCP — по статусу, типу, заголовку, датам, номеру серии и полям;
/// обе реализации хранилища (папка с индексом и SQLite), по каждому типу ключа, составные ключи, обратный порядок, пустые значения (всегда в конце),
/// стабильность страниц, сочетание с фильтром, доска и автодополнение.
/// </summary>
[InProcess]
public class TaskSortTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    public static IEnumerable<object[]> Storages() => TestWorkspace.Storages();

    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    private static Task<CliResult> P(TestWorkspace ws, params string[] args) => ws.InProject("Demo", args);

    /// <summary>
    /// Статусы Todo, Doing, Done, Blocked; наборы Flow (Todo, Doing, Done) и Alt (Done, Blocked); типы Bug и Story на Flow, Chore на Alt;
    /// поля Estimate (int), Weight (float), Note (string), Flag (bool), Due (date), Level (enum Low, Mid, High — не по алфавиту), Labels (string, несколько).
    /// Задачи (по порядку создания):
    /// 1 banana Bug Todo; 2 Apple Story Doing; 3 cherry Bug Done; 4 Äpfel Chore Done; 5 дыня Story Todo (без полей); 6 Яблоко Bug Doing; 7 арбуз Chore Blocked.
    /// </summary>
    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        foreach (var status in new[] { "Todo", "Doing", "Done", "Blocked" })
            await Ok(P(ws, "status", "create", status));
        await Ok(P(ws, "status-set", "create", "Flow", "--status", "Todo", "Doing", "Done"));
        await Ok(P(ws, "status-set", "create", "Alt", "--status", "Done", "Blocked"));
        await Ok(P(ws, "task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(P(ws, "task-type", "create", "Story", "--status-set", "Flow"));
        await Ok(P(ws, "task-type", "create", "Chore", "--status-set", "Alt"));
        await Ok(P(ws, "series", "create", "Tasks", "--prefix", "TSK"));
        await Ok(P(ws, "series", "create", "Proj", "--prefix", "PRJ"));
        await Ok(P(ws, "enum", "create", "LevelValues", "--value", "Low", "Mid", "High"));
        await Ok(P(ws, "field", "create", "Estimate", "--type", "int"));
        await Ok(P(ws, "field", "create", "Weight", "--type", "float"));
        await Ok(P(ws, "field", "create", "Note", "--type", "string"));
        await Ok(P(ws, "field", "create", "Flag", "--type", "bool"));
        await Ok(P(ws, "field", "create", "Due", "--type", "date"));
        await Ok(P(ws, "field", "create", "Level", "--type", "enum", "--enum", "LevelValues"));
        await Ok(P(ws, "field", "create", "Labels", "--type", "string", "--multiple"));

        await New(ws, "banana", "Bug", "Todo", "Estimate=8", "Weight=1.5", "Note=Zeta", "Flag=true", "Due=2026-03-01", "Level=High", "Labels=b", "Labels=a");
        await New(ws, "Apple", "Story", "Doing", "Estimate=3", "Weight=10", "Note=alpha", "Flag=false", "Due=2026-01-15", "Level=Low", "Labels=a");
        await New(ws, "cherry", "Bug", "Done", "Estimate=21", "Weight=2.25", "Note=Beta", "Due=2025-12-31", "Level=Mid");
        await New(ws, "Äpfel", "Chore", "Done", "Estimate=3");
        await New(ws, "дыня", "Story", "Todo");
        await New(ws, "Яблоко", "Bug", "Doing", "Level=Low");
        await New(ws, "арбуз", "Chore", "Blocked", "Note=Gamma");
    }

    private static async Task<Guid> New(TestWorkspace ws, string title, string type, string status, params string[] fields) =>
        (await Ok(P(ws, ["task", "create", title, "--type", type, "--status", status, "--series", "TSK", .. fields.SelectMany(x => new[] { "--field", x }), "--json"])))
            .Json["id"]!.GetValue<Guid>();

    private static string[] Titles(CliResult result) =>
        result.Json["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray();

    private static async Task<string[]> Sorted(TestWorkspace ws, string sort, params string[] more) =>
        Titles(await Ok(P(ws, ["task", "list", "--json", "--all", "--sort", sort, .. more])));

    private static string[] Default { get; } = ["banana", "Apple", "cherry", "Äpfel", "дыня", "Яблоко", "арбуз"];

    // ---- по умолчанию ----

    [Theory, MemberData(nameof(Storages))]
    public async Task Without_sort_the_order_is_the_creation_order(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(Default, Titles(await Ok(P(ws, "task", "list", "--json", "--all"))));
        Assert.Equal(Default, await Sorted(ws, ""));
    }

    // ---- встроенные ключи ----

    [Theory, MemberData(nameof(Storages))]
    public async Task Status_goes_by_position_in_the_set_then_by_set_and_status_name(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // Позиция 0: Alt/Done (набор «alt» раньше «flow»), затем Flow/Todo; позиция 1: Alt/Blocked, затем Flow/Doing; позиция 2: Flow/Done.
        Assert.Equal(["Äpfel", "banana", "дыня", "арбуз", "Apple", "Яблоко", "cherry"], await Sorted(ws, "status"));
        // Убывание — обратный порядок мест; задачи с одним местом остаются в порядке создания.
        Assert.Equal(["cherry", "Apple", "Яблоко", "арбуз", "banana", "дыня", "Äpfel"], await Sorted(ws, "-status"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Type_goes_by_name_ignoring_case(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["banana", "cherry", "Яблоко", "Äpfel", "арбуз", "Apple", "дыня"], await Sorted(ws, "type"));
        Assert.Equal(["Apple", "дыня", "Äpfel", "арбуз", "banana", "cherry", "Яблоко"], await Sorted(ws, "-type"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Title_goes_ignoring_case_for_unicode_too(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // apple, banana, cherry, äpfel (ä после z), арбуз, дыня, яблоко: сравнение по нижнему регистру, кириллица тоже без учёта регистра.
        Assert.Equal(["Apple", "banana", "cherry", "Äpfel", "арбуз", "дыня", "Яблоко"], await Sorted(ws, "title"));
        Assert.Equal(["Яблоко", "дыня", "арбуз", "Äpfel", "cherry", "banana", "Apple"], await Sorted(ws, "-title"));
        // Регистр имени ключа не важен.
        Assert.Equal(await Sorted(ws, "title"), await Sorted(ws, "TITLE"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Created_and_updated_follow_the_times(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(Default, await Sorted(ws, "created"));
        Assert.Equal(Default.Reverse().ToArray(), await Sorted(ws, "-created"));

        await Ok(P(ws, "task", "update", "TSK-3", "--description", "touched"));
        await Ok(P(ws, "task", "update", "TSK-1", "--description", "touched"));
        // Менялись последними (3, потом 1): в конце по возрастанию, в начале по убыванию.
        Assert.Equal(["Apple", "Äpfel", "дыня", "Яблоко", "арбуз", "cherry", "banana"], await Sorted(ws, "updated"));
        Assert.Equal(["banana", "cherry", "арбуз", "Яблоко", "дыня", "Äpfel", "Apple"], await Sorted(ws, "-updated"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Series_goes_by_prefix_then_by_number_as_a_number(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(P(ws, "status", "create", "Todo"));
        await Ok(P(ws, "status-set", "create", "Flow", "--status", "Todo"));
        await Ok(P(ws, "task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(P(ws, "series", "create", "Tasks", "--prefix", "TSK"));
        await Ok(P(ws, "series", "create", "Proj", "--prefix", "PRJ"));
        // TSK-1…TSK-12 (в тексте «TSK-10» раньше «TSK-2»); задача вне серий; задача в обеих сериях (PRJ-1, TSK-13); задача только в PRJ (PRJ-2).
        for (var i = 1; i <= 12; i++)
            await Ok(P(ws, "task", "create", $"t{i}", "--type", "Bug", "--series", "TSK"));
        await Ok(P(ws, "task", "create", "loose", "--type", "Bug"));
        await Ok(P(ws, "task", "create", "both", "--type", "Bug", "--series", "TSK", "--series", "PRJ"));
        await Ok(P(ws, "task", "create", "proj2", "--type", "Bug", "--series", "PRJ"));

        // PRJ раньше TSK: у «both» берётся серия с меньшим префиксом (PRJ-1). Без серии — в конце.
        var up = new[] { "both", "proj2" }.Concat(Enumerable.Range(1, 12).Select(i => $"t{i}")).Append("loose").ToArray();
        Assert.Equal(up, await Sorted(ws, "series"));
        // Убывание: сначала TSK (12 раньше 2 — по числу), потом PRJ; без серии — по-прежнему в конце.
        var down = Enumerable.Range(1, 12).Reverse().Select(i => $"t{i}").Concat(["proj2", "both", "loose"]).ToArray();
        Assert.Equal(down, await Sorted(ws, "-series"));
    }

    // ---- поля по типам ----

    [Theory, MemberData(nameof(Storages))]
    public async Task Int_fields_go_by_number_with_empty_values_last_in_both_directions(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // 3 (Apple), 3 (Äpfel), 8, 21; без значения — в конце в порядке создания (дыня, Яблоко, арбуз).
        Assert.Equal(["Apple", "Äpfel", "banana", "cherry", "дыня", "Яблоко", "арбуз"], await Sorted(ws, "Estimate"));
        Assert.Equal(["cherry", "banana", "Apple", "Äpfel", "дыня", "Яблоко", "арбуз"], await Sorted(ws, "-Estimate"));
        // Имя поля — без учёта регистра.
        Assert.Equal(await Sorted(ws, "Estimate"), await Sorted(ws, "estimate"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Float_fields_go_by_number_not_by_text(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // 1.5, 2.25, 10 (по тексту «10» раньше «2.25»).
        Assert.Equal(["banana", "cherry", "Apple"], (await Sorted(ws, "Weight")).Take(3));
        Assert.Equal(["Apple", "cherry", "banana"], (await Sorted(ws, "-Weight")).Take(3));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task String_fields_go_ignoring_case(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // alpha, Beta, Gamma, Zeta (по байтам «Beta», «Gamma», «Zeta» раньше «alpha»).
        Assert.Equal(["Apple", "cherry", "арбуз", "banana"], (await Sorted(ws, "Note")).Take(4));
        Assert.Equal(["banana", "арбуз", "cherry", "Apple"], (await Sorted(ws, "-Note")).Take(4));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Bool_fields_put_false_before_true(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["Apple", "banana", "cherry", "Äpfel", "дыня", "Яблоко", "арбуз"], await Sorted(ws, "Flag"));
        Assert.Equal(["banana", "Apple", "cherry", "Äpfel", "дыня", "Яблоко", "арбуз"], await Sorted(ws, "-Flag"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Date_fields_go_by_date(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["cherry", "Apple", "banana"], (await Sorted(ws, "Due")).Take(3));
        Assert.Equal(["banana", "Apple", "cherry"], (await Sorted(ws, "-Due")).Take(3));
        Assert.Equal(["Äpfel", "дыня", "Яблоко", "арбуз"], (await Sorted(ws, "-Due")).Skip(3));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Enum_fields_go_by_the_order_of_the_values_not_alphabetically(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // Low (Apple, Яблоко), Mid (cherry), High (banana); по алфавиту было бы High, Low, Mid.
        Assert.Equal(["Apple", "Яблоко", "cherry", "banana", "Äpfel", "дыня", "арбуз"], await Sorted(ws, "Level"));
        Assert.Equal(["banana", "cherry", "Apple", "Яблоко", "Äpfel", "дыня", "арбуз"], await Sorted(ws, "-Level"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task A_multiple_field_goes_by_its_first_value_in_both_directions(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // banana: b, a (первое — b); Apple: a. По возрастанию Apple (a), banana (b); по убыванию — banana, Apple (первое значение, а не наибольшее).
        Assert.Equal(["Apple", "banana", "cherry", "Äpfel", "дыня", "Яблоко", "арбуз"], await Sorted(ws, "Labels"));
        Assert.Equal(["banana", "Apple", "cherry", "Äpfel", "дыня", "Яблоко", "арбуз"], await Sorted(ws, "-Labels"));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Own_fields_of_tasks_are_sorted_by_name_with_one_type_and_refused_with_several(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(P(ws, "task", "update", "TSK-1", "--custom-field", "Risk:int=30"));
        await Ok(P(ws, "task", "update", "TSK-2", "--custom-field", "Risk:int=4"));

        Assert.Equal(["Apple", "banana", "cherry", "Äpfel", "дыня", "Яблоко", "арбуз"], await Sorted(ws, "Risk"));
        Assert.Equal(["banana", "Apple", "cherry", "Äpfel", "дыня", "Яблоко", "арбуз"], await Sorted(ws, "-risk"));

        await Ok(P(ws, "task", "update", "TSK-3", "--custom-field", "Risk:string=high"));
        var failed = await P(ws, "task", "list", "--sort", "Risk");
        Assert.NotEqual(0, failed.Code);
        Assert.Contains("several types (int, string)", failed.Err);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task A_catalog_field_also_sorts_own_fields_with_the_same_name_and_type(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(P(ws, "task", "update", "TSK-5", "--custom-field", "Estimate:int=1"));
        await Ok(P(ws, "task", "update", "TSK-6", "--custom-field", "Estimate:string=text"));

        // дыня получила собственное поле Estimate:int=1 — как поле каталога; Яблоко со строкой другого типа остаётся без значения.
        Assert.Equal(["дыня", "Apple", "Äpfel", "banana", "cherry", "Яблоко", "арбуз"], await Sorted(ws, "Estimate"));
    }

    // ---- составная сортировка, страницы, фильтры ----

    [Theory, MemberData(nameof(Storages))]
    public async Task Several_keys_sort_in_the_order_listed(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // Сначала Estimate по убыванию, при равных (3 и 3, а также у задач без значения) — заголовок: без значения они в конце, но между собой по второму ключу.
        Assert.Equal(["cherry", "banana", "Apple", "Äpfel", "арбуз", "дыня", "Яблоко"], await Sorted(ws, "-Estimate,title"));
        Assert.Equal(["cherry", "banana", "Äpfel", "Apple", "Яблоко", "дыня", "арбуз"], await Sorted(ws, "-Estimate,-title"));
        // Тип, затем статус: в типе Bug (banana Todo, cherry Done, Яблоко Doing).
        Assert.Equal(["banana", "Яблоко", "cherry", "Äpfel", "арбуз", "дыня", "Apple"], await Sorted(ws, "type,status"));
        // Пробелы вокруг ключей не мешают.
        Assert.Equal(await Sorted(ws, "type,status"), await Sorted(ws, " type , status "));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Pages_of_a_sorted_list_do_not_overlap_and_total_count_stays(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        foreach (var sort in new[] { "Estimate", "-Level,title", "status", "-Flag", "Labels", "type" })
        {
            var all = await Sorted(ws, sort);
            var pages = new List<string>();
            for (var offset = 0; offset < all.Length; offset += 3)
            {
                var page = (await Ok(P(ws, "task", "list", "--json", "--sort", sort, "--limit", "3", "--offset", offset.ToString()))).Json;
                Assert.Equal(7, page["totalCount"]!.GetValue<int>());
                pages.AddRange(page["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()));
            }
            Assert.Equal(all, pages);
            Assert.Equal(all.Order().ToArray(), Default.Order().ToArray());
        }
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Sort_combines_with_filters_and_field_conditions(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["cherry", "banana"], await Sorted(ws, "-Estimate", "--type", "Bug", "--field", "Estimate>=8"));
        Assert.Equal(["banana", "дыня"], await Sorted(ws, "status", "--status", "Todo"));
        Assert.Equal(["Apple", "Яблоко"], await Sorted(ws, "Level", "--field", "Level=Low"));
        var page = (await Ok(P(ws, "task", "list", "--json", "--sort", "-title", "--series", "TSK", "--field", "Estimate:set", "--limit", "2"))).Json;
        Assert.Equal(4, page["totalCount"]!.GetValue<int>());
        Assert.Equal(["Äpfel", "cherry"], page["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray());
        // Краткие списки: порядок тот же.
        Assert.Equal(["Apple", "Äpfel", "banana", "cherry", "дыня", "Яблоко", "арбуз"],
            Titles(await Ok(P(ws, "task", "list", "--json", "--all", "--sort", "Estimate", "--description-length", "0"))));
    }

    // ---- ошибки ----

    [Theory, MemberData(nameof(Storages))]
    public async Task Wrong_keys_give_clear_errors_with_the_valid_ones(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var unknown = await P(ws, "task", "list", "--sort", "status,Nope");
        Assert.NotEqual(0, unknown.Code);
        Assert.Contains("unknown key 'Nope'", unknown.Err);
        Assert.Contains("status, type, title, created, updated, series", unknown.Err);
        Assert.Contains("Estimate", unknown.Err);

        Assert.Contains("an empty key", (await P(ws, "task", "list", "--sort", "status,,title")).Err);
        Assert.Contains("an empty key", (await P(ws, "task", "list", "--sort", "-")).Err);
        Assert.Contains("listed twice", (await P(ws, "task", "list", "--sort", "status,-status")).Err);
        Assert.Contains("listed twice", (await P(ws, "task", "list", "--sort", "Estimate,estimate")).Err);
    }

    // ---- доска ----

    [Theory, MemberData(nameof(Storages))]
    public async Task Board_tasks_and_board_show_sort_inside_the_columns(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(P(ws, "board", "create", "Main", "--status-set", "Flow", "Alt",
            "--column", "Open=Todo,Doing", "--column", "Closed=Done,Blocked"));

        // Open: banana (Todo), Apple (Doing), дыня (Todo), Яблоко (Doing).
        Assert.Equal(["banana", "Apple", "дыня", "Яблоко"], Titles(await Ok(P(ws, "board", "tasks", "Main", "Open", "--json"))));
        Assert.Equal(["Apple", "banana", "дыня", "Яблоко"], Titles(await Ok(P(ws, "board", "tasks", "Main", "Open", "--json", "--sort", "title"))));
        Assert.Equal(["Яблоко", "Apple", "banana", "дыня"], Titles(await Ok(P(ws, "board", "tasks", "Main", "Open", "--json", "--sort", "-status,type,title"))));
        // С условием просмотра.
        Assert.Equal(["Яблоко", "Apple"], Titles(await Ok(P(ws, "board", "tasks", "Main", "Open", "--json", "--sort", "-title", "--field", "Level=Low"))));

        var shown = (await Ok(P(ws, "board", "show", "Main", "--json", "--sort", "-title"))).Json["columns"]!.AsArray();
        Assert.Equal(["Яблоко", "дыня", "banana", "Apple"], shown[0]!["tasks"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray());
        Assert.Equal(["арбуз", "Äpfel", "cherry"], shown[1]!["tasks"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray());
        Assert.Equal(4, shown[0]!["totalCount"]!.GetValue<int>());

        var bad = await P(ws, "board", "show", "Main", "--sort", "Nope");
        Assert.NotEqual(0, bad.Code);
        Assert.Contains("unknown key 'Nope'", bad.Err);
    }

    // ---- автодополнение ----

    private static async Task<string[]> Suggest(TestWorkspace ws, string line)
    {
        var text = $"{string.Join(' ', ws.Location.Select(x => $"\"{x}\""))} --project Demo {line}";
        var result = await TestWorkspace.Invoke([$"[suggest:{text.Length}]", text]);
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Err);
        return result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(x => !x.StartsWith("--")).ToArray();
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Sort_keys_and_field_names_are_suggested_with_the_minus_and_after_commas(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var first = await Suggest(ws, "task list --sort ");
        Assert.Contains("status", first);
        Assert.Contains("series", first);
        Assert.Contains("Estimate", first);
        // Записи с «-» (по убыванию) показываются, когда ключ начат с «-»: так же, как параметры, они не должны тонуть в списке значений.
        Assert.DoesNotContain("-updated", first);
        var descending = await Suggest(ws, "task list --sort -");
        Assert.Contains("-updated", descending);
        Assert.Contains("-Estimate", descending);

        Assert.Equal(["status"], await Suggest(ws, "task list --sort st"));
        Assert.Equal(["-updated"], await Suggest(ws, "task list --sort -u"));
        Assert.Equal(["Estimate"], await Suggest(ws, "task list --sort Est"));
        var after = await Suggest(ws, "task list --sort status,");
        Assert.DoesNotContain("status,status", after);
        Assert.Contains("status,title", after);
        Assert.Contains("status,-Estimate", after);
        Assert.Equal(["status,-Level"], await Suggest(ws, "task list --sort status,-Le"));
        Assert.Contains("title", await Suggest(ws, "board tasks Main Open --sort t"));
        Assert.Contains("title", await Suggest(ws, "board show Main --sort t"));
    }

    // ---- REST ----

    [Theory, MemberData(nameof(Storages))]
    public async Task Rest_tasks_and_column_tasks_take_sort(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        async Task<JsonNode> Make(string title) => (await api.Post(api.P("/tasks"), new { title, typeId = api.TypeId })).Body!;
        foreach (var title in new[] { "bravo", "Alpha", "charlie", "Delta" })
            await Make(title);

        string[] Titles(JsonNode list) => list["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray();

        Assert.Equal(["bravo", "Alpha", "charlie", "Delta"], Titles((await api.Get(api.P("/tasks"))).Body!));
        Assert.Equal(["Alpha", "bravo", "charlie", "Delta"], Titles((await api.Get(api.P("/tasks?sort=title"))).Body!));
        Assert.Equal(["Delta", "charlie", "bravo", "Alpha"], Titles((await api.Get(api.P("/tasks?sort=-title"))).Body!));
        var page = (await api.Get(api.P("/tasks?sort=title&offset=1&limit=2"))).Body!;
        Assert.Equal(4, page["totalCount"]!.GetValue<int>());
        Assert.Equal(["bravo", "charlie"], Titles(page));

        var bad = await api.Get(api.P("/tasks?sort=title,Nope"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Contains("unknown key", bad.Body!.ToJsonString());
    }

    // ---- MCP ----

    [Fact]
    public async Task Mcp_list_tasks_and_get_board_take_sort()
    {
        await _daemon.Workspace("a", "Alpha");
        var status = await _daemon.Start();
        var key = status.Workspaces.Single().Key!;
        var projectId = JsonNode.Parse(await _daemon.CallTool(key, "list_projects"))!["data"]![0]!["id"]!.GetValue<string>();

        async Task<JsonNode> Call(string tool, object args)
        {
            var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject();
            node["projectId"] ??= projectId;
            var (isError, text) = await _daemon.CallToolResult(key, tool, node);
            Assert.False(isError, $"{tool} failed: {text}");
            return JsonNode.Parse(text)!;
        }

        string Id(JsonNode node) => node["id"]!.GetValue<string>();
        var open = await Call("create_status", new { name = "Open", color = "#112233" });
        var closed = await Call("create_status", new { name = "Closed", color = "#445566" });
        var set = await Call("create_status_set", new { name = "Flow", statusIds = new[] { Id(open), Id(closed) } });
        var type = await Call("create_task_type", new { name = "Bug", statusSetId = Id(set) });
        var estimate = await Call("create_field", new { name = "Estimate", type = "int" });
        async Task Make(string title, string statusId, int? points) => await Call("create_task", new
        {
            title, typeId = Id(type), statusId,
            fields = points == null ? null : new { values = new[] { new { fieldId = Id(estimate), values = new[] { points.ToString() } } } }
        });
        await Make("one", Id(closed), 5);
        await Make("two", Id(open), null);
        await Make("three", Id(open), 20);

        string[] Titles(JsonNode list) => list["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray();

        Assert.Equal(["one", "two", "three"], Titles(await Call("list_tasks", new { })));
        Assert.Equal(["two", "three", "one"], Titles(await Call("list_tasks", new { sort = "status" })));
        Assert.Equal(["three", "one", "two"], Titles(await Call("list_tasks", new { sort = "-Estimate" })));
        Assert.Equal(["one", "three", "two"], Titles(await Call("list_tasks", new { sort = "Estimate" })));
        var paged = await Call("list_tasks", new { sort = "title", offset = 1, limit = 1 });
        Assert.Equal(3, paged["totalCount"]!.GetValue<int>());
        Assert.Equal(["three"], Titles(paged));

        var (isError, text) = await _daemon.CallToolResult(key, "list_tasks", JsonNode.Parse($$"""{"projectId":"{{projectId}}","sort":"Nope"}"""));
        Assert.True(isError);
        Assert.StartsWith("[invalid]", text);
        Assert.Contains("unknown key 'Nope'", text);

        var drop = new Dictionary<string, string> { [Id(set)] = Id(open) };
        var board = await Call("create_board", new
        {
            name = "Main", statusSetIds = new[] { Id(set) },
            columns = new object[] { new { name = "All", statusIds = new[] { Id(open), Id(closed) }, dropStatuses = drop } }
        });
        var view = await Call("get_board", new { boardId = Id(board), sort = "-title" });
        Assert.Equal(["two", "three", "one"], Titles(view["columns"]![0]!["tasks"]!));
    }
}
