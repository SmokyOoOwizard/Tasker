using System.Text.RegularExpressions;
using Autofac;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core.Dto;
using Tasker.Core.Statuses;
using Tasker.Core.TaskSeries;
using Tasker.Core.Workspace;
using Tasker.Storage.Files;
using Tasker.Storage.Files.Index;
using Tasker.Storage.Files.Storages;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Имена файлов остальных сущностей проекта (серии, типы задач, статусы, наборы, доски, типы связей, поля, перечисления):
/// название + короткий id, как у задач (TSK-77). Раскладка, чтение обоих вариантов имён, переименование при смене названия, миграция.
/// </summary>
public class EntityFileNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _project = Guid.NewGuid();
    private readonly List<IContainer> _containers = [];

    public EntityFileNameTests()
    {
        Directory.CreateDirectory(_root);
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

    private ProjectDirectory Project => new TaskerDirectory(_root).Project(_project);

    private static string[] Names(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "*.yaml").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()! : [];

    private static Status NewStatus(Guid project, string name, Guid? id = null) =>
        new() { Id = id ?? Guid.NewGuid(), ProjectId = project, Name = name, Color = "#808080", Version = "" };

    // ---- раскладка: Classify ----

    public static IEnumerable<object[]> Folders() => EntityFolders.All.Select(x => new object[] { x.Name });

    [Theory]
    [MemberData(nameof(Folders))]
    public void Every_entity_folder_accepts_the_new_name_and_the_old_guid_name(string folder)
    {
        var project = Guid.NewGuid();
        var id = Guid.Parse("3f2a9c1e-7b4d-4e6a-8c1f-5d2b9a7e4c30");
        var kind = EntityFolders.Named(folder)!.Kind;

        var named = WorkspaceLayout.Classify($"projects/{project}/{folder}/исправить-вход-3f2a9c1e.yaml")!;
        Assert.Equal((kind, project, (Guid?)null, "3f2a9c1e"), (named.Kind, named.ProjectId, named.Id, named.IdPrefix));

        var legacy = WorkspaceLayout.Classify($"projects/{project}/{folder}/{id}.yaml")!;
        Assert.Equal((kind, project, (Guid?)id, (string?)null), (legacy.Kind, legacy.ProjectId, legacy.Id, legacy.IdPrefix));

        Assert.Null(WorkspaceLayout.Classify($"projects/{project}/{folder}/readme.yaml"));
        Assert.Null(WorkspaceLayout.Classify($"projects/{project}/{folder}/name-3f2a9c1e.txt"));
        Assert.Null(WorkspaceLayout.Classify($"projects/{project}/{folder}/name-3f2a9c1.yaml")); // 7 знаков
        Assert.Null(WorkspaceLayout.Classify($"projects/{project}/{folder}/sub/name-3f2a9c1e.yaml"));
    }

    [Fact]
    public void Users_and_the_project_file_keep_their_names_and_foreign_folders_are_not_entities()
    {
        var project = Guid.NewGuid();
        var user = Guid.NewGuid();

        Assert.Equal(IndexKind.User, WorkspaceLayout.Classify($"users/{user}.yaml")!.Kind);
        Assert.Null(WorkspaceLayout.Classify("users/ivan-3f2a9c1e.yaml")); // пользователи по-прежнему по Guid
        Assert.Equal(IndexKind.Project, WorkspaceLayout.Classify($"projects/{project}/project.yaml")!.Kind);
        Assert.Null(WorkspaceLayout.Classify($"projects/{project}/notes/name-3f2a9c1e.yaml"));
        Assert.Null(WorkspaceLayout.Classify($"projects/{project}/statuses.yaml"));
    }

    [Fact]
    public void Each_kind_of_entity_has_its_own_name_for_a_title_without_letters()
    {
        var id = Guid.Parse("3f2a9c1e-7b4d-4e6a-8c1f-5d2b9a7e4c30");

        Assert.Equal(
            ["task-3f2a9c1e.yaml", "type-3f2a9c1e.yaml", "status-3f2a9c1e.yaml", "set-3f2a9c1e.yaml", "board-3f2a9c1e.yaml",
             "series-3f2a9c1e.yaml", "link-3f2a9c1e.yaml", "field-3f2a9c1e.yaml", "enum-3f2a9c1e.yaml"],
            EntityFolders.All.Select(x => x.FileName("???", id)));
        Assert.Equal(EntityFolders.All.Count, EntityFolders.All.Select(x => x.Name).Distinct().Count());
        Assert.Equal(EntityFolders.All.Count, EntityFolders.All.Select(x => x.Kind).Distinct().Count());
    }

    [Fact]
    public void Format_version_4_files_are_read_as_version_9_and_a_newer_one_is_refused()
    {
        Assert.Equal(9, FormatVersions.Current);
        Assert.StartsWith("formatVersion: 9\n", FormatVersions.Upgrade("formatVersion: 4\nid: 1\n", "x.yaml"));
        Assert.Throws<UnsupportedFormatException>(() => FormatVersions.Upgrade("formatVersion: 10\nid: 1\n", "x.yaml"));
    }

    // ---- хранилище: создание, переименование, старые файлы ----

    [Fact]
    public async Task A_new_status_is_stored_under_its_name_and_found_by_id_and_a_rename_renames_the_file()
    {
        var statuses = Open().Resolve<IStatusStorage>();
        var status = NewStatus(_project, "В работе");

        var version = await statuses.Add(status);

        Assert.Equal([EntityFolders.Statuses.FileName("В работе", status.Id)], Names(Project.Statuses));
        Assert.StartsWith("в-работе-", Names(Project.Statuses)[0]);
        Assert.Equal(("В работе", version), ((await statuses.GetById(_project, status.Id))!.Name, (await statuses.GetById(_project, status.Id))!.Version));
        Assert.Null(await statuses.GetById(_project, Guid.NewGuid()));

        var renamed = await statuses.Update(status with { Name = "Готово" }, version);

        Assert.NotNull(renamed);
        Assert.StartsWith("готово-", Assert.Single(Names(Project.Statuses)));
        var read = (await statuses.GetById(_project, status.Id))!;
        Assert.Equal(("Готово", renamed), (read.Name, read.Version));
        Assert.Equal("Готово", Assert.Single((await statuses.GetRange(_project, new Page(0, 50))).Data).Name);
        // Тот же id — тот же файл, и другая правка имени не меняет.
        Assert.NotNull(await statuses.Update(read with { Color = "#FF0000" }, renamed!));
        Assert.Single(Names(Project.Statuses));
    }

    [Fact]
    public async Task Entities_with_the_same_name_get_different_files_and_each_is_found_by_its_own_id()
    {
        var statuses = Open().Resolve<IStatusStorage>();
        var a = NewStatus(_project, "Same");
        var b = NewStatus(_project, "Same");
        var versionA = await statuses.Add(a);
        await statuses.Add(b);

        Assert.Equal(2, Names(Project.Statuses).Distinct().Count());
        Assert.All(Names(Project.Statuses), x => Assert.StartsWith("same-", x));
        Assert.Equal(a.Id, (await statuses.GetById(_project, a.Id))!.Id);
        Assert.Equal(b.Id, (await statuses.GetById(_project, b.Id))!.Id);

        await statuses.Update(a with { Name = "Other" }, versionA);

        Assert.Equal(["other", "same"], Names(Project.Statuses).Select(x => x[..x.IndexOf('-')]).Order().ToArray());
        Assert.Equal(2, (await statuses.GetRange(_project, new Page(0, 50))).TotalCount);
    }

    [Fact]
    public async Task A_rejected_rename_with_an_old_version_and_a_taken_name_change_nothing()
    {
        var statuses = Open().Resolve<IStatusStorage>();
        var status = NewStatus(_project, "First");
        var version = await statuses.Add(status);
        Assert.NotNull(await statuses.Update(status with { Color = "#111111" }, version));
        var name = Assert.Single(Names(Project.Statuses));

        Assert.Null(await statuses.Update(status with { Name = "Lost" }, version)); // версия устарела
        Assert.Equal([name], Names(Project.Statuses));

        var current = (await statuses.GetById(_project, status.Id))!;
        var target = Project.EntityFile(EntityFolders.Statuses, status.Id, "Second");
        await File.WriteAllTextAsync(target, "something else"); // чужой файл на месте нового имени
        await Assert.ThrowsAsync<IOException>(() => statuses.Update(current with { Name = "Second" }, current.Version));
        Assert.Equal("something else", await File.ReadAllTextAsync(target));
        Assert.Equal("First", (await statuses.GetById(_project, status.Id))!.Name);
    }

    private string WriteStatusFile(string name, Guid id, string statusName, int? formatVersion = null)
    {
        Directory.CreateDirectory(Project.Statuses);
        var path = Path.Combine(Project.Statuses, name);
        File.WriteAllText(path, (formatVersion is { } v ? $"formatVersion: {v}\n" : "") + $"id: {id}\nname: {statusName}\ncolor: '#808080'\n");
        return path;
    }

    [Fact]
    public async Task A_file_with_the_old_guid_name_is_read_listed_renamed_on_its_first_write_and_can_be_deleted()
    {
        var container = Open();
        var statuses = container.Resolve<IStatusStorage>();
        var id = Guid.NewGuid();
        var old = WriteStatusFile($"{id}.yaml", id, "Legacy status");
        await container.Resolve<WorkspaceIndex>().Rescan();

        var read = (await statuses.GetById(_project, id))!;
        Assert.Equal("Legacy status", read.Name);
        Assert.Equal("Legacy status", Assert.Single((await statuses.GetRange(_project, new Page(0, 50))).Data).Name);

        Assert.NotNull(await statuses.Update(read with { Color = "#222222" }, read.Version));

        Assert.False(File.Exists(old));
        Assert.Equal([EntityFolders.Statuses.FileName("Legacy status", id)], Names(Project.Statuses));
        Assert.StartsWith($"formatVersion: {FormatVersions.Current}\n", File.ReadAllText(Path.Combine(Project.Statuses, Names(Project.Statuses)[0])));

        var other = Guid.NewGuid();
        WriteStatusFile($"{other}.yaml", other, "Legacy delete");
        await container.Resolve<WorkspaceIndex>().Rescan();
        Assert.True(await statuses.Delete(_project, other, (await statuses.GetById(_project, other))!.Version));
        Assert.Single(Names(Project.Statuses));
    }

    [Fact]
    public async Task A_file_that_arrived_behind_the_index_is_found_by_its_name_under_both_naming_schemes()
    {
        var container = Open();
        var statuses = container.Resolve<IStatusStorage>();
        await statuses.GetRange(_project, new Page(0, 50)); // индекс уже открыт и об этих файлах не знает

        var named = Guid.NewGuid();
        var legacy = Guid.NewGuid();
        WriteStatusFile(EntityFolders.Statuses.FileName("Pulled", named), named, "Pulled");
        WriteStatusFile($"{legacy}.yaml", legacy, "Pulled legacy");

        Assert.Equal("Pulled", (await statuses.GetById(_project, named))!.Name);
        Assert.Equal("Pulled legacy", (await statuses.GetById(_project, legacy))!.Name);
        Assert.Null(await statuses.GetById(_project, Guid.NewGuid()));
    }

    [Fact]
    public async Task A_name_whose_id_does_not_match_the_content_is_a_problem_not_an_entity()
    {
        var container = Open();
        var statuses = container.Resolve<IStatusStorage>();
        var id = Guid.NewGuid();
        var wrong = EntityFolders.Statuses.FileName("Mismatch", Guid.NewGuid());
        WriteStatusFile(wrong, id, "Mismatch");
        var index = container.Resolve<WorkspaceIndex>();
        await index.Rescan();

        Assert.Empty((await statuses.GetRange(_project, new Page(0, 50))).Data);
        Assert.Null(await statuses.GetById(_project, id));
        var problem = Assert.Single((await index.GetProblems(new Page(0, 50))).Data);
        Assert.EndsWith(wrong, problem.Path);
        Assert.Contains("does not match the end of the file name", problem.Error);
    }

    [Fact]
    public async Task The_index_reports_the_id_from_the_content_for_added_renamed_and_removed_files()
    {
        var container = Open();
        var index = container.Resolve<WorkspaceIndex>();
        await index.Rescan();
        var changes = new List<WorkspaceFileChange>();
        index.Changed += files =>
        {
            lock (changes)
                changes.AddRange(files);
        };

        var id = Guid.NewGuid();
        var path = WriteStatusFile(EntityFolders.Statuses.FileName("Watched", id), id, "Watched");
        await index.Refresh(path);
        var renamed = Path.Combine(Project.Statuses, EntityFolders.Statuses.FileName("Renamed by hand", id));
        File.Move(path, renamed);
        await index.Refresh([path, renamed]);
        File.Delete(renamed);
        await index.Refresh(renamed);

        lock (changes)
        {
            // Имя говорит только о начале id, а событие несёт настоящий id — и для файла, которого уже нет.
            Assert.Equal(4, changes.Count);
            Assert.All(changes, x => Assert.Equal(id, x.Id));
            Assert.Equal($"projects/{_project}/statuses/{Path.GetFileName(path)}", changes[0].Path);
        }
    }

    [Fact]
    public async Task A_second_process_sees_a_renamed_entity_through_its_own_index()
    {
        var first = Open();
        var other = Open(); // второй контейнер на ту же папку — как второй процесс
        var series = first.Resolve<ISeriesStorage>();
        var item = new Series { Id = Guid.NewGuid(), ProjectId = _project, Name = "Shared", Prefix = "SH", Version = "" };
        var version = await series.Add(item);
        Assert.Equal("Shared", (await other.Resolve<ISeriesStorage>().GetById(_project, item.Id))!.Name);

        await series.Update(item with { Name = "Shared renamed" }, version);

        Assert.Equal("Shared renamed", (await other.Resolve<ISeriesStorage>().GetById(_project, item.Id))!.Name);
        Assert.Equal("Shared renamed", Assert.Single((await other.Resolve<ISeriesStorage>().GetRange(_project, new Page(0, 50))).Data).Name);
    }

    // ---- консоль и tasker migrate: все виды сущностей, оба хранилища ----

    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    /// <summary>Проект Demo: по одной сущности каждого вида, с названиями на двух алфавитах.</summary>
    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("status", "create", "Нужно сделать"));
        await Ok(ws.Run("status", "create", "Done (v2)"));
        await Ok(ws.Run("status-set", "create", "Основной поток", "--status", "Нужно сделать", "Done (v2)"));
        await Ok(ws.Run("task-type", "create", "Ошибка", "--status-set", "Основной поток"));
        await Ok(ws.Run("series", "create", "Разработка", "--prefix", "TSK"));
        await Ok(ws.Run("link-type", "create", "Зависимость", "--outward", "зависит от", "--inward", "является зависимостью"));
        await Ok(ws.Run("enum", "create", "Приоритет", "--value", "Low", "High"));
        await Ok(ws.Run("field", "create", "Оценка", "--type", "int"));
        await Ok(ws.Run("field", "create", "Severity", "--type", "enum", "--enum", "Приоритет"));
        await Ok(ws.Run("board", "create", "Доска", "--status-set", "Основной поток", "--column", "Все=Нужно сделать,Done (v2)"));
        await Ok(ws.Run("task", "create", "Исправить вход", "--type", "Ошибка", "--series", "TSK"));
    }

    private static readonly string[] Folders9 = ["tasks", "task-types", "statuses", "status-sets", "boards", "series", "link-types", "fields", "enums"];

    private static string[] EntityFiles(TestWorkspace ws) =>
        Folders9.SelectMany(folder => Directory
                .GetFiles(Path.Combine(ws.Root, ".tasker", "projects"), "*.yaml", SearchOption.AllDirectories)
                .Where(x => Path.GetFileName(Path.GetDirectoryName(x)) == folder))
            .ToArray();

    private static string[] RelativeEntityNames(TestWorkspace ws) =>
        EntityFiles(ws).Select(x => $"{Path.GetFileName(Path.GetDirectoryName(x))}/{Path.GetFileName(x)}").Order(StringComparer.Ordinal).ToArray();

    /// <summary>Как файлы выглядели до версии 5: сущности называются по Guid, версия формата 4 (у задач имя уже по заголовку).</summary>
    private static void MakeOldLayout(TestWorkspace ws)
    {
        foreach (var file in EntityFiles(ws))
        {
            var lines = File.ReadAllLines(file).Where(x => !x.StartsWith("formatVersion:")).Prepend("formatVersion: 4").ToArray();
            File.WriteAllText(file, string.Join('\n', lines) + "\n");
            if (Path.GetFileName(Path.GetDirectoryName(file)) == "tasks")
                continue;

            var id = lines.First(x => x.StartsWith("id: ")).Substring(4).Trim();
            File.Move(file, Path.Combine(Path.GetDirectoryName(file)!, id + ".yaml"));
        }
        foreach (var file in Directory.GetFiles(Path.Combine(ws.Root, ".tasker"), "project.yaml", SearchOption.AllDirectories))
            File.WriteAllText(file, File.ReadAllText(file).Replace("formatVersion: " + FormatVersions.Current, "formatVersion: 4"));
    }

    [Theory]
    [MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Entities_of_every_kind_work_by_name_and_id_and_a_rename_keeps_references_working(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        if (storage == "files")
        {
            var names = RelativeEntityNames(ws);
            Assert.Contains(names, x => x.StartsWith("statuses/нужно-сделать-"));
            Assert.Contains(names, x => x.StartsWith("statuses/done-v2-"));
            Assert.Contains(names, x => x.StartsWith("status-sets/основной-поток-"));
            Assert.Contains(names, x => x.StartsWith("task-types/ошибка-"));
            Assert.Contains(names, x => x.StartsWith("series/разработка-"));
            Assert.Contains(names, x => x.StartsWith("link-types/зависимость-"));
            Assert.Contains(names, x => x.StartsWith("enums/приоритет-"));
            Assert.Contains(names, x => x.StartsWith("fields/оценка-"));
            Assert.Contains(names, x => x.StartsWith("fields/severity-"));
            Assert.Contains(names, x => x.StartsWith("boards/доска-"));
            Assert.Contains(names, x => x.StartsWith("tasks/исправить-вход-"));
            Assert.All(EntityFiles(ws), x => Assert.Matches(@"^[^/\\]+-[0-9a-f]{8}\.yaml$", Path.GetFileName(x)));
        }

        // Переименование каждой сущности: файл получает новое имя, а всё, что ссылается по id, продолжает работать.
        await Ok(ws.Run("status", "update", "Нужно сделать", "--name", "К выполнению"));
        await Ok(ws.Run("status-set", "update", "Основной поток", "--name", "Главный поток"));
        await Ok(ws.Run("task-type", "update", "Ошибка", "--name", "Дефект"));
        await Ok(ws.Run("series", "update", "TSK", "--name", "Работа"));
        await Ok(ws.Run("link-type", "update", "Зависимость", "--name", "Stops"));
        await Ok(ws.Run("enum", "update", "Приоритет", "--name", "Важность"));
        await Ok(ws.Run("field", "update", "Оценка", "--name", "Трудоёмкость"));
        await Ok(ws.Run("board", "update", "Доска", "--name", "Главная доска"));

        if (storage == "files")
        {
            var names = RelativeEntityNames(ws);
            Assert.Contains(names, x => x.StartsWith("statuses/к-выполнению-"));
            Assert.DoesNotContain(names, x => x.StartsWith("statuses/нужно-сделать-"));
            Assert.Contains(names, x => x.StartsWith("status-sets/главный-поток-"));
            Assert.Contains(names, x => x.StartsWith("task-types/дефект-"));
            Assert.Contains(names, x => x.StartsWith("series/работа-"));
            Assert.Contains(names, x => x.StartsWith("link-types/stops-"));
            Assert.Contains(names, x => x.StartsWith("enums/важность-"));
            Assert.Contains(names, x => x.StartsWith("fields/трудоёмкость-"));
            Assert.Contains(names, x => x.StartsWith("boards/главная-доска-"));
        }

        Assert.Contains("К выполнению", (await Ok(ws.Run("status", "list"))).Out);
        Assert.Contains("К выполнению", (await Ok(ws.Run("status-set", "get", "Главный поток"))).Out);
        Assert.Contains("Дефект", (await Ok(ws.Run("task", "get", "TSK-1"))).Out);
        Assert.Contains("Главная доска", (await Ok(ws.Run("board", "list"))).Out);
        Assert.Contains("Важность", (await Ok(ws.Run("field", "get", "Severity"))).Out);
        Assert.Contains("Трудоёмкость", (await Ok(ws.Run("field", "list"))).Out);

        // Дважды одно и то же название (регистр не важен) сервис не даёт, а удаление убирает именно этот файл.
        var countBefore = storage == "files" ? RelativeEntityNames(ws).Length : 0;
        await Ok(ws.Run("board", "delete", "Главная доска"));
        await Ok(ws.Run("field", "delete", "Трудоёмкость"));
        if (storage == "files")
            Assert.Equal(countBefore - 2, RelativeEntityNames(ws).Length);
    }

    [Fact]
    public async Task Migrate_renames_old_files_of_every_kind_and_the_data_stays_the_same()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        var current = RelativeEntityNames(ws);
        MakeOldLayout(ws);
        var oldNames = RelativeEntityNames(ws);
        Assert.All(oldNames.Where(x => !x.StartsWith("tasks/")), x => Assert.True(Guid.TryParse(Path.GetFileNameWithoutExtension(x), out _), x));

        // Старые файлы читаются как есть, в списках и по ссылкам.
        Assert.Contains("Нужно сделать", (await Ok(ws.Run("status", "list"))).Out);
        Assert.Contains("Основной поток", (await Ok(ws.Run("status-set", "get", "Основной поток"))).Out);
        Assert.Contains("Ошибка", (await Ok(ws.Run("task", "get", "TSK-1"))).Out);

        var check = await ws.Run("migrate", "--check");
        Assert.Equal(2, check.Code);
        var renames = (await ws.Run("migrate", "--check", "--json")).Json["renamed"]!.AsArray()
            .Select(x => (From: x!["from"]!.GetValue<string>(), To: x["to"]!.GetValue<string>())).ToArray();
        Assert.Contains(renames, x => Regex.IsMatch(x.From, @"/statuses/[0-9a-f-]{36}\.yaml$") && Regex.IsMatch(x.To, @"/statuses/нужно-сделать-[0-9a-f]{8}\.yaml$"));
        Assert.Contains(renames, x => Regex.IsMatch(x.To, @"/series/разработка-[0-9a-f]{8}\.yaml$"));
        Assert.Contains("file(s) would be renamed after the names of their entities", check.Out);
        Assert.Equal(oldNames, RelativeEntityNames(ws)); // --check ничего не пишет

        var real = await Ok(ws.Run("migrate"));
        Assert.Contains("file(s) after the names of their entities", real.Out);
        Assert.Equal(current, RelativeEntityNames(ws)); // те же имена, что даёт сам Tasker
        Assert.All(Directory.GetFiles(Path.Combine(ws.Root, ".tasker"), "*.yaml", SearchOption.AllDirectories)
                .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}.cache{Path.DirectorySeparatorChar}")),
            x => Assert.Equal($"formatVersion: {FormatVersions.Current}", File.ReadLines(x).First()));

        Assert.Contains("Нужно сделать", (await Ok(ws.Run("status", "list"))).Out);
        Assert.Contains("Исправить вход", (await Ok(ws.Run("task", "list"))).Out);
        Assert.Contains("Приоритет", (await Ok(ws.Run("field", "get", "Severity"))).Out);
        Assert.Contains("Nothing to migrate", (await Ok(ws.Run("migrate"))).Out);
        Assert.Equal(0, (await ws.Run("migrate", "--check")).Code);
    }

    [Fact]
    public async Task Writing_an_old_named_file_renames_only_that_file_and_migrate_does_the_rest()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        MakeOldLayout(ws);

        await Ok(ws.Run("status", "update", "Done (v2)", "--name", "Finished"));

        var names = RelativeEntityNames(ws);
        Assert.Contains(names, x => x.StartsWith("statuses/finished-"));
        Assert.Single(names, x => x.StartsWith("statuses/") && !Guid.TryParse(Path.GetFileNameWithoutExtension(x), out _)); // только он
        Assert.Contains("Finished", (await Ok(ws.Run("status", "list"))).Out);
        Assert.Equal(1, (await ws.Run("migrate", "--json")).Json["renamed"]!.AsArray().Count(x => x!["from"]!.GetValue<string>().Contains("/statuses/")));
    }

    [Fact]
    public async Task Migrate_does_not_touch_a_file_whose_name_and_id_disagree_nor_overwrite_a_taken_name()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        MakeOldLayout(ws);
        var statuses = Path.Combine(Directory.GetDirectories(Path.Combine(ws.Root, ".tasker", "projects")).Single(), "statuses");
        var old = Directory.GetFiles(statuses).Order().First();
        var other = Directory.GetFiles(statuses).Order().Last();

        // Чужая или перепутанная сущность в папке: имя говорит об одном id, внутри — другой.
        var wrongName = Path.Combine(statuses, Guid.NewGuid() + ".yaml");
        File.Copy(old, wrongName);
        // И файл, занявший имя, которое получил бы другой.
        var title = File.ReadAllLines(other).First(x => x.StartsWith("name: ")).Substring(6);
        var otherId = Guid.Parse(Path.GetFileNameWithoutExtension(other));
        var occupied = Path.Combine(statuses, EntityFolders.Statuses.FileName(title, otherId));
        File.WriteAllText(occupied, "occupied: true\n");

        var result = await ws.Run("migrate", "--json");

        Assert.Equal(1, result.Code);
        var reasons = result.Json["unreadable"]!.AsArray().Select(x => x!["reason"]!.GetValue<string>()).ToArray();
        Assert.Contains(reasons, x => x.Contains("does not match the file name: not touched"));
        Assert.Contains(reasons, x => x.Contains("a file with that name exists"));
        Assert.True(File.Exists(wrongName));
        Assert.True(File.Exists(other));
        Assert.Equal("occupied: true\n", File.ReadAllText(occupied));
        Assert.StartsWith("formatVersion: 4\n", File.ReadAllText(wrongName)); // совсем не тронут, даже версия
    }

    [Fact]
    public async Task A_renamed_file_arriving_with_git_is_found_by_the_id_inside_after_sync()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);
        var folder = Path.Combine(Directory.GetDirectories(Path.Combine(ws.Root, ".tasker", "projects")).Single(), "statuses");
        var file = Directory.GetFiles(folder, "done-v2-*.yaml").Single();

        // Слияние принесло переименование: другое название в имени (и в файле), тот же id.
        var id = Path.GetFileNameWithoutExtension(file)[^8..];
        var moved = Path.Combine(folder, $"завершено-{id}.yaml");
        File.WriteAllText(moved, File.ReadAllText(file).Replace("name: Done (v2)", "name: Завершено"));
        File.Delete(file);

        await Ok(ws.Run("sync"));

        var list = (await Ok(ws.Run("status", "list"))).Out;
        Assert.Contains("Завершено", list);
        Assert.DoesNotContain("Done (v2)", list);
        Assert.Contains("Завершено", (await Ok(ws.Run("status-set", "get", "Основной поток"))).Out); // набор ссылается по id
    }
}
