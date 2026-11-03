using System.CommandLine;
using System.Text.Json.Nodes;
using Tasker.Cli.Completion;
using Tasker.Core;
using Tasker.Core.Links;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;

namespace Tasker.Cli.Commands;

internal static class TaskCommands
{
    /// <summary>
    /// Задачи не ищут по названию: их много и названия повторяются — только по id (полному или короткому — первые 8 и более шестнадцатеричных
    /// символов Guid, как в списках) или ссылке <c>ПРЕФИКС-номер</c>. Короткий id, подходящий нескольким задачам, — ошибка со списком полных id.
    /// </summary>
    /// <returns>Одна задача; у ссылки-дубликата (после слияния веток) — все с этим номером.</returns>
    internal static async Task<TaskItem[]> FindAll(Context ctx, Guid projectId, string reference)
    {
        if (TaskReference.TryParse(reference, allowIdPrefix: true) == null)
            throw new CliException($"Task is given by id (the full one, or the first {ShortId.Length}+ hex characters shown by 'task list') or by a reference like TSK-5, not '{reference}': find it with 'task list'");

        var found = await ctx.Get<TaskService>().Resolve(projectId, reference, ctx.Ct, allowIdPrefix: true);
        return found.Length == 0 ? throw new CliException($"No task '{reference}'") : found;
    }

    /// <summary>Ровно одна задача: несколько с одним номером — ошибка со списком id.</summary>
    internal static async Task<TaskItem> FindOne(Context ctx, Guid projectId, string reference)
    {
        var found = await FindAll(ctx, projectId, reference);
        if (found.Length > 1)
            throw new CliException($"Several tasks are '{reference}' (a duplicate number after a merge), use the id: {string.Join(", ", found.Select(x => x.Id))}");
        return found[0];
    }

    /// <summary>Текст задачи для <c>get</c>: поля, номера в сериях и пометка дубликата.</summary>
    private static async Task<(string Text, TaskFieldView[] Fields, TaskLinkView[] Links)> Describe(Context ctx, Guid projectId, TaskItem task)
    {
        var types = await ctx.Get<ITaskTypeStorage>().GetAll(projectId, ctx.Ct);
        var statuses = await ctx.Get<IStatusStorage>().GetAll(projectId, ctx.Ct);
        var prefixes = await SeriesCommands.Prefixes(ctx, projectId);
        var references = SeriesCommands.References(task, prefixes);

        var text = Kit.Fields(
            ("id", task.Id), ("title", task.Title),
            ("type", Kit.Named(types, task.TypeId, x => x.Id, x => x.Name)),
            ("status", Kit.Named(statuses, task.StatusId, x => x.Id, x => x.Name)),
            ("series", references.Length == 0 ? null : string.Join(", ", references)),
            ("created", task.CreatedAt), ("updated", task.UpdatedAt), ("version", task.Version));

        var storage = ctx.Get<ITaskStorage>();
        foreach (var number in task.SeriesNumbers.Where(x => prefixes.ContainsKey(x.SeriesId)))
        {
            var others = (await storage.FindByNumber(projectId, number.SeriesId, number.Number, ctx.Ct)).Where(x => x.Id != task.Id).ToArray();
            if (others.Length > 0)
                text += $"\nconflict: {prefixes[number.SeriesId]}-{number.Number} is also used by {string.Join(", ", others.Select(x => x.Id))}";
        }

        var fields = await ctx.Get<TaskService>().GetFields(projectId, task, ctx.Ct);
        if (fields.Length > 0)
            text += "\nfields:\n" + string.Join('\n', fields.Select(x => "  " + FieldLine(x)));

        // Те же связи и тот же предел, что в get_task и REST (TaskDetails.LinkViews): текст и --json согласованы.
        var links = await ctx.Get<TaskLinkService>().GetLinks(projectId, task, ctx.Ct);
        if (links.Length > 0)
        {
            var statusNames = statuses.ToDictionary(s => s.Id, s => s.Name);
            var shown = links.Take(TaskLinkService.MaxViewed).Select(x => LinkCells(x, prefixes, statusNames)).ToArray();
            text += "\nlinks:\n" + string.Join('\n', ctx.Table(shown, "  "));
            if (links.Length > TaskLinkService.MaxViewed)
                text += $"\n  ... and {links.Length - TaskLinkService.MaxViewed} more: see 'task links'";
        }

        return (text + (string.IsNullOrEmpty(task.Description) ? "" : "\n\n" + task.Description), fields, links);
    }

    /// <summary>
    /// Поле задачи одной строкой: <c>Priority (enum, required): High</c>. В скобках — тип (<c>[]</c> у полей с несколькими значениями),
    /// обязательность и происхождение (<c>extra</c> — добавлено из каталога, <c>own</c> — собственное поле задачи; у полей типа не пишется).
    /// Нет значения — <c>-</c>.
    /// </summary>
    private static string FieldLine(TaskFieldView field)
    {
        var notes = new List<string> { FieldCommands.TypeText(field.Type, field.Multiple, null) };
        if (field.Required)
            notes.Add("required");
        if (field.Source != TaskFieldSource.Type)
            notes.Add(field.Source.ToString().ToLowerInvariant());
        return $"{field.Name} ({string.Join(", ", notes)}): {(field.Texts.Count == 0 ? "-" : string.Join(", ", field.Texts))}";
    }

    /// <summary>
    /// Задача для <c>--json</c>: как в API, плюс <c>fieldViews</c> — поля с определениями, источником и названиями значений,
    /// и <c>linkViews</c>/<c>linkCount</c> — связи с обеих сторон (не больше <see cref="TaskLinkService.MaxViewed"/>).
    /// </summary>
    private static JsonNode TaskJson(Context ctx, TaskItem task, TaskFieldView[] fields, TaskLinkView[] links)
    {
        var node = ctx.ToNode(task)!.AsObject();
        node["fieldViews"] = ctx.ToNode(fields);
        node["linkViews"] = ctx.ToNode(links.Take(TaskLinkService.MaxViewed).ToArray());
        node["linkCount"] = links.Length;
        return node;
    }

    /// <summary>Пропущенное обязательное поле: к сообщению ядра добавляется, чем его задать.</summary>
    private static async Task<T> WithFieldHint<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (TaskerRequiredFieldsException e)
        {
            throw new CliException($"{e.Message}: set them with --field Name=value" +
                (e.FieldNames.Length == 1 ? $" (e.g. --field \"{e.FieldNames[0]}=...\")" : ""));
        }
    }

    /// <summary>
    /// Ячейки строки задачи в списках (<c>task list</c>, <c>board tasks</c>, <c>board show</c>): ссылка, статус, тип, заголовок;
    /// <see cref="Table"/> выравнивает их по странице и соединяет двумя пробелами: <c>ссылка  статус  тип  заголовок</c>.
    /// Серии, статусы и типы проекта грузятся один раз на весь список, а не по запросу на задачу.
    /// Ссылка серии — ручка задачи; у задачи без серии другой ручки нет, поэтому короткий id (<see cref="ShortId"/>), по которому её находят команды.
    /// Задача с неизвестным типом (удалён в другой ветке) выводится с пустой ячейкой типа: остальные колонки не сдвигаются.
    /// </summary>
    internal static async Task<Func<TaskItem, string[]>> LineFormat(Context ctx, Guid projectId)
    {
        var prefixes = await SeriesCommands.Prefixes(ctx, projectId);
        var statusNames = (await ctx.Get<IStatusStorage>().GetAll(projectId, ctx.Ct)).ToDictionary(x => x.Id, x => x.Name);
        var typeNames = (await ctx.Get<ITaskTypeStorage>().GetAll(projectId, ctx.Ct)).ToDictionary(x => x.Id, x => x.Name);

        return x =>
        {
            var handle = SeriesCommands.References(x, prefixes) is { Length: > 0 } refs ? string.Join(',', refs) : ShortId.Of(x.Id);
            return new[] { handle, statusNames.GetValueOrDefault(x.StatusId, x.StatusId.ToString()), typeNames.GetValueOrDefault(x.TypeId, ""), x.Title };
        };
    }

    /// <summary>Ручка задачи — ссылка серии (<c>TSK-12</c>); у задачи без серии другой ручки нет, поэтому короткий id.</summary>
    private static string Handle(Guid id, IEnumerable<Core.TaskSeries.TaskSeriesNumber> numbers, IReadOnlyDictionary<Guid, string> prefixes) =>
        SeriesCommands.References(numbers, prefixes) is { Length: > 0 } refs ? string.Join(',', refs) : ShortId.Of(id);

    /// <summary>Связь одной строкой (<c>blocks  TSK-7  Бэклог  Название</c>): название стороны, ссылка, статус, заголовок.</summary>
    private static string[] LinkCells(TaskLinkView link, IReadOnlyDictionary<Guid, string> prefixes, IReadOnlyDictionary<Guid, string> statusNames) =>
        [link.Name, Handle(link.Task.Id, link.Task.SeriesNumbers, prefixes), statusNames.GetValueOrDefault(link.Task.StatusId, link.Task.StatusId.ToString()), link.Task.Title];

    /// <summary>
    /// Иерархический тип для <c>--parent</c>, <c>--add-parent</c>, <c>--remove-parent</c>: названный в <c>--parent-type</c> или единственный иерархический тип проекта
    /// («Parent/Child» по умолчанию). Несколько типов без указания — ошибка со списком.
    /// </summary>
    private static async Task<LinkType> ParentType(Context ctx, Guid projectId, string? name)
    {
        var all = (await ctx.Get<LinkTypeService>().GetAll(projectId, ctx.Ct)).Where(x => x.Hierarchical).ToArray();
        if (name != null)
            return Refs.FindItem(all, name, x => x.Id, x => x.Name, "hierarchical link type", listAvailable: true);

        return all.Length switch
        {
            1 => all[0],
            0 => throw new CliException("The project has no hierarchical link type: create one with 'tasker link-type create <name> --outward includes --inward \"is part of\" --hierarchical true'"),
            _ => throw new CliException($"The project has several hierarchical link types ({string.Join(", ", all.Select(x => x.Name))}): choose one with --parent-type")
        };
    }

    /// <summary>Родители из параметров (ссылки на задачи): каждая находится до любых изменений, чтобы ошибка в ссылке ничего не оставила сделанным наполовину.</summary>
    private static async Task<TaskItem[]> FindParents(Context ctx, Guid projectId, string[]? references)
    {
        var found = new List<TaskItem>();
        foreach (var reference in references ?? [])
            found.Add(await FindOne(ctx, projectId, reference));
        return [.. found.DistinctBy(x => x.Id)];
    }

    /// <summary>Связь «родитель —включает→ задача» для каждого из <paramref name="add"/> и снятие её для <paramref name="remove"/> (версия не нужна: связи коммутативны).</summary>
    private static async Task ChangeParents(Context ctx, Guid projectId, LinkType type, TaskItem task, IEnumerable<TaskItem> add, IEnumerable<TaskItem> remove)
    {
        var links = ctx.Get<TaskLinkService>();
        foreach (var parent in add)
            await links.Add(projectId, parent.Id, type.Id, task.Id, null, ctx.Ct);
        foreach (var parent in remove)
            await links.Remove(projectId, parent.Id, type.Id, task.Id, null, ctx.Ct);
    }

    /// <summary>
    /// <c>task link A blocks B</c>: фраза решается в тип и сторону. «is blocked by» — это «B blocks A», поэтому источник — тот, от кого связь исходит.
    /// Без версии: связи коммутативны, версию задачи подставлять не нужно.
    /// </summary>
    private static async Task ChangeLink(
        System.CommandLine.ParseResult parse, Context ctx, Argument<string> reference, Argument<string> phrase, Argument<string> other, bool add)
    {
        var projectId = await ctx.ProjectId();
        var task = await FindOne(ctx, projectId, parse.GetRequiredValue(reference));
        var otherTask = await FindOne(ctx, projectId, parse.GetRequiredValue(other));
        var (type, direction) = await ctx.Get<LinkTypeService>().Resolve(projectId, parse.GetRequiredValue(phrase), ctx.Ct);
        var (source, target) = direction == LinkDirection.Outward ? (task, otherTask) : (otherTask, task);

        var links = ctx.Get<TaskLinkService>();
        if (add)
            await links.Add(projectId, source.Id, type.Id, target.Id, null, ctx.Ct);
        else
            await links.Remove(projectId, source.Id, type.Id, target.Id, null, ctx.Ct);

        var prefixes = await SeriesCommands.Prefixes(ctx, projectId);
        var side = direction == LinkDirection.Outward ? type.OutwardName : type.InwardName;
        ctx.Print(new { type, direction = direction.ToString().ToLowerInvariant(), source = source.Id, target = target.Id, linked = add },
            $"{(add ? "Linked" : "Unlinked")}: {Handle(task.Id, task.SeriesNumbers, prefixes)} {side} {Handle(otherTask.Id, otherTask.SeriesNumbers, prefixes)}");
    }

    public static Command Build(GlobalOptions g)
    {
        var title = Kit.Name("title", "Task title");
        var type = new Option<string>("--type") { Description = "Task type (id or name)", Required = true };
        var status = new Option<string?>("--status") { Description = "Status (id or name). Default: the first status of the type's set" };
        var description = new Option<string?>("--description", "-d") { Description = "Task description" };
        var createSeries = new Option<string[]>("--series") { Description = "Series to put the task into (id or exact prefix); repeat for several series" };
        var createFields = new TaskFieldOptions(update: false);
        var updateFields = new TaskFieldOptions(update: true);

        const string OrHint = "; repeat the option or list several values: a task fits if it matches any of them (different filters must all hold)";
        var filterType = Kit.Many("--type", "Only tasks of this type (id or name)" + OrHint);
        var filterStatus = Kit.Many("--status", "Only tasks with this status (id or name)" + OrHint);
        var filterSeries = Kit.Many("--series", "Only tasks in this series (id or exact prefix)" + OrHint);
        var filterFields = Kit.Many("--field", "Only tasks by a field - a catalog field or an own field of tasks with that name (a catalog field: own fields of the same name and type match too; no catalog field: the type of the own fields, an error if they differ) (repeat or list several: all must hold): \"Name=value\" or \"Name!=value\" (any type; != also takes tasks without a value; a multiple field - contains the value; enum value by name), \"Name>=value\" with > >= < <= (int, float, date; quote them in the shell), Name:set, Name:unset (has a value or not), Name:attached, Name:detached (the field is connected to the task or not)");
        var sort = Kit.Sort();
        var flat = new Option<bool>("--flat")
        {
            Description = "A flat list in the order of the sort, as without hierarchy. By default a task with children (an epic: it has links of a hierarchical type such as Parent/Child) " +
                "is followed by its child tasks indented by four spaces per level; a task with several parents appears under each (the repeats are marked '(+)'), " +
                "'Found N' counts distinct tasks, --offset/--limit count top-level tasks (the ones without a parent in the result) with their subtrees. --json is always flat (parentIds, childCount)"
        };
        var createParents = Kit.Many("--parent", "Make the new task a child of this task (id or reference like TSK-5; repeat or list several: the task gets several parents). Uses the project's hierarchical link type, see --parent-type");
        var addParents = Kit.Many("--add-parent", "Make the task a child of this task too (id or reference; repeat or list several). Uses the project's hierarchical link type, see --parent-type");
        var removeParents = Kit.Many("--remove-parent", "Take the task out of this parent (id or reference; repeat or list several; no such link is not an error)");
        var parentType = new Option<string?>("--parent-type") { Description = "Hierarchical link type for --parent/--add-parent/--remove-parent (id or name); needed only when the project has several" };
        var paging = new Kit.Paging();
        type.Suggests(g, Sources.TaskTypes);
        status.Suggests(g, Sources.TaskStatuses(type));
        createSeries.Suggests(g, Sources.Series);
        filterType.Suggests(g, Sources.TaskTypes);
        filterStatus.Suggests(g, Sources.TaskStatuses(filterType));
        filterSeries.Suggests(g, Sources.Series);
        filterFields.Suggests(g, Sources.FieldValues);
        sort.Suggests(g, Sources.SortKeys);
        createParents.Suggests(g, Sources.TaskReferences());
        addParents.Suggests(g, Sources.TaskReferences());
        removeParents.Suggests(g, Sources.TaskReferences());
        parentType.Suggests(g, Sources.HierarchicalLinkTypes);
        
        var descriptionLength = Kit.DescriptionLength();
        var list = Kit.Leaf(g, "list", "Lists tasks", c =>
        {
            c.Options.Add(filterType);
            c.Options.Add(filterStatus);
            c.Options.Add(filterSeries);
            c.Options.Add(filterFields);
            c.Options.Add(sort);
            c.Options.Add(flat);
            c.Options.Add(descriptionLength);
            paging.AddTo(c);
        }, async (parse, ctx) =>
        {
            var projectId = await ctx.ProjectId();
            Guid[]? typeIds = null, statusIds = null, seriesIds = null;
            if (parse.GetValue(filterType) is { Length: > 0 } typeRefs)
                typeIds = Refs.FindDistinct(await ctx.Get<ITaskTypeStorage>().GetAll(projectId, ctx.Ct), typeRefs, x => x.Id, x => x.Name, "task type");
            if (parse.GetValue(filterStatus) is { Length: > 0 } statusRefs)
                statusIds = Refs.FindDistinct(await ctx.Get<IStatusStorage>().GetAll(projectId, ctx.Ct), statusRefs, x => x.Id, x => x.Name, "status");
            if (parse.GetValue(filterSeries) is { Length: > 0 } seriesRefs)
                seriesIds = await SeriesCommands.FindDistinct(ctx, projectId, seriesRefs);

            var filter = new TaskFilter { TypeIds = typeIds, StatusIds = statusIds, SeriesIds = seriesIds };
            var fieldFilters = parse.GetValue(filterFields);
            var line = await LineFormat(ctx, projectId);
            // --json остаётся плоским списком (parentIds, childCount в записях): клиенты не должны сломаться.
            if (ctx.IsJson || parse.GetValue(flat))
            {
                ctx.Print(await paging.Load(parse, page => ctx.Get<TaskService>().List(projectId, filter, fieldFilters, page, parse.GetValue(descriptionLength), ctx.Ct, parse.GetValue(sort))), line);
                return;
            }

            ctx.PrintTree(await paging.LoadTree(parse, page => ctx.Get<TaskService>().ListTree(projectId, filter, fieldFilters, page, parse.GetValue(descriptionLength), ctx.Ct, parse.GetValue(sort))), line);
        });

        var reference = new Argument<string>("task") { Description = "Task: id or reference like TSK-5" };
        var newTitle = new Option<string?>("--title") { Description = "New title" };
        var newDescription = new Option<string?>("--description", "-d") { Description = "New description; an empty one clears it" };
        var newType = new Option<string?>("--type") { Description = "New task type (id or name)" };
        var newStatus = new Option<string?>("--status") { Description = "New status (id or name); must be in the status set of the (new) type" };
        var expected = Kit.ExpectedVersion();
        var linkPhrase = new Argument<string>("link") { Description = "Link type or its side name: 'blocks', 'is blocked by', 'Duplicate', 'relates to'... (see 'link-type list')" };
        var otherReference = new Argument<string>("other-task") { Description = "The other task: id or reference like TSK-7" };

        createFields.Suggest(g);
        updateFields.Suggest(g);
        reference.Suggests(g, Sources.TaskReferences());
        otherReference.Suggests(g, Sources.TaskReferences());
        linkPhrase.Suggests(g, Sources.LinkPhrases);
        newType.Suggests(g, Sources.TaskTypes);
        newStatus.Suggests(g, Sources.TaskStatuses(newType));

        return Kit.Group("task", "Tasks",
            Kit.Leaf(g, "create", "Creates a task", c =>
            {
                c.Arguments.Add(title);
                c.Options.Add(type);
                c.Options.Add(status);
                c.Options.Add(description);
                c.Options.Add(createSeries);
                c.Options.Add(createParents);
                c.Options.Add(parentType);
                createFields.AddTo(c);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var types = await ctx.Get<ITaskTypeStorage>().GetAll(projectId, ctx.Ct);
                var typeId = Refs.Find(types, parse.GetRequiredValue(type), x => x.Id, x => x.Name, "task type");

                Guid? statusId = null;
                if (parse.GetValue(status) is { } statusRef)
                {
                    var statuses = await ctx.Get<IStatusStorage>().GetAll(projectId, ctx.Ct);
                    statusId = Refs.Find(statuses, statusRef, x => x.Id, x => x.Name, "status");
                }

                Guid[]? seriesIds = null;
                if (parse.GetValue(createSeries) is { Length: > 0 } seriesRefs)
                {
                    seriesIds = new Guid[seriesRefs.Length];
                    for (var i = 0; i < seriesRefs.Length; i++)
                        seriesIds[i] = (await SeriesCommands.Find(ctx, projectId, seriesRefs[i])).Id;
                }

                // Родители находятся до создания: неверная ссылка не оставит задачу без эпика.
                var parents = await FindParents(ctx, projectId, parse.GetValue(createParents));
                var hierarchyType = parents.Length > 0 ? await ParentType(ctx, projectId, parse.GetValue(parentType)) : null;

                var fieldChanges = await createFields.Build(parse, ctx, projectId, null);
                var task = await WithFieldHint(() => ctx.Get<TaskService>().Create(
                    projectId, new CreateTask(parse.GetRequiredValue(title), parse.GetValue(description), typeId, statusId, seriesIds, fieldChanges), ctx.Ct));
                if (hierarchyType != null)
                    await ChangeParents(ctx, projectId, hierarchyType, task, parents, []);
                var references = SeriesCommands.References(task, await SeriesCommands.Prefixes(ctx, projectId));
                ctx.Print(task, $"Created task '{task.Title}' {task.Id}" + (references.Length > 0 ? $" ({string.Join(", ", references)})" : ""));
            }),
            list,
            Kit.Leaf(g, "get", "Shows a task (a reference used by several tasks after a merge shows all of them)", c => c.Arguments.Add(reference), async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                PerfTrace.Mark("get.project");
                var found = await FindAll(ctx, projectId, parse.GetRequiredValue(reference));
                PerfTrace.Mark("get.find");
                var texts = new List<string>();
                var nodes = new JsonArray();
                foreach (var task in found)
                {
                    var (text, fields, links) = await Describe(ctx, projectId, task);
                    texts.Add(text);
                    PerfTrace.Mark("get.describe");
                    // Дерево JSON строится отражением (десятки миллисекунд): в текстовом режиме оно не нужно.
                    if (ctx.IsJson)
                        nodes.Add(TaskJson(ctx, task, fields, links));
                }
                if (ctx.IsJson)
                    ctx.Print(found.Length == 1 ? nodes[0]! : nodes, "");
                else
                    ctx.Print(new object(), string.Join("\n\n----\n\n", texts));
            }),
            Kit.Leaf(g, "update", "Changes a task", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(newTitle);
                c.Options.Add(newDescription);
                c.Options.Add(newType);
                c.Options.Add(newStatus);
                c.Options.Add(addParents);
                c.Options.Add(removeParents);
                c.Options.Add(parentType);
                updateFields.AddTo(c);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                Kit.RequireChange(parse, [newTitle, newDescription, newType, newStatus, addParents, removeParents, .. updateFields.All]);
                var projectId = await ctx.ProjectId();
                var task = await FindOne(ctx, projectId, parse.GetRequiredValue(reference));
                var parentsToAdd = await FindParents(ctx, projectId, parse.GetValue(addParents));
                var parentsToRemove = await FindParents(ctx, projectId, parse.GetValue(removeParents));
                var hierarchyType = parentsToAdd.Length + parentsToRemove.Length > 0 ? await ParentType(ctx, projectId, parse.GetValue(parentType)) : null;
                Option[] taskOptions = [newTitle, newDescription, newType, newStatus, .. updateFields.All];
                var changesTask = taskOptions.Any(x => parse.GetResult(x) is { Implicit: false });
                if (!changesTask)
                {
                    // Только родители: сама задача не меняется (её версия и время остаются), связи лежат у родителей.
                    await ChangeParents(ctx, projectId, hierarchyType!, task, parentsToAdd, parentsToRemove);
                    ctx.Print(task, $"Updated task '{task.Title}' {task.Id}");
                    return;
                }

                Guid? typeId = null, statusId = null;
                if (parse.GetValue(newType) is { } typeRef)
                    typeId = Refs.Find(await ctx.Get<ITaskTypeStorage>().GetAll(projectId, ctx.Ct), typeRef, x => x.Id, x => x.Name, "task type");
                if (parse.GetValue(newStatus) is { } statusRef)
                    statusId = Refs.Find(await ctx.Get<IStatusStorage>().GetAll(projectId, ctx.Ct), statusRef, x => x.Id, x => x.Name, "status");

                var fieldChanges = await updateFields.Build(parse, ctx, projectId, task);
                var updated = await WithFieldHint(() => ctx.Get<TaskService>().Update(projectId, task.Id,
                    new UpdateTask(parse.GetValue(newTitle), parse.GetValue(newDescription), typeId, statusId, parse.GetValue(expected) ?? task.Version, fieldChanges), ctx.Ct))
                    ?? throw new CliException($"No task '{task.Id}'");
                if (hierarchyType != null)
                    await ChangeParents(ctx, projectId, hierarchyType, updated, parentsToAdd, parentsToRemove);
                ctx.Print(updated, $"Updated task '{updated.Title}' {updated.Id}");
            }),
            Kit.Leaf(g, "delete", "Deletes a task", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var task = await FindOne(ctx, projectId, parse.GetRequiredValue(reference));
                await ctx.Get<TaskService>().Delete(projectId, task.Id, parse.GetValue(expected) ?? task.Version, ctx.Ct);
                ctx.Print(new { deleted = task.Id }, Kit.Deleted("task", task.Title, task.Id));
            }),
            Kit.Leaf(g, "link", "Links two tasks: 'task link TSK-5 blocks TSK-7' (or 'TSK-7 is blocked by TSK-5'); types: see 'link-type list'", c =>
            {
                c.Arguments.Add(reference);
                c.Arguments.Add(linkPhrase);
                c.Arguments.Add(otherReference);
            }, (parse, ctx) => ChangeLink(parse, ctx, reference, linkPhrase, otherReference, add: true)),
            Kit.Leaf(g, "unlink", "Removes a link between two tasks (the same arguments as 'task link'; a missing link is not an error)", c =>
            {
                c.Arguments.Add(reference);
                c.Arguments.Add(linkPhrase);
                c.Arguments.Add(otherReference);
            }, (parse, ctx) => ChangeLink(parse, ctx, reference, linkPhrase, otherReference, add: false)),
            Kit.Leaf(g, "links", "Shows the links of a task: the ones it starts and the ones pointing at it", c => c.Arguments.Add(reference), async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var task = await FindOne(ctx, projectId, parse.GetRequiredValue(reference));
                var links = await ctx.Get<TaskLinkService>().GetLinks(projectId, task.Id, ctx.Ct) ?? [];
                var prefixes = await SeriesCommands.Prefixes(ctx, projectId);
                var statusNames = (await ctx.Get<IStatusStorage>().GetAll(projectId, ctx.Ct)).ToDictionary(x => x.Id, x => x.Name);
                ctx.PrintAll(new { links }, links, x => LinkCells(x, prefixes, statusNames));
            }));
    }
}
