using Tasker.Cli.Commands;
using Tasker.Core.Fields;
using Xunit;

namespace Tasker.Tests;

/// <summary>Консоль для полей, перечислений и значений у задач: оба хранилища.</summary>
public class FieldCliTests
{
    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    private static async Task<CliResult> Fail(Task<CliResult> run, string message)
    {
        var result = await run;
        Assert.Equal(1, result.Code);
        Assert.Contains(message, result.Err);
        return result;
    }

    /// <summary>Проект Demo со статусом Todo, типом Bug (набор Flow), серией TSK.</summary>
    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("status", "create", "Todo"));
        await Ok(ws.Run("status-set", "create", "Flow", "--status", "Todo"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(ws.Run("series", "create", "Tasks", "--prefix", "TSK"));
    }

    /// <summary>Каталог: enum Priority, поля Severity (enum Priority), Estimate (int), Labels (string, несколько).</summary>
    private static async Task SeedCatalog(TestWorkspace ws)
    {
        await Ok(ws.Run("enum", "create", "Priority", "--value", "Low", "Medium", "High"));
        await Ok(ws.Run("field", "create", "Severity", "--type", "enum", "--enum", "Priority"));
        await Ok(ws.Run("field", "create", "Estimate", "--type", "int"));
        await Ok(ws.Run("field", "create", "Labels", "--type", "string", "--multiple"));
    }

    private static string[] Lines(CliResult result) => result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Fields_and_enums_are_managed_from_the_console(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var created = await Ok(ws.Run("enum", "create", "Priority", "--value", "Low", "--value", "High"));
        Assert.StartsWith("Created enum 'Priority' (Low, High) ", created.Out);
        await Fail(ws.Run("enum", "create", "priority", "--value", "x"), "already exists");
        await Fail(ws.Run("enum", "create", "Empty"), "--value");

        var field = await Ok(ws.Run("field", "create", "Severity", "--type", "ENUM", "--enum", "Priority"));
        Assert.StartsWith("Created field 'Severity' (enum Priority) ", field.Out);
        await Ok(ws.Run("field", "create", "Labels", "--type", "string", "--multiple"));
        await Fail(ws.Run("field", "create", "Bad", "--type", "number"), "Unknown field type 'number'");
        await Fail(ws.Run("field", "create", "Bad", "--type", "enum"), "enum");
        await Fail(ws.Run("field", "create", "severity", "--type", "int"), "already exists");

        var list = await Ok(ws.Run("field", "list"));
        Assert.Contains(Lines(list), x => System.Text.RegularExpressions.Regex.IsMatch(x, " Severity +enum Priority$"));
        Assert.Contains(Lines(list), x => System.Text.RegularExpressions.Regex.IsMatch(x, " Labels +string\\[\\]$"));
        Assert.Equal(2, (await Ok(ws.Run("field", "list", "--json"))).Json["totalCount"]!.GetValue<int>());

        var get = await Ok(ws.Run("field", "get", "severity"));
        Assert.Contains("type:", get.Out);
        Assert.Contains("Priority (", get.Out);
        Assert.Equal("enum", (await Ok(ws.Run("field", "get", "Severity", "--json"))).Json["type"]!.GetValue<string>());

        await Ok(ws.Run("field", "update", "Labels", "--name", "Tags"));
        await Fail(ws.Run("field", "update", "Tags"), "Nothing to change");
        await Fail(ws.Run("field", "update", "Tags", "--type", "number"), "Unknown field type 'number'");
        await Fail(ws.Run("field", "update", "Tags", "--multiple", "--single"), "not both");

        // Перечисление: значения по одному и списком.
        var enumGet = await Ok(ws.Run("enum", "get", "Priority"));
        Assert.Contains("  Low  ", enumGet.Out);
        await Ok(ws.Run("enum", "update", "Priority", "--add-value", "Critical", "--rename-value", "Low=Minor"));
        Assert.Contains("Priority  Minor, High, Critical", (await Ok(ws.Run("enum", "list"))).Out);
        await Ok(ws.Run("enum", "update", "Priority", "--values", "Critical", "Minor", "High"));
        Assert.Contains("Priority  Critical, Minor, High", (await Ok(ws.Run("enum", "list"))).Out);
        await Fail(ws.Run("enum", "update", "Priority", "--values", "A", "--add-value", "B"), "not both");
        await Fail(ws.Run("enum", "update", "Priority", "--rename-value", "Minor"), "Old=New");
        await Fail(ws.Run("enum", "update", "Priority", "--remove-value", "Nope"), "No value of enum 'Priority' 'Nope'");

        // Занятое не удаляется.
        await Fail(ws.Run("enum", "delete", "Priority"), "In use");
        await Ok(ws.Run("field", "delete", "Severity"));
        await Ok(ws.Run("enum", "delete", "Priority"));
        Assert.Equal(1, (await ws.Run("enum", "get", "Priority")).Code);

        // Блокировки уже умеют field и enum.
        await Ok(ws.Run("lock", "acquire", "field", "Tags"));
        await Ok(ws.Run("lock", "release", "field", "Tags"));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Task_type_fields_and_task_values(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await SeedCatalog(ws);

        await Ok(ws.Run("task-type", "update", "Bug", "--field", "Severity:required", "--field", "Estimate"));
        var typeGet = await Ok(ws.Run("task-type", "get", "Bug"));
        Assert.Contains("  Severity (enum Priority, required)", typeGet.Out);
        Assert.Contains("  Estimate (int)", typeGet.Out);
        Assert.Equal(2, (await Ok(ws.Run("task-type", "get", "Bug", "--json"))).Json["fields"]!.AsArray().Count);
        await Fail(ws.Run("task-type", "update", "Bug", "--field", "Nope"), "No field 'Nope'");

        // Обязательное поле: ошибка называет его и подсказывает --field.
        var missing = await Fail(ws.Run("task", "create", "First", "--type", "Bug", "--series", "TSK"), "Required fields have no value: 'Severity'");
        Assert.Contains("--field", missing.Err);

        var created = await Ok(ws.Run("task", "create", "First", "--type", "Bug", "--series", "TSK", "--field", "Severity=High", "--field", "Estimate=5"));
        Assert.Contains("(TSK-1)", created.Out);
        await Fail(ws.Run("task", "create", "Bad", "--type", "Bug", "--field", "Severity=Nope"), "Nope");
        await Fail(ws.Run("task", "create", "Bad", "--type", "Bug", "--field", "Severity=High", "--field", "Estimate=x"), "Estimate");
        await Fail(ws.Run("task", "create", "Bad", "--type", "Bug", "--field", "Severity"), "Name=value");

        // Дополнительное поле каталога (несколько значений — повтором) и собственное поле задачи.
        await Ok(ws.Run("task", "update", "TSK-1", "--field", "Labels=ui", "--field", "Labels=crash",
            "--custom-field", "Risk:enum:required:enum=Priority=Low", "--custom-field", "Notes:string:multiple=a=b", "--custom-field", "Notes:string:multiple=c:d"));
        var get = await Ok(ws.Run("task", "get", "TSK-1"));
        Assert.Contains("fields:\n", get.Out);
        Assert.Contains("  Severity (enum, required): High\n", get.Out);
        Assert.Contains("  Estimate (int): 5\n", get.Out);
        Assert.Contains("  Labels (string[], extra): ui, crash\n", get.Out);
        Assert.Contains("  Risk (enum, required, own): Low\n", get.Out);
        Assert.Contains("  Notes (string[], own): a=b, c:d\n", get.Out);

        var json = (await Ok(ws.Run("task", "get", "TSK-1", "--json"))).Json;
        var views = json["fieldViews"]!.AsArray();
        Assert.Equal(5, views.Count);
        Assert.Equal("type", views[0]!["source"]!.GetValue<string>());
        Assert.Equal("High", views[0]!["texts"]![0]!.GetValue<string>());
        Assert.Equal("own", views[4]!["source"]!.GetValue<string>());
        Assert.Equal("First", json["title"]!.GetValue<string>());

        // Правка: значения заменяются, Name= очищает, поля убираются по имени.
        await Ok(ws.Run("task", "update", "TSK-1", "--field", "Estimate=", "--field", "Labels=ui", "--remove-field", "Notes"));
        get = await Ok(ws.Run("task", "get", "TSK-1"));
        Assert.Contains("  Estimate (int): -\n", get.Out);
        Assert.Contains("  Labels (string[], extra): ui\n", get.Out);
        Assert.DoesNotContain("Notes", get.Out);
        await Fail(ws.Run("task", "update", "TSK-1", "--remove-field", "Severity"), "cannot be removed");
        await Fail(ws.Run("task", "update", "TSK-1", "--remove-field", "Nope"), "No field 'Nope'");
        await Fail(ws.Run("task", "update", "TSK-1", "--custom-field", "Risk:int"), "already has a field");
        await Fail(ws.Run("task", "update", "TSK-1", "--field", "Severity="), "Required fields have no value: 'Severity'");

        // Смена статуса обязательные поля не проверяет; правка заголовка — проверяет.
        await Ok(ws.Run("task", "create", "Plain", "--type", "Bug", "--field", "Severity=Low"));
        await Ok(ws.Run("task", "update", "TSK-1", "--add-field", "Labels"));

        // Поле каталога у типа без значений: «заметно» только в определении.
        var empty = await Ok(ws.Run("task-type", "create", "Idea", "--status-set", "Flow", "--field", "Estimate:required"));
        Assert.Contains("Created task type 'Idea'", empty.Out);
        await Fail(ws.Run("task", "create", "I", "--type", "Idea"), "'Estimate'");
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Removing_a_field_from_a_type_needs_a_choice_when_tasks_have_values(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await SeedCatalog(ws);
        await Ok(ws.Run("task-type", "update", "Bug", "--add-field", "Estimate", "--add-field", "Labels:required"));
        await Ok(ws.Run("task", "create", "One", "--type", "Bug", "--series", "TSK", "--field", "Estimate=3", "--field", "Labels=x"));

        // Без выбора — ошибка с числом задач и подсказкой параметров.
        var refused = await Fail(ws.Run("task-type", "update", "Bug", "--remove-field", "Estimate"), "In use: 1 task(s)");
        Assert.Contains("--drop-values or --keep-values", refused.Err);
        await Fail(ws.Run("task-type", "update", "Bug", "--remove-field", "Estimate", "--drop-values", "--keep-values"), "not both");
        await Fail(ws.Run("task-type", "update", "Bug", "--remove-field", "Severity"), "has no field 'Severity'");

        // Оставить: поле становится дополнительным у задачи.
        var keep = await Ok(ws.Run("task-type", "update", "Bug", "--remove-field", "Estimate", "--keep-values"));
        Assert.Contains("Kept 'Estimate' as an extra field in 1 task\n", keep.Out);
        Assert.DoesNotContain("Estimate", (await Ok(ws.Run("task-type", "get", "Bug"))).Out);
        Assert.Contains("  Estimate (int, extra): 3\n", (await Ok(ws.Run("task", "get", "TSK-1"))).Out);

        // Убрать значения у задач; список заменяется целиком.
        await Ok(ws.Run("task-type", "update", "Bug", "--field", "Estimate", "--field", "Labels:required"));
        var drop = await Ok(ws.Run("task-type", "update", "Bug", "--field", "Estimate", "--drop-values"));
        Assert.Contains("Removed 'Labels' from 1 task\n", drop.Out);
        // Убранное поле, значений которого ни у кого нет: 0 затронутых задач, в --json — рядом с полями типа.
        await Ok(ws.Run("task-type", "update", "Bug", "--add-field", "Labels"));
        var dropJson = await Ok(ws.Run("task-type", "update", "Bug", "--remove-field", "Labels", "--drop-values", "--json"));
        Assert.Equal(0, dropJson.Json["affectedTasks"]!.GetValue<int>());
        Assert.Equal("Bug", dropJson.Json["name"]!.GetValue<string>());
        var get = (await Ok(ws.Run("task", "get", "TSK-1"))).Out;
        Assert.Contains("  Estimate (int): 3\n", get);
        Assert.DoesNotContain("Labels", get);
        await Fail(ws.Run("task-type", "update", "Bug", "--field", "Estimate", "--add-field", "Labels"), "not both");
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Tasks_and_board_columns_are_filtered_by_field_values_in_the_console(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await SeedCatalog(ws);
        await Ok(ws.Run("task-type", "update", "Bug", "--field", "Severity", "--field", "Estimate", "--field", "Labels"));
        await Ok(ws.Run("task", "create", "One", "--type", "Bug", "--series", "TSK", "--field", "Severity=High", "--field", "Estimate=3", "--field", "Labels=ui", "--field", "Labels=api"));
        await Ok(ws.Run("task", "create", "Two", "--type", "Bug", "--series", "TSK", "--field", "Severity=Low", "--field", "Estimate=3", "--field", "Labels=api"));
        await Ok(ws.Run("task", "create", "Three", "--type", "Bug", "--series", "TSK", "--field", "Severity=High", "--field", "Estimate=5"));
        await Ok(ws.Run("board", "create", "Main", "--status-set", "Flow", "--column", "Open=Todo"));

        string[] Titles(CliResult r) => new[] { "One", "Two", "Three" }.Where(t => r.Out.Contains(t)).ToArray();

        Assert.Equal(["One", "Three"], Titles(await Ok(ws.Run("task", "list", "--field", "Severity=high"))));
        Assert.Equal(["One", "Two"], Titles(await Ok(ws.Run("task", "list", "--field", "Labels=api"))));
        // Повтор — И; несколько условий можно перечислить и после одного --field.
        Assert.Equal(["One"], Titles(await Ok(ws.Run("task", "list", "--field", "Severity=High", "--field", "Estimate=3"))));
        Assert.Equal(["One"], Titles(await Ok(ws.Run("task", "list", "--field", "Labels=ui", "Labels=api"))));
        Assert.Empty(Titles(await Ok(ws.Run("task", "list", "--field", "Estimate=4"))));

        var json = await Ok(ws.Run("task", "list", "--field", "Severity=High", "--limit", "1", "--json"));
        Assert.Equal(2, json.Json["totalCount"]!.GetValue<int>());
        Assert.Single(json.Json["data"]!.AsArray());

        await Fail(ws.Run("task", "list", "--field", "Nope=1"), "no field 'Nope'");
        await Fail(ws.Run("task", "list", "--field", "Estimate=abc"), "is not an integer");
        await Fail(ws.Run("task", "list", "--field", "Estimate"), "Name=value");

        // Сравнения, «!=» и наличие (в консоли условия со знаками «<» и «>» пишут в кавычках; здесь аргументы идут как есть).
        Assert.Equal(["Three"], Titles(await Ok(ws.Run("task", "list", "--field", "Estimate>3"))));
        Assert.Equal(["One", "Two"], Titles(await Ok(ws.Run("task", "list", "--field", "Estimate>=3", "--field", "Estimate<=4"))));
        Assert.Equal(["Two", "Three"], Titles(await Ok(ws.Run("task", "list", "--field", "Labels!=ui"))));
        Assert.Equal(["Three"], Titles(await Ok(ws.Run("task", "list", "--field", "Labels:unset", "--field", "Labels:attached"))));
        Assert.Empty(Titles(await Ok(ws.Run("task", "list", "--field", "Estimate:detached"))));
        await Fail(ws.Run("task", "list", "--field", "Severity>=High"), "does not apply to field 'Severity' of type enum");
        await Fail(ws.Run("task", "list", "--field", "Estimate:soon"), "unknown ':soon'");
        Assert.Equal(["Three"], Titles(await Ok(ws.Run("board", "tasks", "Main", "Open", "--field", "Estimate>=4"))));

        // Колонка доски и вся доска — тот же срез (условие просмотра, колонка его не хранит).
        Assert.Equal(["One", "Two", "Three"], Titles(await Ok(ws.Run("board", "tasks", "Main", "Open"))));
        Assert.Equal(["One", "Two"], Titles(await Ok(ws.Run("board", "tasks", "Main", "Open", "--field", "Estimate=3"))));
        var shown = await Ok(ws.Run("board", "show", "Main", "--field", "Severity=High", "--field", "Estimate=5"));
        Assert.Equal(["Three"], Titles(shown));
        Assert.Contains("Open (1)", shown.Out);

        // Собственные поля задач — тот же синтаксис: Hours нет в каталоге, тип у всех float; Estimate (каталог, int) совпадает с собственным int.
        await Ok(ws.Run("task", "update", "TSK-1", "--custom-field", "Hours:float=2.5"));
        await Ok(ws.Run("task", "update", "TSK-3", "--custom-field", "Hours:float=9"));
        Assert.Equal(["Three"], Titles(await Ok(ws.Run("task", "list", "--field", "Hours>5"))));
        Assert.Equal(["One", "Three"], Titles(await Ok(ws.Run("task", "list", "--field", "Hours:attached"))));
        Assert.Equal(["Two"], Titles(await Ok(ws.Run("board", "tasks", "Main", "Open", "--field", "Hours:detached"))));
        await Ok(ws.Run("task", "update", "TSK-2", "--custom-field", "Code:int=1"));
        await Ok(ws.Run("task", "update", "TSK-3", "--custom-field", "Code:string=x"));
        await Fail(ws.Run("task", "list", "--field", "Code=1"), "several types (int, string)");
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Changing_the_type_multiplicity_or_enum_of_a_field_converts_task_values_and_needs_a_choice_when_they_do_not_fit(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await SeedCatalog(ws);
        await Ok(ws.Run("enum", "create", "Level", "--value", "low", "--value", "Critical"));
        await Ok(ws.Run("task-type", "update", "Bug", "--field", "Severity", "--field", "Labels"));
        await Ok(ws.Run("task", "create", "One", "--type", "Bug", "--series", "TSK", "--field", "Severity=High", "--field", "Labels=7", "--field", "Labels=x"));
        await Ok(ws.Run("task", "create", "Two", "--type", "Bug", "--series", "TSK", "--field", "Severity=Low"));

        // Несколько значений → одно: нужен выбор.
        var several = await Fail(ws.Run("field", "update", "Labels", "--single"), "In use");
        Assert.Contains("1 task(s)", several.Err);
        Assert.Contains("--several", several.Err);
        await Fail(ws.Run("field", "update", "Labels", "--single", "--several", "first"), "Unknown --several");
        await Ok(ws.Run("field", "update", "Labels", "--single", "--several", "keep-first"));
        Assert.Contains("  Labels (string): 7\n", (await Ok(ws.Run("task", "get", "TSK-1"))).Out);

        // Значение не разбирается («x» у второй задачи): нужен выбор, а «7» преобразуется.
        await Ok(ws.Run("field", "update", "Labels", "--multiple"));
        await Ok(ws.Run("task", "update", "TSK-2", "--field", "Labels=x"));
        var bad = await Fail(ws.Run("field", "update", "Labels", "--type", "int"), "In use");
        Assert.Contains("'x'", bad.Err);
        Assert.Contains("--clear-unconvertible", bad.Err);
        var cleared = await Ok(ws.Run("field", "update", "Labels", "--type", "int", "--clear-unconvertible"));
        Assert.Contains("(int[])", cleared.Out);
        Assert.Contains("  Labels (int[]): 7\n", (await Ok(ws.Run("task", "get", "TSK-1"))).Out);
        await Fail(ws.Run("field", "update", "Labels", "--type", "bool"), "string first");

        // Другое перечисление: по названию (Low → low), остальное — соответствием или очисткой.
        var enumFail = await Fail(ws.Run("field", "update", "Severity", "--enum", "Level"), "In use");
        Assert.Contains("'High'", enumFail.Err);
        Assert.Contains("--map", enumFail.Err);
        await Fail(ws.Run("field", "update", "Severity", "--enum", "Level", "--map", "High"), "From=To");
        await Ok(ws.Run("field", "update", "Severity", "--enum", "Level", "--map", "High=Critical"));
        Assert.Contains("  Severity (enum): Critical\n", (await Ok(ws.Run("task", "get", "TSK-1"))).Out);
        Assert.Contains("  Severity (enum): low\n", (await Ok(ws.Run("task", "get", "TSK-2"))).Out);

        // Enum → string: значения становятся названиями.
        var text = await Ok(ws.Run("field", "update", "Severity", "--type", "string"));
        Assert.Contains("(string)", text.Out);
        Assert.Contains("  Severity (string): Critical\n", (await Ok(ws.Run("task", "get", "TSK-1"))).Out);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Removing_an_enum_value_selected_in_tasks_needs_a_choice(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await SeedCatalog(ws);
        await Ok(ws.Run("task-type", "update", "Bug", "--field", "Severity"));
        await Ok(ws.Run("task", "create", "One", "--type", "Bug", "--series", "TSK", "--field", "Severity=High"));
        await Ok(ws.Run("task", "create", "Two", "--type", "Bug", "--series", "TSK", "--field", "Severity=Low"));
        await Ok(ws.Run("task", "create", "Three", "--type", "Bug", "--series", "TSK", "--custom-field", "Own:enum:enum=Priority=High"));

        var refused = await Fail(ws.Run("enum", "update", "Priority", "--remove-value", "High"), "In use");
        Assert.Contains("2 task(s)", refused.Err);
        Assert.Contains("--drop or --replace-with", refused.Err);
        await Fail(ws.Run("enum", "update", "Priority", "--remove-value", "High", "--drop", "--replace-with", "Low"), "not both");
        await Fail(ws.Run("enum", "update", "Priority", "--remove-value", "High", "--replace-with", "High"), "keeps");

        // Переназначить: у всех задач (и у собственных полей) вместо High — Low.
        var reassigned = await Ok(ws.Run("enum", "update", "Priority", "--remove-value", "High", "--replace-with", "Low"));
        Assert.Contains("Reassigned 'High' \u2192 'Low' in 2 tasks\n", reassigned.Out);
        Assert.Contains("  Severity (enum): Low\n", (await Ok(ws.Run("task", "get", "TSK-1"))).Out);
        Assert.Contains("  Own (enum, own): Low\n", (await Ok(ws.Run("task", "get", "TSK-3"))).Out);

        // Убрать значение у задач.
        var dropped = await Ok(ws.Run("enum", "update", "Priority", "--remove-value", "Low", "--drop"));
        Assert.Contains("Removed 'Low' from 3 tasks\n", dropped.Out);
        Assert.Contains("  Severity (enum): -\n", (await Ok(ws.Run("task", "get", "TSK-1"))).Out);
        Assert.Contains("Priority  Medium", (await Ok(ws.Run("enum", "list"))).Out);
    }

    [Theory]
    [InlineData("Estimate:int", "Estimate", FieldType.Int, false, false, null, null)]
    [InlineData("Estimate:Int=5", "Estimate", FieldType.Int, false, false, null, "5")]
    [InlineData("Tags:string:multiple:required=a:b=c", "Tags", FieldType.String, true, true, null, "a:b=c")]
    [InlineData("Risk:enum:enum=Priority=High", "Risk", FieldType.Enum, false, false, "Priority", "High")]
    [InlineData("Risk:enum:required:enum=Priority", "Risk", FieldType.Enum, true, false, "Priority", null)]
    [InlineData("Due:date=", "Due", FieldType.Date, false, false, null, null)]
    public void Custom_field_syntax(string text, string name, FieldType type, bool required, bool multiple, string? enumeration, string? value)
    {
        Assert.Equal((name, type, required, multiple, enumeration, value), TaskFieldOptions.ParseCustomField(text));
    }

    [Theory]
    [InlineData("Estimate")]
    [InlineData(":int")]
    [InlineData("Estimate:")]
    [InlineData("Estimate:number")]
    [InlineData("Estimate:int:sometimes")]
    [InlineData("Estimate:int:enum")]
    [InlineData("Estimate:int:enum=")]
    public void Custom_field_syntax_errors(string text)
    {
        Assert.Throws<Tasker.Cli.CliException>(() => TaskFieldOptions.ParseCustomField(text));
    }
}
