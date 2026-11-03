using System.CommandLine;
using Tasker.Cli.Completion;
using Tasker.Core;
using Tasker.Core.Fields;

namespace Tasker.Cli.Commands;

/// <summary>Каталог полей проекта (<c>field</c>) и перечисления (<c>enum</c>).</summary>
internal static class FieldCommands
{
    private static readonly string TypeNames = string.Join(", ", Enum.GetNames<FieldType>().Select(x => x.ToLowerInvariant()));

    internal static async Task<FieldDefinition> FindField(Context ctx, Guid projectId, string reference) =>
        Refs.FindItem(await ctx.Get<FieldService>().GetAll(projectId, ctx.Ct), reference, x => x.Id, x => x.Name, "field");

    internal static async Task<FieldEnum> FindEnum(Context ctx, Guid projectId, string reference) =>
        Refs.FindItem(await ctx.Get<FieldEnumService>().GetAll(projectId, ctx.Ct), reference, x => x.Id, x => x.Name, "enum");

    /// <summary>Тип значения по названию (<c>string</c>, <c>int</c>, <c>float</c>, <c>bool</c>, <c>date</c>, <c>enum</c>, без учёта регистра).</summary>
    internal static FieldType ParseType(string text) =>
        Enum.TryParse<FieldType>(text, ignoreCase: true, out var type) && Enum.IsDefined(type) && !int.TryParse(text, out _)
            ? type
            : throw new CliException($"Unknown field type '{text}': use one of {TypeNames}");

    private static SeveralValues? ParseSeveral(string? text) => text?.ToLowerInvariant() switch
    {
        null => null,
        "keep-first" or "keepfirst" => SeveralValues.KeepFirst,
        "clear" => SeveralValues.Clear,
        _ => throw new CliException($"Unknown --several '{text}': use keep-first or clear")
    };

    /// <summary>Тип поля одной строкой: <c>string</c>, <c>int[]</c> (несколько значений), <c>enum Priority</c>.</summary>
    internal static string TypeText(FieldType type, bool multiple, string? enumName) =>
        $"{type.ToString().ToLowerInvariant()}{(multiple ? "[]" : "")}{(enumName == null ? "" : " " + enumName)}";

    internal static string TypeText(FieldDefinition field, IReadOnlyDictionary<Guid, string> enumNames) =>
        TypeText(field.Type, field.Multiple, field.EnumId is { } id ? enumNames.GetValueOrDefault(id, id.ToString()) : null);

    internal static async Task<Dictionary<Guid, string>> EnumNames(Context ctx, Guid projectId) =>
        (await ctx.Get<FieldEnumService>().GetAll(projectId, ctx.Ct)).ToDictionary(x => x.Id, x => x.Name);

    public static Command Fields(GlobalOptions g)
    {
        var name = Kit.Name("name", "Field name (unique in the project, case-insensitive)");
        var type = new Option<string>("--type") { Description = $"Type of the value: {TypeNames}", Required = true };
        var multiple = new Option<bool>("--multiple") { Description = "The field holds several values (a list)" };
        var enumeration = new Option<string?>("--enum") { Description = "Enum of the values (id or name): required for the type enum, not allowed for the others" };
        var reference = Kit.Ref("Field");
        var newName = new Option<string?>("--name") { Description = "New name" };
        var newType = new Option<string?>("--type") { Description = $"New type of the values: {TypeNames}. Allowed: any to string, int to float, string to int/float/bool/date/enum; the values in tasks are converted" };
        var setMultiple = new Option<bool>("--multiple") { Description = "The field holds several values from now on" };
        var setSingle = new Option<bool>("--single") { Description = "The field holds one value from now on (tasks with several values: choose --several)" };
        var newEnum = new Option<string?>("--enum") { Description = "New enum of the values (id or name); values of the tasks are matched by name, see --map" };
        var clearUnconvertible = new Option<bool>("--clear-unconvertible") { Description = "Clear the values of tasks that do not fit the new type/enum (the others are converted)" };
        var several = new Option<string?>("--several") { Description = "Tasks with several values when the field becomes single: keep-first or clear" };
        var map = Kit.Many("--map", "With a new enum: From=To pairs matching an old value to a value of the new enum (repeat for several); the rest are matched by name");
        var expected = Kit.ExpectedVersion();
        var paging = new Kit.Paging();

        reference.Suggests(g, Sources.Fields);
        type.Suggests(g, Sources.FieldTypes);
        newType.Suggests(g, Sources.FieldTypes);
        enumeration.Suggests(g, Sources.Enums);
        newEnum.Suggests(g, Sources.Enums);
        several.Suggests(g, Sources.SeveralChoices);
        
        return Kit.Group("field", "Fields of the project's catalog: a task type attaches them, tasks hold their values (see 'task-type' and 'task')",
            Kit.Leaf(g, "create", "Creates a field", c =>
            {
                c.Arguments.Add(name);
                c.Options.Add(type);
                c.Options.Add(multiple);
                c.Options.Add(enumeration);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var kind = ParseType(parse.GetRequiredValue(type));
                Guid? enumId = parse.GetValue(enumeration) is { } enumRef ? (await FindEnum(ctx, projectId, enumRef)).Id : null;

                var field = await ctx.Get<FieldService>().Create(projectId,
                    new CreateField(parse.GetRequiredValue(name), kind, parse.GetValue(multiple) ? true : null, enumId), ctx.Ct);
                var names = await EnumNames(ctx, projectId);
                ctx.Print(field, $"Created field '{field.Name}' ({TypeText(field, names)}) {field.Id}");
            }),
            Kit.Leaf(g, "list", "Lists fields", paging.AddTo, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var names = await EnumNames(ctx, projectId);
                ctx.Print(await paging.Load(parse, page => ctx.Get<FieldService>().GetRange(projectId, page, ctx.Ct)),
                    x => Kit.Row(x.Id, x.Name, TypeText(x, names)));
            }),
            Kit.Leaf(g, "get", "Shows a field", c => c.Arguments.Add(reference), async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var field = await FindField(ctx, projectId, parse.GetRequiredValue(reference));
                var enums = await ctx.Get<FieldEnumService>().GetAll(projectId, ctx.Ct);
                ctx.Print(field, Kit.Fields(("id", field.Id), ("name", field.Name), ("type", field.Type.ToString().ToLowerInvariant()),
                    ("multiple", field.Multiple ? "yes" : null),
                    ("enum", field.EnumId is { } id ? Kit.Named(enums, id, x => x.Id, x => x.Name) : null), ("version", field.Version)));
            }),
            Kit.Leaf(g, "update", "Changes a field: name, type, 'multiple' and enum (the values in tasks are converted)", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(newName);
                c.Options.Add(newType);
                c.Options.Add(setMultiple);
                c.Options.Add(setSingle);
                c.Options.Add(newEnum);
                c.Options.Add(clearUnconvertible);
                c.Options.Add(several);
                c.Options.Add(map);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                Kit.RequireChange(parse, newName, newType, setMultiple, setSingle, newEnum);
                if (parse.GetValue(setMultiple) && parse.GetValue(setSingle))
                    throw new CliException("Use either --multiple or --single, not both");
                var projectId = await ctx.ProjectId();
                var field = await FindField(ctx, projectId, parse.GetRequiredValue(reference));

                var kind = parse.GetValue(newType) is { } typeText ? ParseType(typeText) : (FieldType?)null;
                Guid? enumId = parse.GetValue(newEnum) is { } enumRef ? (await FindEnum(ctx, projectId, enumRef)).Id : null;
                bool? multi = parse.GetValue(setMultiple) ? true : parse.GetValue(setSingle) ? false : null;
                var mapping = Kit.Values(parse, map)?.Select(x =>
                {
                    var parts = x.Split('=', 2);
                    return parts.Length == 2 && parts[0].Length > 0
                        ? new FieldValueMapping(parts[0], parts[1])
                        : throw new CliException($"--map expects From=To, not '{x}'");
                }).ToArray();
                var chosen = parse.GetValue(clearUnconvertible) || parse.GetValue(several) != null || mapping != null;
                var choice = chosen
                    ? new FieldChangeChoice(parse.GetValue(clearUnconvertible), ParseSeveral(parse.GetValue(several)), mapping)
                    : null;

                try
                {
                    var updated = await ctx.Get<FieldService>().Update(projectId, field.Id,
                        new UpdateField(parse.GetValue(newName), parse.GetValue(expected) ?? field.Version, kind, multi, enumId, choice), ctx.Ct)
                        ?? throw new CliException($"No field '{field.Id}'");
                    ctx.Print(updated, $"Updated field '{updated.Name}' ({TypeText(updated, await EnumNames(ctx, projectId))}) {updated.Id}");
                }
                catch (TaskerConflictException e) when (e.Code == ConflictCode.InUse && choice == null)
                {
                    throw new TaskerConflictException(
                        $"{e.Message}: repeat with --clear-unconvertible, --several keep-first|clear or --map From=To as needed", e.Code);
                }
            }),
            Kit.Leaf(g, "delete", "Deletes a field (not used by task types and tasks)", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var field = await FindField(ctx, projectId, parse.GetRequiredValue(reference));
                await ctx.Get<FieldService>().Delete(projectId, field.Id, parse.GetValue(expected) ?? field.Version, ctx.Ct);
                ctx.Print(new { deleted = field.Id }, Kit.Deleted("field", field.Name, field.Id));
            }));
    }

    private static string Values(FieldEnum value) => string.Join(", ", value.Values.Select(x => x.Name));

    /// <summary>Значение перечисления по id или названию (без учёта регистра).</summary>
    private static FieldEnumValue FindValue(FieldEnum value, string reference) =>
        Refs.FindItem(value.Values, reference, x => x.Id, x => x.Name, $"value of enum '{value.Name}'");

    /// <summary>Правка значений: <c>--values</c> (список целиком) или <c>--add-value</c>/<c>--remove-value</c>/<c>--rename-value</c> (по одному).</summary>
    /// <returns>null — значения не меняются.</returns>
    private static FieldEnumValueInput[]? EditedValues(
        FieldEnum current, string[]? replace, string[]? add, string[]? remove, string[]? rename)
    {
        if (replace != null)
        {
            if (add != null || remove != null || rename != null)
                throw new CliException("Use either --values or --add-value/--remove-value/--rename-value, not both");
            // Значение с тем же названием остаётся тем же значением (его id выбран у задач); остальные — новые.
            return replace.Select(x => new FieldEnumValueInput(
                current.Values.FirstOrDefault(v => string.Equals(v.Name, x, StringComparison.OrdinalIgnoreCase))?.Id, x)).ToArray();
        }

        if (add == null && remove == null && rename == null)
            return null;

        var list = current.Values.Select(x => new FieldEnumValueInput(x.Id, x.Name)).ToList();
        foreach (var pair in rename ?? [])
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || parts[0].Length == 0)
                throw new CliException($"--rename-value expects Old=New, not '{pair}'");
            var id = FindValue(current, parts[0]).Id;
            list[list.FindIndex(x => x.Id == id)] = new FieldEnumValueInput(id, parts[1]);
        }
        foreach (var reference in remove ?? [])
            list.RemoveAll(x => x.Id == FindValue(current, reference).Id);
        foreach (var added in add ?? [])
            list.Add(new FieldEnumValueInput(null, added));
        return list.ToArray();
    }

    public static Command Enums(GlobalOptions g)
    {
        var name = Kit.Name("name", "Enum name (unique in the project, case-insensitive)");
        var values = Kit.Many("--value", "Values in display order (several after one --value, or repeat it)", required: true);
        var reference = Kit.Ref("Enum");
        var newName = new Option<string?>("--name") { Description = "New name" };
        var replace = Kit.Many("--values", "Replace the whole list of values and their order; a value with the same name stays the same value (selected in tasks)");
        var add = Kit.Many("--add-value", "Add values at the end of the list");
        var remove = Kit.Many("--remove-value", "Remove values (id or name). If tasks have them selected, choose --drop or --replace-with");
        var rename = new Option<string[]>("--rename-value") { Description = "Rename a value: Old=New (repeat for several); the value stays the same in tasks" };
        var drop = new Option<bool>("--drop") { Description = "With --remove-value: clear the removed values from the tasks that have them selected" };
        var replaceWith = new Option<string?>("--replace-with") { Description = "With --remove-value: give the tasks this value instead (a value the enum keeps: id or name)" };
        var expected = Kit.ExpectedVersion();

        reference.Suggests(g, Sources.Enums);
        remove.Suggests(g, Sources.EnumValues(reference));
        replaceWith.Suggests(g, Sources.EnumValues(reference));
        rename.Suggests(g, Sources.EnumValues(reference, pairs: true));
        
        return Kit.Group("enum", "Enums of the project: lists of values for fields of the type enum",
            Kit.Leaf(g, "create", "Creates an enum", c =>
            {
                c.Arguments.Add(name);
                c.Options.Add(values);
            }, async (parse, ctx) =>
            {
                var created = await ctx.Get<FieldEnumService>().Create(
                    await ctx.ProjectId(), new CreateFieldEnum(parse.GetRequiredValue(name), parse.GetRequiredValue(values)), ctx.Ct);
                ctx.Print(created, $"Created enum '{created.Name}' ({Values(created)}) {created.Id}");
            }),
            Kit.List(g, "Lists enums",
                async (ctx, page) => await ctx.Get<FieldEnumService>().GetRange(await ctx.ProjectId(), page, ctx.Ct),
                x => Kit.Row(x.Id, x.Name, Values(x))),
            Kit.Leaf(g, "get", "Shows an enum with its values", c => c.Arguments.Add(reference), async (parse, ctx) =>
            {
                var value = await FindEnum(ctx, await ctx.ProjectId(), parse.GetRequiredValue(reference));
                ctx.Print(value, Kit.Fields(("id", value.Id), ("name", value.Name), ("version", value.Version))
                    + "\nvalues:\n" + string.Join('\n', value.Values.Select(x => $"  {x.Name}  {x.Id}")));
            }),
            Kit.Leaf(g, "update", "Changes an enum: its name and values", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(newName);
                c.Options.Add(replace);
                c.Options.Add(add);
                c.Options.Add(remove);
                c.Options.Add(rename);
                c.Options.Add(drop);
                c.Options.Add(replaceWith);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                Kit.RequireChange(parse, newName, replace, add, remove, rename);
                var projectId = await ctx.ProjectId();
                var current = await FindEnum(ctx, projectId, parse.GetRequiredValue(reference));

                var edited = EditedValues(current, Kit.Values(parse, replace), Kit.Values(parse, add), Kit.Values(parse, remove), Kit.Values(parse, rename));
                var removing = edited != null && current.Values.Any(x => edited.All(e => e.Id != x.Id));

                if (parse.GetValue(drop) && parse.GetValue(replaceWith) != null)
                    throw new CliException("Use either --drop or --replace-with, not both");
                RemovedEnumValues? choice = null;
                if (parse.GetValue(drop))
                    choice = new RemovedEnumValues(Clear: true);
                else if (parse.GetValue(replaceWith) is { } targetRef)
                {
                    var target = FindValue(current, targetRef);
                    if (edited == null || edited.All(x => x.Id != target.Id))
                        throw new CliException($"--replace-with must be a value the enum keeps, but '{target.Name}' is being removed");
                    choice = new RemovedEnumValues(ReassignTo: target.Id);
                }

                try
                {
                    var result = await ctx.Get<FieldEnumService>().Update(projectId, current.Id,
                        new UpdateFieldEnum(parse.GetValue(newName), edited, parse.GetValue(expected) ?? current.Version, choice), ctx.Ct)
                        ?? throw new CliException($"No enum '{current.Id}'");
                    var updated = result.Value;
                    var text = $"Updated enum '{updated.Name}' ({Values(updated)}) {updated.Id}";
                    if (removing && choice != null)
                    {
                        var names = string.Join(", ", current.Values.Where(x => updated.Values.All(y => y.Id != x.Id)).Select(x => $"'{x.Name}'"));
                        // Условия колонок досок на убираемое значение тоже переписаны (убраны или заменены): их число — после задач, если они были.
                        var tasks = Kit.Tasks(result.AffectedTasks) +
                            (result.AffectedColumns > 0 ? $" and {result.AffectedColumns} board column condition(s)" : "");
                        text += "\n" + (choice.ReassignTo is { } to
                            ? $"Reassigned {names} → '{updated.Values.First(x => x.Id == to).Name}' in {tasks}"
                            : $"Removed {names} from {tasks}");
                    }
                    ctx.Print(result, text);
                }
                catch (TaskerConflictException e) when (e.Code == ConflictCode.InUse && removing && choice == null)
                {
                    throw new TaskerConflictException($"{e.Message}: repeat with --drop or --replace-with <value>", e.Code);
                }
            }),
            Kit.Leaf(g, "delete", "Deletes an enum (not used by fields)", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var value = await FindEnum(ctx, projectId, parse.GetRequiredValue(reference));
                await ctx.Get<FieldEnumService>().Delete(projectId, value.Id, parse.GetValue(expected) ?? value.Version, ctx.Ct);
                ctx.Print(new { deleted = value.Id }, Kit.Deleted("enum", value.Name, value.Id));
            }));
    }
}
