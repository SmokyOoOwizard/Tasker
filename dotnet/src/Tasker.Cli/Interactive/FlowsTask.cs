using Tasker.Cli.Commands;
using Tasker.Core;
using Tasker.Core.Boards;
using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Core.Links;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;

namespace Tasker.Cli.Interactive;

internal static partial class Flows
{
    private const string NoValue = "";

    private sealed record FieldInput(Guid Id, string Name, FieldType Type, bool Multiple, bool Required, Guid? EnumId, List<string> Current);

    private static async Task<TaskType> TypeOf(Wizard w, Guid project, Guid id) =>
        await w.Session.Get<ITaskTypeStorage>().GetById(project, id, w.Ct) ?? throw new CliException($"No task type '{D(id)}'");

    private static async Task<List<Item>> SetStatuses(Wizard w, Guid project, Guid setId)
    {
        var set = await w.Session.Get<IStatusSetStorage>().GetById(project, setId, w.Ct) ?? throw new CliException($"No status set '{D(setId)}'");
        var statuses = await w.Session.Get<IStatusStorage>().GetAll(project, w.Ct);
        return set.StatusIds
            .Select(id => statuses.FirstOrDefault(s => s.Id == id))
            .OfType<Status>()
            .Select(s => new Item(D(s.Id), s.Name, s.Color))
            .ToList();
    }

    private static async Task<List<Item>> EnumValues(Wizard w, Guid project, Guid enumId)
    {
        var e = await w.Session.Get<IFieldEnumStorage>().GetById(project, enumId, w.Ct) ?? throw new CliException($"No enum '{D(enumId)}'");
        return ValueItems(e);
    }

    private static async Task<List<string>> FieldValuesOf(Wizard w, Guid project, FieldInput field)
    {
        var title = field.Name + (field.Required ? " (required)" : " (optional: no value is allowed)");
        switch (field.Type)
        {
            case FieldType.Enum:
            {
                var enumId = field.EnumId ?? throw new CliException($"Field '{field.Name}' has no enum");
                var items = await EnumValues(w, project, enumId);
                if (field.Multiple)
                    return w.Multi(title, items, !field.Required).Select(i => i.Key).ToList();
                if (!field.Required)
                    items.Insert(0, new Item(NoValue, "(no value)"));
                var chosen = w.Select(title, items);
                return chosen.Key == NoValue ? [] : [chosen.Key];
            }
            case FieldType.Bool:
            {
                var items = new List<Item> { new("true", "Yes (true)"), new("false", "No (false)") };
                if (!field.Required)
                    items.Insert(0, new Item(NoValue, "(no value)"));
                var chosen = w.Menu(title, items);
                return chosen.Key == NoValue ? [] : [chosen.Key];
            }
            default:
            {
                var check = field.Type switch
                {
                    FieldType.Int => Check.Int,
                    FieldType.Float => Check.Float,
                    FieldType.Date => Check.Date,
                    _ => Check.Required
                };
                var hint = (field.Multiple, field.Required) switch
                {
                    (true, true) => "Several values: separate them with a semicolon",
                    (true, false) => "Several values: separate them with a semicolon; empty clears",
                    (false, false) => "Empty clears the value",
                    _ => ""
                };
                var text = w.TextWith(title, string.Join("; ", field.Current), check, !field.Required, hint);
                if (field.Multiple)
                {
                    var values = text.Split(';').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
                    foreach (var value in values)
                        if (check.Error(value, false) is { } error)
                            throw new CliException(error);
                    return values;
                }
                return text.Length == 0 ? [] : [text];
            }
        }
    }

    private static List<string> FieldArguments(Guid fieldId, List<string> values)
    {
        var id = D(fieldId);
        return values.Count == 0 ? ["--field", $"{id}="] : values.SelectMany(v => new[] { "--field", $"{id}={v}" }).ToList();
    }

    private static async Task<List<LinkType>> HierarchicalTypes(Wizard w, Guid project) =>
        (await LinkTypes(w, project)).Where(t => t.Hierarchical).ToList();

    private static async Task<List<string>> ParentTypeArguments(Wizard w, Guid project)
    {
        var types = await HierarchicalTypes(w, project);
        if (types.Count <= 1)
            return [];
        var chosen = w.Select("Hierarchical link type of the parent", types.Select(t => new Item(D(t.Id), t.Name, t.OutwardName)).ToList());
        return ["--parent-type", chosen.Key];
    }

    private static async Task<List<Item>> Others(Wizard w, Guid project, string except) =>
        (await Tasks(w, project)).Where(t => t.Key != except).ToList();

    private static async Task<List<string>> TaskFlow(Wizard w, FlowAction action)
    {
        var project = await w.ProjectId();
        switch (action)
        {
            case FlowAction.Create:
                return await CreateTask(w, project);
            case FlowAction.Change:
                return await ChangeTask(w, project);
            case FlowAction.View:
                throw NothingToDo();
            default:
            {
                var task = w.Select("Task", await Tasks(w, project));
                ConfirmDelete(w, "task", task.Label);
                return new List<string> { "task", "delete", task.Key }.With(w.ExpectedVersion(task));
            }
        }
    }

    private static async Task<List<string>> CreateTask(Wizard w, Guid project)
    {
        var title = w.Text("Task title", "", Check.Required);
        var chosenType = w.Select("Task type", await TaskTypes(w, project));
        var taskType = await TypeOf(w, project, ParseId(chosenType.Key, "task type"));
        var statuses = await SetStatuses(w, project, taskType.StatusSetId);
        statuses.Insert(0, new Item("default", "(default: the first status of the type's set)"));
        var status = w.Menu("Status", statuses);
        var description = w.Editor("Description (optional)", "", "Press e to write it in your editor");
        var series = w.Multi("Series to put the task into (optional)", await SeriesItems(w, project), true);
        var parents = w.Multi("Parent tasks (optional)", await Tasks(w, project), true);
        var argv = new List<string> { "task", "create", title, "--type", chosenType.Key };
        if (status.Key != "default")
            argv.AddRange(["--status", status.Key]);
        if (description.Length > 0)
            argv.AddRange(["-d", description]);
        foreach (var s in series)
            argv.AddRange(["--series", s.Key]);
        if (parents.Count > 0)
        {
            argv.Add("--parent");
            argv.AddRange(parents.Select(p => p.Key));
            argv.AddRange(await ParentTypeArguments(w, project));
        }
        var catalog = await w.Session.Get<IFieldStorage>().GetAll(project, w.Ct);
        foreach (var typeField in taskType.Fields)
        {
            if (catalog.FirstOrDefault(f => f.Id == typeField.FieldId) is not { } definition)
                continue;
            var input = new FieldInput(definition.Id, definition.Name, definition.Type, definition.Multiple, typeField.Required, definition.EnumId, []);
            var values = await FieldValuesOf(w, project, input);
            if (values.Count > 0)
                argv.AddRange(FieldArguments(input.Id, values));
        }
        return argv;
    }

    private static string Handle(IReadOnlyList<TaskSeriesNumber> numbers, Guid id, IReadOnlyDictionary<Guid, string> prefixes)
    {
        var refs = SeriesCommands.References(numbers, prefixes);
        return refs.Length == 0 ? ShortId.Of(id) : string.Join(",", refs);
    }

    private static async Task<List<string>> ChangeTask(Wizard w, Guid project)
    {
        var chosen = w.Select("Task", await Tasks(w, project));
        var task = await w.Session.Get<ITaskStorage>().GetById(project, ParseId(chosen.Key, "task"), w.Ct)
                   ?? throw new CliException($"No task '{chosen.Key}'");
        var what = w.Menu("What to change",
        [
            new Item("title", "Title", task.Title),
            new Item("description", "Description", FirstLine(task.Description)),
            new Item("type", "Task type"),
            new Item("status", "Status"),
            new Item("series", "Series: add to one or remove from one"),
            new Item("parents", "Parents: add or remove"),
            new Item("fields", "Value of a field"),
            new Item("links", "Links: link to a task or unlink")
        ]);
        var argv = new List<string> { "task", "update", chosen.Key };
        switch (what.Key)
        {
            case "title":
                argv.AddRange(["--title", w.Text("New title", task.Title, Check.Required)]);
                break;
            case "description":
                argv.AddRange(["-d", w.Editor("Description", task.Description ?? "", "An empty text clears the description")]);
                break;
            case "type":
            {
                var newType = w.Select("New task type", await TaskTypes(w, project));
                var taskType = await TypeOf(w, project, ParseId(newType.Key, "task type"));
                var statuses = await SetStatuses(w, project, taskType.StatusSetId);
                statuses.Insert(0, new Item("keep", "(keep the current status)"));
                var status = w.Menu("Status in the new type", statuses);
                argv.AddRange(["--type", newType.Key]);
                if (status.Key != "keep")
                    argv.AddRange(["--status", status.Key]);
                break;
            }
            case "status":
            {
                var taskType = await TypeOf(w, project, task.TypeId);
                argv.AddRange(["--status", w.Select("New status", await SetStatuses(w, project, taskType.StatusSetId)).Key]);
                break;
            }
            case "series":
            {
                var all = await SeriesItems(w, project);
                var inside = all.Where(s => task.SeriesNumbers.Any(n => D(n.SeriesId) == s.Key)).ToList();
                var outside = all.Where(s => task.SeriesNumbers.All(n => D(n.SeriesId) != s.Key)).ToList();
                var choices = new List<Item>();
                if (outside.Count > 0)
                    choices.Add(new Item("add", "Add to a series", "the task gets the next number"));
                if (inside.Count > 0)
                    choices.Add(new Item("remove", "Remove from a series", "the number becomes free"));
                if (choices.Count == 0)
                    throw new CliException("The project has no series");
                var choice = w.Menu("Series", choices);
                if (choice.Key == "add")
                    return ["series", "add-task", w.Select("Series to add the task to", outside).Key, chosen.Key];
                var series = w.Select("Series to remove the task from", inside);
                var number = task.SeriesNumbers.FirstOrDefault(n => D(n.SeriesId) == series.Key)?.Number
                             ?? throw new CliException("The task is not in that series");
                return ["series", "remove-task", series.Key, number.ToString(System.Globalization.CultureInfo.InvariantCulture)];
            }
            case "parents":
            {
                var links = await w.Session.Get<TaskLinkService>().GetLinks(project, task.Id, w.Ct) ?? [];
                var hierarchical = await HierarchicalTypes(w, project);
                var prefixes = await SeriesCommands.Prefixes(w.Session, project);
                var parents = links
                    .Where(l => l.Direction == LinkDirection.Inward && hierarchical.Any(t => t.Id == l.TypeId))
                    .Select(l => new Item($"{D(l.TypeId)}|{D(l.Task.Id)}", $"{Handle(l.Task.SeriesNumbers, l.Task.Id, prefixes)}  {l.Task.Title}", l.TypeName))
                    .ToList();
                var choices = new List<Item> { new("add", "Add a parent", "the task becomes a child of it") };
                if (parents.Count > 0)
                    choices.Add(new Item("remove", "Remove a parent", $"{parents.Count} now"));
                var choice = w.Menu("Parents", choices);
                if (choice.Key == "add")
                {
                    var parent = w.Select("Parent task", await Others(w, project, chosen.Key));
                    argv.AddRange(["--add-parent", parent.Key]);
                    argv.AddRange(await ParentTypeArguments(w, project));
                }
                else
                {
                    var parent = w.Select("Parent to remove", parents);
                    var split = parent.Key.Split('|', 2);
                    if (split.Length != 2)
                        throw new CliException("Bad parent key");
                    argv.AddRange(["--remove-parent", split[1], "--parent-type", split[0]]);
                }
                return argv;
            }
            case "fields":
            {
                var views = await w.Session.Get<TaskService>().GetFields(project, task, w.Ct);
                if (views.Length == 0)
                    throw new CliException($"Task '{task.Title}' has no fields");
                var items = views.Select(v => new Item(D(v.FieldId), v.Name,
                    $"{FieldCommands.TypeText(v.Type, v.Multiple, null)}: {(v.Texts.Count == 0 ? "-" : string.Join(", ", v.Texts))}")).ToList();
                var field = w.Select("Field", items);
                var view = views.FirstOrDefault(v => D(v.FieldId) == field.Key) ?? throw new CliException("No such field");
                var input = new FieldInput(view.FieldId, view.Name, view.Type, view.Multiple, view.Required, view.EnumId,
                    view.Type == FieldType.Enum ? [.. view.Values] : [.. view.Texts]);
                argv.AddRange(FieldArguments(input.Id, await FieldValuesOf(w, project, input)));
                break;
            }
            default:
            {
                var links = await w.Session.Get<TaskLinkService>().GetLinks(project, task.Id, w.Ct) ?? [];
                var choices = new List<Item> { new("link", "Link to another task") };
                if (links.Length > 0)
                    choices.Add(new Item("unlink", "Remove a link", $"{links.Length} now"));
                var choice = w.Menu("Links", choices);
                if (choice.Key == "link")
                {
                    var phrases = new List<Item>();
                    foreach (var t in await LinkTypes(w, project))
                    {
                        phrases.Add(new Item($"{D(t.Id)}|out", t.OutwardName, t.Name));
                        if (t.InwardName != t.OutwardName)
                            phrases.Add(new Item($"{D(t.Id)}|in", t.InwardName, t.Name));
                    }
                    var phrase = w.Select("How this task relates to the other one", phrases);
                    var other = w.Select("The other task", await Others(w, project, chosen.Key));
                    return ["task", "link", chosen.Key, phrase.Label, other.Key];
                }
                var prefixes = await SeriesCommands.Prefixes(w.Session, project);
                var items = links.Select((l, index) =>
                    new Item(index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        $"{l.Name} {Handle(l.Task.SeriesNumbers, l.Task.Id, prefixes)}  {l.Task.Title}", l.TypeName)).ToList();
                var link = w.Select("Link to remove", items);
                var at = int.TryParse(link.Key, out var parsed) ? parsed : int.MaxValue;
                var linkView = at < links.Length ? links[at] : throw new CliException("No such link");
                return ["task", "unlink", chosen.Key, linkView.Name, D(linkView.Task.Id)];
            }
        }
        return argv.With(w.ExpectedVersion(chosen));
    }

    private static readonly (string Key, string Detail)[] BuiltInSortKeys =
    [
        ("status", "by the order of statuses"),
        ("type", "by the name of the task type"),
        ("title", "alphabetically"),
        ("created", "by the time of creation"),
        ("updated", "by the time of the last change"),
        ("series", "by series and number")
    ];

    private static SelectSpec SelectOf(string title, List<Item> items) => new(title, items);

    private static List<Item> Comparisons(FieldType type)
    {
        var items = new List<Item>
        {
            new("=", "equals", "Name=value; a multiple field: contains the value"),
            new("!=", "differs", "Name!=value; tasks without a value fit too")
        };
        if (type is FieldType.Int or FieldType.Float or FieldType.Date)
            items.AddRange([new Item(">", "greater than"), new Item(">=", "greater or equal"), new Item("<", "less than"), new Item("<=", "less or equal")]);
        items.AddRange(
        [
            new Item(":set", "has a value", "Name:set"),
            new Item(":unset", "has no value", "Name:unset"),
            new Item(":attached", "is connected to the task", "Name:attached"),
            new Item(":detached", "is not connected to the task", "Name:detached")
        ]);
        return items;
    }

    private static async Task<StepSpec> ValueOf(Wizard w, Guid project, FieldDefinition field)
    {
        var title = $"Value of {field.Name}";
        switch (field.Type)
        {
            case FieldType.Enum:
            {
                var enumId = field.EnumId ?? throw new CliException($"Field '{field.Name}' has no enum");
                var e = await w.Session.Get<IFieldEnumStorage>().GetById(project, enumId, w.Ct) ?? throw new CliException($"No enum '{D(enumId)}'");
                return SelectOf(title, e.Values.Select(v => new Item(v.Name, v.Name)).ToList());
            }
            case FieldType.Bool:
                return SelectOf(title, [new Item("true", "Yes (true)"), new Item("false", "No (false)")]);
            default:
            {
                var (check, hint) = field.Type switch
                {
                    FieldType.Int => (Check.Int, "A whole number"),
                    FieldType.Float => (Check.Float, "A number, e.g. 1.5"),
                    FieldType.Date => (Check.Date, "A date as yyyy-MM-dd"),
                    _ => (Check.Required, "Text, compared as a whole")
                };
                return new TextSpec(title, "", hint, check, false);
            }
        }
    }

    private static async Task<Chain> ConditionChain(Wizard w, Guid project, FieldDefinition[] definitions)
    {
        var enums = await FieldCommands.EnumNames(w.Session, project);
        var chain = new Chain(SelectOf("Field", definitions.Select(f => new Item(f.Name, f.Name, FieldCommands.TypeText(f, enums))).ToList()));
        foreach (var field in definitions)
        {
            var comparison = new Chain(SelectOf($"Comparison for {field.Name}", Comparisons(field.Type)));
            var value = await ValueOf(w, project, field);
            foreach (var op in Comparisons(field.Type).Where(op => !op.Key.StartsWith(':')))
                comparison.Next(op.Key, new Chain(value));
            chain.Next(field.Name, comparison);
        }
        return chain;
    }

    private static Chain SortChain(FieldDefinition[] definitions)
    {
        var keys = BuiltInSortKeys.Select(x => new Item(x.Key, x.Key, x.Detail)).ToList();
        keys.AddRange(definitions.Select(f => new Item(f.Name, f.Name, $"field, {TypeName(f.Type)}")));
        var chain = new Chain(SelectOf("Sort key", [.. keys]));
        foreach (var key in keys)
            chain.Next(key.Key, new Chain(SelectOf($"Direction for {key.Label}", [new Item("", "Ascending"), new Item("-", "Descending")])).Prefix());
        return chain;
    }

    private static List<string> ListOrCard(Wizard w, EntityKind kind, List<Item> items, string group)
    {
        var what = w.Menu($"{kind.Label()}: what to show", [new Item("list", "List", "all of them"), new Item("card", "Card of one")]);
        if (what.Key == "list")
            return [group, "list", "--all"];
        return [group, "get", w.Select(kind.Label(), items).Key];
    }

    private static async Task<List<string>> TasksView(Wizard w, Guid project)
    {
        var what = w.Menu("Tasks: what to show",
        [
            new Item("list", "List with filters", "tree by parents, or flat"),
            new Item("card", "Card of a task", "filters, then one task: fields, links, description"),
            new Item("links", "Links of a task", "filters, then one task")
        ]);
        switch (what.Key)
        {
            case "card":
                return ["task", "get", (await TaskToShow(w, project)).Key];
            case "links":
                return ["task", "links", (await TaskToShow(w, project)).Key];
        }

        var settings = await TaskSettings(w, project, "List settings", "Show the list", true);
        var argv = new List<string> { "task", "list", "--all" };
        var sort = new List<string>();
        foreach (var setting in settings)
        {
            foreach (var value in setting.Values)
            {
                switch (setting.Key)
                {
                    case "type" or "status" or "series" or "field":
                        argv.AddRange([$"--{setting.Key}", value]);
                        break;
                    case "sort":
                        sort.Add(value);
                        break;
                    case "shape" when value == "flat":
                        argv.Add("--flat");
                        break;
                }
            }
        }
        if (sort.Count > 0)
            argv.AddRange(["--sort", string.Join(",", sort)]);
        return argv;
    }

    public static async Task<List<FormValue>> TaskSettings(Wizard w, Guid project, string title, string submit, bool forList)
    {
        var definitions = await w.Session.Get<IFieldStorage>().GetAll(project, w.Ct);
        var sections = new List<Section>
        {
            new("type", "Task types", "any", new MultiSection(await TaskTypes(w, project))),
            new("status", "Statuses", "any", new MultiSection(await Statuses(w, project))),
            new("series", "Series", "any", new MultiSection(await SeriesItems(w, project))),
            new("field", "Field conditions", "none", new ListSection(await ConditionChain(w, project, definitions), "Add a condition")),
            new("sort", "Sort", forList ? "creation order" : "newest first", new ListSection(SortChain(definitions), "Add a key"))
        };
        if (!forList)
            sections.Add(new Section("columns", "Extra columns", "none",
                new MultiSection(definitions.Select(f => new Item(f.Name, f.Name, TypeName(f.Type))).ToList())));
        sections.Add(new Section("shape", "Shape", "tree", new SingleSection(
        [
            new Item("tree", "Tree", "children under their parents"),
            new Item("flat", "Flat", "in the order of the sort")
        ])).WithValues(["tree"]));
        return w.Form(title, sections, submit);
    }

    public static async Task<List<Item>> FilteredTasks(Wizard w, Guid project, List<FormValue> settings)
    {
        Guid[]? Ids(string key)
        {
            var values = settings.Where(s => s.Key == key).SelectMany(s => s.Values).Select(v => Guid.TryParse(v, out var id) ? id : (Guid?)null)
                .OfType<Guid>().ToArray();
            return values.Length > 0 ? values : null;
        }

        List<string> Strings(string key) => settings.Where(s => s.Key == key).SelectMany(s => s.Values).ToList();

        var filter = new TaskFilter { TypeIds = Ids("type"), StatusIds = Ids("status"), SeriesIds = Ids("series") };
        var conditions = Strings("field");
        var sortKeys = Strings("sort");
        var sort = sortKeys.Count == 0 ? "-created" : string.Join(",", sortKeys);
        var names = await TaskNames.Load(w, project);
        var columns = Strings("columns");
        IEnumerable<string>? fieldFilters = conditions.Count > 0 ? conditions : null;
        var tasks = w.Session.Get<TaskService>();
        if (Strings("shape").Contains("flat"))
        {
            var found = new List<TaskItem>();
            while (true)
            {
                var page = await tasks.List(project, filter, fieldFilters, new Page(found.Count, Page.MaxLimit), 0, w.Ct, sort);
                found.AddRange(page.Data);
                if (page.Data.Length == 0 || found.Count >= page.TotalCount)
                    return names.Items(found.Select(x => new TaskRow(x)).ToList(), columns);
            }
        }

        var rows = new List<TaskRow>();
        var top = 0;
        while (true)
        {
            var page = await tasks.ListTree(project, filter, fieldFilters, new Page(top, Page.MaxLimit), 0, w.Ct, sort);
            rows.AddRange(page.Data.Select(x => new TaskRow(x, x.Depth, x.Repeated)));
            top += Page.MaxLimit;
            if (top >= page.TopLevelCount)
                break;
        }
        return names.Items(rows, columns);
    }

    private static async Task<Item> TaskToShow(Wizard w, Guid project)
    {
        var settings = await TaskSettings(w, project, "Task filters", "Choose a task", false);
        return w.Select("Task", await FilteredTasks(w, project, settings));
    }

    private static async Task<List<string>> BoardsView(Wizard w, Guid project)
    {
        var what = w.Menu("Boards: what to show",
        [
            new Item("show", "Whole board", "every column with its tasks"),
            new Item("column", "One column of a board"),
            new Item("card", "Board card", "columns and their statuses"),
            new Item("list", "List of boards")
        ]);
        if (what.Key == "list")
            return ["board", "list", "--all"];
        var board = w.Select("Board", await Boards(w, project));
        switch (what.Key)
        {
            case "show":
                return ["board", "show", board.Key, "--all"];
            case "card":
                return ["board", "get", board.Key];
            default:
            {
                var found = await w.Session.Get<IBoardStorage>().GetById(project, ParseId(board.Key, "board"), w.Ct)
                            ?? throw new CliException($"No board '{board.Key}'");
                var column = w.Select("Column", found.Columns.Select(c => new Item(D(c.Id), c.Name, $"{c.StatusIds.Count()} statuses")).ToList());
                return ["board", "tasks", board.Key, column.Key, "--all"];
            }
        }
    }

    private static async Task<List<string>> View(Wizard w, EntityKind kind)
    {
        switch (kind)
        {
            case EntityKind.Project:
                return ["project", "list", "--all"];
            case EntityKind.User:
                return ["user", "list", "--all"];
            case EntityKind.Agent:
                return ["agent", "list", "--all"];
        }
        var project = await w.ProjectId();
        return kind switch
        {
            EntityKind.Task => await TasksView(w, project),
            EntityKind.Board => await BoardsView(w, project),
            EntityKind.Status => ListOrCard(w, kind, await Statuses(w, project), "status"),
            EntityKind.StatusSet => ListOrCard(w, kind, await StatusSets(w, project), "status-set"),
            EntityKind.TaskType => ListOrCard(w, kind, await TaskTypes(w, project), "task-type"),
            EntityKind.Series => ListOrCard(w, kind, await SeriesItems(w, project), "series"),
            EntityKind.Field => ListOrCard(w, kind, await Fields(w, project), "field"),
            EntityKind.Enum => ListOrCard(w, kind, await Enums(w, project), "enum"),
            EntityKind.LinkType => ListOrCard(w, kind, await LinkTypeItems(w, project), "link-type"),
            _ => throw new CliException("Nothing to show")
        };
    }
}
