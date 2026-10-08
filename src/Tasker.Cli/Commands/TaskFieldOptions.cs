using System.CommandLine;
using Tasker.Cli.Completion;
using Tasker.Core.Fields;
using Tasker.Core.Tasks;

namespace Tasker.Cli.Commands;

/// <summary>
/// Параметры полей у <c>task create</c> и <c>task update</c>: значения (<c>--field Имя=значение</c>), поля из каталога без значений
/// (<c>--add-field</c>), собственные поля задачи (<c>--custom-field</c>) и, при правке, снятие полей (<c>--remove-field</c>).
/// </summary>
internal sealed class TaskFieldOptions(bool update)
{
    public Option<string[]> Values { get; } = new("--field")
    {
        Description = "Value of a field: Name=value (the field of the task's type, an additional or own field of the task, or a field of the catalog: it becomes an additional field). " +
            "Repeat the option for several values of a field with several values; Name= clears the values. " +
            "int, float (with a dot), bool (true/false), date (yyyy-MM-dd); enum: the value name or id"
    };

    public Option<string[]> Custom { get; } = new("--custom-field")
    {
        Description = "A field of this task alone: Name:type[:required][:multiple][:enum=Enum][=value], e.g. 'Estimate:int=5', 'Tags:string:multiple=a', 'Risk:enum:required:enum=Priority=High'. " +
            "Types: string, int, float, bool, date, enum (the enum is a name or id). Repeat the same definition to give several values. Names cannot contain ':' or '='"
    };

    public Option<string[]> Add { get; } = new("--add-field")
    {
        Description = "Add a field of the catalog to the task without a value: Name (id or name; repeat for several)"
    };

    public Option<string[]> Remove { get; } = new("--remove-field")
    {
        Description = "Remove an additional or own field of the task with its values: Name (repeat for several); fields of the task's type cannot be removed"
    };

    /// <summary>Подсказки Tab: значения полей, поля каталога для добавления и снятия.</summary>
    public void Suggest(GlobalOptions g)
    {
        Values.Suggests(g, Sources.FieldValues);
        Add.Suggests(g, Sources.Fields);
        Remove.Suggests(g, Sources.Fields);
    }

    public Option[] All => update ? [Values, Custom, Add, Remove] : [Values, Custom, Add];

    public void AddTo(Command command)
    {
        foreach (var option in All)
            command.Options.Add(option);
    }

    /// <summary>
    /// Правка полей из параметров; null — ни один не указан. Поля называются по имени (или id): сначала поля самой задачи
    /// (в том числе собственные — их нет в каталоге), потом каталог.
    /// </summary>
    /// <param name="task">Задача, которую правят; null — создаваемая (полей у неё ещё нет).</param>
    public async Task<TaskFieldChanges?> Build(ParseResult parse, Context ctx, Guid projectId, TaskItem? task)
    {
        var values = Kit.Values(parse, Values);
        var custom = Kit.Values(parse, Custom);
        var add = Kit.Values(parse, Add);
        var remove = update ? Kit.Values(parse, Remove) : null;
        if (values == null && custom == null && add == null && remove == null)
            return null;

        var catalog = await ctx.Get<FieldService>().GetAll(projectId, ctx.Ct);
        var views = task == null ? [] : await ctx.Get<TaskService>().GetFields(projectId, task, ctx.Ct);

        // Поле по имени или id: у задачи (поля типа, дополнительные, собственные), иначе в каталоге.
        Guid Resolve(string reference)
        {
            var own = views.Where(x => x.FieldId.ToString() == reference.Trim().ToLowerInvariant()
                || string.Equals(x.Name, reference.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            return own.Length == 1 ? own[0].FieldId : Refs.Find(catalog, reference, x => x.Id, x => x.Name, "field");
        }

        var changes = new TaskFieldChanges();

        if (values != null)
        {
            var byField = new List<(Guid Id, List<string> Values)>();
            foreach (var text in values)
            {
                var parts = text.Split('=', 2);
                if (parts.Length != 2 || parts[0].Trim().Length == 0)
                    throw new CliException($"--field expects Name=value (Name= clears the values), not '{text}'");

                var id = Resolve(parts[0]);
                var entry = byField.FindIndex(x => x.Id == id);
                if (entry < 0)
                {
                    byField.Add((id, []));
                    entry = byField.Count - 1;
                }
                if (parts[1].Length > 0)
                    byField[entry].Values.Add(parts[1]);
            }
            changes = changes with { Values = byField.Select(x => new TaskFieldValueInput(x.Id, x.Values.ToArray())).ToArray() };
        }

        if (add != null)
            changes = changes with { AddFields = add.Select(Resolve).Distinct().ToArray() };

        if (remove != null)
        {
            var ids = new List<Guid>();
            foreach (var reference in remove)
            {
                var id = Resolve(reference);
                var view = views.FirstOrDefault(x => x.FieldId == id)
                    ?? throw new CliException($"The task has no additional or own field '{reference}'");
                if (view.Source == TaskFieldSource.Type)
                    throw new CliException($"'{view.Name}' is a field of the task's type and cannot be removed from the task: change the task type instead");
                if (!ids.Contains(id))
                    ids.Add(id);
            }
            changes = changes with { RemoveFields = ids.ToArray() };
        }

        if (custom != null)
            changes = changes with { NewOwnFields = await ParseCustom(ctx, projectId, custom) };

        return changes;
    }

    /// <summary>Собственные поля из <c>--custom-field</c>; одно и то же поле, повторенное с тем же определением, собирает значения.</summary>
    private static async Task<NewOwnField[]> ParseCustom(Context ctx, Guid projectId, string[] texts)
    {
        var result = new List<NewOwnField>();
        foreach (var text in texts)
        {
            var (name, type, required, multiple, enumRef, value) = ParseCustomField(text);

            Guid? enumId = enumRef == null ? null : (await FieldCommands.FindEnum(ctx, projectId, enumRef)).Id;
            if (type == FieldType.Enum && enumId == null)
                throw new CliException($"Field '{name}' is of the type enum: name its enum with ':enum=<enum>' (see 'enum list')");
            if (type != FieldType.Enum && enumId != null)
                throw new CliException($"Field '{name}': ':enum=' is only for the type enum");

            var field = new NewOwnField(name, type, null, required, multiple, enumId);
            var index = result.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                result.Add(field with { Values = value == null ? [] : [value] });
                continue;
            }

            var first = result[index];
            if (first with { Values = null } != field)
                throw new CliException($"--custom-field '{name}' is repeated with a different definition");
            result[index] = first with { Values = [.. first.Values ?? [], .. value == null ? [] : new[] { value }] };
        }
        return result.ToArray();
    }

    /// <summary>
    /// <c>Имя:тип[:required][:multiple][:enum=Перечисление][=значение]</c>. Значение — всё после первого <c>=</c>, не считая <c>enum=…</c>
    /// (оно может содержать <c>:</c> и <c>=</c>); модификаторы — в любом порядке.
    /// </summary>
    internal static (string Name, FieldType Type, bool Required, bool Multiple, string? Enum, string? Value) ParseCustomField(string text)
    {
        var syntax = $"--custom-field expects Name:type[:required][:multiple][:enum=Enum][=value], not '{text}'";
        var position = 0;

        string Word(string stops)
        {
            var end = text.IndexOfAny(stops.ToCharArray(), position);
            var word = end < 0 ? text[position..] : text[position..end];
            position += word.Length;
            return word;
        }

        var name = Word(":=").Trim();
        if (name.Length == 0 || position >= text.Length || text[position] != ':')
            throw new CliException(syntax);
        position++;

        var type = FieldCommands.ParseType(Word(":=").Trim());
        bool required = false, multiple = false;
        string? enumeration = null;

        while (position < text.Length && text[position] == ':')
        {
            position++;
            var modifier = Word(":=").Trim();
            switch (modifier.ToLowerInvariant())
            {
                case "required":
                    required = true;
                    break;
                case "multiple":
                    multiple = true;
                    break;
                case "enum" when position < text.Length && text[position] == '=':
                    position++;
                    enumeration = Word(":=").Trim();
                    if (enumeration.Length == 0)
                        throw new CliException(syntax);
                    break;
                default:
                    throw new CliException($"Unknown modifier '{modifier}' in --custom-field '{text}': use required, multiple or enum=<enum>");
            }
        }

        string? value = null;
        if (position < text.Length)
        {
            if (text[position] != '=')
                throw new CliException(syntax);
            value = text[(position + 1)..];
        }

        return (name, type, required, multiple, enumeration, value is { Length: > 0 } ? value : null);
    }
}
