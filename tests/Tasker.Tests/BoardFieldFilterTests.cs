using System.Net;
using System.Text.Json.Nodes;
using Autofac;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core;
using Tasker.Core.Boards;
using Tasker.Core.Fields;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.Workspace;
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Storage.Files;
using Tasker.Storage.Files.Storages;
using Xunit;

namespace Tasker.Tests;

/// <summary>Условия колонки доски по полям (хранятся в колонке): HTTP (оба хранилища), консоль, каскады, перенос задачи, формат файла и таблицы БД.</summary>
public class BoardFieldFilterTests
{
    public static IEnumerable<object[]> Storages() => TestWorkspace.Storages();

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();
    private static string Version(JsonNode node) => node["version"]!.GetValue<string>();
    private static object[] Texts(params string[] values) => values;

    private static async Task<JsonNode> Created(Task<(HttpStatusCode Status, JsonNode? Body)> call)
    {
        var (status, body) = await call;
        Assert.True(status == HttpStatusCode.Created, $"{status}: {body}");
        return body!;
    }

    private static async Task<JsonNode> Ok(Task<(HttpStatusCode Status, JsonNode? Body)> call)
    {
        var (status, body) = await call;
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}");
        return body!;
    }

    private static async Task<JsonNode> Fails(Task<(HttpStatusCode Status, JsonNode? Body)> call, HttpStatusCode expected, string? contains = null)
    {
        var (status, body) = await call;
        Assert.True(status == expected, $"{status}: {body}");
        if (contains != null)
            Assert.Contains(contains, Error(body!), StringComparison.OrdinalIgnoreCase);
        return body!;
    }

    /// <summary>Текст ошибки API (в JSON-представлении узла кавычки были бы экранированы).</summary>
    private static string Error(JsonNode body) => body["error"]?.GetValue<string>() ?? body.ToJsonString();

    private static string[] Titles(JsonNode list) => list["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).Order().ToArray();

    /// <summary>Проект с набором Flow (статусы Open и Closed), типом Bug (поля Level — enum Low/High и Estimate — int) и каталогом полей.</summary>
    private sealed class Project : IAsyncDisposable
    {
        public required ApiHost Api { get; init; }
        public required string SetId { get; init; }
        public required string Open { get; init; }
        public required string Closed { get; init; }
        public required string TypeId { get; init; }
        public required JsonNode Level { get; init; }
        public required JsonNode Estimate { get; init; }
        public required JsonNode Levels { get; init; }
        public ValueTask DisposeAsync() => Api.DisposeAsync();

        public string Low => Levels["values"]![0]!["id"]!.GetValue<string>();
        public string High => Levels["values"]![1]!["id"]!.GetValue<string>();

        public async Task<JsonNode> Task(string title, string? level, int? estimate = null, string? status = null)
        {
            var values = new List<object>();
            if (level != null)
                values.Add(new { fieldId = Id(Level), values = Texts(level) });
            if (estimate != null)
                values.Add(new { fieldId = Id(Estimate), values = Texts(estimate.ToString()!) });
            var task = await Created(Api.Post(Api.P("/tasks"), new { title, typeId = TypeId, statusId = status, fields = new { values } }));
            return task;
        }

        public async Task<JsonNode> Board(string name, params object[] columns) =>
            await Created(Api.Post(Api.P("/boards"), new { name, statusSetIds = new[] { SetId }, columns }));

        public object Column(string name, string[] statuses, params string[] filters) => new
        {
            name, statusIds = statuses, dropStatuses = new Dictionary<string, string> { [SetId] = statuses[0] }, fieldFilters = filters
        };

        public async Task<string[]> Titles(JsonNode board, int column, string query = "") =>
            BoardFieldFilterTests.Titles(await Ok(Api.Get(Api.P($"/boards/{Id(board)}/columns/{board["columns"]![column]!["id"]}/tasks{query}"))));
    }

    private static async Task<Project> Setup(string storage)
    {
        var api = await ApiHost.Start(storage);
        var open = await Created(api.Post(api.P("/statuses"), new { name = "Open", color = "#112233" }));
        var closed = await Created(api.Post(api.P("/statuses"), new { name = "Closed", color = "#445566" }));
        var set = await Created(api.Post(api.P("/status-sets"), new { name = "Flow", statusIds = new[] { Id(open), Id(closed) } }));
        var levels = await Created(api.Post(api.P("/enums"), new { name = "LevelValues", values = Texts("Low", "High") }));
        var level = await Created(api.Post(api.P("/fields"), new { name = "Level", type = "enum", enumId = Id(levels) }));
        var estimate = await Created(api.Post(api.P("/fields"), new { name = "Estimate", type = "int" }));
        var type = await Created(api.Post(api.P("/task-types"), new
        {
            name = "Bug", statusSetId = Id(set),
            fields = new[] { new { fieldId = Id(level), required = false }, new { fieldId = Id(estimate), required = false } }
        }));
        return new Project
        {
            Api = api, SetId = Id(set), Open = Id(open), Closed = Id(closed), TypeId = Id(type), Level = level, Estimate = estimate, Levels = levels
        };
    }

    // ---- условия хранятся в колонке и задачи попадают в колонку сами ----

    [Theory, MemberData(nameof(Storages))]
    public async Task A_column_stores_conditions_by_id_and_collects_the_matching_tasks_by_itself(string storage)
    {
        await using var p = await Setup(storage);
        var api = p.Api;
        // Один статус в двух колонках — можно, если условия исключают друг друга (Level=High и Level!=High).
        var board = await p.Board("Main",
            p.Column("Critical", [p.Open], "Level=High"),
            p.Column("Rest", [p.Open], "Level!=High"),
            p.Column("Closed", [p.Closed]));

        // Хранится по id поля и значения: оператор словом, значение enum — id.
        var stored = board["columns"]![0]!["fieldConditions"]!.AsArray().Single()!;
        Assert.Equal(Id(p.Level), stored["fieldId"]!.GetValue<string>());
        Assert.Equal("equal", stored["operator"]!.GetValue<string>());
        Assert.Equal(p.High, stored["value"]!.GetValue<string>());
        Assert.Equal("notEqual", board["columns"]![1]!["fieldConditions"]![0]!["operator"]!.GetValue<string>());
        Assert.Empty(board["columns"]![2]!["fieldConditions"]!.AsArray());

        await p.Task("a", "High", 5);
        var b = await p.Task("b", "Low", 2);
        await p.Task("c", null);
        await p.Task("d", "High", 1, status: p.Closed);

        Assert.Equal(["a"], await p.Titles(board, 0));
        Assert.Equal(["b", "c"], await p.Titles(board, 1));
        Assert.Equal(["d"], await p.Titles(board, 2));

        // Правка поля задачи — и она в другой колонке, без переноса.
        await Ok(api.Patch(api.P($"/tasks/{Id(b)}"), new
        {
            version = Version(b), fields = new { values = new[] { new { fieldId = Id(p.Level), values = Texts("High") } } }
        }));
        Assert.Equal(["a", "b"], await p.Titles(board, 0));
        Assert.Equal(["c"], await p.Titles(board, 1));

        // Условие колонки И условие просмотра (?field=): пересечение, колонка своего условия не теряет.
        Assert.Equal(["a"], await p.Titles(board, 0, "?field=Estimate%3E%3D5"));
        Assert.Equal(["b"], await p.Titles(board, 0, "?field=Estimate%3C5"));
        Assert.Empty(await p.Titles(board, 1, "?field=Level%3DHigh"));
        var count = await Ok(api.Get(api.P($"/boards/{Id(board)}/columns/{board["columns"]![0]!["id"]}/tasks?field=Estimate%3E%3D5")));
        Assert.Equal(1, count["totalCount"]!.GetValue<int>());

        // Переименование поля и значения enum колонку не ломает: условия хранятся по id.
        var level = (await Ok(api.Get(api.P($"/fields/{Id(p.Level)}"))));
        await Ok(api.Patch(api.P($"/fields/{Id(p.Level)}"), new { version = Version(level), name = "Severity" }));
        var levels = await Ok(api.Get(api.P($"/enums/{Id(p.Levels)}")));
        await Ok(api.Patch(api.P($"/enums/{Id(p.Levels)}"), new
        {
            version = Version(levels), values = new object[] { new { id = p.Low, name = "Minor" }, new { id = p.High, name = "Critical" } }
        }));
        Assert.Equal(["a", "b"], await p.Titles(board, 0));
        Assert.Equal(["c"], await p.Titles(board, 1));
        // Новое имя поля годится и в условии просмотра.
        Assert.Equal(["a"], await p.Titles(board, 0, "?field=Severity%3DCritical&field=Estimate%3E%3D5"));

        // Условия живут вместе с доской: чтение, правка без колонок и правка колонок без fieldFilters их сохраняют.
        var read = await Ok(api.Get(api.P($"/boards/{Id(board)}")));
        Assert.Equal(p.High, read["columns"]![0]!["fieldConditions"]![0]!["value"]!.GetValue<string>());
        var renamed = await Ok(api.Patch(api.P($"/boards/{Id(board)}"), new { version = Version(read), name = "Main 2" }));
        Assert.Equal(Id(p.Level), renamed["columns"]![1]!["fieldConditions"]![0]!["fieldId"]!.GetValue<string>());

        var keep = renamed["columns"]!.AsArray().Select(c => new
        {
            id = c!["id"]!.GetValue<string>(), name = c["name"]!.GetValue<string>(),
            statusIds = c["statusIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray(),
            dropStatuses = new Dictionary<string, string> { [p.SetId] = c["statusIds"]![0]!.GetValue<string>() }
        }).ToArray();
        var kept = await Ok(api.Patch(api.P($"/boards/{Id(board)}"), new { version = Version(renamed), columns = keep }));
        Assert.Single(kept["columns"]![0]!["fieldConditions"]!.AsArray());
        Assert.Single(kept["columns"]![1]!["fieldConditions"]!.AsArray());

        // [] — условий нет (но тогда колонки с общим статусом пересекаются: доска не принимается), другой текст — заменяет.
        var cleared = keep.Select(c => new { c.id, c.name, c.statusIds, c.dropStatuses, fieldFilters = Array.Empty<string>() }).ToArray();
        await Fails(api.Patch(api.P($"/boards/{Id(board)}"), new { version = Version(kept), columns = cleared }), HttpStatusCode.BadRequest, "already used by column");
        var replaced = keep.Select(c => new
        {
            c.id, c.name, c.statusIds, c.dropStatuses,
            fieldFilters = c.name == "Critical" ? new[] { "Estimate>=5" } : c.name == "Rest" ? new[] { "Estimate<5" } : Array.Empty<string>()
        }).ToArray();
        var changed = await Ok(api.Patch(api.P($"/boards/{Id(board)}"), new { version = Version(kept), columns = replaced }));
        Assert.Equal("greaterOrEqual", changed["columns"]![0]!["fieldConditions"]![0]!["operator"]!.GetValue<string>());
        Assert.Equal(Id(p.Estimate), changed["columns"]![0]!["fieldConditions"]![0]!["fieldId"]!.GetValue<string>());
        Assert.Equal("5", changed["columns"]![0]!["fieldConditions"]![0]!["value"]!.GetValue<string>());
        Assert.Equal(["a"], await p.Titles(changed, 0));
        Assert.Equal(["b"], await p.Titles(changed, 1)); // «меньше 5» нужно значение: у c оно не задано
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Conditions_are_validated_against_the_catalog_and_a_shared_status_needs_exclusive_conditions(string storage)
    {
        await using var p = await Setup(storage);
        var api = p.Api;

        async Task Bad(string contains, params string[] filters) =>
            await Fails(api.Post(api.P("/boards"), new { name = "X", statusSetIds = new[] { p.SetId }, columns = new[] { p.Column("C", [p.Open], filters) } }),
                HttpStatusCode.BadRequest, contains);

        await Bad("no field 'Nope'", "Nope=1");
        await Bad("does not apply to field 'Level'", "Level>=High");
        await Bad("is not an integer", "Estimate=abc");
        await Bad("is not a value of enum", "Level=Huge");
        await Bad("expected", "Level");
        await Bad("unknown ':soon'", "Estimate:soon");
        await Bad("Columns[0].FieldFilters", "Nope=1");

        // Общий статус: без условий, с одинаковыми, с условиями, которые могут выполняться вместе, — отказ.
        async Task Shared(string contains, string[] first, string[] second) =>
            await Fails(api.Post(api.P("/boards"), new
            {
                name = "Shared", statusSetIds = new[] { p.SetId }, columns = new[] { p.Column("A", [p.Open], first), p.Column("B", [p.Open], second) }
            }), HttpStatusCode.BadRequest, contains);
        await Shared("already used by column 'A'", [], []);
        await Shared("already used by column 'A'", ["Level=High"], []);
        await Shared("already used by column 'A'", ["Level=High"], ["Level=High"]);
        await Shared("already used by column 'A'", ["Level=High"], ["Estimate=1"]);
        await Shared("already used by column 'A'", ["Estimate>=3"], ["Estimate>=2"]);
        await Shared("already used by column 'A'", ["Level!=High"], ["Level!=Low"]);

        // Условия, которые вместе не выполняются, — доска принимается.
        async Task Exclusive(string[] first, string[] second) => await Created(api.Post(api.P("/boards"), new
        {
            name = "E" + Guid.NewGuid().ToString("N")[..6], statusSetIds = new[] { p.SetId },
            columns = new[] { p.Column("A", [p.Open], first), p.Column("B", [p.Open], second) }
        }));
        await Exclusive(["Level=High"], ["Level=Low"]);
        await Exclusive(["Level=High"], ["Level!=High"]);
        await Exclusive(["Level:set"], ["Level:unset"]);
        await Exclusive(["Level:attached"], ["Level:detached"]);
        await Exclusive(["Estimate>=3"], ["Estimate<3"]);
        await Exclusive(["Estimate>3"], ["Estimate<=3"]);
        await Exclusive(["Estimate>5"], ["Estimate<3", "Level=High"]);
        await Exclusive(["Estimate=3"], ["Estimate=4"]);
        await Exclusive(["Estimate:unset"], ["Estimate>=0"]);
        await Exclusive(["Level=High", "Estimate>=3"], ["Level=High", "Estimate<3"]);

        // Одинаковые условия внутри колонки собираются в одно; статусы одной колонки не пересекаются сами с собой.
        var board = await p.Board("Dup", p.Column("A", [p.Open], "Level=High", "level=high"));
        Assert.Single(board["columns"]![0]!["fieldConditions"]!.AsArray());
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task A_field_used_by_a_column_is_not_deleted_or_retyped_but_may_be_renamed(string storage)
    {
        await using var p = await Setup(storage);
        var api = p.Api;
        var spare = await Created(api.Post(api.P("/fields"), new { name = "Spare", type = "int" }));
        await p.Board("Main", p.Column("Big", [p.Open], "Spare>=3"));

        var deleted = await Fails(api.Delete(api.P($"/fields/{Id(spare)}?version={Version(spare)}")), HttpStatusCode.Conflict, "board 'Main' (column 'Big')");
        Assert.Equal("in_use", deleted["code"]!.GetValue<string>());
        await Fails(api.Patch(api.P($"/fields/{Id(spare)}"), new { version = Version(spare), type = "string" }), HttpStatusCode.Conflict, "board 'Main' (column 'Big')");

        // Множественность тоже: от неё зависит, исключают ли друг друга условия колонок с общим статусом.
        await Fails(api.Patch(api.P($"/fields/{Id(spare)}"), new { version = Version(spare), multiple = true }), HttpStatusCode.Conflict, "board 'Main' (column 'Big')");

        // Имя меняется — условие хранится по id.
        var renamed = await Ok(api.Patch(api.P($"/fields/{Id(spare)}"), new { version = Version(spare), name = "Spare2" }));
        var again = renamed;
        Assert.Equal(Id(spare), (await Ok(api.Get(api.P("/boards"))))["data"]![0]!["columns"]![0]!["fieldConditions"]![0]!["fieldId"]!.GetValue<string>());

        // Убрали условие — поле снова свободно.
        var board = (await Ok(api.Get(api.P("/boards")))) ["data"]![0]!;
        var column = board["columns"]![0]!;
        await Ok(api.Patch(api.P($"/boards/{Id(board)}"), new
        {
            version = Version(board),
            columns = new[]
            {
                new { id = Id(column), name = "Big", statusIds = new[] { p.Open }, dropStatuses = new Dictionary<string, string>(), fieldFilters = Array.Empty<string>() }
            }
        }));
        Assert.Equal(HttpStatusCode.NoContent, (await api.Delete(api.P($"/fields/{Id(spare)}?version={Version(again)}"))).Status);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task A_removed_enum_value_used_by_a_column_needs_a_choice_clear_drops_the_condition_reassign_replaces_the_value(string storage)
    {
        await using var p = await Setup(storage);
        var api = p.Api;
        var board = await p.Board("Main", p.Column("Critical", [p.Open], "Level=High"), p.Column("Closed", [p.Closed], "Level=High", "Estimate>=1"));
        var onlyLow = new object[] { new { id = p.Low, name = "Low" } };

        // Значение входит в условия колонок: без выбора — 409 in_use со всеми колонками (задач с ним нет).
        var levels = await Ok(api.Get(api.P($"/enums/{Id(p.Levels)}")));
        var refused = await Fails(api.Patch(api.P($"/enums/{Id(p.Levels)}"), new { version = Version(levels), values = onlyLow }),
            HttpStatusCode.Conflict, "board 'Main' (column 'Critical')");
        Assert.Contains("board 'Main' (column 'Closed')", Error(refused));
        Assert.Equal("in_use", refused["code"]!.GetValue<string>());
        Assert.Equal(2, (await Ok(api.Get(api.P($"/enums/{Id(p.Levels)}"))))["values"]!.AsArray().Count);

        // Переназначение: условия получают другое значение; доска остаётся прежней по всему остальному.
        var reassigned = await Ok(api.Patch(api.P($"/enums/{Id(p.Levels)}"), new { version = Version(levels), values = onlyLow, removed = new { reassignTo = p.Low } }));
        Assert.Equal(2, reassigned["affectedColumns"]!.GetValue<int>());
        Assert.Equal(0, reassigned["affectedTasks"]!.GetValue<int>());
        var afterReassign = await Ok(api.Get(api.P($"/boards/{Id(board)}")));
        Assert.Equal(p.Low, afterReassign["columns"]![0]!["fieldConditions"]![0]!["value"]!.GetValue<string>());
        Assert.Equal(p.Low, afterReassign["columns"]![1]!["fieldConditions"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("greaterOrEqual", afterReassign["columns"]![1]!["fieldConditions"]![1]!["operator"]!.GetValue<string>());
        Assert.Null((await Ok(api.Get(api.P($"/enums/{Id(p.Levels)}"))))["affectedColumns"]);

        // «Убрать»: условие на значение исчезает из колонки, остальные условия остаются.
        var more = await Ok(api.Patch(api.P($"/enums/{Id(p.Levels)}"), new
        {
            version = Version(reassigned), values = new object[] { new { id = p.Low, name = "Low" }, new { name = "Extra" } }
        }));
        var extra = more["values"]![1]!["id"]!.GetValue<string>();
        var boardNow = await Ok(api.Get(api.P($"/boards/{Id(board)}")));
        var withExtra = boardNow["columns"]!.AsArray().Select(c => new
        {
            id = c!["id"]!.GetValue<string>(), name = c["name"]!.GetValue<string>(),
            statusIds = c["statusIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray(),
            dropStatuses = new Dictionary<string, string>(),
            fieldFilters = c["name"]!.GetValue<string>() == "Closed" ? new[] { "Level=Extra", "Estimate>=1" } : new[] { "Level=Extra" }
        }).ToArray();
        await Ok(api.Patch(api.P($"/boards/{Id(boardNow)}"), new { version = Version(boardNow), columns = withExtra }));
        var cleared = await Ok(api.Patch(api.P($"/enums/{Id(p.Levels)}"), new
        {
            version = Version(more), values = new object[] { new { id = p.Low, name = "Low" } }, removed = new { clear = true }
        }));
        Assert.Equal(2, cleared["affectedColumns"]!.GetValue<int>());
        var afterClear = await Ok(api.Get(api.P($"/boards/{Id(board)}")));
        Assert.Empty(afterClear["columns"]![0]!["fieldConditions"]!.AsArray());
        var left = afterClear["columns"]![1]!["fieldConditions"]!.AsArray().Single()!;
        Assert.Equal(Id(p.Estimate), left["fieldId"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task A_removed_enum_value_is_refused_when_the_change_would_make_two_columns_show_the_same_tasks(string storage)
    {
        await using var p = await Setup(storage);
        var api = p.Api;
        var board = await p.Board("Main", p.Column("Critical", [p.Open], "Level=High"), p.Column("Rest", [p.Open], "Level!=High"));
        var levels = await Ok(api.Get(api.P($"/enums/{Id(p.Levels)}")));
        var onlyLow = new object[] { new { id = p.Low, name = "Low" } };

        // «Убрать» сняло бы условия обеих колонок: общий статус — задачи в обеих. Ничего не меняется: ни доска, ни перечисление.
        await Fails(api.Patch(api.P($"/enums/{Id(p.Levels)}"), new { version = Version(levels), values = onlyLow, removed = new { clear = true } }),
            HttpStatusCode.Conflict, "would show the same tasks");
        Assert.Equal(2, (await Ok(api.Get(api.P($"/enums/{Id(p.Levels)}"))))["values"]!.AsArray().Count);
        Assert.Equal(p.High, (await Ok(api.Get(api.P($"/boards/{Id(board)}"))))["columns"]![0]!["fieldConditions"]![0]!["value"]!.GetValue<string>());

        // Переназначение сохраняет исключающие друг друга условия (Level=Low и Level!=Low) — проходит.
        var reassigned = await Ok(api.Patch(api.P($"/enums/{Id(p.Levels)}"), new { version = Version(levels), values = onlyLow, removed = new { reassignTo = p.Low } }));
        Assert.Equal(2, reassigned["affectedColumns"]!.GetValue<int>());
    }

    // ---- перенос задачи ----

    [Theory, MemberData(nameof(Storages))]
    public async Task A_task_that_does_not_match_the_conditions_of_the_column_is_not_moved_into_it(string storage)
    {
        await using var p = await Setup(storage);
        var api = p.Api;
        var board = await p.Board("Main", p.Column("Critical", [p.Open], "Level=High", "Estimate>=3"), p.Column("Done", [p.Closed]));
        var critical = board["columns"]![0]!;
        var done = board["columns"]![1]!;

        var high = await p.Task("high", "High", 5, status: p.Closed);
        var low = await p.Task("low", "Low", 5, status: p.Closed);
        var small = await p.Task("small", "High", 1, status: p.Closed);

        async Task<(HttpStatusCode Status, JsonNode? Body)> Move(JsonNode column, JsonNode task) =>
            await api.Post(api.P($"/boards/{Id(board)}/columns/{Id(column)}/move"), new { taskId = Id(task), version = Version(task) });

        // Подходящая задача получает статус из правила переноса и сразу видна в колонке.
        var moved = await Ok(Move(critical, high));
        Assert.Equal(p.Open, moved["statusId"]!.GetValue<string>());
        Assert.Equal(["high"], await p.Titles(board, 0));

        // Не подходящая — отказ с условиями, статус не меняется: статус условия по полям не исправит.
        var refused = await Fails(Move(critical, low), HttpStatusCode.BadRequest, "does not match the field conditions of column 'Critical'");
        Assert.Contains("Level=High", Error(refused));
        Assert.Contains("Estimate>=3", Error(refused));
        Assert.Equal(p.Closed, (await Ok(api.Get(api.P($"/tasks/{Id(low)}"))))["statusId"]!.GetValue<string>());
        await Fails(Move(critical, small), HttpStatusCode.BadRequest, "does not match");

        // Колонка без условий принимает любую; после правки полей задача подходит и переносится.
        Assert.Equal(p.Closed, (await Ok(Move(done, moved)))["statusId"]!.GetValue<string>());
        var freshLow = await Ok(api.Get(api.P($"/tasks/{Id(low)}")));
        var fixedLow = await Ok(api.Patch(api.P($"/tasks/{Id(low)}"), new
        {
            version = Version(freshLow), fields = new { values = new[] { new { fieldId = Id(p.Level), values = Texts("High") } } }
        }));
        Assert.Equal(p.Open, (await Ok(Move(critical, fixedLow)))["statusId"]!.GetValue<string>());
        Assert.Equal(["low"], await p.Titles(board, 0));
    }

    // ---- консоль ----

    private static async Task<CliResult> CliOk(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    /// <summary>Вывод <c>board get</c> без id в скобках: названия статусов и наборов идут с ними.</summary>
    private static string Plain(CliResult result) => System.Text.RegularExpressions.Regex.Replace(result.Out, @" \([0-9a-f-]{36}\)", "");

    private static async Task<CliResult> CliFail(Task<CliResult> run, string message)
    {
        var result = await run;
        Assert.Equal(1, result.Code);
        Assert.Contains(message, result.Err);
        return result;
    }

    [InProcess]
    [Theory, MemberData(nameof(Storages))]
    public async Task The_console_creates_and_changes_column_conditions_and_shows_them(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await CliOk(ws.Run("project", "create", "Demo"));
        foreach (var status in new[] { "Open", "Closed" })
            await CliOk(ws.Run("status", "create", status));
        await CliOk(ws.Run("status-set", "create", "Flow", "--status", "Open", "Closed"));
        await CliOk(ws.Run("enum", "create", "Priority", "--value", "Low", "High"));
        await CliOk(ws.Run("field", "create", "Severity", "--type", "enum", "--enum", "Priority"));
        await CliOk(ws.Run("field", "create", "Estimate", "--type", "int"));
        await CliOk(ws.Run("task-type", "create", "Bug", "--status-set", "Flow", "--field", "Severity", "--field", "Estimate"));
        await CliOk(ws.Run("series", "create", "Tasks", "--prefix", "TSK"));

        // Колонка: «--column-filter "Колонка:условие"»; название колонки — как в --column, условия — как у --field.
        await CliOk(ws.Run("board", "create", "Main", "--status-set", "Flow",
            "--column", "Critical=Open", "--column", "Rest=Open", "--column", "Closed=Closed",
            "--column-filter", "Critical:Severity=High", "--column-filter", "Critical:Estimate>=3",
            "--column-filter", "Rest:Severity!=High"));
        foreach (var (title, severity, estimate) in new[] { ("One", "High", "5"), ("Two", "Low", "9"), ("Three", "High", "1") })
            await CliOk(ws.Run("task", "create", title, "--type", "Bug", "--series", "TSK", "--field", $"Severity={severity}", "--field", $"Estimate={estimate}"));

        var get = await CliOk(ws.Run("board", "get", "Main"));
        Assert.Contains("  Critical: Open; where Severity=High and Estimate>=3\n", Plain(get));
        Assert.Contains("  Rest: Open; where Severity!=High\n", Plain(get));
        Assert.Contains("  Closed: Closed\n", Plain(get));

        static string[] Column(CliResult r) => r.Data.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(' ')[^1]).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["One"], Column(await CliOk(ws.Run("board", "tasks", "Main", "Critical"))));
        Assert.Equal(["Two"], Column(await CliOk(ws.Run("board", "tasks", "Main", "Rest")))); // Three — High: «!=High» ему не подходит

        var show = await CliOk(ws.Run("board", "show", "Main"));
        Assert.Contains("Critical [Severity=High and Estimate>=3] (1)", show.Out);
        Assert.Contains("Rest [Severity!=High] (1)", show.Out);
        Assert.Contains("Closed (0)", show.Out);
        // Просмотровое --field пересекается с условиями колонки.
        Assert.Empty(Column(await CliOk(ws.Run("board", "tasks", "Main", "Critical", "--field", "Estimate>=6"))));
        var json = (await CliOk(ws.Run("board", "show", "Main", "--json"))).Json;
        Assert.Equal(["Severity=High", "Estimate>=3"], json["columns"]![0]!["fieldFilters"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());

        // Только условия: колонки остаются, у названной условия заменяются, у остальных сохраняются.
        await CliOk(ws.Run("board", "update", "Main", "--column-filter", "Critical:Severity=High"));
        var changed = await CliOk(ws.Run("board", "get", "Main"));
        Assert.Contains("  Critical: Open; where Severity=High\n", Plain(changed));
        Assert.Contains("  Rest: Open; where Severity!=High\n", Plain(changed));
        Assert.Equal(["One", "Three"], Column(await CliOk(ws.Run("board", "tasks", "Main", "Critical"))));

        // Новые --column: колонка с прежним названием остаётся той же колонкой и сохраняет свои условия.
        await CliOk(ws.Run("board", "update", "Main", "--column", "Critical=Open", "--column", "Rest=Open", "--column", "Closed=Closed"));
        var recreated = await CliOk(ws.Run("board", "get", "Main"));
        Assert.Contains("  Critical: Open; where Severity=High\n", Plain(recreated));
        Assert.Contains("  Rest: Open; where Severity!=High\n", Plain(recreated));
        // Условия колонок с общим статусом должны исключать друг друга: снять условия у одной из них нельзя.
        await CliFail(ws.Run("board", "update", "Main", "--column-filter", "Rest:"), "already used by column");
        // «Колонка:» без условия — убрать условия колонки.
        await CliOk(ws.Run("board", "update", "Main", "--column-filter", "Closed:Estimate>=1"));
        Assert.Contains("  Closed: Closed; where Estimate>=1\n", Plain(await CliOk(ws.Run("board", "get", "Main"))));
        await CliOk(ws.Run("board", "update", "Main", "--column-filter", "Closed:"));
        Assert.Contains("  Closed: Closed\n", Plain(await CliOk(ws.Run("board", "get", "Main"))));

        // Ошибки: колонка не названа, нет поля, оператор не подходит.
        await CliFail(ws.Run("board", "update", "Main", "--column-filter", "Nope:Estimate>=1"), "name of one of the columns");
        await CliFail(ws.Run("board", "update", "Main", "--column-filter", "Estimate>=1"), "name of one of the columns");
        await CliFail(ws.Run("board", "update", "Main", "--column-filter", "Closed:Nope=1"), "no field 'Nope'");
        await CliFail(ws.Run("board", "update", "Main", "--column-filter", "Closed:Severity>=High"), "does not apply to field 'Severity'");
        await CliFail(ws.Run("board", "update", "Main", "--column-filter", "Closed:Estimate"), "Columns[2].FieldFilters");
    }

    // ---- формат файла и таблицы БД ----

    [InProcess]
    [Fact]
    public async Task A_board_file_keeps_conditions_by_id_in_format_6_and_a_file_of_format_5_is_read_without_them_and_migrated()
    {
        var root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var container = OpenFiles(root);
            var project = Guid.NewGuid();
            var (setId, open, board) = NewBoard(project, Guid.NewGuid());
            var boards = container.Resolve<IBoardStorage>();
            await boards.Add(board);

            var folder = Path.Combine(root, ".tasker", "projects", project.ToString(), "boards");
            var text = (await File.ReadAllTextAsync(Directory.GetFiles(folder).Single())).ReplaceLineEndings("\n");
            Assert.StartsWith("formatVersion: 9\n", text);
            var fieldId = board.Columns[0].FieldConditions[0].FieldId;
            Assert.Contains($"  fieldFilters:\n  - field: {fieldId}\n    op: greaterOrEqual\n    value: \"3\"\n  - field: {board.Columns[0].FieldConditions[1].FieldId}\n    op: unset\n", text);
            Assert.DoesNotContain("fieldFilters", text[(text.IndexOf("name: Rest", StringComparison.Ordinal))..]);

            // Другой процесс читает те же условия с диска.
            await using var fresh = OpenFiles(root);
            var read = (await fresh.Resolve<IBoardStorage>().GetById(project, board.Id))!;
            Assert.Equal(board.Columns[0].FieldConditions, read.Columns[0].FieldConditions);
            Assert.Empty(read.Columns[1].FieldConditions);

            // Файл формата 5 (без условий) читается, на диске не меняется; tasker migrate поднимает версию и ничего больше не трогает.
            var path = Directory.GetFiles(folder).Single();
            var old = $"formatVersion: 5\nid: {board.Id}\nname: Old\nstatusSets:\n- {setId}\ncolumns:\n- id: {Guid.NewGuid()}\n  name: A\n  statuses:\n  - {open}\n";
            await File.WriteAllTextAsync(path, old);
            var oldRead = (await fresh.Resolve<IBoardStorage>().GetById(project, board.Id))!;
            Assert.Empty(oldRead.Columns.Single().FieldConditions);
            Assert.Equal(old, await File.ReadAllTextAsync(path));

            var report = await fresh.Resolve<IFileMigration>().Run(new MigrationOptions());
            Assert.Equal(FormatVersions.Current, report.CurrentFormat);
            Assert.Single(report.Migrated);
            Assert.Equal(old.Replace("formatVersion: 5", "formatVersion: 9"), (await File.ReadAllTextAsync(Directory.GetFiles(folder).Single())).ReplaceLineEndings("\n"));

            // Файл с неизвестным оператором (записан более новым Tasker) не читается молча.
            await File.WriteAllTextAsync(Directory.GetFiles(folder).Single(),
                old.Replace("formatVersion: 5", "formatVersion: 9") + $"  fieldFilters:\n  - field: {Guid.NewGuid()}\n    op: matches\n    value: x\n");
            await Assert.ThrowsAsync<UnsupportedFormatException>(() => fresh.Resolve<IBoardStorage>().GetById(project, board.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IContainer OpenFiles(string root)
    {
        var builder = new ContainerBuilder();
        builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterModule(new FileStorageModule(root));
        return builder.Build();
    }

    /// <summary>Доска из двух колонок; у первой условия «Estimate&gt;=3» и «Note:unset».</summary>
    private static (Guid SetId, Guid Open, Board Board) NewBoard(Guid project, Guid fieldA)
    {
        var (setId, open, closed) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var board = new Board
        {
            Id = Guid.NewGuid(), ProjectId = project, Name = "Main", StatusSetIds = [setId], Version = "",
            Columns =
            [
                new BoardColumn
                {
                    Id = Guid.NewGuid(), Name = "Big", StatusIds = [open], DropStatuses = new Dictionary<Guid, Guid> { [setId] = open },
                    FieldConditions = [new ColumnFieldFilter(fieldA, FieldOperator.GreaterOrEqual, "3"), new ColumnFieldFilter(Guid.NewGuid(), FieldOperator.Unset)]
                },
                new BoardColumn { Id = Guid.NewGuid(), Name = "Rest", StatusIds = [closed], DropStatuses = new Dictionary<Guid, Guid>() }
            ]
        };
        return (setId, open, board);
    }

    [Fact]
    public async Task The_database_keeps_conditions_in_a_table_in_order_and_does_not_let_a_used_field_go()
    {
        var root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = new ContainerBuilder();
            builder.RegisterModule(new DbStorageModule(new DbConfigs { SqliteFile = Path.Combine(root, "tasker.db") }));
            await using var container = builder.Build();
            await container.Resolve<IStorageLifecycle>().Start(default);
            await using var scope = container.BeginLifetimeScope();

            var project = Guid.NewGuid();
            await scope.Resolve<IProjectStorage>().Add(new Tasker.Core.Projects.Project { Id = project, Name = "P", CreatedAt = DateTimeOffset.UtcNow, Version = "" });
            var (open, closed) = (Guid.NewGuid(), Guid.NewGuid());
            var statuses = scope.Resolve<IStatusStorage>();
            await statuses.Add(new Status { Id = open, ProjectId = project, Name = "Open", Color = "#111111", Version = "" });
            await statuses.Add(new Status { Id = closed, ProjectId = project, Name = "Closed", Color = "#222222", Version = "" });
            var setId = Guid.NewGuid();
            await scope.Resolve<IStatusSetStorage>().Add(new StatusSet { Id = setId, ProjectId = project, Name = "Flow", StatusIds = [open, closed], Version = "" });
            var fields = scope.Resolve<IFieldStorage>();
            var estimate = new FieldDefinition { Id = Guid.NewGuid(), ProjectId = project, Name = "Estimate", Type = FieldType.Int, Version = "" };
            var note = new FieldDefinition { Id = Guid.NewGuid(), ProjectId = project, Name = "Note", Type = FieldType.String, Version = "" };
            var estimateVersion = await fields.Add(estimate);
            await fields.Add(note);

            var board = new Board
            {
                Id = Guid.NewGuid(), ProjectId = project, Name = "Main", StatusSetIds = [setId], Version = "",
                Columns =
                [
                    new BoardColumn
                    {
                        Id = Guid.NewGuid(), Name = "Big", StatusIds = [open], DropStatuses = new Dictionary<Guid, Guid> { [setId] = open },
                        FieldConditions =
                        [
                            new ColumnFieldFilter(estimate.Id, FieldOperator.GreaterOrEqual, "3"),
                            new ColumnFieldFilter(note.Id, FieldOperator.Unset),
                            new ColumnFieldFilter(estimate.Id, FieldOperator.Less, "10")
                        ]
                    },
                    new BoardColumn { Id = Guid.NewGuid(), Name = "Rest", StatusIds = [closed], DropStatuses = new Dictionary<Guid, Guid>() }
                ]
            };
            var boards = scope.Resolve<IBoardStorage>();
            var version = await boards.Add(board);

            var read = (await boards.GetById(project, board.Id))!;
            Assert.Equal(board.Columns[0].FieldConditions, read.Columns[0].FieldConditions); // порядок условий сохраняется
            Assert.Empty(read.Columns[1].FieldConditions);

            // Поле, на которое ссылается колонка, база не даёт удалить.
            await Assert.ThrowsAsync<SqliteException>(() => fields.Delete(project, estimate.Id, estimateVersion));

            // Правка доски заменяет условия целиком; доска без условий поле отпускает.
            var cleared = board with { Columns = board.Columns.Select(c => new BoardColumn { Id = c.Id, Name = c.Name, StatusIds = c.StatusIds, DropStatuses = c.DropStatuses }).ToArray() };
            // Каждый запрос — свой скоуп (и контекст БД), как у сервера.
            await using var next = container.BeginLifetimeScope();
            Assert.NotNull(await next.Resolve<IBoardStorage>().Update(cleared, version));
            Assert.All((await next.Resolve<IBoardStorage>().GetById(project, board.Id))!.Columns, c => Assert.Empty(c.FieldConditions));
            Assert.True(await next.Resolve<IFieldStorage>().Delete(project, estimate.Id, estimateVersion));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- исключающие условия: сама проверка ----

    private static FieldDefinition Field(FieldType type, bool multiple = false) =>
        new() { Id = Guid.Empty, ProjectId = Guid.Empty, Name = "F", Type = type, Multiple = multiple, Version = "" };

    private static BoardColumn Col(params ColumnFieldFilter[] filters) =>
        new() { Id = Guid.NewGuid(), Name = "C", StatusIds = [], DropStatuses = new Dictionary<Guid, Guid>(), FieldConditions = filters };

    private static ColumnFieldFilter F(FieldOperator op, string? value = null) => new(Guid.Empty, op, value);

    [Theory]
    [InlineData(FieldType.Int, false, FieldOperator.Equal, "1", FieldOperator.Equal, "2", true)]
    [InlineData(FieldType.Int, false, FieldOperator.Equal, "1", FieldOperator.Equal, "1", false)]
    [InlineData(FieldType.Int, true, FieldOperator.Equal, "1", FieldOperator.Equal, "2", false)] // у списка значений оба могут быть
    [InlineData(FieldType.Int, true, FieldOperator.Equal, "1", FieldOperator.NotEqual, "1", true)]
    [InlineData(FieldType.Int, false, FieldOperator.NotEqual, "1", FieldOperator.NotEqual, "2", false)]
    [InlineData(FieldType.Int, false, FieldOperator.GreaterOrEqual, "3", FieldOperator.Less, "3", true)]
    [InlineData(FieldType.Int, false, FieldOperator.Greater, "3", FieldOperator.LessOrEqual, "3", true)]
    [InlineData(FieldType.Int, false, FieldOperator.GreaterOrEqual, "3", FieldOperator.LessOrEqual, "3", false)] // оба верны при 3
    [InlineData(FieldType.Int, false, FieldOperator.Greater, "3", FieldOperator.Less, "4", false)] // целых между 3 и 4 нет, но вещественных — проверка осторожная
    [InlineData(FieldType.Int, false, FieldOperator.Greater, "5", FieldOperator.Less, "3", true)]
    [InlineData(FieldType.Int, false, FieldOperator.Greater, "3", FieldOperator.Less, "5", false)]
    [InlineData(FieldType.Int, false, FieldOperator.Equal, "5", FieldOperator.Less, "5", true)]
    [InlineData(FieldType.Int, false, FieldOperator.Equal, "5", FieldOperator.LessOrEqual, "5", false)]
    [InlineData(FieldType.Int, true, FieldOperator.Greater, "5", FieldOperator.Less, "3", false)] // у списка — разными значениями
    [InlineData(FieldType.Float, false, FieldOperator.Greater, "1.5", FieldOperator.LessOrEqual, "1.5", true)]
    [InlineData(FieldType.Date, false, FieldOperator.Less, "2026-01-01", FieldOperator.GreaterOrEqual, "2026-01-01", true)]
    [InlineData(FieldType.Date, false, FieldOperator.Less, "2026-03-01", FieldOperator.Greater, "2026-01-01", false)]
    [InlineData(FieldType.Bool, false, FieldOperator.Equal, "true", FieldOperator.Equal, "false", true)]
    [InlineData(FieldType.String, false, FieldOperator.Equal, "a", FieldOperator.Equal, "b", true)]
    [InlineData(FieldType.String, false, FieldOperator.Equal, "a", FieldOperator.NotEqual, "b", false)]
    [InlineData(FieldType.Int, false, FieldOperator.Set, null, FieldOperator.Unset, null, true)]
    [InlineData(FieldType.Int, true, FieldOperator.Set, null, FieldOperator.Unset, null, true)]
    [InlineData(FieldType.Int, false, FieldOperator.Set, null, FieldOperator.Detached, null, true)]
    [InlineData(FieldType.Int, false, FieldOperator.Equal, "1", FieldOperator.Unset, null, true)]
    [InlineData(FieldType.Int, false, FieldOperator.Greater, "1", FieldOperator.Detached, null, true)]
    [InlineData(FieldType.Int, false, FieldOperator.NotEqual, "1", FieldOperator.Unset, null, false)] // без значения «не равно» верно
    [InlineData(FieldType.Int, false, FieldOperator.Attached, null, FieldOperator.Detached, null, true)]
    [InlineData(FieldType.Int, false, FieldOperator.Attached, null, FieldOperator.Unset, null, false)] // подключено, но пусто
    [InlineData(FieldType.Int, false, FieldOperator.Unset, null, FieldOperator.Detached, null, false)]
    [InlineData(FieldType.Int, false, FieldOperator.Set, null, FieldOperator.Equal, "1", false)]
    public void Exclusive_conditions_are_recognised_only_when_they_cannot_hold_together(
        FieldType type, bool multiple, FieldOperator first, string? firstValue, FieldOperator second, string? secondValue, bool exclusive)
    {
        var fields = new Dictionary<Guid, FieldDefinition> { [Guid.Empty] = Field(type, multiple) };
        var a = Col(F(first, firstValue));
        var b = Col(F(second, secondValue));

        Assert.Equal(exclusive, ColumnFilters.AreExclusive(a, b, fields));
        Assert.Equal(exclusive, ColumnFilters.AreExclusive(b, a, fields));
    }

    [Fact]
    public void Columns_without_conditions_or_with_conditions_on_different_fields_are_never_exclusive()
    {
        var fields = new Dictionary<Guid, FieldDefinition> { [Guid.Empty] = Field(FieldType.Int) };
        Assert.False(ColumnFilters.AreExclusive(Col(), Col(), fields));
        Assert.False(ColumnFilters.AreExclusive(Col(F(FieldOperator.Equal, "1")), Col(), fields));
        Assert.False(ColumnFilters.AreExclusive(Col(F(FieldOperator.Equal, "1")), Col(new ColumnFieldFilter(Guid.NewGuid(), FieldOperator.Equal, "2")), fields));
        // Исключают друг друга колонки уже по одной паре условий: остальные условия не нужны.
        Assert.True(ColumnFilters.AreExclusive(Col(F(FieldOperator.Equal, "1"), F(FieldOperator.Set)), Col(F(FieldOperator.Set), F(FieldOperator.Equal, "2")), fields));
    }
}
