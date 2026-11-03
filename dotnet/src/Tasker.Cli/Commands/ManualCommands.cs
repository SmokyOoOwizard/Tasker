using System.CommandLine;
using System.Text;
using Tasker.Cli.Completion;
using Tasker.Global;

namespace Tasker.Cli.Commands;

/// <summary>
/// <c>tasker manual</c> (<c>howto</c>): микро-справочник «как сделать то-то». Страницы встроены в сборку (см. <see cref="ManualCatalog"/>),
/// рабочая область не нужна. Язык — <c>--lang</c>, переменная <c>TASKER_LANG</c>, иначе язык по умолчанию; нет страницы — откат на язык
/// по умолчанию с пометкой (в stderr и в <c>--json</c>).
/// </summary>
internal static class ManualCommands
{
    public const string LanguageVariable = "TASKER_LANG";

    public static Command Build(GlobalOptions g, ManualCatalog catalog)
    {
        var topic = new Argument<string[]>("topic")
        {
            Description = "Topic name from the list, or words to search for (in names, titles and descriptions, then in the text). Without it: the list of topics",
            Arity = ArgumentArity.ZeroOrMore
        };
        var lang = new Option<string?>("--lang")
        {
            Description = $"Language of the pages. Default: the {LanguageVariable} variable, else {ManualCatalog.DefaultLanguage}. "
                + $"A topic without a page in this language is shown in {ManualCatalog.DefaultLanguage}, with a note"
        };

        topic.Suggests(g, Sources.ManualTopics(catalog, lang));
        lang.Suggests(g, Sources.ManualLanguages(catalog));

        // Область у справочника не нужна: -w/-p/--sqlite ему нечего делать, Tab их не предлагает.
        var command = Kit.Plain(g, "manual", "Short how-to recipes with ready commands: the list of topics, 'manual <topic>' for a recipe, 'manual <word>' to search", c =>
        {
            c.Arguments.Add(topic);
            c.Options.Add(lang);
        }, (parse, ctx) =>
        {
            var requested = parse.GetValue(lang) ?? AppEnvironment.Get(LanguageVariable);
            var words = parse.GetValue(topic) ?? [];
            var language = catalog.Resolve(requested);
            if (!language.Found)
                ctx.Error.WriteLine($"Note: no pages in language '{requested!.Trim()}' (available: {string.Join(", ", catalog.Languages)}); showing '{language.Language}'");

            if (words.Length == 0)
            {
                Show(ctx, catalog, requested, null, catalog.Topics(requested));
                return Task.CompletedTask;
            }

            var query = string.Join(' ', words);
            var found = catalog.Find(requested, query);
            if (found.Count == 0)
                throw new CliException($"No topic matches '{query}': 'tasker manual' lists the topics");
            Show(ctx, catalog, requested, query, found);
            return Task.CompletedTask;
        }, usesWorkspace: false);
        command.Aliases.Add("howto");
        return command;
    }

    private static void Show(Context ctx, ManualCatalog catalog, string? requested, string? query, IReadOnlyList<ManualPage> pages)
    {
        var language = catalog.Resolve(requested).Language;
        var fallback = pages.Where(x => x.Language != language).Select(x => x.Id).ToArray();
        if (fallback.Length > 0)
            ctx.Error.WriteLine($"Note: no '{language}' page for {string.Join(", ", fallback)}; shown in {ManualCatalog.DefaultLanguage}");

        object Info(ManualPage x) => new { id = x.Id, title = x.Title, description = x.Description, language = x.Language, fallback = x.Language != language };

        // Одна тема из поиска (или точное имя) — сама страница; иначе оглавление или список найденного.
        if (query != null && pages.Count == 1)
        {
            var page = pages[0];
            ctx.Print(new
            {
                id = page.Id, title = page.Title, description = page.Description, language = page.Language,
                requestedLanguage = language, fallback = page.Language != language, content = page.Markdown
            }, page.RenderText());
            return;
        }

        var text = new StringBuilder(query == null
            ? $"Tasker manual ({language}): 'tasker manual <topic>' shows a recipe, 'tasker manual <word>' searches\n"
            : $"Several topics match '{query}':\n").AppendLine();
        var width = pages.Max(x => x.Id.Length);
        foreach (var page in pages)
            text.AppendLine($"  {page.Id.PadRight(width)}  {page.Title}{(page.Language == language ? "" : $" [{page.Language}]")}");
        ctx.Print(new { query, language, languages = catalog.Languages, topics = pages.Select(Info).ToArray() }, text.ToString().TrimEnd());
    }
}
