using System.Text.Json.Nodes;
using Tasker.Global;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Справочник <c>tasker manual</c> как команда: список тем, поиск, язык, <c>--json</c>, работа без рабочей области. Каталог страниц и то,
/// что команды в примерах существуют, проверяют модульные тесты Rust (<c>commands/manual.rs</c>, <c>app.rs</c>).
/// </summary>
public class ManualTests
{
    private static Task<CliResult> Run(params string[] args) => TestWorkspace.Invoke(args);

    [Fact]
    public async Task Without_arguments_the_list_of_topics_is_printed()
    {
        var result = await Run("manual");

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Err);
        var topics = (await Run("manual", "--json")).Json["topics"]!.AsArray();
        Assert.Equal(13, topics.Count);
        foreach (var topic in topics)
        {
            Assert.Contains(topic!["id"]!.GetValue<string>(), result.Out);
            Assert.Contains(topic["title"]!.GetValue<string>(), result.Out);
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
        using var _ = AppEnvironment.Override("TASKER_LANG", "fr");

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
        Assert.Equal(13, topics.Count);
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
}
