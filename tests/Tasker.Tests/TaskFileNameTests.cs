using System.Text;
using Autofac;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core.Dto;
using Tasker.Core.Tasks;
using Tasker.Global;
using Tasker.Storage.Files;
using Tasker.Storage.Files.Storages;
using Xunit;

namespace Tasker.Tests;

/// <summary>Имя файла задачи по заголовку: правила, создание, переименование, старые файлы и <c>tasker migrate</c>.</summary>
public class TaskFileNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _project = Guid.NewGuid();
    private readonly List<IContainer> _containers = [];

    public TaskFileNameTests()
    {
        Directory.CreateDirectory(_root);
        Open();
    }

    public void Dispose()
    {
        foreach (var container in _containers)
            container.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private IContainer Open()
    {
        var builder = new ContainerBuilder();
        builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterModule(new FileStorageModule(_root));
        var container = builder.Build();
        _containers.Add(container);
        return container;
    }

    private ITaskStorage Tasks => _containers[0].Resolve<ITaskStorage>();
    private ProjectDirectory Project => new TaskerDirectory(_root).Project(_project);
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private TaskItem NewTask(string title, int minute = 0, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), ProjectId = _project, Title = title, TypeId = Guid.NewGuid(), StatusId = Guid.NewGuid(),
        CreatedAt = Start.AddMinutes(minute), UpdatedAt = Start.AddMinutes(minute), Version = ""
    };

    private string[] TaskFiles() =>
        Directory.Exists(Project.Tasks) ? Directory.GetFiles(Project.Tasks, "*.yaml").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()! : [];

    // ---- правила имени ----

    [Theory]
    [InlineData("Fix login", "fix-login")]
    [InlineData("  Fix   login!!  ", "fix-login")]
    [InlineData("Исправить вход", "исправить-вход")]
    [InlineData("Ёлка и ЁЖ", "ёлка-и-ёж")]
    [InlineData("CI/CD: pipeline (v2)", "ci-cd-pipeline-v2")]
    [InlineData("a<b>c:d\"e|f?g*h\\i/j", "a-b-c-d-e-f-g-h-i-j")] // знаки, недопустимые в именах файлов, в имя не попадают
    [InlineData("TSK-5 — миграции БД", "tsk-5-миграции-бд")]
    [InlineData("日本語のタイトル", "日本語のタイトル")]
    [InlineData("Mixed: Привет, world 42", "mixed-привет-world-42")]
    [InlineData("", "task")]
    [InlineData("   ", "task")]
    [InlineData("!!! ???", "task")]
    [InlineData("😀 only emoji 😀", "only-emoji")]
    [InlineData("CON", "con")] // зарезервированное имя Windows безопасно благодаря суффиксу id
    public void The_title_becomes_a_safe_lowercase_slug(string title, string expected) =>
        Assert.Equal(expected, EntityFileNames.Slug(title, "task"));

    [Fact]
    public void A_long_title_is_cut_at_a_word_boundary_character_and_never_ends_with_a_dash()
    {
        var slug = EntityFileNames.Slug(string.Join(' ', Enumerable.Repeat("слово", 30)), "task");

        Assert.True(slug.Length <= EntityFileNames.MaxSlugLength);
        Assert.StartsWith("слово-слово", slug);
        Assert.False(slug.EndsWith('-'));
        Assert.Equal(EntityFileNames.MaxSlugLength, EntityFileNames.Slug(new string('x', 500), "task").Length);
        // Сурогатные пары не режутся пополам.
        var emojiLetters = string.Concat(Enumerable.Repeat("𝒜", 100)); // математическая буква — вне BMP
        Assert.True(EntityFileNames.Slug(emojiLetters, "task").EnumerateRunes().All(x => Rune.IsLetter(x)));
    }

    [Fact]
    public void The_file_name_has_the_slug_and_the_first_eight_characters_of_the_id()
    {
        var id = Guid.Parse("3f2a9c1e-5d4b-4a7c-8e1f-0a1b2c3d4e5f");

        Assert.Equal("fix-login-3f2a9c1e.yaml", EntityFolders.Tasks.FileName("Fix login", id));
        Assert.Equal("3f2a9c1e", EntityFileNames.IdPrefix(id));
        Assert.Equal("task-3f2a9c1e.yaml", EntityFolders.Tasks.FileName(null, id));
    }

    [Fact]
    public void Names_are_parsed_back_into_a_full_id_or_an_id_prefix_and_other_names_are_rejected()
    {
        var id = Guid.NewGuid();

        Assert.True(EntityFileNames.TryParse($"{id}.yaml", out var full, out var prefix));
        Assert.Equal((id, null), (full, prefix)); // старое имя — полный Guid

        Assert.True(EntityFileNames.TryParse("исправить-вход-3f2a9c1e.yaml", out full, out prefix));
        Assert.Equal((null, "3f2a9c1e"), (full, prefix));

        Assert.True(EntityFileNames.TryParse("a-b-c-0123abcd.yaml", out _, out prefix));
        Assert.Equal("0123abcd", prefix);

        foreach (var name in new[] { "notes.yaml", "fix-login.yaml", "fix-login-3F2A9C1E.yaml", "fix-login-3f2a9c1.yaml", "fix-login-3f2a9c1e2.yaml",
                     "-3f2a9c1e.yaml", "fix-login-3f2a9c1e.yml", "fix-login-3f2a9c1e.yaml.tmp", "fix-login-3f2a9c1e", ".yaml" })
            Assert.False(EntityFileNames.TryParse(name, out _, out _), name);
    }

    [Fact]
    public void A_name_is_current_only_when_it_matches_the_title_and_id_whatever_the_unicode_form()
    {
        var id = Guid.NewGuid();
        var name = EntityFolders.Tasks.FileName("Исправить вход", id);

        Assert.True(EntityFolders.Tasks.IsCurrent(name, "Исправить вход", id));
        Assert.True(EntityFolders.Tasks.IsCurrent(name.Normalize(NormalizationForm.FormD), "Исправить вход", id)); // файловая система могла разложить «й»
        Assert.True(EntityFolders.Tasks.IsCurrent(name, "ИСПРАВИТЬ,  вход!", id)); // тот же slug
        Assert.False(EntityFolders.Tasks.IsCurrent(name, "Другой заголовок", id));
        Assert.False(EntityFolders.Tasks.IsCurrent($"{id}.yaml", "Исправить вход", id)); // старое имя
        Assert.False(EntityFolders.Tasks.IsCurrent(name, "Исправить вход", Guid.NewGuid()));
    }

    // ---- создание и переименование ----

    [Fact]
    public async Task A_new_task_is_stored_under_a_name_made_of_its_title_and_found_by_id()
    {
        var task = NewTask("Исправить вход");

        var version = await Tasks.Add(task);

        Assert.Equal([EntityFolders.Tasks.FileName("Исправить вход", task.Id)], TaskFiles());
        Assert.StartsWith("исправить-вход-", TaskFiles()[0]);
        var read = (await Tasks.GetById(_project, task.Id))!;
        Assert.Equal((task.Title, version), (read.Title, read.Version));
        Assert.Equal(task.Id, Assert.Single((await Tasks.GetRange(_project, null, new Page(0, 50))).Data).Id);
        Assert.Null(await Tasks.GetById(_project, Guid.NewGuid()));
    }

    [Fact]
    public async Task Tasks_with_the_same_title_get_different_files()
    {
        var a = NewTask("Same title", 1);
        var b = NewTask("Same title", 2);

        await Tasks.Add(a);
        await Tasks.Add(b);

        Assert.Equal(2, TaskFiles().Distinct().Count());
        Assert.All(TaskFiles(), x => Assert.StartsWith("same-title-", x));
        Assert.Equal("Same title", (await Tasks.GetById(_project, a.Id))!.Title);
        Assert.Equal(2, (await Tasks.GetRange(_project, null, new Page(0, 50))).TotalCount);
    }

    [Fact]
    public async Task Changing_the_title_renames_the_file_and_keeps_everything_else_working()
    {
        var task = NewTask("Old title");
        var version = await Tasks.Add(task);
        var before = TaskFiles().Single();

        var renamed = task with { Title = "New title" };
        var newVersion = await Tasks.Update(renamed, version);

        Assert.NotNull(newVersion);
        var after = TaskFiles().Single();
        Assert.NotEqual(before, after);
        Assert.StartsWith("new-title-", after);
        Assert.False(File.Exists(Path.Combine(Project.Tasks, before)));
        // Задача находится по id, список видит одну задачу под новым названием, версия — настоящая версия нового файла.
        var read = (await Tasks.GetById(_project, task.Id))!;
        Assert.Equal(("New title", newVersion), (read.Title, read.Version));
        var listed = Assert.Single((await Tasks.GetRange(_project, null, new Page(0, 50))).Data);
        Assert.Equal("New title", listed.Title);

        // Ещё раз с тем же заголовком (другое поле) — имя не меняется.
        Assert.NotNull(await Tasks.Update(read with { Description = "d" }, newVersion!));
        Assert.Equal([after], TaskFiles());
    }

    [Fact]
    public async Task A_rejected_update_with_an_old_version_does_not_rename_anything()
    {
        var task = NewTask("Old title");
        var version = await Tasks.Add(task);
        Assert.NotNull(await Tasks.Update(task with { Description = "first" }, version));
        var name = TaskFiles().Single();

        var stale = await Tasks.Update(task with { Title = "Lost title" }, version);

        Assert.Null(stale);
        Assert.Equal([name], TaskFiles());
        Assert.Equal("Old title", (await Tasks.GetById(_project, task.Id))!.Title);
    }

    [Fact]
    public async Task Updating_a_task_whose_file_does_not_exist_returns_null_and_deleting_it_returns_false()
    {
        var ghost = NewTask("Ghost");

        Assert.Null(await Tasks.Update(ghost, "whatever"));
        Assert.False(await Tasks.Delete(_project, ghost.Id, "whatever"));
        Assert.Empty(TaskFiles());
    }

    [Fact]
    public async Task Deleting_removes_the_file_under_its_current_name()
    {
        var task = NewTask("To delete");
        var version = await Tasks.Add(task);
        var renamed = await Tasks.Update(task with { Title = "To delete now" }, version);

        Assert.False(await Tasks.Delete(_project, task.Id, version)); // старая версия
        Assert.Single(TaskFiles());
        Assert.True(await Tasks.Delete(_project, task.Id, renamed!));

        Assert.Empty(TaskFiles());
        Assert.Null(await Tasks.GetById(_project, task.Id));
        Assert.Empty((await Tasks.GetRange(_project, null, new Page(0, 50))).Data);
    }

    [Fact]
    public async Task A_rename_never_overwrites_another_file_with_the_target_name()
    {
        var task = NewTask("First title");
        var version = await Tasks.Add(task);
        var original = TaskFiles().Single();
        // Чужой файл уже занимает имя, которое получила бы задача (на практике — совпадение 8 знаков id).
        var target = Project.TaskFile(task.Id, "Second title");
        await File.WriteAllTextAsync(target, "something else");

        await Assert.ThrowsAsync<IOException>(() => Tasks.Update(task with { Title = "Second title" }, version));

        Assert.Equal("something else", await File.ReadAllTextAsync(target));
        Assert.True(File.Exists(Path.Combine(Project.Tasks, original)));
    }

    [Fact]
    public async Task Parallel_updates_of_one_task_have_exactly_one_winner()
    {
        var task = NewTask("Race");
        var version = await Tasks.Add(task);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => Tasks.Update(task with { Title = $"Title {i}" }, version))));

        Assert.Single(results.Where(x => x != null));
        Assert.Single(TaskFiles());
        Assert.StartsWith("title-", TaskFiles()[0]);
        Assert.Single((await Tasks.GetRange(_project, null, new Page(0, 50))).Data);
    }

    // ---- старые файлы (по Guid) и файлы, принесённые снаружи ----

    private string WriteFile(string name, TaskItem task, bool withVersion = true)
    {
        Directory.CreateDirectory(Project.Tasks);
        var path = Path.Combine(Project.Tasks, name);
        File.WriteAllText(path, (withVersion ? $"formatVersion: {FormatVersions.Current}\n" : "") +
            $"id: {task.Id}\ntitle: {task.Title}\ntypeId: {task.TypeId}\nstatusId: {task.StatusId}\n" +
            "createdAt: 2026-01-01T10:00:00.0000000+00:00\nupdatedAt: 2026-01-01T10:00:00.0000000+00:00\n");
        return path;
    }

    [Fact]
    public async Task A_file_with_the_old_guid_name_is_read_listed_and_renamed_on_its_first_write()
    {
        var task = NewTask("Legacy task");
        var old = WriteFile($"{task.Id}.yaml", task, withVersion: false);
        await _containers[0].Resolve<Tasker.Storage.Files.Index.WorkspaceIndex>().Rescan();

        var read = (await Tasks.GetById(_project, task.Id))!;
        Assert.Equal("Legacy task", read.Title);
        Assert.Equal("Legacy task", Assert.Single((await Tasks.GetRange(_project, null, new Page(0, 50))).Data).Title);

        var version = await Tasks.Update(read with { Description = "touched" }, read.Version);

        Assert.NotNull(version);
        Assert.False(File.Exists(old)); // записали — файл получил новое имя и текущую версию формата
        Assert.Equal([EntityFolders.Tasks.FileName("Legacy task", task.Id)], TaskFiles());
        Assert.StartsWith($"formatVersion: {FormatVersions.Current}\n", File.ReadAllText(Path.Combine(Project.Tasks, TaskFiles()[0])));
        Assert.Equal("touched", (await Tasks.GetById(_project, task.Id))!.Description);
    }

    [Fact]
    public async Task A_legacy_named_file_can_be_deleted()
    {
        var task = NewTask("Legacy delete");
        WriteFile($"{task.Id}.yaml", task);
        await _containers[0].Resolve<Tasker.Storage.Files.Index.WorkspaceIndex>().Rescan();
        var version = (await Tasks.GetById(_project, task.Id))!.Version;

        Assert.True(await Tasks.Delete(_project, task.Id, version));

        Assert.Empty(TaskFiles());
    }

    [Fact]
    public async Task A_file_that_arrived_behind_the_index_is_found_by_its_name_under_both_naming_schemes()
    {
        await Tasks.GetRange(_project, null, new Page(0, 50)); // индекс уже открыт и об этих файлах не знает

        var named = NewTask("Pulled by name");
        var legacy = NewTask("Pulled legacy");
        WriteFile(EntityFolders.Tasks.FileName(named.Title, named.Id), named);
        WriteFile($"{legacy.Id}.yaml", legacy);

        Assert.Equal("Pulled by name", (await Tasks.GetById(_project, named.Id))!.Title);
        Assert.Equal("Pulled legacy", (await Tasks.GetById(_project, legacy.Id))!.Title);
        Assert.Null(await Tasks.GetById(_project, Guid.NewGuid()));
    }

    [Fact]
    public async Task A_file_name_whose_id_does_not_match_the_content_is_a_problem_not_a_task()
    {
        var task = NewTask("Mismatch");
        // Имя говорит об одном id, внутри файла — другой (ручная правка, неудачное слияние).
        var wrongName = EntityFolders.Tasks.FileName("Mismatch", Guid.NewGuid());
        WriteFile(wrongName, task);
        var index = _containers[0].Resolve<Tasker.Storage.Files.Index.WorkspaceIndex>();
        await index.Rescan();

        Assert.Empty((await Tasks.GetRange(_project, null, new Page(0, 50))).Data);
        Assert.Null(await Tasks.GetById(_project, task.Id));
        var problems = await index.GetProblems(new Page(0, 50));
        var problem = Assert.Single(problems.Data);
        Assert.EndsWith(wrongName, problem.Path);
        Assert.Contains("does not match the end of the file name", problem.Error);
    }

    [Fact]
    public async Task A_second_process_sees_a_renamed_task_through_its_own_index()
    {
        var other = Open(); // второй контейнер на ту же папку — как второй процесс
        var task = NewTask("Shared");
        var version = await Tasks.Add(task);
        Assert.Equal("Shared", (await other.Resolve<ITaskStorage>().GetById(_project, task.Id))!.Title);

        await Tasks.Update(task with { Title = "Shared renamed" }, version);

        var seen = await other.Resolve<ITaskStorage>().GetById(_project, task.Id);
        Assert.Equal("Shared renamed", seen!.Title);
        Assert.Equal("Shared renamed", Assert.Single((await other.Resolve<ITaskStorage>().GetRange(_project, null, new Page(0, 50))).Data).Title);
    }

    // ---- tasker migrate ----

    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    /// <summary>Как файлы выглядели до версии 2: задачи называются по Guid, версии формата нет.</summary>
    private static void MakeOldLayout(TestWorkspace ws)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(ws.Root, ".tasker"), "*.yaml", SearchOption.AllDirectories)
                     .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}.cache{Path.DirectorySeparatorChar}")))
        {
            var lines = File.ReadAllLines(file).Where(x => !x.StartsWith("formatVersion:")).ToArray();
            File.WriteAllText(file, string.Join('\n', lines) + "\n");
            if (Path.GetFileName(Path.GetDirectoryName(file)) == "tasks")
            {
                var id = lines.First(x => x.StartsWith("id: ")).Substring(4).Trim();
                File.Move(file, Path.Combine(Path.GetDirectoryName(file)!, id + ".yaml"));
            }
        }
    }

    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("status", "create", "Todo"));
        await Ok(ws.Run("status-set", "create", "Flow", "--status", "Todo"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(ws.Run("series", "create", "Tasks", "--prefix", "TSK"));
        await Ok(ws.Run("task", "create", "Исправить вход", "--type", "Bug", "--series", "TSK"));
        await Ok(ws.Run("task", "create", "Fix: logout (v2)", "--type", "Bug", "--series", "TSK", "-d", "Строка один.\nСтрока два."));
        await Ok(ws.Run("task", "link", "TSK-1", "blocks", "TSK-2"));
    }

    private static string[] TaskFileNamesOf(TestWorkspace ws) =>
        Directory.GetFiles(Path.Combine(ws.Root, ".tasker", "projects"), "*.yaml", SearchOption.AllDirectories)
            .Where(x => Path.GetFileName(Path.GetDirectoryName(x)) == "tasks")
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Fact]
    public async Task Tasks_created_by_tasker_get_names_by_title_and_every_command_works_with_them()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);

        var names = TaskFileNamesOf(ws);

        Assert.Equal(2, names.Length);
        Assert.Contains(names, x => x.StartsWith("исправить-вход-"));
        Assert.Contains(names, x => x.StartsWith("fix-logout-v2-"));
        await Ok(ws.Run("task", "update", "TSK-1", "--title", "Исправить выход"));
        Assert.Contains(TaskFileNamesOf(ws), x => x.StartsWith("исправить-выход-"));
        Assert.DoesNotContain(TaskFileNamesOf(ws), x => x.StartsWith("исправить-вход-"));
        Assert.Contains("Исправить выход", (await Ok(ws.Run("task", "list"))).Out);
        Assert.Contains("is blocked by", (await Ok(ws.Run("task", "get", "TSK-2"))).Out); // связь по id пережила переименование
        await Ok(ws.Run("task", "delete", "TSK-2"));
        Assert.Single(TaskFileNamesOf(ws));
    }

    [Fact]
    public async Task Migrate_renames_old_task_files_and_brings_them_to_the_current_format()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        var current = TaskFileNamesOf(ws);
        MakeOldLayout(ws);
        Assert.All(TaskFileNamesOf(ws), x => Assert.True(Guid.TryParse(Path.GetFileNameWithoutExtension(x), out _)));

        // Старые файлы читаются как есть.
        Assert.Contains("Исправить вход", (await Ok(ws.Run("task", "list"))).Out);
        Assert.Contains("is blocked by", (await Ok(ws.Run("task", "get", "TSK-2"))).Out);

        var check = await ws.Run("migrate", "--check");
        Assert.Equal(2, check.Code);
        Assert.Contains("file(s) would be renamed after the names of their entities", check.Out);
        Assert.Matches(@"would rename +projects/\S+/tasks/[0-9a-f-]{36}\.yaml +-> +исправить-вход-[0-9a-f]{8}\.yaml", check.Out);
        Assert.All(TaskFileNamesOf(ws), x => Assert.True(Guid.TryParse(Path.GetFileNameWithoutExtension(x), out _))); // --check ничего не пишет

        var real = await Ok(ws.Run("migrate"));
        Assert.Contains("Renamed 2 file(s) after the names of their entities", real.Out);
        Assert.Equal(current, TaskFileNamesOf(ws)); // те же имена, что даёт сам Tasker
        Assert.All(Directory.GetFiles(Path.Combine(ws.Root, ".tasker"), "*.yaml", SearchOption.AllDirectories)
                .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}.cache{Path.DirectorySeparatorChar}")),
            x => Assert.Equal($"formatVersion: {FormatVersions.Current}", File.ReadLines(x).First()));

        // Данные после миграции прежние, и повторный запуск ничего не делает.
        Assert.Contains("Исправить вход", (await Ok(ws.Run("task", "list"))).Out);
        Assert.Contains("Строка два.", (await Ok(ws.Run("task", "get", "TSK-2"))).Out);
        Assert.Contains("is blocked by", (await Ok(ws.Run("task", "get", "TSK-2"))).Out);
        Assert.Contains("Nothing to migrate", (await Ok(ws.Run("migrate"))).Out);
        Assert.Equal(0, (await ws.Run("migrate", "--check")).Code);
    }

    [Fact]
    public async Task Migrate_json_lists_renames_and_a_file_whose_title_was_edited_by_hand_is_renamed_too()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        var file = Directory.GetFiles(Path.Combine(ws.Root, ".tasker", "projects"), "исправить-вход-*.yaml", SearchOption.AllDirectories).Single();
        File.WriteAllText(file, File.ReadAllText(file).Replace("title: Исправить вход", "title: Починить вход"));

        var json = (await ws.Run("migrate", "--check", "--json")).Json;

        var rename = Assert.Single(json["renamed"]!.AsArray())!;
        Assert.Contains("исправить-вход-", rename["from"]!.GetValue<string>());
        Assert.Contains("починить-вход-", rename["to"]!.GetValue<string>());
        Assert.Empty(json["migrated"]!.AsArray());
        Assert.True(json["needsAttention"]!.GetValue<bool>());

        await Ok(ws.Run("migrate"));
        Assert.Contains(TaskFileNamesOf(ws), x => x.StartsWith("починить-вход-"));
        Assert.Contains("Починить вход", (await Ok(ws.Run("task", "list"))).Out);
    }

    [Fact]
    public async Task Migrate_does_not_overwrite_a_file_that_already_has_the_target_name()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        MakeOldLayout(ws);
        var tasks = Path.Combine(ws.Root, ".tasker", "projects");
        var old = Directory.GetFiles(tasks, "*.yaml", SearchOption.AllDirectories).First(x => Path.GetFileName(Path.GetDirectoryName(x)) == "tasks");
        var id = File.ReadAllLines(old).First(x => x.StartsWith("id: ")).Substring(4).Trim();
        var title = File.ReadAllLines(old).First(x => x.StartsWith("title: ")).Substring(7);
        var occupied = Path.Combine(Path.GetDirectoryName(old)!, EntityFolders.Tasks.FileName(title, Guid.Parse(id)));
        File.WriteAllText(occupied, "occupied: true\n");

        var result = await ws.Run("migrate", "--json");

        Assert.Equal(1, result.Code);
        Assert.Contains("a file with that name exists", result.Json["unreadable"]![0]!["reason"]!.GetValue<string>());
        Assert.True(File.Exists(old));
        Assert.Equal("occupied: true\n", File.ReadAllText(occupied));
    }
}
