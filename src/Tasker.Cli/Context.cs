using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tasker.Cli.Completion;
using Tasker.Core;
using Tasker.Core.Dto;
using Tasker.Core.Projects;
using Tasker.Daemon;
using Tasker.Daemon.Services;
using Tasker.Global;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Cli;

/// <summary>Параметры, общие для всех команд (можно указывать в любом месте строки).</summary>
/// <param name="input">Откуда читать ответы на вопросы; null — стандартный ввод.</param>
/// <param name="interactive">Можно ли задавать вопросы; null — если стандартный ввод не перенаправлен.</param>
/// <param name="terminal">Терминал вывода; null — настоящий, читается при каждом вызове (размер окна мог измениться).</param>
internal sealed class GlobalOptions(TextWriter output, TextWriter error, TextReader? input = null, bool? interactive = null, Terminal? terminal = null)
{
    public TextWriter Error => error;

    public Option<string?> Workspace { get; } = new("--workspace", "-w")
    {
        Description = "Folder with the workspace (data lives in <folder>/.tasker). Default: the current folder",
        Recursive = true
    };

    public Option<string?> Sqlite { get; } = new("--sqlite")
    {
        Description = "SQLite file with the workspace instead of a folder",
        Recursive = true
    };

    public Option<string?> Project { get; } = new("--project", "-p")
    {
        Description = "Project (id or name) for commands inside a project. Default: the TASKER_PROJECT variable, or the only project of the workspace",
        Recursive = true
    };

    public Option<bool> Json { get; } = new("--json")
    {
        Description = "Print the result as JSON",
        Recursive = true
    };

    public Option<bool> Quiet { get; } = new("--quiet", "-q")
    {
        Description = "Print less: lists skip the first line with the number of found items ('Found N'), sync prints nothing unless something needs attention",
        Recursive = true
    };

    public Option<bool> Truncate { get; } = new("--truncate", "--no-wrap")
    {
        Description = "Cut long lines of list output to the window width with an ellipsis instead of wrapping (default: on in a terminal and under watch - COLUMNS and LINES both set; off when redirected)",
        Recursive = true
    };

    public Option<bool> NoTruncate { get; } = new("--no-truncate")
    {
        Description = "Never cut lines of list output, even in a terminal",
        Recursive = true
    };

    public Option<string?> Width { get; } = new Option<string?>("--width")
    {
        Description = "Line width for --truncate in characters (minimum 20); 0 or 'auto' - the terminal width, 'off' - never cut. Default: the TASKER_WIDTH variable (off or 0 - never cut), else the terminal",
        Recursive = true
    }.NoSuggestions();

    public void AddTo(Command command)
    {
        command.Options.Add(Workspace);
        command.Options.Add(Sqlite);
        command.Options.Add(Project);
        command.Options.Add(Json);
        command.Options.Add(Quiet);
        command.Options.Add(Truncate);
        command.Options.Add(NoTruncate);
        command.Options.Add(Width);
        Project.Suggests(this, Sources.Projects);
    }

    /// <summary>Открывает рабочую область, выполняет команду и переводит ожидаемые ошибки в сообщение и код 1.</summary>
    public Task<int> Run(ParseResult parse, CancellationToken ct, Func<Context, Task> action) =>
        Guard(async () =>
        {
            var settings = new WorkspaceSettings(UserPath.Expand(parse.GetValue(Workspace)), UserPath.Expand(parse.GetValue(Sqlite)));
            if (settings.Folder != null && settings.SqliteFile != null)
                throw new CliException("Use either --workspace or --sqlite, not both");

            PerfTrace.Mark("invoke");
            await using var session = await Session.Open(settings, ct);
            PerfTrace.Mark("session-open");
            var context = new Context(session, output, error, parse.GetValue(Json), parse.GetValue(Project) ?? AppEnvironment.Get("TASKER_PROJECT"), ct)
            {
                MaxWidth = MaxWidth(parse),
                IsQuiet = parse.GetValue(Quiet),
                Input = input ?? Console.In,
                IsInteractive = interactive ?? !Console.IsInputRedirected
            };
            await action(context);
            PerfTrace.Mark("action");
            return context.ExitCode;
        });

    /// <summary>Команда без рабочей области (настройки, демон): только вывод и обработка ошибок.</summary>
    public Task<int> RunPlain(ParseResult parse, CancellationToken ct, Func<Context, Task> action) =>
        Guard(async () =>
        {
            var context = new Context(null, output, error, parse.GetValue(Json), null, ct)
            {
                MaxWidth = MaxWidth(parse),
                IsQuiet = parse.GetValue(Quiet),
                Input = input ?? Console.In,
                IsInteractive = interactive ?? !Console.IsInputRedirected
            };
            await action(context);
            return context.ExitCode;
        });

    /// <summary>Предел ширины строк списков (см. <see cref="Terminal.Limit"/>); в <c>--json</c> текст не печатается, обрезать нечего.</summary>
    private int? MaxWidth(ParseResult parse) =>
        (terminal ?? Terminal.Current(output)).Limit(parse.GetValue(Truncate), parse.GetValue(NoTruncate), parse.GetValue(Width));

    private Task<int> Guard(Func<Task> action) => Guard(async () =>
    {
        await action();
        return 0;
    });

    private async Task<int> Guard(Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception e) when (e is CliException or TaskerValidationException or TaskerNotFoundException or TaskerConflictException or TaskerForbiddenException
            or SettingsException or DaemonStartException or ServiceException or PlatformNotSupportedException)
        {
            error.WriteLine(e switch
            {
                TaskerNotFoundException => $"Not found: {e.Message}",
                TaskerConflictException { Code: ConflictCode.InUse } => $"In use: {e.Message}",
                TaskerConflictException { Code: ConflictCode.Locked } => $"Locked: {e.Message}",
                TaskerConflictException { Code: ConflictCode.UnsupportedFormat } => $"Unsupported format: {e.Message}",
                TaskerConflictException => $"Modified by someone else: {e.Message}",
                _ => $"Error: {e.Message}"
            });
            return 1;
        }
    }
}

/// <summary>Открытая рабочая область и вывод результата одной команды.</summary>
internal sealed class Context(Session? session, TextWriter output, TextWriter error, bool json, string? projectReference, CancellationToken ct)
{
    public CancellationToken Ct { get; } = ct;

    /// <summary>Предел ширины строк таблиц в знаках; null — не обрезать (<see cref="Terminal.Limit"/>).</summary>
    public int? MaxWidth { get; init; }

    /// <summary>Строки таблицы (<see cref="Table"/>) с обрезкой по ширине окна, если она включена.</summary>
    public string[] Table(IReadOnlyList<string[]> rows, string indent = "") => Cli.Table.Format(rows, indent, MaxWidth);

    /// <summary>Режим <c>-q</c>/<c>--quiet</c>: в списках нет первой строки с количеством найденного.</summary>
    public bool IsQuiet { get; init; }

    /// <summary>Режим <c>--json</c>: результат — JSON, а не текст.</summary>
    public bool IsJson => json;

    /// <summary>Откуда читать ответы на вопросы.</summary>
    public TextReader Input { get; init; } = TextReader.Null;

    /// <summary>Можно ли ждать ответа: нет, если ввод перенаправлен (скрипт, CI).</summary>
    public bool IsInteractive { get; init; }

    /// <summary>В режиме <c>--json</c> stdout занят результатом: пояснения и вопросы идут в stderr.</summary>
    public TextWriter Prompts => json ? error : output;

    /// <summary>Код выхода команды, если она завершилась без ошибки, но результат не «успех» (например, демон не запущен).</summary>
    public int ExitCode { get; set; }

    public T Get<T>() where T : notnull =>
        (session ?? throw new InvalidOperationException("The command has no workspace")).Get<T>();

    /// <summary>Сервис, которого может не быть в этой области (например, миграция файлов — только у папок); null — нет.</summary>
    public T? GetOptional<T>() where T : class =>
        (session ?? throw new InvalidOperationException("The command has no workspace")).GetOptional<T>();

    public TextWriter Out => output;

    public TextWriter Error => error;

    /// <summary>
    /// Проект из <c>--project</c> (id или имя). Параметр можно не указывать, если в рабочей области ровно один проект:
    /// команда работает с ним. Проектов нет или несколько, а параметра нет — ошибка с подсказкой.
    /// </summary>
    public async Task<Guid> ProjectId()
    {
        if (!string.IsNullOrWhiteSpace(projectReference))
            return (await FindProject(projectReference)).Id;

        // Достаточно двух: нужно знать только «один ли».
        var projects = await Get<ProjectService>().GetRange(new Page(0, 2), Ct);
        return projects.TotalCount switch
        {
            1 => projects.Data[0].Id,
            0 => throw new CliException("There are no projects in this workspace: create one with 'tasker project create <name>'"),
            var count => throw new CliException(
                $"Project is required: this workspace has {count} projects, use --project <id or name> or set TASKER_PROJECT")
        };
    }

    /// <summary>Проект из <c>--project</c>; null — не указан (команда работает со всеми проектами).</summary>
    public async Task<Project?> OptionalProject() =>
        string.IsNullOrWhiteSpace(projectReference) ? null : await FindProject(projectReference);

    /// <summary>Проект по id или имени.</summary>
    public async Task<Project> FindProject(string reference) =>
        Refs.FindItem(await AllProjects(), reference, x => x.Id, x => x.Name, "project");

    /// <summary>Все проекты рабочей области.</summary>
    public async Task<List<Project>> AllProjects()
    {
        var projects = Get<ProjectService>();
        var all = new List<Project>();
        while (true)
        {
            var page = await projects.GetRange(new Page(all.Count, Page.MaxLimit), Ct);
            all.AddRange(page.Data);
            if (page.Data.Length == 0 || all.Count >= page.TotalCount)
                break;
        }

        return all;
    }

    /// <summary>Значение в виде JSON, как его печатает <c>--json</c>: чтобы дополнить результат полями.</summary>
    public System.Text.Json.Nodes.JsonNode? ToNode(object value) => JsonSerializer.SerializeToNode(value, TaskerJson.Options);

    /// <summary>Результат создания или изменения одной сущности.</summary>
    public void Print(object value, string text) => output.WriteLine(json ? JsonSerializer.Serialize(value, TaskerJson.Options) : text);

    /// <summary>
    /// Страница списка: в JSON — как в API, в тексте — первая строка с количеством найденного (<see cref="FoundLine"/>; нет с <c>-q</c>),
    /// затем по строке на элемент, колонки выровнены по странице (<see cref="Table"/>).
    /// </summary>
    public void Print<T>(ListDto<T> list, Func<T, string[]> cells)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(list, TaskerJson.Options));
            return;
        }

        if (!IsQuiet)
            output.WriteLine(FoundLine(list.TotalCount, list.Offset, list.Data.Length));
        foreach (var line in Table(list.Data.Select(cells).ToArray()))
            output.WriteLine(line);
    }

    /// <summary>
    /// Страница списка деревом (<c>task list</c>): первая строка с количеством найденного (<see cref="FoundTreeLine"/>; нет с <c>-q</c>), затем строки деревьев —
    /// дочерние задачи с отступом в первой колонке (<see cref="TreeIndent"/> на уровень), повтор задачи под вторым родителем — с пометкой <see cref="RepeatMark"/>
    /// после ссылки. Колонки выровнены по странице вместе с отступом (<see cref="Table"/>).
    /// </summary>
    public void PrintTree(Core.Tasks.TaskTreeList list, Func<Core.Tasks.TaskTreeItem, string[]> cells)
    {
        if (!IsQuiet)
            output.WriteLine(FoundTreeLine(list.TotalCount, list.TopLevelCount, list.Offset, list.Data.Count(x => x.Depth == 0)));
        foreach (var line in Table(list.Data.Select(x =>
                 {
                     var row = cells(x);
                     row[0] = new string(' ', TreeIndent * x.Depth) + row[0] + (x.Repeated ? RepeatMark : "");
                     return row;
                 }).ToArray()))
            output.WriteLine(line);
    }

    /// <summary>Отступ одного уровня вложенности в списке деревом: четыре пробела (один «таб»).</summary>
    public const int TreeIndent = 4;

    /// <summary>Пометка повтора: задача входит в несколько эпиков и показана снова под вторым (см. <see cref="Core.Tasks.TaskTreeItem.Repeated"/>).</summary>
    public const string RepeatMark = " (+)";

    /// <summary>Список без страниц (всё показано): <paramref name="value"/> — результат в <c>--json</c>, в тексте — как <see cref="Print{T}(ListDto{T}, Func{T, string[]})"/>.</summary>
    public void PrintAll<T>(object value, IReadOnlyList<T> items, Func<T, string[]> cells)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(value, TaskerJson.Options));
            return;
        }

        Print(new ListDto<T> { TotalCount = items.Count, Offset = 0, Limit = items.Count, Data = [.. items] }, cells);
    }

    /// <summary>
    /// Первая строка списка: «Found 3» — показано всё; «Found 106, shown 1-2 (use --offset/--limit)» — страница; «Found 0» — ничего не нашлось.
    /// Число — всего после фильтров. Строка не обрезается по ширине и не входит в таблицу.
    /// </summary>
    internal static string FoundLine(int total, int offset, int shown) => shown >= total
        ? $"Found {total}"
        : $"Found {total}, shown {(shown == 0 ? "none" : $"{offset + 1}-{offset + shown}")} (use --offset/--limit)";

    /// <summary>
    /// Первая строка списка деревом. «Found 8» — показано всё. Число — уникальные задачи (задача под двумя эпиками считается один раз), а не строки.
    /// Постранично листается верхний уровень, поддеревья идут целиком: «Found 106, shown top-level 1-20 of 57 (use --offset/--limit)» — 57 задач без родителя
    /// в результате, показаны первые 20 с их дочерними. Нет вложенности (верхний уровень — все найденные) — обычная форма <see cref="FoundLine"/>.
    /// </summary>
    internal static string FoundTreeLine(int total, int topLevel, int offset, int shownTop)
    {
        if (topLevel == total)
            return FoundLine(total, offset, shownTop);
        return offset == 0 && shownTop >= topLevel
            ? $"Found {total}"
            : $"Found {total}, shown top-level {(shownTop == 0 ? "none" : $"{offset + 1}-{offset + shownTop}")} of {topLevel} (use --offset/--limit)";
    }
}

/// <summary>
/// Ссылки на сущности в командах: полный Guid, имя, затем короткий id — префикс Guid из 8 и более шестнадцатеричных символов
/// (<see cref="ShortId"/>; регистр не важен). Точное имя сильнее префикса: сущность с именем из 8 hex-символов находится по имени,
/// а префикс подходит, только если имени нет ни у кого.
/// </summary>
internal static class Refs
{
    public static Guid Find<T>(IEnumerable<T> items, string reference, Func<T, Guid> id, Func<T, string> name, string kind) where T : class =>
        id(FindItem(items, reference, id, name, kind));

    public static T FindItem<T>(IEnumerable<T> items, string reference, Func<T, Guid> id, Func<T, string> name, string kind, bool listAvailable = false) where T : class
    {
        var all = items.ToArray();
        if (Guid.TryParse(reference, out var guid) && all.FirstOrDefault(x => id(x) == guid) is { } byId)
            return byId;

        var byName = all.Where(x => string.Equals(name(x), reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byName.Length > 1)
            throw new CliException($"Several {kind}s are named '{reference}', use the id: {string.Join(", ", byName.Select(x => id(x)))}");
        if (byName.Length == 1)
            return byName[0];

        return ByIdPrefix(all, reference, id, kind)
            ?? throw new CliException($"No {kind} '{reference}'" + (listAvailable ? $". Available: {string.Join(", ", all.Select(name).Order(StringComparer.OrdinalIgnoreCase))}" : ""));
    }

    /// <summary>Сущность по короткому id; null — текст не префикс id или такой сущности нет.</summary>
    /// <exception cref="CliException">Префиксу подходят несколько сущностей: в сообщении их полные id.</exception>
    public static T? ByIdPrefix<T>(IEnumerable<T> items, string reference, Func<T, Guid> id, string kind) where T : class
    {
        if (ShortId.TryKey(reference) is not { } key)
            return null;

        var found = items.Where(x => ShortId.Matches(id(x), key)).ToArray();
        return found.Length switch
        {
            0 => null,
            1 => found[0],
            _ => throw new CliException($"Several {kind}s start with '{reference.Trim()}', use a longer prefix or the full id: {string.Join(", ", found.Select(x => id(x)))}")
        };
    }

    public static Guid[] FindAll<T>(IEnumerable<T> items, IEnumerable<string> references, Func<T, Guid> id, Func<T, string> name, string kind) where T : class
    {
        var all = items.ToArray();
        return references.Select(x => Find(all, x, id, name, kind)).ToArray();
    }

    /// <summary>
    /// Значения одного параметра-фильтра («ИЛИ»): каждое — id, имя или короткий id; повторы убираются. Неизвестное значение — ошибка с перечнем допустимых.
    /// </summary>
    public static Guid[] FindDistinct<T>(IEnumerable<T> items, IEnumerable<string> references, Func<T, Guid> id, Func<T, string> name, string kind) where T : class
    {
        var all = items.ToArray();
        return references.Select(x => id(FindItem(all, x, id, name, kind, listAvailable: true))).Distinct().ToArray();
    }
}
