using System.CommandLine;
using Tasker.Cli;
using Xunit;

namespace Tasker.Tests;

/// <summary>Автодополнение справочника <c>manual</c>/<c>howto</c> (темы, <c>--lang</c>) и сторож: у аргументов команд есть источник значений.</summary>
[InProcess]
public class ManualCompletionTests
{
    // Тестовый каталог: ru и en переведены частично, de — язык, которого нет в сборке; тема only-en есть только на en.
    private static readonly ManualCatalog Catalog = new(
    [
        ("ru", "01-first-project.md", "# Первый проект\n\nО.\n"),
        ("ru", "02-tasks.md", "# Задачи\n\nО.\n"),
        ("en", "01-first-project.md", "# First project\n\nAbout.\n"),
        ("en", "03-only-en.md", "# Only English\n\nAbout.\n"),
        ("de", "02-tasks.md", "# Aufgaben\n\nUeber.\n")
    ]);

    private static async Task<string[]> Suggest(string text, ManualCatalog? catalog = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApp.Run([$"[suggest:{text.Length}]", text], output, error, null, null, new StringReader(""), false, catalog);
        Assert.Equal(0, code);
        Assert.Empty(error.ToString());
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Theory]
    [InlineData("manual ")]
    [InlineData("howto ")]
    public async Task Topics_of_the_embedded_manual_are_suggested(string line)
    {
        var topics = await Suggest(line);

        Assert.Equal(
            ["agent", "boards", "fields", "first-project", "install-daemon", "links", "locks", "migrate", "series", "statuses-types", "sync-cleanup", "tasks", "windows"],
            topics.Where(x => !x.StartsWith('-')).Order());
    }

    [Theory]
    [InlineData("manual fir", "first-project")]
    [InlineData("howto fir", "first-project")]
    [InlineData("manual FIRST", "first-project")]
    public async Task Topics_are_filtered_by_the_typed_prefix(string line, string expected) =>
        Assert.Equal([expected], await Suggest(line));

    [Fact]
    public async Task Languages_found_in_the_catalog_are_suggested_for_lang()
    {
        Assert.Equal(["de", "en", "ru"], await Suggest("manual --lang ", Catalog));
        Assert.Equal(["en"], await Suggest("howto --lang e", Catalog));
        Assert.Equal(["ru"], await Suggest("manual --lang r"));
    }

    [Fact]
    public async Task Topics_follow_the_language_that_is_already_typed()
    {
        // Набор тем задаёт язык по умолчанию (ru), темы только на других языках идут следом — на любом выбранном языке.
        Assert.Equal(["first-project", "only-en", "tasks"], await Suggest("manual ", Catalog));
        Assert.Equal(["first-project", "only-en", "tasks"], await Suggest("manual --lang de ", Catalog));
        Assert.Equal(["only-en"], await Suggest("manual --lang en on", Catalog));
        Assert.Equal(["only-en"], await Suggest("howto --lang en on", Catalog));
    }

    [Fact]
    public async Task Topics_and_languages_do_not_need_a_workspace()
    {
        // Рабочей области, которой нет, дополнение не открывает и не создаёт.
        var folder = Path.Combine(Path.GetTempPath(), "tasker-no-workspace-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(["first-project"], await Suggest($"manual -w \"{folder}\" fir"));
        Assert.Equal(["ru"], await Suggest($"manual --sqlite \"{folder}.db\" --lang r"));
        Assert.False(Directory.Exists(folder));
        Assert.False(File.Exists(folder + ".db"));
    }

    [Theory]
    [InlineData("manual --")]
    [InlineData("howto --")]
    [InlineData("completion --")]
    public async Task Workspace_options_are_not_offered_where_there_is_no_workspace(string line)
    {
        var options = await Suggest(line);

        Assert.DoesNotContain(options, x => x is "--workspace" or "-w" or "--sqlite" or "--project" or "-p");
        Assert.Contains("--json", options);
    }

    [Fact]
    public async Task Other_commands_keep_the_workspace_options()
    {
        Assert.Contains("--lang", await Suggest("manual --"));
        Assert.Contains("--workspace", await Suggest("task list --"));
        Assert.Contains("--workspace", await Suggest("sync --"));
        Assert.Contains("--project", await Suggest("project get --"));
    }

    [Fact]
    public async Task Workspace_options_typed_by_hand_are_still_accepted_by_manual()
    {
        var result = await TestWorkspace.Invoke(["manual", "-w", Path.GetTempPath(), "-p", "x", "tasks"]);

        Assert.Equal(0, result.Code);
    }

    /// <summary>
    /// Сторож: у каждого аргумента команды есть источник значений (<c>Suggests</c>), даже если подсказать нечего — тогда явная пометка
    /// <c>NoSuggestions()</c> («свободный текст, число, путь»). Новая команда не должна остаться без подсказок по недосмотру.
    /// </summary>
    [Fact]
    public void Every_argument_of_every_command_has_a_value_source_or_an_explicit_note()
    {
        var root = CliApp.BuildRoot(TextWriter.Null, TextWriter.Null);

        var without = Walk(root)
            .SelectMany(c => c.Arguments.Where(a => a.CompletionSources.Count == 0).Select(a => $"{CommandPath(c)} <{a.Name}>"))
            .ToArray();

        Assert.True(without.Length == 0,
            "Arguments without a completion source (add .Suggests(g, Sources.X), or .NoSuggestions() for free text): " + string.Join(", ", without));
    }

    private static IEnumerable<Command> Walk(Command command) => command.Subcommands.SelectMany(Walk).Prepend(command);

    private static string CommandPath(Command command)
    {
        var names = new List<string>();
        for (Command? c = command; c != null; c = c.Parents.OfType<Command>().FirstOrDefault())
            names.Insert(0, c.Name);
        return string.Join(' ', names.Skip(1));
    }
}
