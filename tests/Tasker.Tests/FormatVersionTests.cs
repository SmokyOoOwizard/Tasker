using Autofac;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core.Dto;
using Tasker.Core.Workspace;
using Tasker.Global;
using Tasker.Storage.Files;
using Tasker.Storage.Files.Storages;
using Xunit;

namespace Tasker.Tests;

/// <summary>Версия формата файлов .tasker: <c>formatVersion</c> первой строкой, чтение старых и новых файлов, <c>tasker migrate</c>.</summary>
public class FormatVersionTests
{
    // ---- сама версия ----

    [Theory]
    [InlineData("id: 1\nname: x\n", 0)]
    [InlineData("formatVersion: 1\nid: 1\n", 1)]
    [InlineData("formatVersion: 0\nid: 1\n", 0)]
    [InlineData("formatVersion:   7  \nid: 1\n", 7)]
    [InlineData("id: 1\r\nformatVersion: 3\r\nname: x\r\n", 3)]
    // Строка внутри блока (с отступом) — это текст описания, а не поле файла.
    [InlineData("id: 1\ndescription: |\n  formatVersion: 9\n  text\n", 0)]
    public void The_version_is_read_from_the_top_level_field_only(string text, int expected) =>
        Assert.Equal(expected, FormatVersions.Of(text, "x.yaml"));

    [Theory]
    [InlineData("formatVersion: abc\nid: 1\n")]
    [InlineData("formatVersion: -1\nid: 1\n")]
    [InlineData("formatVersion:\nid: 1\n")]
    public void An_unreadable_version_is_reported_as_an_unsupported_format(string text)
    {
        var error = Assert.Throws<UnsupportedFormatException>(() => FormatVersions.Of(text, "tasks/x.yaml"));
        Assert.Contains("x.yaml", error.Message);
        Assert.Equal(Tasker.Core.ConflictCode.UnsupportedFormat, error.Code);
    }

    [Fact]
    public void An_old_file_is_upgraded_in_memory_by_adding_the_version_and_nothing_else()
    {
        var old = "id: 1\nname: Тест\ndescription: |\n  Строка один.\n  Строка два.\n";

        var upgraded = FormatVersions.Upgrade(old, "x.yaml");

        Assert.Equal($"formatVersion: {FormatVersions.Current}\n" + old, upgraded);
        Assert.Equal(upgraded, FormatVersions.Upgrade(upgraded, "x.yaml")); // текущий формат не меняется
    }

    [Fact]
    public void Upgrading_keeps_windows_line_endings_and_replaces_an_existing_old_version()
    {
        Assert.Equal($"formatVersion: {FormatVersions.Current}\r\nid: 1\r\n", FormatVersions.Upgrade("id: 1\r\n", "x.yaml"));
        Assert.Equal($"formatVersion: {FormatVersions.Current}\nid: 1\n", FormatVersions.Upgrade("formatVersion: 0\nid: 1\n", "x.yaml"));
        Assert.Equal("formatVersion: 5\nid: 1\n", FormatVersions.SetVersion("formatVersion: 1\nid: 1\n", 5));
    }

    [Fact]
    public void A_newer_file_is_not_upgraded_but_refused_with_a_hint_to_update()
    {
        var error = Assert.Throws<UnsupportedFormatException>(() => FormatVersions.Upgrade($"formatVersion: {FormatVersions.Current + 1}\nid: 1\n", "tasks/abc.yaml"));

        Assert.Contains($"format version {FormatVersions.Current + 1} is newer than this Tasker supports ({FormatVersions.Current})", error.Message);
        Assert.Contains("update Tasker", error.Message);
        Assert.Contains("abc.yaml", error.Message);
    }

    // ---- файлы, записанные Tasker ----

    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    /// <summary>Файлы всех видов сущностей: проект, пользователь, статус, набор, тип, доска, серия, задача (со связью), тип связи.</summary>
    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("user", "create", "Ivan"));
        await Ok(ws.Run("status", "create", "Todo"));
        await Ok(ws.Run("status", "create", "Done"));
        await Ok(ws.Run("status-set", "create", "Flow", "--status", "Todo", "Done"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(ws.Run("board", "create", "Main", "--status-set", "Flow", "--column", "All=Todo,Done"));
        await Ok(ws.Run("series", "create", "Tasks", "--prefix", "TSK"));
        await Ok(ws.Run("task", "create", "First", "--type", "Bug", "--series", "TSK", "-d", "Строка один.\nformatVersion: 77\nСтрока три."));
        await Ok(ws.Run("task", "create", "Second", "--type", "Bug", "--series", "TSK"));
        await Ok(ws.Run("task", "link", "TSK-1", "blocks", "TSK-2")); // сохраняет и типы связей
    }

    private static string[] YamlFiles(TestWorkspace ws) =>
        Directory.GetFiles(Path.Combine(ws.Root, ".tasker"), "*.yaml", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}.cache{Path.DirectorySeparatorChar}"))
            .Order(StringComparer.Ordinal).ToArray();

    private static Dictionary<string, string> Snapshot(TestWorkspace ws) =>
        YamlFiles(ws).ToDictionary(x => x, File.ReadAllText);

    /// <summary>Как файлы выглядели до введения версии: без первой строки.</summary>
    private static void MakeLegacy(TestWorkspace ws)
    {
        foreach (var file in YamlFiles(ws))
        {
            var lines = File.ReadAllLines(file);
            Assert.Equal($"formatVersion: {FormatVersions.Current}", lines[0]);
            File.WriteAllText(file, string.Join('\n', lines.Skip(1)) + "\n");
        }
    }

    [InProcess]
    [Fact]
    public async Task Every_kind_of_file_is_written_with_the_format_version_as_the_first_line()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);

        var files = YamlFiles(ws);
        // project.yaml, users/, tasks/, statuses/, status-sets/, task-types/, boards/, series/, link-types/
        foreach (var kind in new[] { "project.yaml", "users", "tasks", "statuses", "status-sets", "task-types", "boards", "series", "link-types" })
            Assert.Contains(files, x => x.Contains(kind));
        foreach (var file in files)
            Assert.Equal($"formatVersion: {FormatVersions.Current}", File.ReadLines(file).First());
    }

    [InProcess]
    [Fact]
    public async Task Old_files_without_a_version_are_read_everywhere_and_reading_does_not_change_them()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        MakeLegacy(ws);
        var before = Snapshot(ws);

        Assert.Contains("First", (await Ok(ws.Run("task", "list"))).Out);
        Assert.Contains("is blocked by", (await Ok(ws.Run("task", "get", "TSK-2"))).Out);
        Assert.Contains("Строка три.", (await Ok(ws.Run("task", "get", "TSK-1"))).Out);
        Assert.Contains("Flow", (await Ok(ws.Run("status-set", "list"))).Out);
        Assert.Contains("Main", (await Ok(ws.Run("board", "list"))).Out);
        Assert.Contains("Ivan", (await Ok(ws.Run("user", "list"))).Out);

        Assert.Equal(before, Snapshot(ws)); // чтение файлы не трогает
    }

    [InProcess]
    [Fact]
    public async Task Writing_an_old_file_stamps_that_file_only_and_keeps_the_rest_of_its_text()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        MakeLegacy(ws);
        var before = Snapshot(ws);

        await Ok(ws.Run("task", "update", "TSK-2", "-d", "Second described"));

        var after = Snapshot(ws);
        var changed = after.Keys.Where(x => before[x] != after[x]).ToArray();
        Assert.Single(changed);
        Assert.Contains("tasks", changed[0]);
        Assert.StartsWith($"formatVersion: {FormatVersions.Current}\n", after[changed[0]]);
        Assert.Contains("description: Second described", after[changed[0]]);
        // Остальные файлы так и остались без версии.
        Assert.All(after.Where(x => x.Key != changed[0]), x => Assert.DoesNotContain($"formatVersion: {FormatVersions.Current}", x.Value));
    }

    [InProcess]
    [Fact]
    public async Task A_description_line_that_looks_like_the_version_does_not_confuse_anything()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);

        var task = YamlFiles(ws).Single(x => File.ReadAllText(x).Contains("title: First"));
        Assert.Equal($"formatVersion: {FormatVersions.Current}", File.ReadLines(task).First());
        Assert.Contains("formatVersion: 77", File.ReadAllText(task)); // внутри блока описания
        Assert.Contains("formatVersion: 77", (await Ok(ws.Run("task", "get", "TSK-1"))).Out);
        Assert.Contains("Nothing to migrate", (await Ok(ws.Run("migrate"))).Out);
    }

    // ---- файлы более нового формата ----

    [InProcess]
    [Fact]
    public async Task A_newer_file_is_not_listed_is_reported_as_a_problem_and_cannot_be_read_or_written()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        var file = YamlFiles(ws).Single(x => File.ReadAllText(x).Contains("title: Second"));
        var original = File.ReadAllText(file);
        var id = original.Split('\n').First(x => x.StartsWith("id: ")).Substring("id: ".Length).Trim();
        var newer = FormatVersions.Current + 1;
        File.WriteAllText(file, original.Replace($"formatVersion: {FormatVersions.Current}", $"formatVersion: {newer}"));

        // В списках его нет.
        var list = (await Ok(ws.Run("task", "list"))).Out;
        Assert.Contains("First", list);
        Assert.DoesNotContain("Second", list);

        // Прямое чтение и запись — понятная ошибка, файл не тронут.
        var get = await ws.Run("task", "get", id);
        Assert.Equal(1, get.Code);
        Assert.StartsWith("Unsupported format: ", get.Err);
        Assert.Contains("update Tasker", get.Err);
        var update = await ws.Run("task", "update", id, "--title", "X");
        Assert.Equal(1, update.Code);
        Assert.StartsWith("Unsupported format: ", update.Err);
        Assert.Contains($"formatVersion: {newer}", File.ReadAllText(file));

        // В списке проблем рабочей области.
        var builder = new ContainerBuilder();
        builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterModule(new FileStorageModule(ws.Root));
        await using var container = builder.Build();
        var problems = await container.Resolve<IWorkspaceIndex>().GetProblems(new Page(0, 50));
        var problem = Assert.Single(problems.Data);
        Assert.EndsWith($"{id[..8]}.yaml", problem.Path);
        Assert.Contains("newer than this Tasker supports", problem.Error);
    }

    // ---- tasker migrate ----

    [Fact]
    public async Task Migrate_check_dry_run_and_the_real_run_work_step_by_step()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files", asProcess: true);
        await Seed(ws);
        var files = YamlFiles(ws).Length;
        var current = Snapshot(ws);
        MakeLegacy(ws);
        var legacy = Snapshot(ws);

        // Все в текущем формате — ничего не нужно (повторный запуск после миграции — то же).
        var check = await ws.Run("migrate", "--check");
        Assert.Equal(2, check.Code);
        Assert.Contains($"{files} file(s) would be migrated to format {FormatVersions.Current}", check.Out);
        Assert.Equal(legacy, Snapshot(ws)); // --check ничего не пишет

        var dry = await Ok(ws.Run("migrate", "--dry-run"));
        Assert.StartsWith("Nothing is written (dry run):", dry.Out);
        Assert.Contains("would migrate", dry.Out);
        Assert.Contains("... and ", dry.Out); // показано не больше десяти файлов
        Assert.Equal(legacy, Snapshot(ws));

        var real = await Ok(ws.Run("migrate"));
        Assert.Contains($"Migrated {files} file(s) to format {FormatVersions.Current}", real.Out);
        // Каждый файл: добавилась ровно одна первая строка, остальное — байт в байт.
        var migrated = Snapshot(ws);
        Assert.Equal(legacy.Keys, migrated.Keys);
        Assert.All(migrated, x => Assert.Equal($"formatVersion: {FormatVersions.Current}\n" + legacy[x.Key], x.Value));
        Assert.Equal(current.Values.Order(), migrated.Values.Order()); // и это те же файлы, что записывает сам Tasker

        var again = await Ok(ws.Run("migrate"));
        Assert.Contains($"Nothing to migrate: all {files} file(s) are in the current format ({FormatVersions.Current})", again.Out);
        Assert.Equal(0, (await ws.Run("migrate", "--check")).Code);
        Assert.Equal(migrated, Snapshot(ws));

        // Данные после миграции прежние, а индекс видит их сразу.
        Assert.Contains("is blocked by", (await Ok(ws.Run("task", "get", "TSK-2"))).Out);
        Assert.Contains("Строка три.", (await Ok(ws.Run("task", "get", "TSK-1"))).Out);
    }

    [Fact]
    public async Task Migrate_json_describes_the_files_and_newer_or_broken_files_are_left_alone()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files", asProcess: true);
        await Seed(ws);
        MakeLegacy(ws);
        var files = YamlFiles(ws);
        var newer = files.First(x => x.Contains("statuses"));
        var conflicted = files.First(x => x.Contains("status-sets"));
        File.WriteAllText(newer, $"formatVersion: {FormatVersions.Current + 1}\n" + File.ReadAllText(newer));
        File.WriteAllText(conflicted, "<<<<<<< HEAD\nname: A\n=======\nname: B\n>>>>>>> branch\n");
        var newerText = File.ReadAllText(newer);
        var conflictedText = File.ReadAllText(conflicted);

        var result = await ws.Run("migrate", "--json");

        Assert.Equal(1, result.Code); // что-то осталось как было
        Assert.Contains("some files were not migrated", result.Err);
        var json = result.Json;
        Assert.Equal(FormatVersions.Current, json["currentFormat"]!.GetValue<int>());
        Assert.Equal(files.Length, json["scanned"]!.GetValue<int>());
        Assert.Equal(files.Length - 2, json["migrated"]!.AsArray().Count);
        Assert.Equal(FormatVersions.Current + 1, json["newer"]![0]!["version"]!.GetValue<int>());
        Assert.Contains("statuses/", json["newer"]![0]!["path"]!.GetValue<string>());
        Assert.Contains("merge conflict", json["unreadable"]![0]!["reason"]!.GetValue<string>());
        Assert.True(json["needsAttention"]!.GetValue<bool>());

        Assert.Equal(newerText, File.ReadAllText(newer));
        Assert.Equal(conflictedText, File.ReadAllText(conflicted));
        // Остальные мигрированы, и --check теперь видит только эти два.
        Assert.Equal(2, (await ws.Run("migrate", "--check")).Code);
    }

    [Fact]
    public async Task Migrate_does_nothing_for_a_database_workspace()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("sqlite", asProcess: true);
        await Ok(ws.Run("project", "create", "Demo"));

        var result = await Ok(ws.Run("migrate"));

        Assert.Contains("database", result.Out);
        Assert.False((await Ok(ws.Run("migrate", "--json"))).Json["applicable"]!.GetValue<bool>());
        Assert.Equal(0, (await ws.Run("migrate", "--check")).Code);
    }

    [Fact]
    public async Task Migrate_on_an_empty_folder_has_nothing_to_do()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files", asProcess: true);

        var result = await Ok(ws.Run("migrate"));

        Assert.Contains("Nothing to migrate: all 0 file(s)", result.Out);
    }
}
