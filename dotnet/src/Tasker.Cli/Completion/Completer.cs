using System.CommandLine;
using System.CommandLine.Completions;
using System.CommandLine.Parsing;
using Tasker.Global;

namespace Tasker.Cli.Completion;

/// <summary>Откуда брать варианты: <paramref name="Get"/> получает открытую область (если <paramref name="NeedsWorkspace"/>) и слово под курсором.</summary>
internal sealed record ValueSource(Func<Lookup, Task<IEnumerable<string>>> Get, bool NeedsWorkspace = true);

/// <summary>Что известно источнику: область, разобранная строка до курсора и слово, которое дописывают.</summary>
internal sealed class Lookup(Context? context, ParseResult parse, string word)
{
    public ParseResult Parse => parse;

    /// <summary>Слово под курсором (без кавычек): то, что набрано, а не разобрано.</summary>
    public string Word => word;

    public Context Ctx => context ?? throw new InvalidOperationException("The source works without a workspace");

    public CancellationToken Ct => Ctx.Ct;

    public T Get<T>() where T : notnull => Ctx.Get<T>();

    public Task<Guid> ProjectId() => Ctx.ProjectId();

    /// <summary>Уже набранное значение параметра; null — не набрано (или набрано с ошибкой: строка ещё не дописана).</summary>
    public string? Value<T>(Option<T> option) => Completer.Safe(() => parse.GetResult(option) is { Implicit: false } ? parse.GetValue(option) : default) as string;

    /// <summary>Уже набранные значения параметра, который можно повторять и перечислять (<c>--type A --type B C</c>); пусто — не набраны.</summary>
    public string[] Values(Option<string[]> option) =>
        Completer.Safe(() => parse.GetResult(option) is { Implicit: false } ? parse.GetValue(option) : null) ?? [];

    /// <summary>Уже набранное значение аргумента; null — не набрано.</summary>
    public string? Value<T>(Argument<T> argument) => Completer.Safe(() => parse.GetResult(argument) is { Implicit: false } ? parse.GetValue(argument) : default) as string;
}

/// <summary>
/// Источники значений для автодополнения: названия проектов, статусов, задач и т. п. из хранилища текущей рабочей области
/// (<c>-w</c>/<c>--sqlite</c> или текущая папка, проект — <c>-p</c>, <c>TASKER_PROJECT</c> или единственный).
/// <para>
/// Дополнение вызывают на каждое нажатие Tab, поэтому оно быстрое и тихое: область открывается только если она есть
/// (ничего не создаётся), демон не запускается, за <see cref="Timeout"/> не уложились или что-то пошло не так (нет области,
/// нет доступа, формат новее, проект не выбран) — пустой список без единого символа в stderr. Вариантов не больше <see cref="Limit"/>;
/// пустой список оболочка заменяет своим дополнением по файлам.
/// </para>
/// </summary>
internal static class Completer
{
    public const int Limit = 50;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1.5);

    /// <summary>Значения аргумента подсказывает <paramref name="source"/>.</summary>
    public static Argument<T> Suggests<T>(this Argument<T> argument, GlobalOptions g, ValueSource source)
    {
        argument.CompletionSources.Add(context => Complete(g, context, source, argument));
        return argument;
    }

    /// <summary>
    /// Явная пометка «подсказывать нечего»: значение придумывает человек (имя новой сущности, заголовок), число или путь — для пути
    /// оболочка предложит файлы сама. Без источника или такой пометки аргумент команды не проходит проверку в тестах.
    /// </summary>
    public static Argument<T> NoSuggestions<T>(this Argument<T> argument)
    {
        argument.CompletionSources.Add(_ => []);
        return argument;
    }

    /// <summary>Параметр-число или путь: подсказывать нечего (см. <see cref="NoSuggestions{T}(Argument{T})"/>).</summary>
    public static Option<T> NoSuggestions<T>(this Option<T> option)
    {
        option.CompletionSources.Add(_ => []);
        return option;
    }

    /// <summary>Значения параметра подсказывает <paramref name="source"/>.</summary>
    public static Option<T> Suggests<T>(this Option<T> option, GlobalOptions g, ValueSource source)
    {
        option.CompletionSources.Add(context => Complete(g, context, source, null));
        return option;
    }

    /// <summary>Читает разобранное значение; ошибка разбора (строка ещё не дописана) — null.</summary>
    internal static T? Safe<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static IEnumerable<CompletionItem> Complete(GlobalOptions g, CompletionContext context, ValueSource source, Argument? argument)
    {
        try
        {
            var (preceding, word) = Split(context);
            var parse = context.ParseResult;
            if (argument != null && !IsCurrent(parse, preceding, argument))
                return [];

            var work = Task.Run(() => Collect(g, parse, word, source));
            return work.Wait(Timeout) ? work.Result.Select(x => new CompletionItem(x)).ToArray() : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Строка до слова под курсором и само слово. Слово определяем сами, а не по <see cref="CompletionContext.WordToComplete"/>:
    /// у слов с пробелами («"Основная до») System.CommandLine отдаёт только кусок после последнего пробела.
    /// </summary>
    private static (string Preceding, string Word) Split(CompletionContext context) =>
        context is TextCompletionContext { CommandLineText: { } text } text2
            ? ShellWords.SplitLast(text[..Math.Min(text2.CursorPosition, text.Length)])
            : ("", ShellWords.Unquote(context.WordToComplete ?? ""));

    private static async Task<string[]> Collect(GlobalOptions g, ParseResult parse, string word, ValueSource source)
    {
        using var cts = new CancellationTokenSource(Timeout);

        Session? session = null;
        try
        {
            Context? ctx = null;
            if (source.NeedsWorkspace)
            {
                var settings = new WorkspaceSettings(Expand(Safe(() => parse.GetValue(g.Workspace))), Expand(Safe(() => parse.GetValue(g.Sqlite))));
                if (settings.Folder != null && settings.SqliteFile != null)
                    return [];

                session = await Session.Open(settings, cts.Token, forCompletion: true);
                ctx = new Context(session, TextWriter.Null, TextWriter.Null, false,
                    Safe(() => parse.GetValue(g.Project)) ?? AppEnvironment.Get("TASKER_PROJECT"), cts.Token);
            }

            var found = await source.Get(new Lookup(ctx, parse, word));
            return found
                .Where(x => x.Length > 0 && !x.Contains('\n') && x.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .OrderBy(x => x, NaturalOrder.Instance)
                .Take(Limit)
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
        finally
        {
            if (session != null)
                await session.DisposeAsync();
        }
    }

    // Оболочка отдаёт путь как набран: ~ не раскрыта (PowerShell и cmd.exe не раскрывают её и при обычном вызове).
    private static string? Expand(string? path) => UserPath.Expand(path);

    /// <summary>
    /// Дописывают ли именно этот аргумент. System.CommandLine спрашивает источники всех аргументов команды, а нужен тот, на
    /// чьей позиции стоит курсор: после уже набранных аргументов команды, и не значение параметра («--status &lt;Tab&gt;»).
    /// </summary>
    private static bool IsCurrent(ParseResult current, string preceding, Argument argument)
    {
        var parse = current.RootCommandResult.Command.Parse(preceding);
        var command = parse.CommandResult.Command;
        if (command.Arguments.IndexOf(argument) is not (>= 0 and var index))
            return false;

        // Последний набранный токен — параметр, которому ещё нужно значение: курсор на нём.
        if (parse.Tokens.LastOrDefault() is { Type: TokenType.Option } last
            && OptionsAround(command).FirstOrDefault(x => x.Name == last.Value || x.Aliases.Contains(last.Value)) is { Arity.MaximumNumberOfValues: > 0 })
            return false;

        var typed = parse.CommandResult.Children.OfType<ArgumentResult>()
            .Where(x => command.Arguments.Contains(x.Argument))
            .Sum(x => x.Tokens.Count);
        return index == typed;
    }

    // Параметры команды и всех родительских (общие параметры живут в корне).
    private static IEnumerable<Option> OptionsAround(Command command)
    {
        for (Command? c = command; c != null; c = c.Parents.OfType<Command>().FirstOrDefault())
            foreach (var option in c.Options)
                yield return option;
    }
}
