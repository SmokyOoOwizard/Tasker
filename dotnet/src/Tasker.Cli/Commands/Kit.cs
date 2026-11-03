using System.CommandLine;
using Tasker.Cli.Completion;
using Tasker.Core;
using Tasker.Core.Dto;

namespace Tasker.Cli.Commands;

/// <summary>Мелочи для описания команд.</summary>
internal static class Kit
{
    public static Command Group(string name, string description, params Command[] subcommands)
    {
        var command = new Command(name, description);
        foreach (var subcommand in subcommands)
            command.Subcommands.Add(subcommand);
        return command;
    }

    /// <summary>Имя новой сущности: придумывает человек, подсказывать нечего.</summary>
    public static Argument<string> Name(string name, string description) => new Argument<string>(name) { Description = description }.NoSuggestions();

    public static Option<string[]> Many(string name, string description, bool required = false) => new(name)
    {
        Description = description,
        AllowMultipleArgumentsPerToken = true,
        Required = required
    };

    public static Argument<string> Ref(string what) => new("id-or-name") { Description = $"{what} (id or name)" };

    /// <summary>
    /// <c>--description-length</c> списков задач: в <c>--json</c> описание в записях полное (-1, как в API) или усечённое; в тексте описаний нет.
    /// Значение проверяет ядро (<see cref="Tasker.Core.Tasks.DescriptionPreview.Check"/>).
    /// </summary>
    public static Option<int> DescriptionLength() => new Option<int>("--description-length")
    {
        Description = "For --json: how many characters of each task's description to include - 0 none, N the first N characters, " +
            "-1 the full text (default). The record also has descriptionTruncated, descriptionLength (the full length), linksCount, parentIds and childCount",
        DefaultValueFactory = _ => Tasker.Core.Tasks.DescriptionPreview.Full
    }.NoSuggestions();

    /// <summary>
    /// <c>--description-length</c> списков статусов и типов задач: то же усечение, что у задач (<see cref="DescriptionLength"/>), но для записей этих списков;
    /// действует на <c>--json</c> (в тексте описаний нет).
    /// </summary>
    public static Option<int> EntityDescriptionLength(string what) => new Option<int>("--description-length")
    {
        Description = $"For --json: how many characters of each {what}'s description to include - 0 none, N the first N characters, " +
            "-1 the full text (default). The record also has descriptionTruncated and descriptionLength (the full length)",
        DefaultValueFactory = _ => Tasker.Core.Tasks.DescriptionPreview.Full
    }.NoSuggestions();

    /// <summary><c>-d</c>/<c>--description</c> при создании сущности: свободный текст, подсказывать нечего.</summary>
    public static Option<string?> NewDescription(string what) =>
        new Option<string?>("--description", "-d") { Description = $"What the {what} means and when to use it (free text, may have line breaks)" }.NoSuggestions();

    /// <summary><c>-d</c>/<c>--description</c> при правке сущности: пустая строка очищает.</summary>
    public static Option<string?> ChangedDescription() =>
        new Option<string?>("--description", "-d") { Description = "New description; an empty one clears it" }.NoSuggestions();

    /// <summary>Описание в выводе <c>get</c>: после полей, отдельным абзацем; пустого нет.</summary>
    public static string WithDescription(string text, string description) =>
        description.Length == 0 ? text : text + "\n\n" + description;

    /// <summary><c>--sort</c> списков задач: ключи через запятую, <c>-</c> — по убыванию (разбирает ядро, <see cref="Tasker.Core.Tasks.TaskService.ParseSort"/>).</summary>
    public static Option<string?> Sort() => new("--sort")
    {
        Description = "Order of the tasks: keys separated by commas, '-' before a key for descending order, e.g. status,-updated,Estimate. " +
            "Keys: status (position in the task type's status set), type, title, created, updated, series (TSK-9 before TSK-12), or the name of a field " +
            "(catalog or own field of tasks: numbers by value, dates by date, enum by the order of its values, bool false before true, strings ignoring case; " +
            "a multiple field - by its first value). Tasks without the value go last in either direction; equal keys keep the default order (creation time). " +
            "Default: creation order"
    };

    public static Option<string?> ExpectedVersion() => new("--expected-version")
    {
        Description = "Fail if the entity was changed after this version (as shown by get). Default: the current version"
    };

    /// <summary>Значения повторяемого параметра; null — параметр не указан (System.CommandLine в этом случае отдаёт пустой массив).</summary>
    public static string[]? Values(ParseResult parse, Option<string[]> option) =>
        parse.GetResult(option) is { Implicit: false } ? parse.GetValue(option) : null;

    /// <summary>Хотя бы один из параметров изменения должен быть указан.</summary>
    public static void RequireChange(ParseResult parse, params Option[] options)
    {
        if (options.All(x => parse.GetResult(x) is not { Implicit: false }))
            throw new CliException($"Nothing to change: pass at least one of {string.Join(", ", options.Select(x => x.Name))}");
    }

    /// <summary>Вывод <c>get</c>: строки «поле: значение», пустые значения пропускаются.</summary>
    public static string Fields(params (string Name, object? Value)[] fields) =>
        string.Join('\n', fields
            .Where(x => x.Value != null)
            .Select(x => $"{x.Name + ":",-13} {(x.Value is DateTimeOffset date ? date.ToString("yyyy-MM-dd HH:mm:ss zzz") : x.Value)}"));

    /// <summary>«Имя (id)» для читаемого вывода ссылок; неизвестный id — как есть.</summary>
    public static string Named<T>(IEnumerable<T> items, Guid id, Func<T, Guid> idOf, Func<T, string> nameOf) =>
        items.FirstOrDefault(x => idOf(x) == id) is { } item ? $"{nameOf(item)} ({id})" : id.ToString();

    /// <summary>«1 task» / «12 tasks» — для итога каскадных правок.</summary>
    public static string Tasks(int count) => count == 1 ? "1 task" : $"{count} tasks";

    public static string Deleted(string what, string name, Guid id) => $"Deleted {what} '{name}' {id}";

    /// <summary>Команда, которой нужна рабочая область.</summary>
    public static Command Leaf(GlobalOptions g, string name, string description, Action<Command> configure, Func<ParseResult, Context, Task> action)
    {
        var command = new Command(name, description);
        configure(command);
        command.SetAction((parse, ct) => g.Run(parse, ct, ctx => action(parse, ctx)));
        return command;
    }

    /// <summary>Параметры списка: <c>--offset</c> и <c>--limit</c> (страница) или <c>--all</c> (всё без ограничения по количеству).</summary>
    public sealed class Paging
    {
        private readonly Option<int> _offset = new("--offset") { Description = "How many to skip", DefaultValueFactory = _ => 0 };
        private readonly Option<int> _limit = new("--limit") { Description = $"How many to show, 1-{Page.MaxLimit}", DefaultValueFactory = _ => Page.DefaultLimit };
        private readonly Option<bool> _all = new("--all") { Description = "Show everything, not just a page (cannot be combined with --offset/--limit)" };

        public void AddTo(Command command)
        {
            command.Options.Add(_offset);
            command.Options.Add(_limit);
            command.Options.Add(_all);
        }

        /// <summary>
        /// Список для вывода: страница из <c>--offset</c>/<c>--limit</c> или, с <c>--all</c>, все элементы — постранично по
        /// <see cref="Page.MaxLimit"/>, но одним списком (в <c>--json</c> — одной страницей: <c>offset</c> 0, <c>limit</c> и <c>totalCount</c> равны числу элементов).
        /// </summary>
        /// <exception cref="CliException"><c>--all</c> вместе с <c>--offset</c> или <c>--limit</c>.</exception>
        public async Task<ListDto<T>> Load<T>(ParseResult parse, Func<Page, Task<ListDto<T>>> loadPage)
        {
            if (!parse.GetValue(_all))
                return await loadPage(Page.Of(parse.GetValue(_offset), parse.GetValue(_limit)));

            if (parse.GetResult(_offset) is { Implicit: false } || parse.GetResult(_limit) is { Implicit: false })
                throw new CliException("Use either --all or --offset/--limit, not both");

            var items = new List<T>();
            while (true)
            {
                var page = await loadPage(new Page(items.Count, Page.MaxLimit));
                items.AddRange(page.Data);
                if (page.Data.Length == 0 || items.Count >= page.TotalCount)
                    break;
            }

            return new ListDto<T> { TotalCount = items.Count, Offset = 0, Limit = items.Count, Data = [.. items] };
        }

        /// <summary>
        /// То же для списка деревом: <c>--offset</c>/<c>--limit</c> считают задачи верхнего уровня (поддеревья идут целиком), <c>--all</c> листает верхний уровень
        /// страницами по <see cref="Page.MaxLimit"/> и собирает строки в один список.
        /// </summary>
        public async Task<Core.Tasks.TaskTreeList> LoadTree(ParseResult parse, Func<Page, Task<Core.Tasks.TaskTreeList>> loadPage)
        {
            if (!parse.GetValue(_all))
                return await loadPage(Page.Of(parse.GetValue(_offset), parse.GetValue(_limit)));

            if (parse.GetResult(_offset) is { Implicit: false } || parse.GetResult(_limit) is { Implicit: false })
                throw new CliException("Use either --all or --offset/--limit, not both");

            var rows = new List<Core.Tasks.TaskTreeItem>();
            var top = 0;
            Core.Tasks.TaskTreeList page;
            do
            {
                page = await loadPage(new Page(top, Page.MaxLimit));
                rows.AddRange(page.Data);
                top += Page.MaxLimit;
            }
            while (top < page.TopLevelCount);

            return new Core.Tasks.TaskTreeList
            {
                TotalCount = page.TotalCount, TopLevelCount = page.TopLevelCount, Offset = 0, Limit = page.TopLevelCount, Data = [.. rows]
            };
        }
    }

    /// <summary>
    /// Команда, которой рабочая область не нужна (настройки, демон). <paramref name="usesWorkspace"/> = false — команда не читает и
    /// <c>-w</c>/<c>--sqlite</c>/<c>-p</c>: автодополнение их у неё не предлагает (<see cref="WorkspaceFreeCommand"/>).
    /// </summary>
    public static Command Plain(GlobalOptions g, string name, string description, Action<Command> configure, Func<ParseResult, Context, Task> action, bool usesWorkspace = true)
    {
        var command = usesWorkspace ? new Command(name, description) : new WorkspaceFreeCommand(name, description, [g.Workspace, g.Sqlite, g.Project]);
        configure(command);
        command.SetAction((parse, ct) => g.RunPlain(parse, ct, ctx => action(parse, ctx)));
        return command;
    }

    /// <summary>Команда <c>list</c>: страница списка или, с <c>--all</c>, весь список.</summary>
    public static Command List<T>(GlobalOptions g, string description, Func<Context, Page, Task<ListDto<T>>> load, Func<T, string[]> cells)
    {
        var paging = new Paging();
        return Leaf(g, "list", description, paging.AddTo,
            async (parse, ctx) => ctx.Print(await paging.Load(parse, page => load(ctx, page)), cells));
    }

    /// <summary>
    /// <c>list</c> сущностей с описанием: как <see cref="List{T}"/>, плюс <c>--description-length</c> (усечение описаний в <c>--json</c>;
    /// в текстовом списке описаний нет).
    /// </summary>
    public static Command ListWithDescriptions<T>(
        GlobalOptions g, string description, string what, Func<Context, Page, int, Task<ListDto<T>>> load, Func<T, string[]> cells)
    {
        var paging = new Paging();
        var length = EntityDescriptionLength(what);
        return Leaf(g, "list", description, c =>
            {
                paging.AddTo(c);
                c.Options.Add(length);
            },
            async (parse, ctx) => ctx.Print(await paging.Load(parse, page => load(ctx, page, parse.GetValue(length))), cells));
    }

    /// <summary>Ячейки строки списка: короткий id (<see cref="ShortId"/>) и остальные колонки (см. <see cref="Table"/>); полный id — в <c>--json</c> и в <c>get</c>.</summary>
    public static string[] Row(Guid id, params string[] cells) => [ShortId.Of(id), .. cells];
}
