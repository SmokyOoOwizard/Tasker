using Tasker.Cli.Commands;
using Tasker.Core;
using Tasker.Core.Boards;
using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Core.Links;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;
using Tasker.Core.Users;

namespace Tasker.Cli.Interactive;

internal sealed record TaskRow(TaskItem Task, int Depth = 0, bool Repeated = false);

internal static partial class Flows
{
    public static Task<List<string>> Run(Wizard w, FlowAction action, EntityKind kind)
    {
        if (action == FlowAction.View)
            return View(w, kind);
        return kind switch
        {
            EntityKind.Project => Project(w, action),
            EntityKind.Task => TaskFlow(w, action),
            EntityKind.Status => Status(w, action),
            EntityKind.StatusSet => StatusSet(w, action),
            EntityKind.TaskType => TaskType(w, action),
            EntityKind.Series => Series(w, action),
            EntityKind.Field => Field(w, action),
            EntityKind.Enum => Enum(w, action),
            EntityKind.Board => Board(w, action),
            EntityKind.LinkType => LinkType(w, action),
            EntityKind.User => User(w, action, false),
            _ => User(w, action, true)
        };
    }

    private static string D(Guid id) => id.ToString("D");

    private static string TypeName(FieldType type) => type.ToString().ToLowerInvariant();

    private static CliException NothingToDo() => new("Nothing to do");

    private static string FirstLine(string? text) => (text ?? "").Split('\n')[0].TrimEnd('\r');

    private static Guid ParseId(string key, string what) => Guid.TryParse(key, out var id) ? id : throw new CliException($"Bad {what} id");

    private static List<string> With(this List<string> argv, IEnumerable<string> more)
    {
        argv.AddRange(more);
        return argv;
    }

    public static async Task<List<Item>> Statuses(Wizard w, Guid project) =>
        (await w.Session.Get<IStatusStorage>().GetAll(project, w.Ct)).Select(x => new Item(D(x.Id), x.Name, x.Color, x.Version)).ToList();

    public static async Task<List<Item>> StatusSets(Wizard w, Guid project) =>
        (await w.Session.Get<IStatusSetStorage>().GetAll(project, w.Ct))
        .Select(x => new Item(D(x.Id), x.Name, $"{x.StatusIds.Count()} statuses", x.Version)).ToList();

    public static async Task<List<Item>> TaskTypes(Wizard w, Guid project) =>
        (await w.Session.Get<ITaskTypeStorage>().GetAll(project, w.Ct)).Select(x => new Item(D(x.Id), x.Name, "", x.Version)).ToList();

    public static async Task<List<Item>> Fields(Wizard w, Guid project)
    {
        var enums = await FieldCommands.EnumNames(w.Session, project);
        return (await w.Session.Get<IFieldStorage>().GetAll(project, w.Ct))
            .Select(x => new Item(D(x.Id), x.Name, FieldCommands.TypeText(x, enums), x.Version)).ToList();
    }

    public static async Task<List<Item>> Enums(Wizard w, Guid project) =>
        (await w.Session.Get<IFieldEnumStorage>().GetAll(project, w.Ct))
        .Select(x => new Item(D(x.Id), x.Name, string.Join(", ", x.Values.Select(v => v.Name)), x.Version)).ToList();

    public static async Task<List<Item>> Boards(Wizard w, Guid project) =>
        (await w.Session.Get<IBoardStorage>().GetAll(project, w.Ct))
        .Select(x => new Item(D(x.Id), x.Name, $"{x.Columns.Count()} columns", x.Version)).ToList();

    public static async Task<LinkType[]> LinkTypes(Wizard w, Guid project) => await w.Session.Get<LinkTypeService>().GetAll(project, w.Ct);

    public static async Task<List<Item>> LinkTypeItems(Wizard w, Guid project) =>
        (await LinkTypes(w, project)).Select(x => new Item(D(x.Id), x.Name, $"{x.OutwardName} / {x.InwardName}", x.Version)).ToList();

    public static async Task<List<Item>> SeriesItems(Wizard w, Guid project) =>
        (await w.Session.Get<ISeriesStorage>().GetAll(project, w.Ct)).Select(x => new Item(D(x.Id), x.Prefix, x.Name, x.Version)).ToList();

    public static async Task<List<Item>> Tasks(Wizard w, Guid project)
    {
        var names = await TaskNames.Load(w, project);
        var items = (await w.Session.Get<ITaskStorage>().GetAll(project, new TaskFilter(), w.Ct)).OrderByDescending(t => t.CreatedAt).ToList();
        return names.Items(items.Select(x => new TaskRow(x)).ToList(), []);
    }

    public static void ConfirmDelete(Wizard w, string what, string name) =>
        w.Confirm($"Delete {what}", $"Delete {what} '{name}'? This cannot be undone.");

    public static List<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    internal sealed class TaskNames
    {
        private Dictionary<Guid, string> _prefixes = [];
        private Dictionary<Guid, string> _statuses = [];
        private Dictionary<Guid, string> _types = [];
        private FieldDefinition[] _definitions = [];
        private Dictionary<Guid, FieldEnum> _enums = [];

        public static async Task<TaskNames> Load(Wizard w, Guid project) => new()
        {
            _prefixes = await SeriesCommands.Prefixes(w.Session, project),
            _statuses = (await w.Session.Get<IStatusStorage>().GetAll(project, w.Ct)).ToDictionary(s => s.Id, s => s.Name),
            _types = (await w.Session.Get<ITaskTypeStorage>().GetAll(project, w.Ct)).ToDictionary(t => t.Id, t => t.Name),
            _definitions = await w.Session.Get<IFieldStorage>().GetAll(project, w.Ct),
            _enums = (await w.Session.Get<IFieldEnumStorage>().GetAll(project, w.Ct)).ToDictionary(e => e.Id)
        };

        private string FieldText(TaskItem task, string name)
        {
            if (_definitions.FirstOrDefault(d => d.Name == name) is not { } definition)
                return "-";
            if (task.Fields.FirstOrDefault(f => f.FieldId == definition.Id) is not { } field)
                return "-";
            var enumeration = definition.EnumId is { } enumId ? _enums.GetValueOrDefault(enumId) : null;
            var texts = FieldValues.Texts(definition.Type, enumeration, field.Values);
            return texts.Length == 0 ? "-" : string.Join(", ", texts);
        }

        public List<Item> Items(List<TaskRow> tasks, List<string> columns)
        {
            var rows = tasks.Select(row =>
            {
                var task = row.Task;
                var refs = SeriesCommands.References(task.SeriesNumbers, _prefixes);
                var handle = refs.Length == 0 ? ShortId.Of(task.Id) : string.Join(",", refs);
                var cells = new List<string>
                {
                    new string(' ', 4 * row.Depth) + handle + (row.Repeated ? " (+)" : ""),
                    _statuses.GetValueOrDefault(task.StatusId) ?? "",
                    _types.GetValueOrDefault(task.TypeId) ?? ""
                };
                cells.AddRange(columns.Select(name => FieldText(task, name)));
                return cells;
            }).ToList();
            var widths = Enumerable.Range(0, 3 + columns.Count).Select(c => rows.Count == 0 ? 0 : rows.Max(r => Table.Width(r[c]))).ToList();
            return tasks.Zip(rows).Select(x =>
            {
                var label = string.Concat(x.Second.Select((cell, at) => cell + new string(' ', widths[at] - Table.Width(cell) + 2))) + x.First.Task.Title;
                return new Item(D(x.First.Task.Id), label, "", x.First.Task.Version);
            }).ToList();
        }
    }

    private static async Task<List<string>> Project(Wizard w, FlowAction action)
    {
        async Task<List<Item>> Projects() =>
            (await w.Session.AllProjects()).Select(p => new Item(D(p.Id), p.Name, "", p.Version)).ToList();

        switch (action)
        {
            case FlowAction.Create:
                return ["project", "create", w.Text("Project name", "", Check.Required)];
            case FlowAction.Change:
            {
                var project = w.Select("Project", await Projects());
                var name = w.Text("New name", project.Label, Check.Required);
                return new List<string> { "project", "update", project.Key, "--name", name }.With(w.ExpectedVersion(project));
            }
            case FlowAction.View:
                throw NothingToDo();
            default:
            {
                var project = w.Select("Project", await Projects());
                var id = Guid.TryParse(project.Key, out var parsed) ? parsed : throw new CliException("Bad project id");
                var stats = await w.Session.Get<ProjectService>().GetStats(id, w.Ct) ?? throw new CliException($"No project '{project.Key}'");
                var summary = ProjectCommands.Summary(project.Label, stats);
                w.Confirm("Delete project", $"{summary}\n\nDelete project '{project.Label}'?");
                return new List<string> { "project", "delete", project.Key, "--yes" }.With(w.ExpectedVersion(project));
            }
        }
    }

    private static async Task<List<string>> User(Wizard w, FlowAction action, bool agent)
    {
        var group = agent ? "agent" : "user";

        async Task<List<Item>> Users()
        {
            var storage = w.Session.Get<IUserStorage>();
            var filter = new UserFilter { Kind = agent ? UserKind.Agent : UserKind.Human };
            var all = new List<User>();
            while (true)
            {
                var page = await storage.GetRange(filter, new Page(all.Count, Page.MaxLimit), w.Ct);
                all.AddRange(page.Data);
                if (page.Data.Length == 0 || all.Count >= page.TotalCount)
                    break;
            }
            return all.Select(u => new Item(D(u.Id), u.Username, "", u.Version)).ToList();
        }

        switch (action)
        {
            case FlowAction.Create:
                return [group, "create", w.Text($"{(agent ? "Agent" : "User")} name", "", Check.Required)];
            case FlowAction.Change:
            {
                var user = w.Select(group, await Users());
                var name = w.Text("New name", user.Label, Check.Required);
                return new List<string> { group, "update", user.Key, "--name", name }.With(w.ExpectedVersion(user));
            }
            case FlowAction.View:
                throw NothingToDo();
            default:
            {
                var user = w.Select(group, await Users());
                ConfirmDelete(w, group, user.Label);
                return new List<string> { group, "delete", user.Key }.With(w.ExpectedVersion(user));
            }
        }
    }

    private static async Task<List<string>> Series(Wizard w, FlowAction action)
    {
        var project = await w.ProjectId();
        switch (action)
        {
            case FlowAction.Create:
            {
                var name = w.Text("Series name", "", Check.Required);
                var prefix = w.Text("Prefix (Latin letters and digits, e.g. TSK)", "", Check.Prefix);
                return ["series", "create", name, "--prefix", prefix];
            }
            case FlowAction.Change:
            {
                var series = w.Select("Series", await SeriesItems(w, project));
                var what = w.Menu("What to change", [new Item("name", "Name", series.Detail), new Item("prefix", "Prefix", series.Label)]);
                var argv = new List<string> { "series", "update", series.Key };
                if (what.Key == "name")
                    argv.AddRange(["--name", w.Text("New name", series.Detail, Check.Required)]);
                else
                    argv.AddRange(["--prefix", w.Text("New prefix (references written elsewhere stop working)", series.Label, Check.Prefix)]);
                return argv.With(w.ExpectedVersion(series));
            }
            case FlowAction.View:
                throw NothingToDo();
            default:
            {
                var series = w.Select("Series", await SeriesItems(w, project));
                w.Confirm("Delete series",
                    $"Delete series '{series.Detail}' ({series.Label})? Its tasks stay and lose their numbers in it. This cannot be undone.");
                return new List<string> { "series", "delete", series.Key }.With(w.ExpectedVersion(series));
            }
        }
    }

    private static async Task<List<string>> Status(Wizard w, FlowAction action)
    {
        var project = await w.ProjectId();
        switch (action)
        {
            case FlowAction.Create:
            {
                var name = w.Text("Status name", "", Check.Required);
                var color = w.Text("Color (#RRGGBB)", "#808080", Check.Color);
                var description = w.Editor("Description (optional)", "", "What the status means and when to use it");
                var argv = new List<string> { "status", "create", name, "--color", color };
                if (description.Length > 0)
                    argv.AddRange(["-d", description]);
                return argv;
            }
            case FlowAction.Change:
            {
                var chosen = w.Select("Status", await Statuses(w, project));
                var status = await w.Session.Get<IStatusStorage>().GetById(project, ParseId(chosen.Key, "status"), w.Ct)
                             ?? throw new CliException($"No status '{chosen.Key}'");
                var what = w.Menu("What to change",
                [
                    new Item("name", "Name", status.Name),
                    new Item("color", "Color", status.Color),
                    new Item("description", "Description", FirstLine(status.Description))
                ]);
                var argv = new List<string> { "status", "update", chosen.Key };
                switch (what.Key)
                {
                    case "name":
                        argv.AddRange(["--name", w.Text("New name", status.Name, Check.Required)]);
                        break;
                    case "color":
                        argv.AddRange(["--color", w.Text("New color (#RRGGBB)", status.Color, Check.Color)]);
                        break;
                    default:
                        argv.AddRange(["-d", w.Editor("Description", status.Description ?? "", "An empty text clears the description")]);
                        break;
                }
                return argv.With(w.ExpectedVersion(chosen));
            }
            case FlowAction.View:
                throw NothingToDo();
            default:
            {
                var status = w.Select("Status", await Statuses(w, project));
                ConfirmDelete(w, "status", status.Label);
                return new List<string> { "status", "delete", status.Key }.With(w.ExpectedVersion(status));
            }
        }
    }

    private static async Task<List<string>> StatusSet(Wizard w, FlowAction action)
    {
        var project = await w.ProjectId();
        switch (action)
        {
            case FlowAction.Create:
            {
                var name = w.Text("Status set name", "", Check.Required);
                var statuses = w.Multi("Statuses of the set, in order of marking", await Statuses(w, project), false);
                var argv = new List<string> { "status-set", "create", name };
                foreach (var status in statuses)
                    argv.AddRange(["--status", status.Key]);
                return argv;
            }
            case FlowAction.Change:
            {
                var chosen = w.Select("Status set", await StatusSets(w, project));
                var set = await w.Session.Get<IStatusSetStorage>().GetById(project, ParseId(chosen.Key, "status set"), w.Ct)
                          ?? throw new CliException($"No status set '{chosen.Key}'");
                var what = w.Menu("What to change",
                    [new Item("name", "Name", set.Name), new Item("statuses", "Statuses", $"{set.StatusIds.Count()} now")]);
                var argv = new List<string> { "status-set", "update", chosen.Key };
                if (what.Key == "name")
                    argv.AddRange(["--name", w.Text("New name", set.Name, Check.Required)]);
                else
                    foreach (var status in w.Multi("New statuses of the set, in order of marking", await Statuses(w, project), false))
                        argv.AddRange(["--status", status.Key]);
                return argv.With(w.ExpectedVersion(chosen));
            }
            case FlowAction.View:
                throw NothingToDo();
            default:
            {
                var set = w.Select("Status set", await StatusSets(w, project));
                ConfirmDelete(w, "status set", set.Label);
                return new List<string> { "status-set", "delete", set.Key }.With(w.ExpectedVersion(set));
            }
        }
    }

    private static string FieldArgument(Wizard w, Item field) =>
        w.YesNo($"Must every task of the type have a value of '{field.Label}'") ? $"{field.Key}:required" : field.Key;

    private static async Task<List<string>> TaskType(Wizard w, FlowAction action)
    {
        var project = await w.ProjectId();
        switch (action)
        {
            case FlowAction.Create:
            {
                var name = w.Text("Task type name", "", Check.Required);
                var set = w.Select("Status set of the type", await StatusSets(w, project));
                var description = w.Editor("Description (optional)", "", "What the task type means and when to use it");
                var fields = w.Multi("Fields of the catalog to attach (optional)", await Fields(w, project), true);
                var argv = new List<string> { "task-type", "create", name, "--status-set", set.Key };
                if (description.Length > 0)
                    argv.AddRange(["-d", description]);
                foreach (var field in fields)
                    argv.AddRange(["--field", FieldArgument(w, field)]);
                return argv;
            }
            case FlowAction.Change:
            {
                var chosen = w.Select("Task type", await TaskTypes(w, project));
                var taskType = await w.Session.Get<ITaskTypeStorage>().GetById(project, ParseId(chosen.Key, "task type"), w.Ct)
                               ?? throw new CliException($"No task type '{chosen.Key}'");
                var catalog = await Fields(w, project);
                var attached = taskType.Fields
                    .Select(f => catalog.FirstOrDefault(c => c.Key == D(f.FieldId)))
                    .OfType<Item>()
                    .Select(c => taskType.Fields.Any(f => D(f.FieldId) == c.Key && f.Required) ? c.WithDetail("required") : c)
                    .ToList();
                var what = w.Menu("What to change",
                [
                    new Item("name", "Name", taskType.Name),
                    new Item("description", "Description", FirstLine(taskType.Description)),
                    new Item("status-set", "Status set"),
                    new Item("add-field", "Attach a field, or change whether it is required"),
                    new Item("remove-field", "Detach a field", $"{attached.Count} attached")
                ]);
                var argv = new List<string> { "task-type", "update", chosen.Key };
                switch (what.Key)
                {
                    case "name":
                        argv.AddRange(["--name", w.Text("New name", taskType.Name, Check.Required)]);
                        break;
                    case "description":
                        argv.AddRange(["-d", w.Editor("Description", taskType.Description ?? "", "An empty text clears the description")]);
                        break;
                    case "status-set":
                        argv.AddRange(["--status-set",
                            w.Select("New status set (its statuses must cover the tasks of the type)", await StatusSets(w, project)).Key]);
                        break;
                    case "add-field":
                    {
                        var field = w.Select("Field of the catalog", catalog);
                        argv.AddRange(["--add-field", FieldArgument(w, field)]);
                        break;
                    }
                    default:
                    {
                        var field = w.Select("Field to detach", attached);
                        var values = w.Menu("Values of the field in the tasks of the type",
                        [
                            new Item("drop", "Clear them"),
                            new Item("keep", "Keep them: the field becomes an additional field of those tasks")
                        ]);
                        argv.AddRange(["--remove-field", field.Key, values.Key == "drop" ? "--drop-values" : "--keep-values"]);
                        break;
                    }
                }
                return argv.With(w.ExpectedVersion(chosen));
            }
            case FlowAction.View:
                throw NothingToDo();
            default:
            {
                var taskType = w.Select("Task type", await TaskTypes(w, project));
                ConfirmDelete(w, "task type", taskType.Label);
                return new List<string> { "task-type", "delete", taskType.Key }.With(w.ExpectedVersion(taskType));
            }
        }
    }

    private static bool? BoolChoice(Wizard w, string title, bool withDefault)
    {
        var items = new List<Item> { new("true", "Yes"), new("false", "No") };
        if (withDefault)
            items.Add(new Item("default", "Default"));
        return w.Menu(title, items).Key switch
        {
            "true" => true,
            "false" => false,
            _ => null
        };
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static async Task<List<string>> LinkType(Wizard w, FlowAction action)
    {
        var project = await w.ProjectId();
        switch (action)
        {
            case FlowAction.Create:
            {
                var name = w.Text("Link type name", "", Check.Required);
                var outward = w.Text("Outward name: how the link reads for the task it starts from (e.g. 'depends on')", "", Check.Required);
                var inward = w.OptionalText("Inward name: how it reads for the task it points to (empty: no direction)", "", Check.Any);
                var cycles = BoolChoice(w, "May a chain of links of this type close a cycle", true);
                var hierarchical = BoolChoice(w, "Is it a parent/child type (an epic includes its children)", true);
                var argv = new List<string> { "link-type", "create", name, "--outward", outward };
                if (inward.Length > 0)
                    argv.AddRange(["--inward", inward]);
                if (cycles is { } c)
                    argv.AddRange(["--allow-cycles", Bool(c)]);
                if (hierarchical is { } h)
                    argv.AddRange(["--hierarchical", Bool(h)]);
                return argv;
            }
            case FlowAction.Change:
            {
                var chosen = w.Select("Link type", await LinkTypeItems(w, project));
                var linkType = (await LinkTypes(w, project)).FirstOrDefault(t => D(t.Id) == chosen.Key)
                               ?? throw new CliException($"No link type '{chosen.Key}'");
                var what = w.Menu("What to change",
                [
                    new Item("name", "Name", linkType.Name),
                    new Item("outward", "Outward name", linkType.OutwardName),
                    new Item("inward", "Inward name", linkType.InwardName),
                    new Item("allow-cycles", "Cycles allowed", linkType.AllowCycles ? "yes" : "no"),
                    new Item("hierarchical", "Parent/child type", linkType.Hierarchical ? "yes" : "no")
                ]);
                var argv = new List<string> { "link-type", "update", chosen.Key };
                switch (what.Key)
                {
                    case "name":
                        argv.AddRange(["--name", w.Text("New name", linkType.Name, Check.Required)]);
                        break;
                    case "outward":
                        argv.AddRange(["--outward", w.Text("New outward name", linkType.OutwardName, Check.Required)]);
                        break;
                    case "inward":
                        argv.AddRange(["--inward", w.Text("New inward name", linkType.InwardName, Check.Required)]);
                        break;
                    case "allow-cycles":
                        argv.AddRange(["--allow-cycles", Bool(BoolChoice(w, "May a chain of links of this type close a cycle", false) ?? true)]);
                        break;
                    default:
                        argv.AddRange(["--hierarchical", Bool(BoolChoice(w, "Is it a parent/child type", false) ?? false)]);
                        break;
                }
                return argv.With(w.ExpectedVersion(chosen));
            }
            case FlowAction.View:
                throw NothingToDo();
            default:
            {
                var linkType = w.Select("Link type", await LinkTypeItems(w, project));
                ConfirmDelete(w, "link type", linkType.Label);
                return new List<string> { "link-type", "delete", linkType.Key }.With(w.ExpectedVersion(linkType));
            }
        }
    }

    private const string ColumnsHint = "One column per line: Name=status,status (statuses by name)";

    private static List<string> Columns(string text) =>
        Lines(text) is { Count: > 0 } lines ? lines : throw new CliException("A board needs at least one column");

    private static async Task<List<string>> Board(Wizard w, FlowAction action)
    {
        var project = await w.ProjectId();
        switch (action)
        {
            case FlowAction.Create:
            {
                var name = w.Text("Board name", "", Check.Required);
                var sets = w.Multi("Status sets whose tasks are on the board", await StatusSets(w, project), false);
                var text = w.Editor("Columns", "", ColumnsHint);
                var argv = new List<string> { "board", "create", name, "--status-set" };
                argv.AddRange(sets.Select(s => s.Key));
                argv.Add("--column");
                argv.AddRange(Columns(text));
                return argv;
            }
            case FlowAction.Change:
            {
                var chosen = w.Select("Board", await Boards(w, project));
                var board = await w.Session.Get<IBoardStorage>().GetById(project, ParseId(chosen.Key, "board"), w.Ct)
                            ?? throw new CliException($"No board '{chosen.Key}'");
                var what = w.Menu("What to change",
                [
                    new Item("name", "Name", board.Name),
                    new Item("status-set", "Status sets", $"{board.StatusSetIds.Count()} now"),
                    new Item("columns", "Columns", $"{board.Columns.Count()} now")
                ]);
                var argv = new List<string> { "board", "update", chosen.Key };
                switch (what.Key)
                {
                    case "name":
                        argv.AddRange(["--name", w.Text("New name", board.Name, Check.Required)]);
                        break;
                    case "status-set":
                        argv.Add("--status-set");
                        argv.AddRange(w.Multi("New status sets of the board", await StatusSets(w, project), false).Select(s => s.Key));
                        break;
                    default:
                    {
                        var statuses = await w.Session.Get<IStatusStorage>().GetAll(project, w.Ct);
                        var current = string.Join("\n", board.Columns.Select(c =>
                            $"{c.Name}={string.Join(",", c.StatusIds.Select(id => statuses.FirstOrDefault(s => s.Id == id)?.Name ?? D(id)))}"));
                        var text = w.Editor("Columns (a column with the name of an existing one keeps its conditions)", current, ColumnsHint);
                        argv.Add("--column");
                        argv.AddRange(Columns(text));
                        break;
                    }
                }
                return argv.With(w.ExpectedVersion(chosen));
            }
            case FlowAction.View:
                throw NothingToDo();
            default:
            {
                var board = w.Select("Board", await Boards(w, project));
                ConfirmDelete(w, "board", board.Label);
                return new List<string> { "board", "delete", board.Key }.With(w.ExpectedVersion(board));
            }
        }
    }

    private static readonly string[] FieldTypes = ["bool", "date", "enum", "float", "int", "string"];

    private static List<Item> TypeItems() => FieldTypes.Select(t => new Item(t, t)).ToList();

    private static async Task<List<string>> Field(Wizard w, FlowAction action)
    {
        var project = await w.ProjectId();
        switch (action)
        {
            case FlowAction.Create:
            {
                var name = w.Text("Field name (unique in the project)", "", Check.Required);
                var fieldType = w.Menu("Type of the value", TypeItems());
                var multiple = w.YesNo("Does the field hold several values (a list)");
                var argv = new List<string> { "field", "create", name, "--type", fieldType.Key };
                if (multiple)
                    argv.Add("--multiple");
                if (fieldType.Key == "enum")
                    argv.AddRange(["--enum", w.Select("Enum of the values", await Enums(w, project)).Key]);
                return argv;
            }
            case FlowAction.Change:
            {
                var chosen = w.Select("Field", await Fields(w, project));
                var field = await w.Session.Get<IFieldStorage>().GetById(project, ParseId(chosen.Key, "field"), w.Ct)
                            ?? throw new CliException($"No field '{chosen.Key}'");
                var what = w.Menu("What to change",
                [
                    new Item("name", "Name", field.Name),
                    new Item("type", "Type", TypeName(field.Type)),
                    new Item("multiple", "One or several values", field.Multiple ? "several" : "one"),
                    new Item("enum", "Enum of the values")
                ]);
                var argv = new List<string> { "field", "update", chosen.Key };
                switch (what.Key)
                {
                    case "name":
                        argv.AddRange(["--name", w.Text("New name", field.Name, Check.Required)]);
                        break;
                    case "type":
                    {
                        var newType = w.Menu("New type (the values in tasks are converted)", TypeItems());
                        argv.AddRange(["--type", newType.Key]);
                        if (newType.Key == "enum")
                            argv.AddRange(["--enum", w.Select("Enum of the values", await Enums(w, project)).Key]);
                        if (w.YesNo("Clear the values of tasks that do not fit the new type (otherwise such a task stops the change)"))
                            argv.Add("--clear-unconvertible");
                        break;
                    }
                    case "multiple":
                    {
                        var several = w.Menu("From now on the field holds",
                            [new Item("several", "Several values (a list)"), new Item("one", "One value")]);
                        if (several.Key == "several")
                            argv.Add("--multiple");
                        else
                        {
                            argv.Add("--single");
                            var choice = w.Menu("Tasks with several values",
                                [new Item("keep-first", "Keep the first value"), new Item("clear", "Clear the values")]);
                            argv.AddRange(["--several", choice.Key]);
                        }
                        break;
                    }
                    default:
                        if (field.Type != FieldType.Enum)
                            throw new CliException($"Field '{field.Name}' is not an enum field: change its type first");
                        argv.AddRange(["--enum", w.Select("New enum (values of tasks are matched by name)", await Enums(w, project)).Key]);
                        if (w.YesNo("Clear the values of tasks that have no match in the new enum"))
                            argv.Add("--clear-unconvertible");
                        break;
                }
                return argv.With(w.ExpectedVersion(chosen));
            }
            case FlowAction.View:
                throw NothingToDo();
            default:
            {
                var field = w.Select("Field", await Fields(w, project));
                ConfirmDelete(w, "field", field.Label);
                return new List<string> { "field", "delete", field.Key }.With(w.ExpectedVersion(field));
            }
        }
    }

    private static List<Item> ValueItems(FieldEnum e) => e.Values.Select(v => new Item(D(v.Id), v.Name)).ToList();

    private static async Task<List<string>> Enum(Wizard w, FlowAction action)
    {
        var project = await w.ProjectId();
        switch (action)
        {
            case FlowAction.Create:
            {
                var name = w.Text("Enum name (unique in the project)", "", Check.Required);
                var values = Lines(w.Editor("Values, one per line, in display order", "", "At least one value"));
                if (values.Count == 0)
                    throw new CliException("An enum needs at least one value");
                return new List<string> { "enum", "create", name, "--value" }.With(values);
            }
            case FlowAction.Change:
            {
                var chosen = w.Select("Enum", await Enums(w, project));
                var e = await w.Session.Get<IFieldEnumStorage>().GetById(project, ParseId(chosen.Key, "enum"), w.Ct)
                        ?? throw new CliException($"No enum '{chosen.Key}'");
                var what = w.Menu("What to change",
                [
                    new Item("name", "Name", e.Name),
                    new Item("add", "Add a value at the end"),
                    new Item("rename", "Rename a value"),
                    new Item("remove", "Remove a value"),
                    new Item("values", "Replace the whole list of values")
                ]);
                var argv = new List<string> { "enum", "update", chosen.Key };
                switch (what.Key)
                {
                    case "name":
                        argv.AddRange(["--name", w.Text("New name", e.Name, Check.Required)]);
                        break;
                    case "add":
                        argv.AddRange(["--add-value", w.Text("New value", "", Check.Required)]);
                        break;
                    case "rename":
                    {
                        var value = w.Select("Value to rename", ValueItems(e));
                        var name = w.Text("New name of the value", value.Label, Check.Required);
                        argv.AddRange(["--rename-value", $"{value.Label}={name}"]);
                        break;
                    }
                    case "remove":
                    {
                        var value = w.Select("Value to remove", ValueItems(e));
                        var rest = ValueItems(e).Where(v => v.Key != value.Key).ToList();
                        var choices = new List<Item> { new("drop", "Clear it from the tasks that have it selected") };
                        if (rest.Count > 0)
                            choices.Add(new Item("replace", "Give those tasks another value instead"));
                        var choice = w.Menu("Tasks with this value", choices);
                        argv.AddRange(["--remove-value", value.Key]);
                        if (choice.Key == "drop")
                            argv.Add("--drop");
                        else
                            argv.AddRange(["--replace-with", w.Select("Value to give instead", rest).Key]);
                        break;
                    }
                    default:
                    {
                        var current = string.Join("\n", e.Values.Select(v => v.Name));
                        var values = Lines(w.Editor("Values, one per line, in display order", current, "A value with the same name stays the same value"));
                        if (values.Count == 0)
                            throw new CliException("An enum needs at least one value");
                        argv.Add("--values");
                        argv.AddRange(values);
                        break;
                    }
                }
                return argv.With(w.ExpectedVersion(chosen));
            }
            case FlowAction.View:
                throw NothingToDo();
            default:
            {
                var e = w.Select("Enum", await Enums(w, project));
                ConfirmDelete(w, "enum", e.Label);
                return new List<string> { "enum", "delete", e.Key }.With(w.ExpectedVersion(e));
            }
        }
    }
}
