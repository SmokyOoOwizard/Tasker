using System.CommandLine;
using Tasker.Cli.Completion;
using Tasker.Core;
using Tasker.Core.Fields;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;

namespace Tasker.Cli.Commands;

internal static class TaskTypeCommands
{
    private static async Task<TaskType> Find(Context ctx, Guid projectId, string reference) =>
        Refs.FindItem(await ctx.Get<ITaskTypeStorage>().GetAll(projectId, ctx.Ct), reference, x => x.Id, x => x.Name, "task type");

    private const string RequiredMark = "required";

    /// <summary>Поле типа из <c>Имя</c> или <c>Имя:required</c> (обязательное: у каждой задачи типа должно быть значение).</summary>
    private static TaskTypeField ParseField(FieldDefinition[] catalog, string text)
    {
        var required = text.EndsWith(":" + RequiredMark, StringComparison.OrdinalIgnoreCase);
        var reference = required ? text[..^(RequiredMark.Length + 1)] : text;
        return new TaskTypeField(Refs.Find(catalog, reference, x => x.Id, x => x.Name, "field"), required);
    }

    /// <summary>Поля типа в виде для вывода: <c>Priority (enum Priority, required)</c>.</summary>
    private static string FieldLine(TaskTypeField field, FieldDefinition[] catalog, IReadOnlyDictionary<Guid, string> enumNames) =>
        catalog.FirstOrDefault(x => x.Id == field.FieldId) is { } definition
            ? $"{definition.Name} ({FieldCommands.TypeText(definition, enumNames)}{(field.Required ? ", required" : "")})"
            : $"{field.FieldId} (unknown field)";

    /// <summary>Выбор «что делать со значениями убранных полей у задач»: <c>--drop-values</c> или <c>--keep-values</c>; null — не выбрано.</summary>
    private static RemovedFieldValues? Choice(bool drop, bool keep) => (drop, keep) switch
    {
        (true, true) => throw new CliException("Use either --drop-values or --keep-values, not both"),
        (true, _) => RemovedFieldValues.Clear,
        (_, true) => RemovedFieldValues.Keep,
        _ => null
    };

    /// <summary>Новый список полей типа из <c>--field</c> (целиком) или <c>--add-field</c>/<c>--remove-field</c> (по одному); null — поля не меняются.</summary>
    /// <returns>Removing — убирается ли хотя бы одно поле.</returns>
    private static async Task<(TaskTypeField[]? Fields, bool Removing)> EditedFields(
        Context ctx, Guid projectId, TaskType type, string[]? replace, string[]? add, string[]? remove)
    {
        if (replace == null && add == null && remove == null)
            return (null, false);
        if (replace != null && (add != null || remove != null))
            throw new CliException("Use either --field or --add-field/--remove-field, not both");

        var catalog = await ctx.Get<FieldService>().GetAll(projectId, ctx.Ct);
        var list = replace != null ? replace.Select(x => ParseField(catalog, x)).ToList() : type.Fields.ToList();
        foreach (var text in add ?? [])
        {
            var field = ParseField(catalog, text);
            var index = list.FindIndex(x => x.FieldId == field.FieldId);
            if (index >= 0)
                list[index] = field;
            else
                list.Add(field);
        }
        foreach (var reference in remove ?? [])
        {
            var id = Refs.Find(catalog, reference, x => x.Id, x => x.Name, "field");
            if (list.RemoveAll(x => x.FieldId == id) == 0)
                throw new CliException($"Task type '{type.Name}' has no field '{reference}'");
        }

        var removing = type.Fields.Any(x => list.All(y => y.FieldId != x.FieldId));
        return (list.ToArray(), removing);
    }

    public static Command Build(GlobalOptions g)
    {
        var name = Kit.Name("name", "Task type name");
        var statusSet = new Option<string>("--status-set") { Description = "Status set of the type (id or name)", Required = true };
        var reference = Kit.Ref("Task type");
        var newName = new Option<string?>("--name") { Description = "New name" };
        var description = Kit.NewDescription("task type");
        var newDescription = Kit.ChangedDescription();
        var newStatusSet = new Option<string?>("--status-set") { Description = "New status set (id or name); its statuses must cover the statuses of the type's tasks" };
        var expected = Kit.ExpectedVersion();
        var fields = new Option<string[]>("--field") { Description = "Attach a field of the catalog: Name or Name:required (a required field must have a value in every task of the type). Repeat for several; see 'field list'" };
        var newFields = new Option<string[]>("--field") { Description = "Replace the whole list of the type's fields: Name or Name:required (repeat for several; the order is kept)" };
        var addField = new Option<string[]>("--add-field") { Description = "Attach a field, or change whether it is required: Name or Name:required (repeat for several)" };
        var removeField = new Option<string[]>("--remove-field") { Description = "Detach a field (name or id; repeat for several). If tasks have its values, choose --drop-values or --keep-values" };
        var dropValues = new Option<bool>("--drop-values") { Description = "With a detached field: clear its values from the tasks of the type" };
        var keepValues = new Option<bool>("--keep-values") { Description = "With a detached field: keep the values, the field becomes an additional field of the tasks that have values" };

        reference.Suggests(g, Sources.TaskTypes);
        statusSet.Suggests(g, Sources.StatusSets);
        newStatusSet.Suggests(g, Sources.StatusSets);
        fields.Suggests(g, Sources.TypeFields);
        newFields.Suggests(g, Sources.TypeFields);
        addField.Suggests(g, Sources.TypeFields);
        removeField.Suggests(g, Sources.Fields);
        
        return Kit.Group("task-type", "Task types",
            Kit.Leaf(g, "create", "Creates a task type", c =>
            {
                c.Arguments.Add(name);
                c.Options.Add(statusSet);
                c.Options.Add(description);
                c.Options.Add(fields);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var sets = await ctx.Get<IStatusSetStorage>().GetAll(projectId, ctx.Ct);
                var setId = Refs.Find(sets, parse.GetRequiredValue(statusSet), x => x.Id, x => x.Name, "status set");

                TaskTypeField[]? typeFields = null;
                if (parse.GetValue(fields) is { Length: > 0 } texts)
                {
                    var catalog = await ctx.Get<FieldService>().GetAll(projectId, ctx.Ct);
                    typeFields = texts.Select(x => ParseField(catalog, x)).ToArray();
                }

                var type = await ctx.Get<TaskTypeService>().Create(projectId, new CreateTaskType(parse.GetRequiredValue(name), setId, typeFields, parse.GetValue(description)), ctx.Ct);
                ctx.Print(type, $"Created task type '{type.Name}' {type.Id}");
            }),
            Kit.ListWithDescriptions(g, "Lists task types", "task type",
                async (ctx, page, length) => await ctx.Get<TaskTypeService>().List(await ctx.ProjectId(), page, length, ctx.Ct),
                x => Kit.Row(x.Id, x.Name)),
            Kit.Leaf(g, "get", "Shows a task type", c => c.Arguments.Add(reference), async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var type = await Find(ctx, projectId, parse.GetRequiredValue(reference));
                var sets = await ctx.Get<IStatusSetStorage>().GetAll(projectId, ctx.Ct);
                var text = Kit.Fields(("id", type.Id), ("name", type.Name),
                    ("status set", Kit.Named(sets, type.StatusSetId, x => x.Id, x => x.Name)), ("version", type.Version));

                if (type.Fields.Count > 0)
                {
                    var catalog = await ctx.Get<FieldService>().GetAll(projectId, ctx.Ct);
                    var enumNames = await FieldCommands.EnumNames(ctx, projectId);
                    text += "\nfields:\n" + string.Join('\n', type.Fields.Select(x => "  " + FieldLine(x, catalog, enumNames)));
                }
                ctx.Print(type, Kit.WithDescription(text, type.Description));
            }),
            Kit.Leaf(g, "update", "Changes a task type", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(newName);
                c.Options.Add(newDescription);
                c.Options.Add(newStatusSet);
                c.Options.Add(newFields);
                c.Options.Add(addField);
                c.Options.Add(removeField);
                c.Options.Add(dropValues);
                c.Options.Add(keepValues);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                Kit.RequireChange(parse, newName, newDescription, newStatusSet, newFields, addField, removeField);
                var projectId = await ctx.ProjectId();
                var type = await Find(ctx, projectId, parse.GetRequiredValue(reference));

                Guid? setId = null;
                if (parse.GetValue(newStatusSet) is { } setRef)
                    setId = Refs.Find(await ctx.Get<IStatusSetStorage>().GetAll(projectId, ctx.Ct), setRef, x => x.Id, x => x.Name, "status set");

                var choice = Choice(parse.GetValue(dropValues), parse.GetValue(keepValues));
                var (typeFields, removing) = await EditedFields(ctx, projectId, type, Kit.Values(parse, newFields), Kit.Values(parse, addField), Kit.Values(parse, removeField));

                try
                {
                    var result = await ctx.Get<TaskTypeService>().Update(projectId, type.Id,
                        new UpdateTaskType(parse.GetValue(newName), setId, parse.GetValue(expected) ?? type.Version, typeFields, choice, parse.GetValue(newDescription)), ctx.Ct)
                        ?? throw new CliException($"No task type '{type.Id}'");
                    var updated = result.Value;
                    var text = $"Updated task type '{updated.Name}' {updated.Id}";
                    if (removing && choice != null)
                    {
                        var catalog = await ctx.Get<FieldService>().GetAll(projectId, ctx.Ct);
                        var names = string.Join(", ", type.Fields.Where(x => updated.Fields.All(y => y.FieldId != x.FieldId))
                            .Select(x => $"'{catalog.FirstOrDefault(f => f.Id == x.FieldId)?.Name ?? x.FieldId.ToString()}'"));
                        var tasks = Kit.Tasks(result.AffectedTasks);
                        text += "\n" + (choice == RemovedFieldValues.Keep
                            ? $"Kept {names} as an extra field in {tasks}"
                            : $"Removed {names} from {tasks}");
                    }
                    ctx.Print(result, text);
                }
                catch (TaskerConflictException e) when (e.Code == ConflictCode.InUse && removing && choice == null && e.Message.Contains("have values in the field"))
                {
                    throw new TaskerConflictException($"{e.Message}: repeat with --drop-values or --keep-values", e.Code);
                }
            }),
            Kit.Leaf(g, "delete", "Deletes a task type (without tasks)", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var type = await Find(ctx, projectId, parse.GetRequiredValue(reference));
                await ctx.Get<TaskTypeService>().Delete(projectId, type.Id, parse.GetValue(expected) ?? type.Version, ctx.Ct);
                ctx.Print(new { deleted = type.Id }, Kit.Deleted("task type", type.Name, type.Id));
            }));
    }
}
