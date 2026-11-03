using System.CommandLine;
using Tasker.Cli.Commands;
using Tasker.Core.Boards;
using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Core.Links;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;
using Tasker.Core.Users;
using Tasker.Global;

namespace Tasker.Cli.Completion;

/// <summary>
/// Каталог источников значений для автодополнения (см. <see cref="Completer"/>). Ссылки на сущности в командах — «id или имя»,
/// поэтому предлагаются имена; у серий — префиксы, у задач — ссылки вида <c>TSK-7</c>.
/// </summary>
internal static class Sources
{
    private static ValueSource Names(Func<Lookup, Task<IEnumerable<string>>> get) => new(get);

    private static ValueSource InProject(Func<Lookup, Guid, Task<IEnumerable<string>>> get) =>
        new(async lookup => await get(lookup, await lookup.ProjectId()));

    private static ValueSource Fixed(params string[] values) =>
        new(_ => Task.FromResult<IEnumerable<string>>(values), NeedsWorkspace: false);

    /// <summary>Источник без рабочей области: значения известны программе или лежат в глобальных настройках.</summary>
    private static ValueSource Local(Func<Lookup, IEnumerable<string>> get) =>
        new(x => Task.FromResult(get(x)), NeedsWorkspace: false);

    public static ValueSource Projects { get; } = Names(async x => (await x.Ctx.AllProjects()).Select(p => p.Name));

    public static ValueSource Statuses { get; } = InProject(async (x, project) =>
        (await x.Get<IStatusStorage>().GetAll(project, x.Ct)).Select(s => s.Name));

    public static ValueSource StatusSets { get; } = InProject(async (x, project) =>
        (await x.Get<IStatusSetStorage>().GetAll(project, x.Ct)).Select(s => s.Name));

    public static ValueSource TaskTypes { get; } = InProject(async (x, project) =>
        (await x.Get<ITaskTypeStorage>().GetAll(project, x.Ct)).Select(t => t.Name));

    /// <summary>Серия — по id или точному префиксу: предлагаются префиксы.</summary>
    public static ValueSource Series { get; } = InProject(async (x, project) =>
        (await x.Get<ISeriesStorage>().GetAll(project, x.Ct)).Select(s => s.Prefix));

    public static ValueSource Boards { get; } = InProject(async (x, project) =>
        (await x.Get<IBoardStorage>().GetAll(project, x.Ct)).Select(b => b.Name));

    public static ValueSource LinkTypes { get; } = InProject(async (x, project) =>
        (await x.Get<LinkTypeService>().GetAll(project, x.Ct)).Select(t => t.Name));

    /// <summary>Фраза связи в <c>task link</c>: название типа или название любой из сторон («blocks», «is blocked by»).</summary>
    public static ValueSource LinkPhrases { get; } = InProject(async (x, project) =>
        (await x.Get<LinkTypeService>().GetAll(project, x.Ct)).SelectMany(t => new[] { t.Name, t.OutwardName, t.InwardName }));

    public static ValueSource Fields { get; } = InProject(async (x, project) =>
        (await x.Get<FieldService>().GetAll(project, x.Ct)).Select(f => f.Name));

    public static ValueSource Enums { get; } = InProject(async (x, project) =>
        (await x.Get<FieldEnumService>().GetAll(project, x.Ct)).Select(e => e.Name));

    public static ValueSource Users { get; } = Names(x => UserNames(x, UserKind.Human));

    public static ValueSource Agents { get; } = Names(x => UserNames(x, UserKind.Agent));

    /// <summary>Области, разрешённые в MCP (<c>mcp workspace remove</c>): пути из глобальных настроек, без хранилища области.</summary>
    public static ValueSource McpWorkspaces { get; } = new(
        _ => Task.FromResult<IEnumerable<string>>(new SettingsStore().Load().Mcp.Workspaces.Select(w => w.Path).ToArray()),
        NeedsWorkspace: false);

    public static ValueSource FieldTypes { get; } = Fixed([.. Enum.GetNames<FieldType>().Select(x => x.ToLowerInvariant())]);

    /// <summary>Иерархические типы связей проекта (<c>--parent-type</c>).</summary>
    public static ValueSource HierarchicalLinkTypes { get; } = InProject(async (x, project) =>
        (await x.Get<LinkTypeService>().GetAll(project, x.Ct)).Where(t => t.Hierarchical).Select(t => t.Name));

    public static ValueSource SeveralChoices { get; } = Fixed("keep-first", "clear");

    /// <summary>
    /// Темы справочника <c>manual</c> на языке, уже набранном в строке (<c>--lang</c>, иначе <c>TASKER_LANG</c>): набор тем берётся
    /// из каталога страниц, новые страницы и языки подхватываются сами.
    /// </summary>
    public static ValueSource ManualTopics(ManualCatalog catalog, Option<string?> lang) => Local(x =>
        catalog.Topics(x.Value(lang) ?? AppEnvironment.Get(ManualCommands.LanguageVariable)).Select(p => p.Id));

    /// <summary>Языки справочника: те, на которых в каталоге есть страницы.</summary>
    public static ValueSource ManualLanguages(ManualCatalog catalog) => Local(_ => catalog.Languages);

    private static readonly Dictionary<string, ValueSource> LockTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["project"] = Projects,
        ["task"] = TaskReferences(),
        ["taskType"] = TaskTypes,
        ["linkType"] = LinkTypes,
        ["field"] = Fields,
        ["enum"] = Enums,
        ["status"] = Statuses,
        ["statusSet"] = StatusSets,
        ["board"] = Boards,
        ["series"] = Series
    };

    /// <summary>Вид сущности в <c>lock</c> (то, что принимает команда).</summary>
    public static ValueSource EntityKinds { get; } = new(_ => Task.FromResult<IEnumerable<string>>(LockTargets.Keys), NeedsWorkspace: false);

    /// <summary>Сущность в <c>lock</c>: источник зависит от уже набранного вида.</summary>
    public static ValueSource LockedEntity(Argument<string> kind) => new(async x =>
        x.Value(kind) is { } name && LockTargets.TryGetValue(name, out var source) ? await source.Get(x) : []);

    /// <summary>Колонки доски, названной в <paramref name="board"/> (<c>board tasks Основная &lt;Tab&gt;</c>).</summary>
    public static ValueSource BoardColumns(Argument<string> board) => InProject(async (x, project) =>
    {
        if (x.Value(board) is not { } reference)
            return [];
        var boards = await x.Get<IBoardStorage>().GetAll(project, x.Ct);
        return Refs.FindItem(boards, reference, b => b.Id, b => b.Name, "board").Columns.Select(c => c.Name);
    });

    /// <summary>Значения перечисления, названного в <paramref name="enumeration"/> (<c>enum update Приоритет --remove-value &lt;Tab&gt;</c>); с <paramref name="pairs"/> — <c>Старое=</c>.</summary>
    public static ValueSource EnumValues(Argument<string> enumeration, bool pairs = false) => InProject(async (x, project) =>
    {
        if (x.Value(enumeration) is not { } reference)
            return [];
        var all = await x.Get<FieldEnumService>().GetAll(project, x.Ct);
        var values = Refs.FindItem(all, reference, e => e.Id, e => e.Name, "enum").Values.Select(v => v.Name);
        return pairs ? values.Select(v => v + "=") : values;
    });

    /// <summary>
    /// Статусы: у <c>task create/update</c> — только статусы набора типа, названного в <paramref name="type"/>
    /// (иначе все статусы проекта).
    /// </summary>
    public static ValueSource TaskStatuses<T>(Option<T> type) => InProject(async (x, project) =>
        await StatusesOfTypes(x, project, x.Value(type) is { } reference ? [reference] : []));

    /// <summary>
    /// Статусы у <c>task list --status</c>: типов в <paramref name="types"/> можно набрать несколько (<c>--type A --type B</c>) —
    /// тогда статусы наборов всех этих типов (значения параметра — «ИЛИ»); не набраны — все статусы проекта.
    /// </summary>
    public static ValueSource TaskStatuses(Option<string[]> types) => InProject(async (x, project) =>
        await StatusesOfTypes(x, project, x.Values(types)));

    private static async Task<IEnumerable<string>> StatusesOfTypes(Lookup x, Guid project, string[] typeReferences)
    {
        var statuses = await x.Get<IStatusStorage>().GetAll(project, x.Ct);
        if (typeReferences.Length == 0)
            return statuses.Select(s => s.Name);

        var types = await x.Get<ITaskTypeStorage>().GetAll(project, x.Ct);
        var sets = await x.Get<IStatusSetStorage>().GetAll(project, x.Ct);
        var inSet = typeReferences
            .Select(r => Refs.FindItem(types, r, t => t.Id, t => t.Name, "task type").StatusSetId)
            .SelectMany(setId => sets.Where(s => s.Id == setId).SelectMany(s => s.StatusIds))
            .ToHashSet();
        return inSet.Count == 0 ? statuses.Select(s => s.Name) : statuses.Where(s => inSet.Contains(s.Id)).Select(s => s.Name);
    }

    /// <summary>
    /// Ссылки задач (<c>TSK-7</c>): до дефиса — префиксы серий с дефисом, после — номера задач серии, начинающиеся с набранных цифр
    /// (<c>TSK-7</c> → <c>TSK-7</c>, <c>TSK-70</c>, <c>TSK-71</c>…).
    /// </summary>
    public static ValueSource TaskReferences() => InProject(async (x, project) =>
    {
        var series = await x.Get<ISeriesStorage>().GetAll(project, x.Ct);
        var dash = x.Word.IndexOf('-');
        if (dash < 0)
            return series.Select(s => s.Prefix + "-");

        var digits = x.Word[(dash + 1)..];
        if (!digits.All(char.IsAsciiDigit))
            return [];

        var references = new List<(int Number, string Text)>();
        foreach (var found in series.Where(s => s.Prefix == x.Word[..dash]))
        {
            var filter = new TaskFilter { SeriesIds = [found.Id] };
            for (var loaded = 0; loaded < MaxScannedTasks;)
            {
                var page = await x.Get<TaskService>().List(project, filter, null, new Page(loaded, Page.MaxLimit), ct: x.Ct);
                foreach (var task in page.Data)
                    foreach (var number in task.SeriesNumbers.Where(n => n.SeriesId == found.Id && n.Number.ToString().StartsWith(digits, StringComparison.Ordinal)))
                        references.Add((number.Number, $"{found.Prefix}-{number.Number}"));

                loaded += page.Data.Length;
                if (page.Data.Length == 0 || loaded >= page.TotalCount)
                    break;
            }
        }

        return references.OrderBy(r => r.Number).Select(r => r.Text);
    });

    // Дальше просматривать задачи серии дольше, чем живёт дополнение, незачем.
    private const int MaxScannedTasks = 5000;

    /// <summary>
    /// <c>--field Имя=значение</c> у задач и досок: до знака — имена полей каталога с «=»; после «=» и «!=» — значения поля (значения его
    /// перечисления, у логического поля — true и false); после «:» — слова наличия (set, unset, attached, detached). У сравнений («&gt;=» и других) значения
    /// не подсказываются: числа и даты не из чего выбирать.
    /// </summary>
    public static ValueSource FieldValues { get; } = InProject((x, project) => FieldCondition(x, project, x.Word));

    /// <summary>Условие по полю (<see cref="FieldValues"/>) для слова <paramref name="word"/>: целиком набранное условие или его часть.</summary>
    private static async Task<IEnumerable<string>> FieldCondition(Lookup x, Guid project, string word)
    {
        var fields = await x.Get<FieldService>().GetAll(project, x.Ct);
        var sign = word.IndexOfAny(['=', '!', '<', '>', ':']);
        if (sign < 0)
            return fields.Select(f => f.Name + "=");

        var typed = word[..sign];
        if (fields.FirstOrDefault(f => string.Equals(f.Name, typed.Trim(), StringComparison.OrdinalIgnoreCase)) is not { } field)
            return [];

        if (word[sign] == ':')
            return new[] { "set", "unset", "attached", "detached" }.Select(w => $"{typed}:{w}");

        var prefix = word.AsSpan(sign).StartsWith("!=") ? "!=" : word[sign] == '=' ? "=" : null;
        if (prefix == null)
            return [];

        IEnumerable<string> values = field switch
        {
            { Type: FieldType.Bool } => ["true", "false"],
            { Type: FieldType.Enum, EnumId: { } enumId } =>
                (await x.Get<FieldEnumService>().GetAll(project, x.Ct)).Where(e => e.Id == enumId).SelectMany(e => e.Values).Select(v => v.Name),
            _ => []
        };
        return values.Select(v => $"{typed}{prefix}{v}");
    }

    /// <summary>
    /// <c>--sort status,-updated,Estimate</c>: ключи — status, type, title, created, updated, series и имена полей каталога; после запятой —
    /// следующий ключ (уже названные не повторяются). Пока ключ не начат, предлагаются и записи с «-» (по убыванию); набранный «-» оставляет только их.
    /// </summary>
    public static ValueSource SortKeys { get; } = InProject(async (x, project) =>
    {
        var comma = x.Word.LastIndexOf(',');
        var head = x.Word[..(comma + 1)];
        var typed = x.Word[(comma + 1)..];
        var used = head.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim().TrimStart('-').ToLowerInvariant()).ToHashSet();

        var names = TaskSortNames.BuiltinNames.Concat((await x.Get<FieldService>().GetAll(project, x.Ct)).Select(f => f.Name))
            .Where(n => !used.Contains(n.ToLowerInvariant()))
            .ToArray();
        var keys = typed.StartsWith('-') ? names.Select(n => "-" + n) : names.Concat(names.Select(n => "-" + n));
        return keys.Select(k => head + k);
    });

    /// <summary>
    /// <c>--column "Название=статус,статус"</c>: название колонки придумывает человек; после «=» и «,» — статусы проекта.
    /// </summary>
    public static ValueSource ColumnSpec { get; } = InProject(async (x, project) =>
    {
        var equals = x.Word.IndexOf('=');
        if (equals <= 0)
            return [];
        var head = x.Word[..(x.Word.LastIndexOf(',') is var comma and > 0 ? comma + 1 : equals + 1)];
        return (await x.Get<IStatusStorage>().GetAll(project, x.Ct)).Select(s => head + s.Name);
    });

    /// <summary>
    /// <c>--column-filter "Колонка:условие"</c>: до «:» — названия колонок (из набранных <paramref name="columns"/> и, если есть, колонок доски
    /// <paramref name="board"/>); после — условие по полю, как у <c>--field</c> (<see cref="FieldValues"/>).
    /// </summary>
    public static ValueSource ColumnFilters(Option<string[]> columns, Argument<string>? board = null) => InProject(async (x, project) =>
    {
        var names = (Completer.Safe(() => x.Parse.GetValue(columns)) ?? [])
            .Select(c => c.IndexOf('=') is var eq and > 0 ? c[..eq].Trim() : c.Trim()).ToList();
        if (names.Count == 0 && board != null && x.Value(board) is { } reference)
            names.AddRange(Refs.FindItem(await x.Get<IBoardStorage>().GetAll(project, x.Ct), reference, b => b.Id, b => b.Name, "board").Columns.Select(c => c.Name));

        // Названия колонок могут содержать «:»: условие начинается после самого длинного названия, за которым стоит «:».
        var column = names.Where(n => x.Word.StartsWith(n + ":", StringComparison.OrdinalIgnoreCase)).OrderByDescending(n => n.Length).FirstOrDefault();
        if (column == null)
            return names.Select(n => n + ":");

        var head = x.Word[..(column.Length + 1)];
        return (await FieldCondition(x, project, x.Word[head.Length..])).Select(v => head + v);
    });

    /// <summary>
    /// <c>--field Имя</c> и <c>--field Имя:required</c> у типов задач: имена полей каталога; после «:» — <c>required</c>.
    /// </summary>
    public static ValueSource TypeFields { get; } = InProject(async (x, project) =>
    {
        var colon = x.Word.LastIndexOf(':');
        return colon < 0
            ? (await x.Get<FieldService>().GetAll(project, x.Ct)).Select(f => f.Name)
            : [x.Word[..colon] + ":required"];
    });

    private static async Task<IEnumerable<string>> UserNames(Lookup x, UserKind kind)
    {
        var page = await x.Get<IUserStorage>().GetRange(new UserFilter { Kind = kind }, new Page(0, Page.MaxLimit), x.Ct);
        return page.Data.Select(u => u.Username);
    }
}
