using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Tasker.Tests;

/// <summary>HTTP API полей: каталог, перечисления, поля типа задачи, значения и собственные поля в задаче. Папка и SQLite.</summary>
public class FieldsApiTests
{
    public static IEnumerable<object[]> Storages() => TestWorkspace.Storages();

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();
    private static string Version(JsonNode node) => node["version"]!.GetValue<string>();

    private static async Task<JsonNode> Created(Task<(HttpStatusCode Status, JsonNode? Body)> call)
    {
        var (status, body) = await call;
        Assert.Equal(HttpStatusCode.Created, status);
        return body!;
    }

    private static async Task<JsonNode> Ok(Task<(HttpStatusCode Status, JsonNode? Body)> call)
    {
        var (status, body) = await call;
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}");
        return body!;
    }

    private static async Task Fails(Task<(HttpStatusCode Status, JsonNode? Body)> call, HttpStatusCode expected, string? code = null, string? contains = null)
    {
        var (status, body) = await call;
        Assert.True(status == expected, $"{status}: {body}");
        if (code != null)
            Assert.Equal(code, body!["code"]!.GetValue<string>());
        if (contains != null)
            Assert.Contains(contains, body!.ToJsonString());
    }

    private static object[] Texts(params string[] values) => values;

    private static JsonNode FieldView(JsonNode task, string name) =>
        task["fieldViews"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == name)!;

    private static string[] Values(JsonNode view) => view["values"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
    private static string[] TextsOf(JsonNode view) => view["texts"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    [Theory, MemberData(nameof(Storages))]
    public async Task Field_catalog_crud_paging_and_errors(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var levels = await Created(api.Post(api.P("/enums"), new { name = "Level", values = Texts("Low", "High") }));

        var estimate = await Created(api.Post(api.P("/fields"), new { name = "Estimate", type = "int" }));
        var tags = await Created(api.Post(api.P("/fields"), new { name = "Tags", type = "string", multiple = true }));
        var level = await Created(api.Post(api.P("/fields"), new { name = "Level", type = "enum", enumId = Id(levels) }));
        Assert.Equal("int", estimate["type"]!.GetValue<string>());
        Assert.False(estimate["multiple"]!.GetValue<bool>());
        Assert.True(tags["multiple"]!.GetValue<bool>());
        Assert.Equal(Id(levels), level["enumId"]!.GetValue<string>());

        var list = (await api.Get(api.P("/fields"))).Body!;
        Assert.Equal(3, list["totalCount"]!.GetValue<int>());
        Assert.Equal(["Estimate", "Level", "Tags"], list["data"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray());
        var page = (await api.Get(api.P("/fields?offset=1&limit=1"))).Body!;
        Assert.Equal(3, page["totalCount"]!.GetValue<int>());
        Assert.Equal(["Level"], page["data"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal("Tags", (await Ok(api.Get(api.P($"/fields/{Id(tags)}"))))["name"]!.GetValue<string>());

        // Переименование не трогает тип.
        var renamed = await Ok(api.Patch(api.P($"/fields/{Id(estimate)}"), new { name = "Points", version = Version(estimate) }));
        Assert.Equal("Points", renamed["name"]!.GetValue<string>());
        Assert.Equal("int", renamed["type"]!.GetValue<string>());
        await Fails(api.Patch(api.P($"/fields/{Id(estimate)}"), new { name = "Stale", version = Version(estimate) }), HttpStatusCode.Conflict, "modified");

        // Ошибки: имя занято (регистр не важен), нет имени, неизвестный тип, enum без перечисления, перечисление у не-enum, чужое перечисление.
        await Fails(api.Post(api.P("/fields"), new { name = "points", type = "int" }), HttpStatusCode.Conflict, "in_use");
        await Fails(api.Post(api.P("/fields"), new { name = " ", type = "int" }), HttpStatusCode.BadRequest);
        await Fails(api.Post(api.P("/fields"), new { name = "X", type = "weird" }), HttpStatusCode.BadRequest);
        await Fails(api.Post(api.P("/fields"), new { name = "X", type = "enum" }), HttpStatusCode.BadRequest);
        await Fails(api.Post(api.P("/fields"), new { name = "X", type = "int", enumId = Id(levels) }), HttpStatusCode.BadRequest);
        await Fails(api.Post(api.P("/fields"), new { name = "X", type = "enum", enumId = Guid.NewGuid() }), HttpStatusCode.BadRequest);

        // Нет поля — 404 на всех методах.
        var unknown = Guid.NewGuid();
        await Fails(api.Get(api.P($"/fields/{unknown}")), HttpStatusCode.NotFound);
        await Fails(api.Patch(api.P($"/fields/{unknown}"), new { name = "Z", version = "v" }), HttpStatusCode.NotFound);
        await Fails(api.Delete(api.P($"/fields/{unknown}?version=v")), HttpStatusCode.NotFound);

        // Удаление: устаревшая версия — 409 modified, потом по актуальной.
        await Fails(api.Delete(api.P($"/fields/{Id(tags)}?version=stale")), HttpStatusCode.Conflict, "modified");
        Assert.Equal(HttpStatusCode.NoContent, (await api.Delete(api.P($"/fields/{Id(tags)}?version={Version(tags)}"))).Status);
        Assert.Equal(2, (await api.Get(api.P("/fields"))).Body!["totalCount"]!.GetValue<int>());

        // Поле «Level» ссылается на перечисление — его удалить нельзя, пока поле есть.
        await Fails(api.Delete(api.P($"/enums/{Id(levels)}?version={Version(levels)}")), HttpStatusCode.Conflict, "in_use");
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Enum_crud_and_value_edits(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var levels = await Created(api.Post(api.P("/enums"), new { name = "Level", values = Texts("Low", "High") }));
        await Created(api.Post(api.P("/enums"), new { name = "Area", values = Texts("UI") }));
        var lowId = levels["values"]![0]!["id"]!.GetValue<string>();

        var list = (await api.Get(api.P("/enums"))).Body!;
        Assert.Equal(["Area", "Level"], list["data"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(1, (await api.Get(api.P("/enums?offset=1&limit=1"))).Body!["data"]!.AsArray().Count);
        Assert.Equal(2, (await Ok(api.Get(api.P($"/enums/{Id(levels)}"))))["values"]!.AsArray().Count);

        // Правка: «Low» остаётся тем же значением (переименование по id), «Mid» новое, «High» убрано (нигде не выбрано — выбор не нужен).
        var edited = await Ok(api.Patch(api.P($"/enums/{Id(levels)}"), new
        {
            name = "Priority",
            version = Version(levels),
            values = new object[] { new { id = lowId, name = "Lowest" }, new { name = "Mid" } }
        }));
        Assert.Equal("Priority", edited["name"]!.GetValue<string>());
        Assert.Equal(["Lowest", "Mid"], edited["values"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(lowId, edited["values"]![0]!["id"]!.GetValue<string>());

        await Fails(api.Post(api.P("/enums"), new { name = "priority", values = Texts("A") }), HttpStatusCode.Conflict, "in_use");
        await Fails(api.Post(api.P("/enums"), new { name = "Empty", values = Texts() }), HttpStatusCode.BadRequest);
        await Fails(api.Post(api.P("/enums"), new { name = "Twice", values = Texts("a", "A") }), HttpStatusCode.BadRequest);
        await Fails(api.Patch(api.P($"/enums/{Id(levels)}"), new { name = "Stale", version = Version(levels) }), HttpStatusCode.Conflict, "modified");
        await Fails(api.Get(api.P($"/enums/{Guid.NewGuid()}")), HttpStatusCode.NotFound);

        Assert.Equal(HttpStatusCode.NoContent, (await api.Delete(api.P($"/enums/{Id(edited)}?version={Version(edited)}"))).Status);
        await Fails(api.Get(api.P($"/enums/{Id(edited)}")), HttpStatusCode.NotFound);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Type_fields_values_and_computed_field_views_in_the_task(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var levels = await Created(api.Post(api.P("/enums"), new { name = "Level", values = Texts("Low", "High") }));
        var estimate = await Created(api.Post(api.P("/fields"), new { name = "Estimate", type = "int" }));
        var tags = await Created(api.Post(api.P("/fields"), new { name = "Tags", type = "string", multiple = true }));
        var level = await Created(api.Post(api.P("/fields"), new { name = "Level", type = "enum", enumId = Id(levels) }));
        var due = await Created(api.Post(api.P("/fields"), new { name = "Due", type = "date" }));

        // Поля типа задаются при создании типа и заменяются при правке.
        var type = (await api.Get(api.P($"/task-types/{api.TypeId}"))).Body!;
        var updatedType = await Ok(api.Patch(api.P($"/task-types/{api.TypeId}"), new
        {
            version = Version(type),
            fields = new object[]
            {
                new { fieldId = Id(estimate), required = true },
                new { fieldId = Id(level), required = false },
                new { fieldId = Id(tags), required = false }
            }
        }));
        Assert.Equal(3, updatedType["fields"]!.AsArray().Count);
        Assert.True(updatedType["fields"]![0]!["required"]!.GetValue<bool>());
        Assert.Equal(3, (await api.Get(api.P($"/task-types/{api.TypeId}"))).Body!["fields"]!.AsArray().Count);

        // Нет обязательного значения / значение не того типа / лишние значения / чужое поле — 400.
        await Fails(api.Post(api.P("/tasks"), new { title = "t", typeId = api.TypeId }), HttpStatusCode.BadRequest, contains: "Estimate");
        await Fails(api.Post(api.P("/tasks"), new { title = "t", typeId = api.TypeId, fields = new { values = new[] { new { fieldId = Id(estimate), values = Texts("many") } } } }),
            HttpStatusCode.BadRequest);
        await Fails(api.Post(api.P("/tasks"), new { title = "t", typeId = api.TypeId, fields = new { values = new[] { new { fieldId = Id(estimate), values = Texts("1", "2") } } } }),
            HttpStatusCode.BadRequest);
        await Fails(api.Post(api.P("/tasks"), new { title = "t", typeId = api.TypeId, fields = new { values = new[] { new { fieldId = Guid.NewGuid(), values = Texts("1") } } } }),
            HttpStatusCode.BadRequest);

        // Создание: значения (enum — названием), множественное поле, дополнительное поле каталога, собственное поле.
        var task = await Created(api.Post(api.P("/tasks"), new
        {
            title = "Feature",
            typeId = api.TypeId,
            fields = new
            {
                values = new object[]
                {
                    new { fieldId = Id(estimate), values = Texts("007") },
                    new { fieldId = Id(level), values = Texts("high") },
                    new { fieldId = Id(tags), values = Texts("a", "b") },
                    new { fieldId = Id(due), values = Texts("2026-10-02") }
                },
                newOwnFields = new object[] { new { name = "Note", type = "string", values = Texts("hello"), required = true } }
            }
        }));
        var views = task["fieldViews"]!.AsArray();
        Assert.Equal(["Estimate", "Level", "Tags", "Due", "Note"], views.Select(x => x!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(["7"], Values(FieldView(task, "Estimate")));
        Assert.Equal("type", FieldView(task, "Estimate")["source"]!.GetValue<string>());
        Assert.True(FieldView(task, "Estimate")["required"]!.GetValue<bool>());
        Assert.Equal(["High"], TextsOf(FieldView(task, "Level")));
        Assert.Equal(levels["values"]![1]!["id"]!.GetValue<string>(), Values(FieldView(task, "Level"))[0]);
        Assert.Equal(["a", "b"], Values(FieldView(task, "Tags")));
        Assert.Equal("extra", FieldView(task, "Due")["source"]!.GetValue<string>());
        Assert.Equal("own", FieldView(task, "Note")["source"]!.GetValue<string>());
        Assert.True(FieldView(task, "Note")["required"]!.GetValue<bool>());
        Assert.NotNull(task["fields"]);

        // GET одной задачи даёт то же; список — без вычисленного вида.
        var read = await Ok(api.Get(api.P($"/tasks/{Id(task)}")));
        Assert.Equal(5, read["fieldViews"]!.AsArray().Count);
        Assert.Null((await api.Get(api.P("/tasks"))).Body!["data"]![0]!["fieldViews"]);

        // Правка: новое значение, убрать значение (пустой список), убрать дополнительное поле. Версия меняется.
        var edited = await Ok(api.Patch(api.P($"/tasks/{Id(task)}"), new
        {
            version = Version(task),
            fields = new
            {
                values = new object[] { new { fieldId = Id(estimate), values = Texts("13") }, new { fieldId = Id(tags), values = Texts() } },
                removeFields = new[] { Id(due) }
            }
        }));
        Assert.Equal(["13"], Values(FieldView(edited, "Estimate")));
        Assert.Empty(Values(FieldView(edited, "Tags")));
        Assert.DoesNotContain(edited["fieldViews"]!.AsArray(), x => x!["name"]!.GetValue<string>() == "Due");

        // Нельзя: убрать поле типа, обязательное оставить пустым, неверная версия.
        await Fails(api.Patch(api.P($"/tasks/{Id(task)}"), new { version = Version(edited), fields = new { removeFields = new[] { Id(estimate) } } }), HttpStatusCode.BadRequest);
        await Fails(api.Patch(api.P($"/tasks/{Id(task)}"), new { version = Version(edited), fields = new { values = new[] { new { fieldId = Id(estimate), values = Texts() } } } }),
            HttpStatusCode.BadRequest);
        await Fails(api.Patch(api.P($"/tasks/{Id(task)}"), new { version = Version(task), title = "stale" }), HttpStatusCode.Conflict, "modified");

        // Удаление поля типа с выбором: без выбора — 409 in_use с числом задач, «keep» оставляет значения как дополнительное поле.
        var currentType = (await api.Get(api.P($"/task-types/{api.TypeId}"))).Body!;
        var withoutEstimate = new object[] { new { fieldId = Id(level), required = false }, new { fieldId = Id(tags), required = false } };
        await Fails(api.Patch(api.P($"/task-types/{api.TypeId}"), new { version = Version(currentType), fields = withoutEstimate }),
            HttpStatusCode.Conflict, "in_use", "1 task");
        var kept = await Ok(api.Patch(api.P($"/task-types/{api.TypeId}"), new { version = Version(currentType), fields = withoutEstimate, removedFields = "keep" }));
        Assert.Equal(2, kept["fields"]!.AsArray().Count);
        Assert.Equal(1, kept["affectedTasks"]!.GetValue<int>());
        Assert.Equal("Task", kept["name"]!.GetValue<string>()); // тело по-прежнему плоское: поля типа и счётчик рядом
        var afterKeep = await Ok(api.Get(api.P($"/tasks/{Id(task)}")));
        Assert.Equal("extra", FieldView(afterKeep, "Estimate")["source"]!.GetValue<string>());
        Assert.Equal(["13"], Values(FieldView(afterKeep, "Estimate")));

        // «clear» убирает значения у задач.
        var again = new object[] { new { fieldId = Id(tags), required = false } };
        var cleared = await Ok(api.Patch(api.P($"/task-types/{api.TypeId}"), new { version = Version(kept), fields = again, removedFields = "clear" }));
        Assert.Single(cleared["fields"]!.AsArray());
        Assert.Equal(1, cleared["affectedTasks"]!.GetValue<int>());
        var afterClear = await Ok(api.Get(api.P($"/tasks/{Id(task)}")));
        Assert.Equal(["Estimate", "Note", "Tags"], afterClear["fieldViews"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).Order().ToArray());
        Assert.Equal("extra", FieldView(afterClear, "Estimate")["source"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Removed_enum_values_need_a_choice_when_tasks_use_them(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var levels = await Created(api.Post(api.P("/enums"), new { name = "Level", values = Texts("Low", "Mid", "High") }));
        var level = await Created(api.Post(api.P("/fields"), new { name = "Level", type = "enum", enumId = Id(levels) }));
        var values = levels["values"]!.AsArray();
        var (low, mid, high) = (values[0]!["id"]!.GetValue<string>(), values[1]!["id"]!.GetValue<string>(), values[2]!["id"]!.GetValue<string>());

        var type = (await api.Get(api.P($"/task-types/{api.TypeId}"))).Body!;
        await Ok(api.Patch(api.P($"/task-types/{api.TypeId}"), new { version = Version(type), fields = new[] { new { fieldId = Id(level), required = true } } }));

        async Task<JsonNode> NewTask(string title, string value) =>
            await Created(api.Post(api.P("/tasks"), new { title, typeId = api.TypeId, fields = new { values = new[] { new { fieldId = Id(level), values = Texts(value) } } } }));

        var one = await NewTask("one", "Low");
        var two = await NewTask("two", "High");

        // «Mid» нигде не выбран — убирается без выбора.
        var current = (await api.Get(api.P($"/enums/{Id(levels)}"))).Body!;
        var noMid = await Ok(api.Patch(api.P($"/enums/{Id(levels)}"), new
        {
            version = Version(current),
            values = new object[] { new { id = low, name = "Low" }, new { id = high, name = "High" } }
        }));

        // «Low» выбрана у задачи: без выбора — 409 in_use с числом задач.
        var onlyHigh = new object[] { new { id = high, name = "High" } };
        await Fails(api.Patch(api.P($"/enums/{Id(levels)}"), new { version = Version(noMid), values = onlyHigh }), HttpStatusCode.Conflict, "in_use", "1 task");

        // Переназначение на значение, которого нет среди оставшихся, — 400; на оставшееся — задача получает его.
        await Fails(api.Patch(api.P($"/enums/{Id(levels)}"), new { version = Version(noMid), values = onlyHigh, removed = new { reassignTo = mid } }), HttpStatusCode.BadRequest);
        var reassigned = await Ok(api.Patch(api.P($"/enums/{Id(levels)}"), new { version = Version(noMid), values = onlyHigh, removed = new { reassignTo = high } }));
        Assert.Single(reassigned["values"]!.AsArray());
        Assert.Equal(0, noMid["affectedTasks"]!.GetValue<int>()); // Mid нигде не выбран
        Assert.Equal(1, reassigned["affectedTasks"]!.GetValue<int>()); // Low была у одной задачи
        Assert.Equal(["High"], TextsOf(FieldView(await Ok(api.Get(api.P($"/tasks/{Id(one)}"))), "Level")));
        Assert.Equal(["High"], TextsOf(FieldView(await Ok(api.Get(api.P($"/tasks/{Id(two)}"))), "Level")));

        // «Убрать у задач»: добавляем значение, выбираем у задачи, убираем с clear.
        var more = await Ok(api.Patch(api.P($"/enums/{Id(levels)}"), new { version = Version(reassigned), values = new object[] { new { id = high, name = "High" }, new { name = "Extra" } } }));
        var extra = more["values"]![1]!["id"]!.GetValue<string>();
        var three = await NewTask("three", extra);
        var cleared = await Ok(api.Patch(api.P($"/enums/{Id(levels)}"), new { version = Version(more), values = new object[] { new { id = high, name = "High" } }, removed = new { clear = true } }));
        Assert.Single(cleared["values"]!.AsArray());
        Assert.Equal(1, cleared["affectedTasks"]!.GetValue<int>());
        Assert.Empty(Values(FieldView(await Ok(api.Get(api.P($"/tasks/{Id(three)}"))), "Level")));
        Assert.Null((await api.Get(api.P($"/enums/{Id(levels)}"))).Body!["affectedTasks"]); // у обычного чтения счётчика нет

        // Поле обязательное, но значения у задачи нет: смена статуса не мешает, правка содержимого — да.
        var emptyTask = await Ok(api.Get(api.P($"/tasks/{Id(three)}")));
        var statusId = emptyTask["statusId"]!.GetValue<string>();
        await Ok(api.Patch(api.P($"/tasks/{Id(three)}"), new { version = Version(emptyTask), statusId }));
        var afterStatus = await Ok(api.Get(api.P($"/tasks/{Id(three)}")));
        await Fails(api.Patch(api.P($"/tasks/{Id(three)}"), new { version = Version(afterStatus), title = "renamed" }), HttpStatusCode.BadRequest, contains: "Level");
        await Ok(api.Patch(api.P($"/tasks/{Id(three)}"), new { version = Version(afterStatus), title = "renamed", fields = new { values = new[] { new { fieldId = Id(level), values = Texts("High") } } } }));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Tasks_and_board_columns_are_filtered_by_field_values_over_http(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var levels = await Created(api.Post(api.P("/enums"), new { name = "Level", values = Texts("Low", "High") }));
        var level = await Created(api.Post(api.P("/fields"), new { name = "Level", type = "enum", enumId = Id(levels) }));
        var tags = await Created(api.Post(api.P("/fields"), new { name = "Tags", type = "string", multiple = true }));
        var type = (await api.Get(api.P($"/task-types/{api.TypeId}"))).Body!;
        await Ok(api.Patch(api.P($"/task-types/{api.TypeId}"), new { version = Version(type), fields = new[] { new { fieldId = Id(level), required = false }, new { fieldId = Id(tags), required = false } } }));

        async Task New(string title, string levelValue, params string[] tagValues) =>
            await Created(api.Post(api.P("/tasks"), new
            {
                title, typeId = api.TypeId,
                fields = new { values = new[] { new { fieldId = Id(level), values = Texts(levelValue) }, new { fieldId = Id(tags), values = (object[])tagValues } } }
            }));
        await New("a", "High", "ui", "api");
        await New("b", "Low", "api");
        await New("c", "High", "db");

        static string[] Titles(JsonNode list) => list["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).Order().ToArray();

        var high = await Ok(api.Get(api.P("/tasks?field=Level%3DHigh")));
        Assert.Equal(["a", "c"], Titles(high));
        Assert.Equal(2, high["totalCount"]!.GetValue<int>());
        Assert.Equal(["a", "b"], Titles(await Ok(api.Get(api.P("/tasks?field=Tags%3Dapi")))));
        // Повтор параметра — И.
        Assert.Equal(["a"], Titles(await Ok(api.Get(api.P("/tasks?field=Level%3DHigh&field=Tags%3Dapi")))));
        var paged = await Ok(api.Get(api.P("/tasks?field=Level%3DHigh&limit=1&offset=1")));
        Assert.Equal((2, 1), (paged["totalCount"]!.GetValue<int>(), paged["data"]!.AsArray().Count));
        await Fails(api.Get(api.P("/tasks?field=Nope%3D1")), HttpStatusCode.BadRequest, contains: "Nope");
        await Fails(api.Get(api.P("/tasks?field=Level%3DHuge")), HttpStatusCode.BadRequest, contains: "Huge");
        await Fails(api.Get(api.P("/tasks?field=Level")), HttpStatusCode.BadRequest, contains: "Name=value");
        // Операторы: «!=», наличие и подключение (знаки в URL закодированы); сравнения — только для int, float и date.
        Assert.Equal(["b"], Titles(await Ok(api.Get(api.P("/tasks?field=Level%21%3DHigh")))));
        Assert.Equal(["a", "b", "c"], Titles(await Ok(api.Get(api.P("/tasks?field=Level%3Aset")))));
        Assert.Equal(["a", "b", "c"], Titles(await Ok(api.Get(api.P("/tasks?field=Tags%3Aattached&field=Level%3Aattached")))));
        Assert.Empty(Titles(await Ok(api.Get(api.P("/tasks?field=Tags%3Adetached")))));
        await Fails(api.Get(api.P("/tasks?field=Tags%3E%3Da")), HttpStatusCode.BadRequest, contains: "does not apply");

        // Колонка доски: тот же срез как условие просмотра.
        var setId = type["statusSetId"]!.GetValue<string>();
        var set = (await api.Get(api.P($"/status-sets/{setId}"))).Body!;
        var statusId = set["statusIds"]![0]!.GetValue<string>();
        var board = await Created(api.Post(api.P("/boards"), new
        {
            name = "Main", statusSetIds = new[] { setId },
            columns = new[] { new { name = "All", statusIds = new[] { statusId }, dropStatuses = new Dictionary<string, string> { [setId] = statusId } } }
        }));
        var columnId = board["columns"]![0]!["id"]!.GetValue<string>();
        var column = api.P($"/boards/{Id(board)}/columns/{columnId}/tasks");
        Assert.Equal(3, (await Ok(api.Get(column)))["totalCount"]!.GetValue<int>());
        var filtered = await Ok(api.Get(column + "?field=Level%3DHigh&field=Tags%3Dui"));
        Assert.Equal(["a"], Titles(filtered));
        Assert.Equal(1, filtered["totalCount"]!.GetValue<int>());
        await Fails(api.Get(column + "?field=Nope%3D1"), HttpStatusCode.BadRequest);
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Patching_a_field_changes_type_multiplicity_and_enum_with_choices_for_affected_tasks(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var levels = await Created(api.Post(api.P("/enums"), new { name = "Level", values = Texts("Low", "High") }));
        var tags = await Created(api.Post(api.P("/fields"), new { name = "Tags", type = "string", multiple = true }));
        var type = (await api.Get(api.P($"/task-types/{api.TypeId}"))).Body!;
        await Ok(api.Patch(api.P($"/task-types/{api.TypeId}"), new { version = Version(type), fields = new[] { new { fieldId = Id(tags), required = false } } }));
        var task = await Created(api.Post(api.P("/tasks"), new
        {
            title = "t", typeId = api.TypeId, fields = new { values = new[] { new { fieldId = Id(tags), values = Texts("low", "oops") } } }
        }));

        // Несколько значений → одно: без выбора 409 in_use с числом задач, ничего не меняется.
        await Fails(api.Patch(api.P($"/fields/{Id(tags)}"), new { version = Version(tags), multiple = false }), HttpStatusCode.Conflict, "in_use", "1 task");
        Assert.True((await Ok(api.Get(api.P($"/fields/{Id(tags)}"))))["multiple"]!.GetValue<bool>());
        await Fails(api.Patch(api.P($"/fields/{Id(tags)}"), new { version = Version(tags), multiple = false, choice = new { several = "nope" } }), HttpStatusCode.BadRequest);

        var single = await Ok(api.Patch(api.P($"/fields/{Id(tags)}"), new { version = Version(tags), multiple = false, choice = new { several = "keepFirst" } }));
        Assert.False(single["multiple"]!.GetValue<bool>());
        Assert.Equal(["low"], Values(FieldView(await Ok(api.Get(api.P($"/tasks/{Id(task)}"))), "Tags")));

        // string → enum: «low» находится по названию.
        var asEnum = await Ok(api.Patch(api.P($"/fields/{Id(tags)}"), new { version = Version(single), type = "enum", enumId = Id(levels) }));
        Assert.Equal(Id(levels), asEnum["enumId"]!.GetValue<string>());
        Assert.Equal(["Low"], TextsOf(FieldView(await Ok(api.Get(api.P($"/tasks/{Id(task)}"))), "Tags")));

        // enum → string, а смена, теряющая данные, — 400.
        await Fails(api.Patch(api.P($"/fields/{Id(tags)}"), new { version = Version(asEnum), type = "int" }), HttpStatusCode.BadRequest, contains: "string first");
        await Fails(api.Patch(api.P($"/fields/{Id(tags)}"), new { version = Version(asEnum), type = "string", enumId = Id(levels) }), HttpStatusCode.BadRequest);
        var text = await Ok(api.Patch(api.P($"/fields/{Id(tags)}"), new { version = Version(asEnum), type = "string" }));
        Assert.Equal("string", text["type"]!.GetValue<string>());
        Assert.Equal(["Low"], Values(FieldView(await Ok(api.Get(api.P($"/tasks/{Id(task)}"))), "Tags")));

        // string → int: «Low» не разбирается — нужен выбор; с clearUnconvertible значение убирается.
        await Fails(api.Patch(api.P($"/fields/{Id(tags)}"), new { version = Version(text), type = "int" }), HttpStatusCode.Conflict, "in_use", "Low");
        var asInt = await Ok(api.Patch(api.P($"/fields/{Id(tags)}"), new { version = Version(text), type = "int", choice = new { clearUnconvertible = true } }));
        Assert.Equal("int", asInt["type"]!.GetValue<string>());
        Assert.Empty(Values(FieldView(await Ok(api.Get(api.P($"/tasks/{Id(task)}"))), "Tags")));
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Fields_of_one_project_are_not_visible_or_usable_in_another(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var (otherProject, otherType) = await api.CreateProject("Other");
        var levels = await Created(api.Post(api.P("/enums"), new { name = "Level", values = Texts("Low") }));
        var field = await Created(api.Post(api.P("/fields"), new { name = "Estimate", type = "int" }));
        var other = $"/projects/{otherProject}";

        Assert.Equal(0, (await api.Get(other + "/fields")).Body!["totalCount"]!.GetValue<int>());
        Assert.Equal(0, (await api.Get(other + "/enums")).Body!["totalCount"]!.GetValue<int>());
        await Fails(api.Get(other + $"/fields/{Id(field)}"), HttpStatusCode.NotFound);
        await Fails(api.Get(other + $"/enums/{Id(levels)}"), HttpStatusCode.NotFound);
        await Fails(api.Patch(other + $"/fields/{Id(field)}", new { name = "Hacked", version = Version(field) }), HttpStatusCode.NotFound);
        await Fails(api.Delete(other + $"/enums/{Id(levels)}?version={Version(levels)}"), HttpStatusCode.NotFound);

        // Поле и перечисление чужого проекта нельзя подключить ни к полю, ни к типу, ни к задаче.
        await Fails(api.Post(other + "/fields", new { name = "Level", type = "enum", enumId = Id(levels) }), HttpStatusCode.BadRequest);
        var type = (await api.Get(other + $"/task-types/{otherType}")).Body!;
        await Fails(api.Patch(other + $"/task-types/{otherType}", new { version = Version(type), fields = new[] { new { fieldId = Id(field), required = false } } }), HttpStatusCode.BadRequest);
        await Fails(api.Post(other + "/tasks", new { title = "t", typeId = otherType, fields = new { values = new[] { new { fieldId = Id(field), values = Texts("1") } } } }), HttpStatusCode.BadRequest);

        // Несуществующий проект — 404 на всех маршрутах полей.
        var nowhere = $"/projects/{Guid.NewGuid()}";
        await Fails(api.Get(nowhere + "/fields"), HttpStatusCode.NotFound);
        await Fails(api.Post(nowhere + "/enums", new { name = "X", values = Texts("a") }), HttpStatusCode.NotFound);

        // В своём проекте всё осталось.
        Assert.Equal("Estimate", (await Ok(api.Get(api.P($"/fields/{Id(field)}"))))["name"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Storages))]
    public async Task Fields_and_enums_support_the_edit_lock(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var field = await Created(api.Post(api.P("/fields"), new { name = "Estimate", type = "int" }));
        var levels = await Created(api.Post(api.P("/enums"), new { name = "Level", values = Texts("Low") }));

        Assert.Equal(HttpStatusCode.OK, (await api.Post(api.P($"/fields/{Id(field)}/lock"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await api.Post(api.P($"/enums/{Id(levels)}/lock"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await api.Get(api.P($"/fields/{Id(field)}/lock"))).Status);
    }
}
