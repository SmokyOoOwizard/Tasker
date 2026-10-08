using System.Text.Json.Nodes;
using Xunit;

namespace Tasker.Tests;

/// <summary>Инструменты MCP для полей, перечислений и значений у задач — через настоящий демон MCP; данные проверяются и командной строкой.</summary>
public class FieldsMcpTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();

    public void Dispose() => _daemon.Dispose();

    private sealed class Agent(DaemonFixture daemon, string key, string folder, Guid projectId, Guid typeId)
    {
        public string Folder { get; } = folder;
        public Guid ProjectId { get; } = projectId;
        public Guid TypeId { get; } = typeId;

        private object With(object? args)
        {
            var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args ?? new { }))!.AsObject();
            node["projectId"] ??= ProjectId.ToString();
            return node;
        }

        public async Task<JsonNode> Call(string tool, object? args = null)
        {
            var (isError, text) = await daemon.CallToolResult(key, tool, With(args));
            Assert.False(isError, $"{tool} failed: {text}");
            return JsonNode.Parse(text)!;
        }

        public async Task<string> Text(string tool, object? args = null)
        {
            var (isError, text) = await daemon.CallToolResult(key, tool, With(args));
            Assert.False(isError, $"{tool} failed: {text}");
            return text;
        }

        public async Task<string> Fail(string tool, object? args = null)
        {
            var (isError, text) = await daemon.CallToolResult(key, tool, With(args));
            Assert.True(isError, $"{tool} unexpectedly succeeded: {text}");
            return text;
        }
    }

    private async Task<Agent> Start()
    {
        var folder = await _daemon.Workspace("a", "Alpha");
        var status = await _daemon.Start();
        var key = status.Workspaces.Single().Key!;
        var projectId = Guid.Parse(JsonNode.Parse(await _daemon.CallTool(key, "list_projects"))!["data"]![0]!["id"]!.GetValue<string>());

        async Task<JsonNode> Create(string tool, object args)
        {
            var (isError, text) = await _daemon.CallToolResult(key, tool, args);
            Assert.False(isError, text);
            return JsonNode.Parse(text)!;
        }

        var todo = await Create("create_status", new { projectId, name = "Todo", color = "#112233" });
        var set = await Create("create_status_set", new { projectId, name = "Set", statusIds = new[] { todo["id"]!.GetValue<string>() } });
        var type = await Create("create_task_type", new { projectId, name = "Task", statusSetId = set["id"]!.GetValue<string>() });
        return new Agent(_daemon, key, folder, projectId, Guid.Parse(type["id"]!.GetValue<string>()));
    }

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();
    private static string Version(JsonNode node) => node["version"]!.GetValue<string>();
    private static string[] Names(JsonNode list) => list["data"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray();

    private static JsonNode View(JsonNode task, string name) =>
        task["fieldViews"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == name)!;

    private static string[] Texts(JsonNode view) => view["texts"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    [Fact]
    public async Task The_field_tools_are_listed_with_the_right_markers_and_parameters()
    {
        await Start();
        var tools = (await _daemon.Rpc("tools/list"))["tools"]!.AsArray().ToDictionary(x => x!["name"]!.GetValue<string>(), x => x!);

        foreach (var name in new[] { "list_fields", "get_field", "create_field", "update_field", "delete_field",
                     "list_enums", "get_enum", "create_enum", "update_enum", "delete_enum" })
            Assert.True(tools.ContainsKey(name), $"tool {name} is missing");
        foreach (var name in new[] { "list_fields", "get_field", "list_enums", "get_enum" })
            Assert.True(tools[name]["annotations"]!["readOnlyHint"]!.GetValue<bool>(), name);
        foreach (var name in new[] { "delete_field", "delete_enum" })
            Assert.True(tools[name]["annotations"]!["destructiveHint"]!.GetValue<bool>(), name);

        // projectId необязателен и у новых инструментов, workspace добавлен.
        foreach (var name in new[] { "list_fields", "create_field", "update_enum", "delete_enum", "create_task", "update_task_type" })
        {
            var schema = tools[name]["inputSchema"]!;
            Assert.DoesNotContain("projectId", schema["required"]?.ToJsonString() ?? "");
            Assert.NotNull(schema["properties"]!["workspace"]);
        }

        // Параметры полей и выборов есть в схеме и необязательны.
        foreach (var (tool, parameter) in new[]
                 {
                     ("create_task", "fields"), ("update_task", "fields"), ("create_task_type", "fields"), ("update_task_type", "fields"),
                     ("update_task_type", "removedFields"), ("update_enum", "removedValues"), ("update_enum", "reassignTo"), ("update_enum", "values")
                 })
        {
            var schema = tools[tool]["inputSchema"]!;
            Assert.NotNull(schema["properties"]![parameter]);
            Assert.DoesNotContain($"\"{parameter}\"", schema["required"]?.ToJsonString() ?? "");
        }
        Assert.Contains("clear", tools["update_task_type"]["inputSchema"]!["properties"]!["removedFields"]!.ToJsonString());
        Assert.Contains("reassign", tools["update_enum"]["inputSchema"]!["properties"]!["removedValues"]!.ToJsonString());
        Assert.Contains("enum", tools["create_field"]["inputSchema"]!["properties"]!["type"]!.ToJsonString());
    }

    [Fact]
    public async Task Catalog_type_fields_and_task_values_through_mcp_are_visible_in_the_cli()
    {
        var agent = await Start();

        var levels = await agent.Call("create_enum", new { name = "Level", values = new[] { "Low", "High" } });
        var level = await agent.Call("create_field", new { name = "Level", type = "enum", enumId = Id(levels) });
        var estimate = await agent.Call("create_field", new { name = "Estimate", type = "int" });
        var tags = await agent.Call("create_field", new { name = "Tags", type = "string", multiple = true });

        Assert.Equal(["Estimate", "Level", "Tags"], Names(await agent.Call("list_fields")));
        Assert.Equal(["Level"], Names(await agent.Call("list_fields", new { offset = 1, limit = 1 })));
        Assert.Equal(Id(tags), Id(await agent.Call("get_field", new { field = "tags" })));
        Assert.Equal(Id(estimate), Id(await agent.Call("get_field", new { field = Id(estimate) })));
        Assert.Equal(["Level"], Names(await agent.Call("list_enums")));
        Assert.Equal(Id(levels), Id(await agent.Call("get_enum", new { enumeration = "level" })));

        // Поля типа: список с обязательностью.
        var type = (await agent.Call("list_task_types"))["data"]![0]!;
        var typed = await agent.Call("update_task_type", new
        {
            taskTypeId = Id(type),
            version = Version(type),
            fields = new object[] { new { fieldId = Id(estimate), required = true }, new { fieldId = Id(level), required = false } }
        });
        Assert.Equal(2, typed["fields"]!.AsArray().Count);

        // Задача: значения, enum — по названию; обязательное без значения — [invalid].
        Assert.StartsWith("[invalid]", await agent.Fail("create_task", new { title = "no estimate", typeId = agent.TypeId }));
        var task = await agent.Call("create_task", new
        {
            title = "Feature",
            typeId = agent.TypeId,
            fields = new
            {
                values = new object[]
                {
                    new { fieldId = Id(estimate), values = new[] { "5" } },
                    new { fieldId = Id(level), values = new[] { "high" } },
                    new { fieldId = Id(tags), values = new[] { "x", "y" } }
                },
                newOwnFields = new object[] { new { name = "Note", type = "string", values = new[] { "hello" } } }
            }
        });
        Assert.Equal(["5"], Texts(View(task, "Estimate")));
        Assert.Equal(["High"], Texts(View(task, "Level")));
        Assert.Equal("extra", View(task, "Tags")["source"]!.GetValue<string>());
        Assert.Equal("own", View(task, "Note")["source"]!.GetValue<string>());

        var read = await agent.Call("get_task", new { taskId = Id(task) });
        Assert.Equal(4, read["fieldViews"]!.AsArray().Count);
        Assert.Null((await agent.Call("list_tasks"))["data"]![0]!["fieldViews"]);

        // update_task: сменить значение, убрать дополнительное поле.
        var edited = await agent.Call("update_task", new
        {
            taskId = Id(task),
            version = Version(task),
            fields = new { values = new[] { new { fieldId = Id(estimate), values = new[] { "8" } } }, removeFields = new[] { Id(tags) } }
        });
        Assert.Equal(["8"], Texts(View(edited, "Estimate")));
        Assert.DoesNotContain(edited["fieldViews"]!.AsArray(), x => x!["name"]!.GetValue<string>() == "Tags");
        Assert.StartsWith("[invalid]", await agent.Fail("update_task", new { taskId = Id(task), version = Version(edited), fields = new { values = new[] { new { fieldId = Id(estimate), values = new[] { "abc" } } } } }));

        // Командная строка видит то же, что записал MCP.
        var cli = await TaskerProcess.Run("task", "list", "--json", "--project", agent.ProjectId.ToString(), "-w", agent.Folder);
        Assert.Equal(0, cli.Code);
        Assert.Equal("Feature", cli.Json["data"]![0]!["title"]!.GetValue<string>());
        Assert.True(File.Exists(FileFinder.In(Path.Combine(agent.Folder, ".tasker", "projects", agent.ProjectId.ToString("D"), "fields"), Guid.Parse(Id(estimate)))));
        Assert.True(File.Exists(FileFinder.In(Path.Combine(agent.Folder, ".tasker", "projects", agent.ProjectId.ToString("D"), "enums"), Guid.Parse(Id(levels)))));

        // Переименование поля и перечисления; значение перечисления остаётся тем же.
        var renamed = await agent.Call("update_field", new { fieldId = Id(estimate), version = Version(estimate), name = "Points" });
        Assert.Equal("Points", renamed["name"]!.GetValue<string>());
        var highId = levels["values"]![1]!["id"]!.GetValue<string>();
        var enumEdit = await agent.Call("update_enum", new
        {
            enumId = Id(levels), version = Version(levels), name = "Priority",
            values = new object[] { new { id = levels["values"]![0]!["id"]!.GetValue<string>(), name = "Low" }, new { id = highId, name = "Urgent" } }
        });
        Assert.Equal("Priority", enumEdit["name"]!.GetValue<string>());
        Assert.Equal(["Urgent"], Texts(View(await agent.Call("get_task", new { taskId = Id(task) }), "Level")));
    }

    [Fact]
    public async Task update_field_changes_type_multiplicity_and_enum_with_choices_for_affected_tasks()
    {
        var agent = await Start();
        var levels = await agent.Call("create_enum", new { name = "Level", values = new[] { "Low", "High" } });
        var tags = await agent.Call("create_field", new { name = "Tags", type = "string", multiple = true });
        var type = (await agent.Call("list_task_types"))["data"]![0]!;
        await agent.Call("update_task_type", new { taskTypeId = Id(type), version = Version(type), fields = new[] { new { fieldId = Id(tags), required = false } } });
        var task = await agent.Call("create_task", new
        {
            title = "t", typeId = agent.TypeId, fields = new { values = new[] { new { fieldId = Id(tags), values = new[] { "low", "oops" } } } }
        });

        var several = await agent.Fail("update_field", new { fieldId = Id(tags), version = Version(tags), multiple = false });
        Assert.StartsWith("[in_use]", several);
        Assert.Contains("1 task", several);
        var single = await agent.Call("update_field", new { fieldId = Id(tags), version = Version(tags), multiple = false, several = "keepFirst" });
        Assert.False(single["multiple"]!.GetValue<bool>());

        var asEnum = await agent.Call("update_field", new { fieldId = Id(tags), version = Version(single), type = "enum", enumId = Id(levels) });
        Assert.Equal(Id(levels), asEnum["enumId"]!.GetValue<string>());
        Assert.Equal(["Low"], Texts(View(await agent.Call("get_task", new { taskId = Id(task) }), "Tags")));

        Assert.StartsWith("[invalid]", await agent.Fail("update_field", new { fieldId = Id(tags), version = Version(asEnum), type = "float" }));
        var asText = await agent.Call("update_field", new { fieldId = Id(tags), version = Version(asEnum), type = "string" });
        Assert.Equal("string", asText["type"]!.GetValue<string>());
        var cleared = await agent.Call("update_field", new { fieldId = Id(tags), version = Version(asText), type = "int", clearUnconvertible = true });
        Assert.Equal("int", cleared["type"]!.GetValue<string>());
        Assert.Empty(Texts(View(await agent.Call("get_task", new { taskId = Id(task) }), "Tags")));
    }

    [Fact]
    public async Task Choices_when_removing_fields_from_a_type_and_values_from_an_enum()
    {
        var agent = await Start();
        var levels = await agent.Call("create_enum", new { name = "Level", values = new[] { "Low", "Mid", "High" } });
        var mid = levels["values"]![1]!["id"]!.GetValue<string>();
        var high = levels["values"]![2]!["id"]!.GetValue<string>();
        var level = await agent.Call("create_field", new { name = "Level", type = "enum", enumId = Id(levels) });
        var estimate = await agent.Call("create_field", new { name = "Estimate", type = "int" });
        var type = (await agent.Call("list_task_types"))["data"]![0]!;
        var typed = await agent.Call("update_task_type", new
        {
            taskTypeId = Id(type), version = Version(type),
            fields = new object[] { new { fieldId = Id(level), required = false }, new { fieldId = Id(estimate), required = false } }
        });

        var one = await agent.Call("create_task", new
        {
            title = "one", typeId = agent.TypeId,
            fields = new { values = new object[] { new { fieldId = Id(level), values = new[] { "Low" } }, new { fieldId = Id(estimate), values = new[] { "3" } } } }
        });
        var two = await agent.Call("create_task", new
        {
            title = "two", typeId = agent.TypeId, fields = new { values = new[] { new { fieldId = Id(level), values = new[] { "Mid" } } } }
        });

        // Значение enum, выбранное у задач: без выбора — [in_use] с числом задач; неполный выбор — [invalid].
        var removeLow = new[] { new { id = mid, name = "Mid" }, new { id = high, name = "High" } };
        var noChoice = await agent.Fail("update_enum", new { enumId = Id(levels), version = Version(levels), values = removeLow });
        Assert.StartsWith("[in_use]", noChoice);
        Assert.Contains("1 task", noChoice);
        Assert.StartsWith("[invalid]", await agent.Fail("update_enum", new { enumId = Id(levels), version = Version(levels), values = removeLow, removedValues = "reassign" }));
        Assert.StartsWith("[invalid]", await agent.Fail("update_enum", new { enumId = Id(levels), version = Version(levels), values = removeLow, removedValues = "clear", reassignTo = mid }));
        Assert.StartsWith("[invalid]", await agent.Fail("update_enum", new { enumId = Id(levels), version = Version(levels), values = removeLow, reassignTo = mid }));
        Assert.StartsWith("[invalid]", await agent.Fail("update_enum", new { enumId = Id(levels), version = Version(levels), values = removeLow, removedValues = "reassign", reassignTo = Guid.NewGuid() }));

        var reassigned = await agent.Call("update_enum", new { enumId = Id(levels), version = Version(levels), values = removeLow, removedValues = "reassign", reassignTo = high });
        Assert.Equal(2, reassigned["values"]!.AsArray().Count);
        Assert.Equal(1, reassigned["affectedTasks"]!.GetValue<int>());
        Assert.Equal(["High"], Texts(View(await agent.Call("get_task", new { taskId = Id(one) }), "Level")));

        var cleared = await agent.Call("update_enum", new
        {
            enumId = Id(levels), version = Version(reassigned), values = new[] { new { id = high, name = "High" } }, removedValues = "clear"
        });
        Assert.Single(cleared["values"]!.AsArray());
        Assert.Equal(1, cleared["affectedTasks"]!.GetValue<int>());
        Assert.Empty(Texts(View(await agent.Call("get_task", new { taskId = Id(two) }), "Level")));

        // Поле типа со значениями: без выбора — [in_use]; keep оставляет значение как дополнительное поле; clear убирает.
        var onlyLevel = new[] { new { fieldId = Id(level), required = false } };
        var noFieldChoice = await agent.Fail("update_task_type", new { taskTypeId = Id(type), version = Version(typed), fields = onlyLevel });
        Assert.StartsWith("[in_use]", noFieldChoice);
        Assert.Contains("1 task", noFieldChoice);

        var kept = await agent.Call("update_task_type", new { taskTypeId = Id(type), version = Version(typed), fields = onlyLevel, removedFields = "keep" });
        Assert.Single(kept["fields"]!.AsArray());
        Assert.Equal(1, kept["affectedTasks"]!.GetValue<int>());
        var extra = View(await agent.Call("get_task", new { taskId = Id(one) }), "Estimate");
        Assert.Equal("extra", extra["source"]!.GetValue<string>());
        Assert.Equal(["3"], Texts(extra));

        // Поле каталога, у которого есть значения или подключение, не удаляется; после очистки — удаляется.
        Assert.StartsWith("[in_use]", await agent.Fail("delete_field", new { fieldId = Id(level), version = Version(level) }));
        Assert.StartsWith("[in_use]", await agent.Fail("delete_enum", new { enumId = Id(levels), version = Version(cleared) }));
        var cleanType = await agent.Call("update_task_type", new { taskTypeId = Id(type), version = Version(kept), fields = Array.Empty<object>(), removedFields = "clear" });
        Assert.Empty(cleanType["fields"]!.AsArray());
        Assert.Equal(1, cleanType["affectedTasks"]!.GetValue<int>());
        var oneNow = await agent.Call("get_task", new { taskId = Id(one) });
        Assert.Equal(["Estimate"], oneNow["fieldViews"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray());
        Assert.StartsWith("[in_use]", await agent.Fail("delete_field", new { fieldId = Id(estimate), version = Version(estimate) }));
        var stripped = await agent.Call("update_task", new { taskId = Id(one), version = Version(oneNow), fields = new { removeFields = new[] { Id(estimate) } } });
        Assert.Empty(stripped["fieldViews"]!.AsArray());
        Assert.Equal("deleted", (await agent.Text("delete_field", new { fieldId = Id(level), version = Version(level) })));
        Assert.Equal("deleted", (await agent.Text("delete_field", new { fieldId = Id(estimate), version = Version(estimate) })));
        Assert.Equal("deleted", (await agent.Text("delete_enum", new { enumId = Id(levels), version = Version(cleared) })));
        Assert.Empty((await agent.Call("list_fields"))["data"]!.AsArray());
    }

    [Fact]
    public async Task Errors_carry_codes_an_agent_can_react_to()
    {
        var agent = await Start();
        var levels = await agent.Call("create_enum", new { name = "Level", values = new[] { "Low" } });
        var field = await agent.Call("create_field", new { name = "Estimate", type = "int" });
        var unknown = Guid.NewGuid().ToString();

        // [invalid]
        Assert.StartsWith("[invalid]", await agent.Fail("create_field", new { name = " ", type = "int" }));
        Assert.StartsWith("[invalid]", await agent.Fail("create_field", new { name = "X", type = "enum" }));
        Assert.StartsWith("[invalid]", await agent.Fail("create_field", new { name = "X", type = "int", enumId = Id(levels) }));
        Assert.StartsWith("[invalid]", await agent.Fail("create_enum", new { name = "Empty", values = Array.Empty<string>() }));
        Assert.StartsWith("[invalid]", await agent.Fail("create_enum", new { name = "Twice", values = new[] { "a", "A" } }));

        // [in_use]: занятые имена.
        Assert.StartsWith("[in_use]", await agent.Fail("create_field", new { name = "estimate", type = "int" }));
        Assert.StartsWith("[in_use]", await agent.Fail("create_enum", new { name = "level", values = new[] { "a" } }));

        // [modified]
        await agent.Call("update_field", new { fieldId = Id(field), version = Version(field), name = "Points" });
        Assert.StartsWith("[modified]", await agent.Fail("update_field", new { fieldId = Id(field), version = Version(field), name = "Stale" }));
        Assert.StartsWith("[modified]", await agent.Fail("delete_field", new { fieldId = Id(field), version = Version(field) }));
        Assert.StartsWith("[modified]", await agent.Fail("update_enum", new { enumId = Id(levels), version = "stale", name = "Stale" }));
        Assert.StartsWith("[modified]", await agent.Fail("delete_enum", new { enumId = Id(levels), version = "stale" }));

        // [not_found]
        Assert.StartsWith("[not_found]", await agent.Fail("get_field", new { field = "nope" }));
        Assert.StartsWith("[not_found]", await agent.Fail("get_field", new { field = unknown }));
        Assert.StartsWith("[not_found]", await agent.Fail("get_enum", new { enumeration = "nope" }));
        Assert.StartsWith("[not_found]", await agent.Fail("update_field", new { fieldId = unknown, version = "v" }));
        Assert.StartsWith("[not_found]", await agent.Fail("delete_field", new { fieldId = unknown, version = "v" }));
        Assert.StartsWith("[not_found]", await agent.Fail("update_enum", new { enumId = unknown, version = "v" }));
        Assert.StartsWith("[not_found]", await agent.Fail("delete_enum", new { enumId = unknown, version = "v" }));
        Assert.StartsWith("[not_found]", await agent.Fail("list_fields", new { projectId = unknown }));
        Assert.StartsWith("[not_found]", await agent.Fail("list_enums", new { projectId = unknown }));
        Assert.StartsWith("[not_found]", await agent.Fail("create_field", new { projectId = unknown, name = "X", type = "int" }));
    }

    [Fact]
    public async Task Fields_follow_the_workspace_argument_and_the_default_project()
    {
        await _daemon.Workspace("a", "Alpha");
        await _daemon.Workspace("b", "Beta");
        var status = await _daemon.Start();
        var a = status.Workspaces.Single(x => x.Path.EndsWith("/a")).Key!;
        var b = status.Workspaces.Single(x => x.Path.EndsWith("/b")).Key!;

        async Task<JsonNode> Call(string? workspace, string tool, object args)
        {
            var (isError, text) = await _daemon.CallToolResult(workspace, tool, args);
            Assert.False(isError, $"{tool}: {text}");
            return JsonNode.Parse(text)!;
        }

        // Проект по умолчанию (один в области) и область аргументом — без projectId.
        await Call(a, "create_field", new { name = "Estimate", type = "int" });
        await Call(b, "create_enum", new { name = "Level", values = new[] { "Low" } });
        Assert.Equal(["Estimate"], Names(await Call(a, "list_fields", new { })));
        Assert.Empty((await Call(b, "list_fields", new { }))["data"]!.AsArray());
        Assert.Equal(["Level"], Names(await Call(b, "list_enums", new { })));
        Assert.Empty((await Call(a, "list_enums", new { }))["data"]!.AsArray());

        // Область не указана, а их две — понятная ошибка; у второго проекта в области projectId обязателен.
        var (isError, text) = await _daemon.CallToolResult(null, "list_fields", new { });
        Assert.True(isError);
        Assert.StartsWith("[invalid] workspace is required", text);

        await Call(a, "create_project", new { name = "Gamma" });
        (isError, text) = await _daemon.CallToolResult(a, "list_fields", new { });
        Assert.True(isError);
        Assert.StartsWith("[invalid] projectId is required", text);
    }
    [Fact]
    public async Task List_tasks_and_get_board_filter_by_field_values()
    {
        var agent = await Start();
        var level = await agent.Call("create_field", new { name = "Level", type = "string" });
        var tags = await agent.Call("create_field", new { name = "Tags", type = "string", multiple = true });
        var type = (await agent.Call("list_task_types"))["data"]![0]!;
        await agent.Call("update_task_type", new
        {
            taskTypeId = Id(type), version = Version(type),
            fields = new object[] { new { fieldId = Id(level), required = false }, new { fieldId = Id(tags), required = false } }
        });

        async Task Task(string title, string levelValue, params string[] tagValues) =>
            await agent.Call("create_task", new
            {
                title, typeId = agent.TypeId,
                fields = new { values = new object[] { new { fieldId = Id(level), values = new[] { levelValue } }, new { fieldId = Id(tags), values = tagValues } } }
            });
        await Task("a", "x", "ui", "api");
        await Task("b", "y", "api");
        await Task("c", "x", "db");

        static string[] Titles(JsonNode list) => list["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).Order().ToArray();

        var x = await agent.Call("list_tasks", new { field = new[] { "Level=x" } });
        Assert.Equal(["a", "c"], Titles(x));
        Assert.Equal(2, x["totalCount"]!.GetValue<int>());
        Assert.Equal(["a", "b"], Titles(await agent.Call("list_tasks", new { field = new[] { "Tags=api" } })));
        Assert.Equal(["a"], Titles(await agent.Call("list_tasks", new { field = new[] { "Level=x", "Tags=api" } })));
        var paged = await agent.Call("list_tasks", new { field = new[] { "Level=x" }, limit = 1 });
        Assert.Equal((2, 1), (paged["totalCount"]!.GetValue<int>(), paged["data"]!.AsArray().Count));
        Assert.Contains("no field 'Nope'", await agent.Fail("list_tasks", new { field = new[] { "Nope=1" } }));
        Assert.Equal(["b"], Titles(await agent.Call("list_tasks", new { field = new[] { "Level!=x" } })));
        Assert.Equal(["a", "b", "c"], Titles(await agent.Call("list_tasks", new { field = new[] { "Tags:set", "Level:attached" } })));
        Assert.Empty(Titles(await agent.Call("list_tasks", new { field = new[] { "Level:unset" } })));
        Assert.Contains("does not apply", await agent.Fail("list_tasks", new { field = new[] { "Level>x" } }));

        var setId = type["statusSetId"]!.GetValue<string>();
        var statusId = Id((await agent.Call("list_statuses"))["data"]![0]!);
        var board = await agent.Call("create_board", new
        {
            name = "Main", statusSetIds = new[] { setId },
            columns = new[] { new { name = "All", statusIds = new[] { statusId }, dropStatuses = new Dictionary<string, string> { [setId] = statusId } } }
        });
        var shown = await agent.Call("get_board", new { boardId = Id(board), field = new[] { "Level=y" } });
        var column = shown["columns"]![0]!["tasks"]!;
        Assert.Equal(["b"], Titles(column));
        Assert.Equal(1, column["totalCount"]!.GetValue<int>());
    }
}
