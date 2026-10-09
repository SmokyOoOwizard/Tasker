using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using Tasker.Cli;
using Tasker.Cli.Commands;
using Tasker.Global;
using Xunit;

namespace Tasker.Tests;

/// <summary>Справочник <c>tasker manual</c>: страницы, встроенные в сборку, поиск, язык, <c>--json</c>; и главное — команды в примерах существуют.</summary>
[InProcess]
public class ManualTests
{
    private static readonly ManualCatalog Catalog = ManualCatalog.Embedded;

    private static IEnumerable<ManualPage> AllPages() => Catalog.Languages.SelectMany(x => Catalog.Topics(x)).Distinct();

    public static IEnumerable<object[]> PageNames() => AllPages().Select(x => new object[] { $"{x.Language}/{x.Id}" });

    private static ManualPage PageNamed(string name) => AllPages().Single(x => $"{x.Language}/{x.Id}" == name);

    private static Task<CliResult> Run(params string[] args) => TestWorkspace.Invoke(args);

    [Fact]
    public void The_manual_has_the_planned_topics_in_russian()
    {
        var ids = Catalog.Topics("ru").Select(x => x.Id).ToArray();

        Assert.Equal(
            ["first-project", "tasks", "series", "links", "boards", "fields", "locks", "agent", "migrate", "sync-cleanup", "install-daemon", "statuses-types", "windows"],
            ids);
        Assert.Contains("ru", Catalog.Languages);
        Assert.All(Catalog.Topics("ru"), x => Assert.Equal("ru", x.Language));
    }

    [Theory, MemberData(nameof(PageNames))]
    public void Every_page_has_a_title_a_description_sections_and_commands(string name)
    {
        var page = PageNamed(name);

        Assert.False(string.IsNullOrWhiteSpace(page.Title));
        Assert.False(string.IsNullOrWhiteSpace(page.Description));
        Assert.True(page.Markdown.Split('\n').Count(x => x.StartsWith("## ", StringComparison.Ordinal)) >= 3, "A recipe has sections");
        Assert.NotEmpty(page.ShellBlocks());
    }

    [Fact]
    public void Russian_pages_follow_the_recipe_structure()
    {
        foreach (var page in Catalog.Topics("ru"))
            foreach (var section in new[] { "## Что нужно", "## Шаги", "## Пример", "## На что обратить внимание" })
                Assert.True(page.Markdown.Contains(section + "\n") || page.Markdown.Contains(section + "\r\n"), $"{page.Id}: no section '{section}'");
    }

    /// <summary>
    /// Справочник не должен устаревать: каждая команда <c>tasker …</c> из блоков <c>```bash</c> разбирается деревом команд —
    /// команда и параметры существуют, обязательные параметры указаны, команда выполняема (не группа). Команду не запускаем.
    /// </summary>
    [Theory, MemberData(nameof(PageNames))]
    public void Every_tasker_command_in_the_examples_exists(string name)
    {
        var root = CliApp.BuildRoot(TextWriter.Null, TextWriter.Null);
        var problems = new List<string>();
        var count = 0;

        foreach (var block in PageNamed(name).ShellBlocks())
            foreach (var command in ShellCommands(block))
            {
                var tokens = Tokenize(command);
                if (tokens.Count == 0 || tokens[0] != "tasker")
                    continue;

                count++;
                var parse = root.Parse(tokens.Skip(1).ToArray());
                if (parse.Errors.Count > 0)
                    problems.Add($"{command}: {string.Join("; ", parse.Errors.Select(x => x.Message))}");
                else if (parse.CommandResult.Command.Action == null)
                    problems.Add($"{command}: not an executable command (a group without a subcommand)");
            }

        Assert.True(problems.Count == 0, "Commands of the page do not match the command tree:\n" + string.Join('\n', problems));
        Assert.True(count > 0, "The page has no tasker commands in ```bash blocks");
    }

    [Fact]
    public void The_command_check_catches_a_renamed_command_and_option()
    {
        var root = CliApp.BuildRoot(TextWriter.Null, TextWriter.Null);

        Assert.NotEmpty(root.Parse(["task", "make", "x"]).Errors);
        Assert.NotEmpty(root.Parse(["task", "list", "--nonexistent"]).Errors);
        Assert.NotEmpty(root.Parse(["task", "create", "x"]).Errors); // обязательный --type пропущен
        Assert.Empty(root.Parse(["task", "create", "x", "--type", "Task"]).Errors);
    }

    [Fact]
    public async Task Without_arguments_the_list_of_topics_is_printed()
    {
        var result = await Run("manual");

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Err);
        foreach (var page in Catalog.Topics("ru"))
        {
            Assert.Contains(page.Id, result.Out);
            Assert.Contains(page.Title, result.Out);
        }
    }

    [Fact]
    public async Task Howto_is_an_alias()
    {
        var manual = await Run("manual");
        var howto = await Run("howto");
        var topic = await Run("howto", "series");

        Assert.Equal(manual.Out, howto.Out);
        Assert.Equal(0, topic.Code);
        Assert.Contains("Серии и ссылки TSK-N", topic.Out);
    }

    [Fact]
    public async Task A_topic_by_name_prints_the_recipe_as_plain_text()
    {
        var result = await Run("manual", "locks");

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Err);
        Assert.StartsWith("Блокировка на время правки\n====", result.Out.Replace("\r\n", "\n"));
        Assert.Contains("tasker lock acquire task TSK-1", result.Out);
        Assert.DoesNotContain("```", result.Out);
        Assert.DoesNotContain("## ", result.Out);
        Assert.Contains("    tasker lock acquire task TSK-1", result.Out); // код с отступом
    }

    [Fact]
    public async Task Search_by_a_part_of_the_title_shows_the_only_match()
    {
        var result = await Run("manual", "демон");

        Assert.Equal(0, result.Code);
        Assert.Contains("Установка и MCP-демон", result.Out);
        Assert.Contains("tasker mcp start", result.Out); // показана страница, а не список
    }

    [Fact]
    public async Task Search_with_several_matches_lists_them()
    {
        var result = await Run("manual", "задач");

        Assert.Equal(0, result.Code);
        Assert.Contains("Several topics match 'задач'", result.Out);
        Assert.Contains("tasks", result.Out);
        Assert.Contains("links", result.Out);
        Assert.DoesNotContain("====", result.Out);
    }

    [Fact]
    public async Task Search_takes_several_words_and_ignores_case()
    {
        var result = await Run("manual", "ПОЛЯ", "перечисления");

        Assert.Equal(0, result.Code);
        Assert.Contains("Поля задач и перечисления\n===", result.Out.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Search_falls_back_to_the_text_of_the_pages()
    {
        // «launchd» нет ни в названиях, ни в описаниях: только в тексте страницы про демон.
        var result = await Run("manual", "launchd");

        Assert.Equal(0, result.Code);
        Assert.Contains("Установка и MCP-демон", result.Out);
    }

    [Fact]
    public async Task An_unknown_topic_is_an_error_with_a_hint()
    {
        var result = await Run("manual", "несуществующая-тема");

        Assert.Equal(1, result.Code);
        Assert.Empty(result.Out);
        Assert.Contains("No topic matches 'несуществующая-тема'", result.Err);
        Assert.Contains("tasker manual", result.Err);
    }

    [Fact]
    public async Task A_language_without_pages_falls_back_to_russian_with_a_note()
    {
        var list = await Run("manual", "--lang", "de");
        var topic = await Run("manual", "tasks", "--lang", "de");

        Assert.Equal(0, list.Code);
        Assert.Contains("Note: no pages in language 'de' (available: ru); showing 'ru'", list.Err);
        Assert.Contains("Задачи и статусы", list.Out);
        Assert.Equal(0, topic.Code);
        Assert.Contains("Note: no pages in language 'de'", topic.Err);
        Assert.Contains("Задачи и статусы\n===", topic.Out.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task The_language_is_taken_from_the_variable_when_the_option_is_absent()
    {
        using var _ = AppEnvironment.Override(ManualCommands.LanguageVariable, "fr");

        var variable = await Run("manual");
        var option = await Run("manual", "--lang", "ru");

        Assert.Contains("language 'fr'", variable.Err);
        Assert.Empty(option.Err);
    }

    [Fact]
    public async Task The_russian_language_and_its_regional_variant_need_no_note()
    {
        var ru = await Run("manual", "--lang", "ru");
        var regional = await Run("manual", "--lang", "RU-ru");

        Assert.Empty(ru.Err);
        Assert.Empty(regional.Err);
        Assert.Equal(ru.Out, regional.Out);
    }

    [Fact]
    public async Task Json_of_the_list_has_the_topics()
    {
        var result = await Run("manual", "--json");

        Assert.Equal(0, result.Code);
        var json = result.Json;
        Assert.Equal("ru", json["language"]!.GetValue<string>());
        Assert.Contains("ru", json["languages"]!.AsArray().Select(x => x!.GetValue<string>()));
        var topics = json["topics"]!.AsArray();
        Assert.Equal(Catalog.Topics("ru").Count, topics.Count);
        Assert.Equal("first-project", topics[0]!["id"]!.GetValue<string>());
        Assert.Equal("Проект и первая доска", topics[0]!["title"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(topics[0]!["description"]!.GetValue<string>()));
        Assert.False(topics[0]!["fallback"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Json_of_a_topic_has_the_content()
    {
        var result = await Run("manual", "agent", "--json");

        Assert.Equal(0, result.Code);
        var json = result.Json;
        Assert.Equal("agent", json["id"]!.GetValue<string>());
        Assert.Equal("Подключить агента", json["title"]!.GetValue<string>());
        Assert.Equal("ru", json["language"]!.GetValue<string>());
        Assert.False(json["fallback"]!.GetValue<bool>());
        Assert.Contains("http://127.0.0.1:5719/mcp", json["content"]!.GetValue<string>());
        Assert.Contains("```", json["content"]!.GetValue<string>()); // Markdown как есть: агенту разметка не мешает
    }

    [Fact]
    public async Task Json_with_a_fallback_language_keeps_stdout_clean()
    {
        var result = await Run("manual", "--lang", "de", "--json");

        Assert.Contains("Note:", result.Err);
        Assert.Equal("ru", result.Json["language"]!.GetValue<string>());
    }

    [Fact]
    public async Task Manual_needs_no_workspace_and_works_from_an_empty_folder()
    {
        var empty = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var result = await Run("manual", "--workspace", empty);

            Assert.Equal(0, result.Code);
            Assert.False(Directory.Exists(Path.Combine(empty, ".tasker")));
        }
        finally
        {
            Directory.Delete(empty, true);
        }
    }

    // ---- Каталог: языки без переделки кода ----

    private static ManualCatalog Partial() => new(
    [
        ("ru", "01-alpha.md", "# Альфа\n\nПервая тема.\n\n## Шаги\n\n```bash\ntasker manual\n```\n"),
        ("ru", "02-beta.md", "# Бета\n\nВторая тема.\n"),
        ("en", "02-beta.md", "# Beta\n\nThe second topic.\n"),
        ("en", "03-gamma.md", "# Gamma\n\nOnly in English.\n")
    ]);

    [Fact]
    public void A_new_language_needs_only_new_pages()
    {
        var catalog = Partial();

        Assert.Equal(["en", "ru"], catalog.Languages);
        Assert.Equal(new ManualLanguage("en", true), catalog.Resolve("EN"));
        Assert.Equal(new ManualLanguage("en", true), catalog.Resolve("en-GB"));
        Assert.Equal(new ManualLanguage("ru", false), catalog.Resolve("de"));
        Assert.Equal(new ManualLanguage("ru", true), catalog.Resolve(null));
    }

    [Fact]
    public void Missing_translations_fall_back_to_the_default_language_page_by_page()
    {
        var topics = Partial().Topics("en");

        Assert.Equal(["alpha", "beta", "gamma"], topics.Select(x => x.Id));
        Assert.Equal(["ru", "en", "en"], topics.Select(x => x.Language));
        Assert.Equal(["Альфа", "Beta", "Gamma"], topics.Select(x => x.Title));
    }

    [Fact]
    public void Search_works_in_the_chosen_language()
    {
        var catalog = Partial();

        Assert.Equal(["beta"], catalog.Find("en", "second").Select(x => x.Id));
        Assert.Empty(catalog.Find("ru", "second"));
        Assert.Equal(["gamma"], catalog.Find("en", "GAMMA").Select(x => x.Id));
    }

    [Fact]
    public void Pages_without_a_title_or_description_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => new ManualCatalog([("ru", "01-a.md", "Без заголовка\n")]));
        Assert.Throws<InvalidDataException>(() => new ManualCatalog([("ru", "01-a.md", "# Заголовок\n\n## Сразу раздел\n")]));
        Assert.Throws<InvalidDataException>(() => new ManualCatalog([("ru", "01-a.md", "# А\n\nо\n"), ("ru", "02-a.md", "# А\n\nо\n")]));
    }

    // ---- Разбор примеров ----

    /// <summary>Команды блока: строки, склеенные по <c>\</c> в конце; пустые и комментарии пропускаются.</summary>
    private static IEnumerable<string> ShellCommands(string block)
    {
        var current = new StringBuilder();
        foreach (var raw in block.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.EndsWith('\\'))
            {
                current.Append(line[..^1]).Append(' ');
                continue;
            }

            current.Append(line);
            var command = current.ToString().Trim();
            current.Clear();
            if (command.Length > 0 && !command.StartsWith('#'))
                yield return command;
        }
    }

    /// <summary>Разбор строки как в оболочке: слова по пробелам, кавычки (' и ") склеивают слово, <c>#</c> в начале слова — комментарий до конца строки.</summary>
    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        var started = false;
        char? quote = null;
        foreach (var c in command)
        {
            if (quote != null)
            {
                if (c == quote)
                    quote = null;
                else
                    token.Append(c);
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                started = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (started)
                    tokens.Add(token.ToString());
                token.Clear();
                started = false;
            }
            else if (c == '#' && !started)
                return tokens;
            else
            {
                token.Append(c);
                started = true;
            }
        }

        Assert.Null(quote);
        if (started)
            tokens.Add(token.ToString());
        return tokens;
    }

    [Fact]
    public void The_tokenizer_understands_quotes_and_comments()
    {
        Assert.Equal(["tasker", "task", "create", "Два слова", "--type", "Task"], Tokenize("tasker task create \"Два слова\" --type Task # комментарий"));
        Assert.Equal(["tasker", "board", "create", "Main", "--column", "To do=Todo"], Tokenize("tasker board create Main --column 'To do=Todo'"));
        Assert.Equal(["a", ""], Tokenize("a \"\""));
    }
}
