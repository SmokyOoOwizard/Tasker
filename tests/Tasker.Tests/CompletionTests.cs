using System.Diagnostics;
using Tasker.Cli.Completion;
using Tasker.Storage.Files.Workspaces;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Автодополнение по Tab: директива <c>[suggest]</c> (команды, параметры, значения из хранилища области) и скрипты оболочек
/// (<c>tasker completion zsh|bash</c>).
/// </summary>
public class CompletionTests
{
    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    /// <summary>Что предложит Tab после <paramref name="line"/> (строка без имени программы, курсор в конце) — в области <paramref name="ws"/>.</summary>
    private static async Task<string[]> Suggest(TestWorkspace ws, string line)
    {
        var location = string.Join(' ', ws.Location.Select(x => $"\"{x}\""));
        return await Suggest($"{location} {line}");
    }

    private static async Task<string[]> Suggest(string text)
    {
        var result = await TestWorkspace.Invoke([$"[suggest:{text.Length}]", text]);
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Err);
        return result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task<string[]> Values(string text) => (await Suggest(text)).Where(x => !x.StartsWith('-')).ToArray();

    /// <summary>Только значения: без параметров (<c>--status</c>) и общих ключей.</summary>
    private static async Task<string[]> Values(TestWorkspace ws, string line) =>
        (await Suggest(ws, line)).Where(x => !x.StartsWith('-')).ToArray();

    /// <summary>
    /// Проект «Мой проект», статусы «В работе», «Готово», «Лишний» (набор «Основной» — первые два), тип «Фича» на этом наборе, серия TSK и
    /// 11 задач TSK-1…TSK-11, перечисление «Приоритет» (с пробелом в значении «Очень срочно»), поля «Приоритет», «Готово» и «StoryPoints»,
    /// доска «Основная доска» с колонками «Все» и «Сделано».
    /// </summary>
    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Мой проект"));
        foreach (var status in new[] { "В работе", "Готово", "Лишний" })
            await Ok(ws.Run("status", "create", status));
        await Ok(ws.Run("status-set", "create", "Основной", "--status", "В работе", "Готово"));
        await Ok(ws.Run("task-type", "create", "Фича", "--status-set", "Основной"));
        await Ok(ws.Run("series", "create", "Задачи", "--prefix", "TSK"));
        for (var i = 1; i <= 11; i++)
            await Ok(ws.Run("task", "create", $"Задача {i}", "--type", "Фича", "--series", "TSK"));
        await Ok(ws.Run("enum", "create", "Приоритет", "--value", "Высокий", "Низкий", "Очень срочно"));
        await Ok(ws.Run("field", "create", "Приоритет", "--type", "enum", "--enum", "Приоритет"));
        await Ok(ws.Run("field", "create", "Готово", "--type", "bool"));
        await Ok(ws.Run("field", "create", "StoryPoints", "--type", "int"));
        await Ok(ws.Run("board", "create", "Основная доска", "--status-set", "Основной", "--column", "Все=В работе", "--column", "Сделано=Готово"));
    }

    // ---- команды и параметры (без рабочей области) ----

    [Theory]
    [InlineData("pro", "project")]
    [InlineData("task cr", "create")]
    [InlineData("task-type up", "update")]
    [InlineData("mcp workspace re", "remove")]
    public async Task Commands_and_nested_commands_are_completed(string line, string expected)
    {
        var suggestions = await Suggest(line);

        Assert.Contains(expected, suggestions);
    }

    [Theory]
    [InlineData("task create X --st", "--status")]
    [InlineData("task list --fi", "--field")]
    [InlineData("--js", "--json")]
    [InlineData("task list --qui", "--quiet")]
    [InlineData("status list --qui", "--quiet")]
    [InlineData("sync --qui", "--quiet")]
    [InlineData("task list --trun", "--truncate")]
    [InlineData("task list --no-t", "--no-truncate")]
    [InlineData("board show X --wid", "--width")]
    [InlineData("project get --wo", "--workspace")]
    public async Task Options_and_global_options_are_completed(string line, string expected)
    {
        var suggestions = await Suggest(line);

        Assert.Contains(expected, suggestions);
    }

    [Theory]
    [InlineData("task list --status Бэклог --al", "--all")]
    [InlineData("task list --status Бэклог -", "--all")]
    [InlineData("task list --status Бэклог --status В --al", "--all")]
    [InlineData("task list --type Баг --al", "--all")]
    [InlineData("task list --series TSK --al", "--all")]
    [InlineData("task list --field Priority=High --al", "--all")]
    [InlineData("task list --status A B --al", "--all")]
    [InlineData("task list --status Бэклог --stat", "--status")]
    [InlineData("task list --status Бэклог --ty", "--type")]
    [InlineData("task list --field Priority=High --fi", "--field")]
    [InlineData("task list --sort status --al", "--all")]
    [InlineData("enum create X --value A B --js", "--json")]
    [InlineData("status-set create X --status A B --js", "--json")]
    public async Task Options_are_suggested_after_a_multi_value_option(string line, string expected)
    {
        Assert.Contains(expected, await Suggest(line));
    }

    /// <summary>Сторож: после любого многозначного параметра любой команды «--&lt;параметр&gt; значение --» предлагает параметры этой команды.</summary>
    [Fact]
    public async Task Options_follow_every_multi_value_option_of_every_command()
    {
        var root = Tasker.Cli.CliApp.BuildRoot(TextWriter.Null, TextWriter.Null);
        var cases = new List<(string Path, string Option, string[] Others)>();

        void Walk(System.CommandLine.Command command, string path)
        {
            foreach (var many in command.Options.Where(x => !x.Hidden && (x.AllowMultipleArgumentsPerToken || x.Arity.MaximumNumberOfValues > 1)))
            {
                var others = command.Options.Where(x => !x.Hidden && x != many && x.Name.StartsWith("--")).Select(x => x.Name).ToArray();
                cases.Add((path, many.Name, others));
            }

            foreach (var sub in command.Subcommands)
                Walk(sub, $"{path} {sub.Name}".Trim());
        }

        Walk(root, "");
        Assert.NotEmpty(cases);

        foreach (var (path, option, others) in cases)
        {
            var suggestions = await Suggest($"{path} {option} value --");
            Assert.True(suggestions.Contains(option), $"'{path} {option} value --' does not repeat {option}: {string.Join(' ', suggestions)}");
            foreach (var other in others)
                Assert.True(suggestions.Contains(other), $"'{path} {option} value --' does not suggest {other}: {string.Join(' ', suggestions)}");
        }
    }

    [Fact]
    public async Task The_width_value_has_nothing_to_suggest()
    {
        Assert.Empty(await Suggest("task list --width "));
    }

    [Fact]
    public async Task Short_global_options_are_offered_and_the_unix_noise_is_not()
    {
        var suggestions = await Suggest("task list ");

        Assert.Contains("-w", suggestions);
        Assert.Contains("-p", suggestions);
        Assert.Contains("-q", suggestions);
        Assert.DoesNotContain(suggestions, x => x.StartsWith('/') || x == "-?");
    }

    [Fact]
    public async Task The_directive_takes_the_cursor_position_into_account()
    {
        // Курсор после «tas»: хвост строки не мешает.
        var result = await TestWorkspace.Invoke(["[suggest:3]", "tas project list"]);

        Assert.Equal(["task", "task-type"], result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    // ---- значения из хранилища ----

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Names_of_entities_are_suggested_in_both_storages(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["Мой проект"], await Values(ws, "project get "));
        Assert.Equal(["В работе", "Готово", "Лишний"], await Values(ws, "status get "));
        Assert.Equal(["Основной"], await Values(ws, "status-set get "));
        Assert.Equal(["Фича"], await Values(ws, "task-type get "));
        Assert.Equal(["TSK"], await Values(ws, "series get "));
        Assert.Equal(["Основная доска"], await Values(ws, "board get "));
        Assert.Equal(["StoryPoints", "Готово", "Приоритет"], await Values(ws, "field get "));
        Assert.Equal(["Приоритет"], await Values(ws, "enum get "));
        Assert.Contains("Blocks", await Values(ws, "link-type get "));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Values_are_filtered_by_the_typed_prefix_and_the_case_is_ignored(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["Готово"], await Values(ws, "status get го"));
        Assert.Equal(["В работе"], await Values(ws, "status get В"));
        Assert.Empty(await Values(ws, "status get Нет"));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Names_with_spaces_are_matched_when_typed_with_quotes_or_backslashes(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // Оболочка присылает слово как набрано: в кавычках или с «\ ».
        Assert.Equal(["В работе"], await Values(ws, "status get \"В р"));
        Assert.Equal(["В работе"], await Values(ws, "status get 'В р"));
        Assert.Equal(["В работе"], await Values(ws, "status get В\\ р"));
        Assert.Equal(["Основная доска"], await Values(ws, "board get Основная\\ д"));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Statuses_of_a_task_follow_the_type_that_is_already_typed(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["В работе", "Готово", "Лишний"], await Values(ws, "task list --status "));
        // «Лишний» не входит в набор типа «Фича».
        Assert.Equal(["В работе", "Готово"], await Values(ws, "task create X --type Фича --status "));
        Assert.Equal(["Фича"], await Values(ws, "task create X --type "));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Board_columns_are_suggested_after_the_board_argument(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["Основная доска"], await Values(ws, "board tasks "));
        Assert.Equal(["Все", "Сделано"], await Values(ws, "board tasks \"Основная доска\" "));
        Assert.Equal(["Сделано"], await Values(ws, "board tasks Основная\\ доска Сд"));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Task_references_are_suggested_by_the_typed_number(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["TSK-"], await Values(ws, "task get T"));
        Assert.Equal(["TSK-1", "TSK-10", "TSK-11"], await Values(ws, "task get TSK-1"));
        Assert.Equal(["TSK-2"], await Values(ws, "task get TSK-2"));
        Assert.Equal(Enumerable.Range(1, 11).Select(x => $"TSK-{x}"), await Values(ws, "task get TSK-"));
        Assert.Empty(await Values(ws, "task get TSK-x"));
        // Вторая ссылка в «task link» — тоже задача; между ними — фраза связи.
        Assert.Equal(["TSK-11"], await Values(ws, "task link TSK-1 blocks TSK-11"));
        Assert.Contains("is blocked by", await Values(ws, "task link TSK-1 "));
        Assert.Contains("blocks", await Values(ws, "task link TSK-1 bl"));
        Assert.Equal(["TSK-1", "TSK-10", "TSK-11"], await Values(ws, "series add-task TSK TSK-1"));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Fields_and_their_values_are_suggested_for_field_options(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["StoryPoints=", "Готово=", "Приоритет="], await Values(ws, "task list --field "));
        Assert.Equal(["Приоритет="], await Values(ws, "task list --field Пр"));
        // Значения перечисления — после «=»; у логического поля — true и false; у числового подсказывать нечего.
        Assert.Equal(["Приоритет=Высокий", "Приоритет=Низкий", "Приоритет=Очень срочно"], await Values(ws, "task list --field Приоритет="));
        Assert.Equal(["Приоритет=Очень срочно"], await Values(ws, "task create X --type Фича --field Приоритет=Оч"));
        Assert.Equal(["Приоритет=Очень срочно"], await Values(ws, "task update TSK-1 --field Приоритет=Очень\\ "));
        Assert.Equal(["Готово=false", "Готово=true"], await Values(ws, "board show Основная\\ доска --field Готово="));
        Assert.Empty(await Values(ws, "task list --field \"StoryPoints="));
        Assert.Equal(["StoryPoints="], await Values(ws, "task list --field Story"));
        // Операторы TSK-84: «!=» подсказывает значения, как «=»; у сравнений значений нет; после «:» — слова наличия.
        Assert.Equal(["Приоритет!=Высокий", "Приоритет!=Низкий", "Приоритет!=Очень срочно"], await Values(ws, "task list --field Приоритет!="));
        Assert.Empty(await Values(ws, "task list --field StoryPoints>="));
        Assert.Empty(await Values(ws, "task list --field \"StoryPoints<"));
        Assert.Equal(["Приоритет:attached", "Приоритет:detached", "Приоритет:set", "Приоритет:unset"], await Values(ws, "task list --field Приоритет:"));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Column_specs_and_column_conditions_are_suggested_for_board_options(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        // --column: название придумывает человек; после «=» и «,» — статусы проекта.
        Assert.Empty(await Values(ws, "board create X --status-set Основной --column Вс"));
        Assert.Equal(["Все=В работе", "Все=Готово", "Все=Лишний"], await Values(ws, "board create X --status-set Основной --column Все="));
        Assert.Equal(["Все=В работе,Готово"], await Values(ws, "board create X --status-set Основной --column \"Все=В работе,Го"));
        // --column-filter: до «:» — названия уже набранных колонок, дальше — условие, как у --field.
        Assert.Equal(["Все:", "Готово:"], await Values(ws, "board create X --status-set Основной --column Все=Готово --column Готово=Лишний --column-filter "));
        Assert.Equal(["Все:Приоритет="], await Values(ws, "board create X --status-set Основной --column Все=Готово --column-filter Все:Пр"));
        Assert.Equal(["Все:Приоритет=Высокий", "Все:Приоритет=Низкий", "Все:Приоритет=Очень срочно"],
            await Values(ws, "board create X --status-set Основной --column Все=Готово --column-filter Все:Приоритет="));
        Assert.Equal(["Все:Приоритет:attached", "Все:Приоритет:detached", "Все:Приоритет:set", "Все:Приоритет:unset"],
            await Values(ws, "board create X --status-set Основной --column Все=Готово --column-filter Все:Приоритет:"));
        Assert.Equal(["Все:Готово=false", "Все:Готово=true"], await Values(ws, "board create X --status-set Основной --column Все=Готово --column-filter Все:Готово="));
        Assert.Empty(await Values(ws, "board create X --status-set Основной --column Все=Готово --column-filter Все:StoryPoints>="));
        // У update без --column — колонки самой доски.
        Assert.Equal(["Все:", "Сделано:"], await Values(ws, "board update Основная\\ доска --column-filter "));
        Assert.Equal(["Сделано:Приоритет="], await Values(ws, "board update Основная\\ доска --column-filter Сделано:Пр"));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Enums_fields_and_enum_values_are_suggested_where_commands_take_them(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Equal(["Приоритет"], await Values(ws, "field create X --type enum --enum "));
        Assert.Contains("enum", await Values(ws, "field create X --type "));
        Assert.Equal(["clear", "keep-first"], await Values(ws, "field update Готово --several "));
        Assert.Equal(["Высокий", "Низкий", "Очень срочно"], await Values(ws, "enum update Приоритет --remove-value "));
        Assert.Equal(["Высокий=", "Низкий=", "Очень срочно="], await Values(ws, "enum update Приоритет --rename-value "));
        Assert.Equal(["StoryPoints", "Готово", "Приоритет"], await Values(ws, "task-type update Фича --add-field "));
        Assert.Equal(["Приоритет:required"], await Values(ws, "task-type update Фича --field Приоритет:"));
        Assert.Equal(["Основной"], await Values(ws, "task-type create X --status-set "));
        Assert.Equal(["Основной"], await Values(ws, "board create X --status-set "));
        Assert.Equal(["В работе", "Готово", "Лишний"], await Values(ws, "status-set update Основной --status "));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Lock_entities_depend_on_the_kind_that_is_already_typed(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Contains("taskType", await Values(ws, "lock acquire "));
        Assert.Equal(["Фича"], await Values(ws, "lock acquire taskType "));
        Assert.Equal(["TSK-2"], await Values(ws, "lock release task TSK-2"));
        Assert.Equal(["Основная доска"], await Values(ws, "lock show board "));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task The_project_comes_from_the_option_or_the_only_project(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(ws.Run("project", "create", "Второй"));

        // Проектов два, выбранного нет — значения проекта не угадать: пусто (а не ошибка).
        Assert.Empty(await Values(ws, "status get "));
        Assert.Equal(["В работе", "Готово", "Лишний"], await Values(ws, "status get --project \"Мой проект\" "));
        Assert.Equal(["В работе", "Готово", "Лишний"], await Values(ws, "-p Мой\\ проект status get "));
        Assert.Empty(await Values(ws, "-p Второй status get "));
        // Сами проекты не зависят от выбора.
        Assert.Equal(["Второй", "Мой проект"], await Values(ws, "task list --project "));
        Assert.Equal(["Второй"], await Values(ws, "project get Вт"));
    }

    [Fact]
    public async Task Mcp_workspaces_come_from_the_global_settings_without_any_workspace()
    {
        using var home = new IsolatedHome();
        var folder = Directory.CreateDirectory(Path.Combine(home.Path, "ws one")).FullName;
        var (entry, _) = await home.Store.AddWorkspace(WorkspaceLocation.Files(folder));

        Assert.Equal([entry.Path],(await Suggest("mcp workspace remove ")).Where(x => !x.StartsWith('-')));
        Assert.Empty((await Suggest("mcp workspace remove /nothing")).Where(x => !x.StartsWith('-')));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task At_most_fifty_values_are_suggested(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Ok(ws.Run("project", "create", "Много"));
        for (var i = 1; i <= 60; i++)
            await Ok(ws.Run("status", "create", $"S{i:00}"));

        var values = await Values(ws, "status get ");

        Assert.Equal(50, values.Length);
        Assert.Equal("S01", values[0]);
        Assert.Equal(50, (await Values(ws, "status get S")).Length);
        Assert.Equal(["S60"], await Values(ws, "status get S6"));
    }

    // ---- тихо и ничего не создаёт ----

    [Fact]
    public async Task No_workspace_means_no_values_and_no_messages_and_nothing_is_created()
    {
        using var folder = TestWorkspace.Create("files");
        var missing = Path.Combine(folder.Root, "missing");
        var emptyFolder = Directory.CreateDirectory(Path.Combine(folder.Root, "empty")).FullName;
        var sqlite = Path.Combine(folder.Root, "absent.db");

        Assert.Empty(await Values($"--workspace \"{missing}\" status get "));
        Assert.Empty(await Values($"--workspace \"{emptyFolder}\" status get "));
        Assert.Empty(await Values($"--sqlite \"{sqlite}\" status get "));
        Assert.Empty(await Values($"--sqlite \"{sqlite}\" --workspace \"{emptyFolder}\" status get "));

        Assert.False(Directory.Exists(missing));
        Assert.False(Directory.Exists(Path.Combine(emptyFolder, ".tasker")), "Tab must not create the workspace");
        Assert.False(File.Exists(sqlite), "Tab must not create the SQLite file");
    }

    [Fact]
    public async Task An_unreadable_workspace_is_an_empty_answer_not_an_error()
    {
        using var ws = TestWorkspace.Create("files");
        await Ok(ws.Run("project", "create", "P"));
        // Не SQLite и не рабочая папка: файл вместо базы.
        var notDatabase = Path.Combine(ws.Root, "not-a-database.db");
        await File.WriteAllTextAsync(notDatabase, "this is not a SQLite file at all, just text to break the reader");

        Assert.Empty(await Values($"--sqlite \"{notDatabase}\" status get "));
        Assert.Empty(await Values($"--workspace \"{notDatabase}\" status get "));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Suggesting_does_not_change_the_data(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var before = (await Ok(ws.Run("task", "list", "--all", "--json"))).Out;

        await Values(ws, "task get TSK-");
        await Values(ws, "board tasks ");
        await Values(ws, "task list --field Приоритет=");

        Assert.Equal(before, (await Ok(ws.Run("task", "list", "--all", "--json"))).Out);
    }

    [Fact]
    public async Task The_executable_answers_from_a_separate_process_in_the_current_folder()
    {
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);

        // Без -w: рабочая область — текущая папка процесса.
        var line = "task get TSK-1";
        var result = await TaskerProcess.RunIn(ws.Root, $"[suggest:{line.Length}]", line);
        Assert.Equal(0, result.Code);
        Assert.Equal(["TSK-1", "TSK-10", "TSK-11"], result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty(result.Err);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Hierarchy_options_and_their_values_are_suggested(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Contains("--flat", await Suggest(ws, "task list --"));
        Assert.Contains("--parent", await Suggest(ws, "task create X --type Фича --"));
        Assert.Contains("--parent-type", await Suggest(ws, "task create X --type Фича --"));
        foreach (var option in new[] { "--add-parent", "--remove-parent", "--parent-type" })
            Assert.Contains(option, await Suggest(ws, "task update TSK-1 --"));
        Assert.Contains("--hierarchical", await Suggest(ws, "link-type create X --outward a --"));
        Assert.Contains("--hierarchical", await Suggest(ws, "link-type update Blocks --"));

        // Родители — ссылки на задачи, тип — иерархические типы проекта, признак — true/false.
        Assert.Equal(["TSK-"], await Values(ws, "task create X --type Фича --parent T"));
        Assert.Equal(["TSK-2"], await Values(ws, "task update TSK-1 --add-parent TSK-2"));
        Assert.Equal(["TSK-3"], await Values(ws, "task update TSK-1 --remove-parent TSK-3"));
        Assert.Equal(["Parent/Child"], await Values(ws, "task update TSK-1 --add-parent TSK-2 --parent-type "));
        // true/false предлагает сама библиотека разбора команд (регистр её).
        Assert.Contains("true", await Values(ws, "link-type create X --outward a --hierarchical "), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("false", await Values(ws, "link-type update Blocks --hierarchical "), StringComparer.OrdinalIgnoreCase);
    }

    // ---- разбор слов, набранных в оболочке ----

    [Theory]
    [InlineData("task get ", "task get ")]
    [InlineData("task get  TSK-1", "task get TSK-1")]
    [InlineData("status get 'В работе' ", "status get \"В работе\" ")]
    [InlineData("status get В\\ р", "status get \"В р")]
    [InlineData("status get \"В р", "status get \"В р")]
    [InlineData("status get 'it''s'", "status get its")]
    [InlineData("x \"\" y", "x \"\" y")]
    [InlineData("", "")]
    public void Shell_words_are_rewritten_for_the_parser(string typed, string expected)
    {
        Assert.Equal(expected, ShellWords.Normalize(typed));
    }

    [Theory]
    [InlineData("task get TSK-1", "task get ", "TSK-1")]
    [InlineData("task get ", "task get ", "")]
    [InlineData("status get \"В р", "status get ", "В р")]
    [InlineData("status get \"В работе\"", "status get ", "В работе")]
    public void The_last_word_is_split_off_without_quotes(string normalized, string preceding, string word)
    {
        Assert.Equal((preceding, word), ShellWords.SplitLast(normalized));
    }

    // ---- команда completion и скрипты ----

    [Theory]
    [InlineData("zsh", "#compdef tasker")]
    [InlineData("bash", "complete -o default -F _tasker tasker")]
    public async Task The_completion_command_prints_the_script_that_calls_the_directive(string shell, string marker)
    {
        var result = await TestWorkspace.Invoke(["completion", shell]);

        Assert.Equal(0, result.Code);
        Assert.Contains(marker, result.Out);
        Assert.Contains("[suggest:", result.Out);
        Assert.Empty(result.Err);
    }

    [Fact]
    public async Task An_unknown_shell_is_rejected_with_the_list_of_supported_ones()
    {
        var result = await TestWorkspace.Invoke(["completion", "fish"]);

        Assert.NotEqual(0, result.Code);
        Assert.Contains("zsh", result.Err);
        Assert.Contains("bash", result.Err);
    }

    [Theory]
    [InlineData("zsh", "-n")]
    [InlineData("bash", "-n")]
    public async Task The_scripts_are_valid_shell_code(string shell, string flag)
    {
        if (!HasShell(shell))
            return;

        var script = (await Ok(TestWorkspace.Invoke(["completion", shell]))).Out;
        var path = Path.Combine(Path.GetTempPath(), $"tasker-completion-{Guid.NewGuid():N}.{shell}");
        await File.WriteAllTextAsync(path, script);
        try
        {
            var check = await Shell(shell, [flag, path]);
            Assert.True(check.Code == 0, check.Err);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Подставной <c>tasker</c>: отвечает на директиву готовыми строками — так проверяется только работа скрипта с ними.</summary>
    private sealed class FakeTasker : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tasker-fake-" + Guid.NewGuid().ToString("N"))).FullName;

        public FakeTasker(string[] answer, string? record = null)
        {
            var body = "#!/bin/sh\n" + (record == null ? "" : $"printf '%s\\n' \"$1|$2\" >> '{record}'\n")
                + string.Join("", answer.Select(x => $"printf '%s\\n' '{x.Replace("'", "'\\''")}'\n"));
            var tasker = Path.Combine(Directory, "tasker");
            File.WriteAllText(tasker, body);
            File.SetUnixFileMode(tasker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private static bool HasShell(string shell) => File.Exists("/bin/" + shell) || File.Exists("/usr/bin/" + shell) || File.Exists("/usr/local/bin/" + shell);

    private static async Task<CliResult> Shell(string shell, string[] args, string? path = null)
    {
        var info = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args)
            info.ArgumentList.Add(argument);
        if (path != null)
            info.Environment["PATH"] = path + ":" + Environment.GetEnvironmentVariable("PATH");

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await output, await error);
    }

    /// <summary>Вызывает функцию дополнения bash так, как это делает readline: строка и позиция курсора в COMP_*; печатает COMPREPLY по одному в строке.</summary>
    private static async Task<string[]> BashComplete(string line, params string[] answer)
    {
        using var fake = new FakeTasker(answer);
        var script = (await Ok(TestWorkspace.Invoke(["completion", "bash"]))).Out;
        var driver = $$"""
            eval "$1"
            COMP_LINE=$2
            COMP_POINT=${#COMP_LINE}
            COMP_WORDBREAKS=$' \t\n"\'><=;|&(:'
            read -r -a COMP_WORDS <<< "$COMP_LINE"
            if [[ $COMP_LINE == *' ' ]]; then COMP_WORDS+=(''); fi
            COMP_CWORD=$(( ${#COMP_WORDS[@]} - 1 ))
            _tasker
            if (( ${#COMPREPLY[@]} )); then printf '%s\n' "${COMPREPLY[@]}"; fi
            """;
        var result = await Shell("bash", ["-c", driver, "driver", script, line], fake.Directory);
        Assert.True(result.Code == 0, result.Err);
        Assert.Empty(result.Err);
        return result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task Bash_passes_the_line_without_the_program_name_and_the_cursor_to_the_directive()
    {
        using var fake = new FakeTasker(["project"]);
        var record = Path.Combine(fake.Directory, "calls");
        File.WriteAllText(Path.Combine(fake.Directory, "tasker"),
            $"#!/bin/sh\nprintf '%s\\n' \"$1|$2\" >> '{record}'\nprintf 'project\\n'\n");
        var script = (await Ok(TestWorkspace.Invoke(["completion", "bash"]))).Out;
        var driver = """
            eval "$1"
            COMP_LINE='tasker task  cr'
            COMP_POINT=${#COMP_LINE}
            COMP_WORDS=(tasker task cr)
            COMP_CWORD=2
            COMP_WORDBREAKS=$' \t\n"\'><=;|&(:'
            _tasker
            """;

        var result = await Shell("bash", ["-c", driver, "driver", script], fake.Directory);

        Assert.True(result.Code == 0, result.Err);
        Assert.Equal("[suggest:8]|task  cr\n", await File.ReadAllTextAsync(record));
    }

    [Fact]
    public async Task Bash_filters_by_the_typed_word_and_escapes_spaces_and_cyrillic_is_kept()
    {
        var replies = await BashComplete("tasker status get В", "В работе", "Готово", "--version", "Весна");

        Assert.Equal(["В\\ работе", "Весна"], replies);
    }

    [Fact]
    public async Task Bash_escapes_shell_specials_and_respects_the_quote_the_word_was_opened_with()
    {
        Assert.Equal(["a\\ \\$b\\(1\\)\\;\\&\\\\"], await BashComplete("tasker x a", "a $b(1);&\\"));
        Assert.Equal(["В работе"], await BashComplete("tasker x \"В р", "В работе"));
        Assert.Equal(["a\\\"b\\$"], await BashComplete("tasker x \"a", "a\"b$"));
        Assert.Equal(["a'\\''b"], await BashComplete("tasker x 'a", "a'b"));
        Assert.Equal(["В\\ работе"], await BashComplete("tasker x В\\ р", "В работе"));
        Assert.Equal(["\\~tilde"], await BashComplete("tasker x ", "~tilde"));
    }

    [Fact]
    public async Task Bash_cuts_the_part_before_the_word_break_that_readline_keeps()
    {
        // «=» — разделитель слов readline: после «Приоритет=» заменяется только то, что за ним.
        Assert.Equal(["Высокий", "Очень\\ срочно"], await BashComplete("tasker task list --field Приоритет=", "Приоритет=Высокий", "Приоритет=Очень срочно"));
        Assert.Equal(["Очень\\ срочно"], await BashComplete("tasker task list --field Приоритет=Оч", "Приоритет=Очень срочно"));
        // В кавычках разделителей нет: заменяется всё слово после кавычки.
        Assert.Equal(["Приоритет=Очень срочно"], await BashComplete("tasker task list --field \"Приор", "Приоритет=Очень срочно"));
    }

    [Fact]
    public async Task Bash_gives_nothing_when_tasker_has_no_suggestions()
    {
        Assert.Empty(await BashComplete("tasker status get ", []));
    }

    /// <summary>Вызывает функцию дополнения zsh: <c>words</c>/<c>CURRENT</c> как у zsh, а <c>compadd</c> и <c>_files</c> печатают свои вызовы.</summary>
    private static async Task<string[]> ZshComplete(string[] words, params string[] answer)
    {
        using var fake = new FakeTasker(answer);
        var script = (await Ok(TestWorkspace.Invoke(["completion", "zsh"]))).Out;
        var driver = """
            compdef() { :; }
            compadd() { print -r -- "compadd $*"; }
            _files() { print -r -- "_files"; }
            eval "$1"
            shift
            words=("$@")
            CURRENT=$#words
            _tasker
            """;
        var result = await Shell("zsh", ["-f", "-c", driver, "driver", script, .. words], fake.Directory);
        Assert.True(result.Code == 0, result.Err);
        Assert.Empty(result.Err);
        return result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task Zsh_hands_the_candidates_to_compadd_and_keeps_names_with_spaces_whole()
    {
        if (!HasShell("zsh"))
            return;

        Assert.Equal(["compadd -- В работе Готово"], await ZshComplete(["tasker", "status", "get", ""], "В работе", "Готово"));
    }

    [Fact]
    public async Task Zsh_adds_no_space_after_names_that_continue_in_the_same_word()
    {
        if (!HasShell("zsh"))
            return;

        Assert.Equal(["compadd -- project", "compadd -S  -- Приоритет= TSK-"],
            await ZshComplete(["tasker", "x", ""], "project", "Приоритет=", "TSK-"));
    }

    [Fact]
    public async Task Zsh_falls_back_to_files_when_tasker_has_nothing_to_suggest()
    {
        if (!HasShell("zsh"))
            return;

        Assert.Equal(["_files"], await ZshComplete(["tasker", "task", "list", "-w", ""], []));
    }

    [Fact]
    public async Task Zsh_sends_the_words_up_to_the_current_one_as_typed()
    {
        if (!HasShell("zsh"))
            return;

        using var fake = new FakeTasker(["x"]);
        var record = Path.Combine(fake.Directory, "calls");
        File.WriteAllText(Path.Combine(fake.Directory, "tasker"), $"#!/bin/sh\nprintf '%s\\n' \"$1|$2\" >> '{record}'\nprintf 'x\\n'\n");
        var script = (await Ok(TestWorkspace.Invoke(["completion", "zsh"]))).Out;
        var driver = """
            compdef() { :; }
            compadd() { :; }
            eval "$1"
            words=(tasker status get 'В\ р' ignored)
            CURRENT=4
            _tasker
            """;

        var result = await Shell("zsh", ["-f", "-c", driver, "driver", script], fake.Directory);

        Assert.True(result.Code == 0, result.Err);
        // Длина в знаках или в байтах — по локали; директива принимает и то и другое.
        var call = await File.ReadAllTextAsync(record);
        Assert.StartsWith("[suggest:", call);
        Assert.EndsWith("]|status get В\\ р\n", call);
    }
}
