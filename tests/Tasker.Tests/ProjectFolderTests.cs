using Autofac;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core.Dto;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Workspace;
using Tasker.Storage.Files;
using Tasker.Storage.Files.Index;
using Tasker.Storage.Files.Storages;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Папка проекта <c>projects/&lt;slug имени&gt;-&lt;id8&gt;/</c> (TSK-169): раскладка принимает новое и прежнее имя (Guid), новый проект —
/// в папке по названию, переименование переносит папку (параллельный читатель видит проект всё время), две папки одного проекта
/// читаются вместе и сообщаются как проблема, <c>migrate</c> переименовывает и сливает папки.
/// </summary>
public class ProjectFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<IContainer> _containers = [];

    public ProjectFolderTests() => Directory.CreateDirectory(_root);

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

    private string Projects => new TaskerDirectory(_root).Projects;

    private string[] Folders() =>
        Directory.Exists(Projects) ? Directory.GetDirectories(Projects).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()! : [];

    private static Project NewProject(string name) => new() { Id = Guid.NewGuid(), Name = name, CreatedAt = DateTimeOffset.UnixEpoch, Version = "" };

    private static Status NewStatus(Guid project, string name) =>
        new() { Id = Guid.NewGuid(), ProjectId = project, Name = name, Color = "#808080", Version = "" };

    private static string Id8(Guid id) => EntityFileNames.IdPrefix(id);

    [Fact]
    public void Folder_names_follow_the_entity_file_rules_and_both_names_are_recognised()
    {
        var id = Guid.Parse("3f2a9c1e-7b4d-4e6a-8c1f-5d2b9a7e4c30");
        Assert.Equal("мой-проект-v2-3f2a9c1e", ProjectFolders.FolderName("Мой проект: v2", id));
        Assert.Equal("project-3f2a9c1e", ProjectFolders.FolderName("!!!", id));
        Assert.True(ProjectFolders.IsCurrent("мой-проект-v2-3f2a9c1e", "Мой проект: v2", id));
        Assert.True(ProjectFolders.TryParse(id.ToString(), out var id8, out var guid));
        Assert.Equal(("3f2a9c1e", (Guid?)id), (id8, guid));
        Assert.True(ProjectFolders.TryParse("golden-11111111", out id8, out guid));
        Assert.Equal(("11111111", (Guid?)null), (id8, guid));
        Assert.False(ProjectFolders.TryParse("golden", out _, out _));
    }

    [Fact]
    public async Task A_new_project_lives_in_a_folder_named_after_it_and_renaming_moves_the_folder()
    {
        var container = Open();
        var projects = container.Resolve<IProjectStorage>();
        var statuses = container.Resolve<IStatusStorage>();
        var project = NewProject("Test");
        var version = await projects.Add(project);
        var status = NewStatus(project.Id, "Todo");
        await statuses.Add(status);
        Assert.Equal([$"test-{Id8(project.Id)}"], Folders());

        Assert.NotNull(await projects.Update(project with { Name = "Мой проект: v2" }, version));
        Assert.Equal([$"мой-проект-v2-{Id8(project.Id)}"], Folders());
        Assert.Equal("Мой проект: v2", (await projects.GetById(project.Id))!.Name);
        Assert.Equal("Todo", (await statuses.GetById(project.Id, status.Id))!.Name);
        Assert.Single((await projects.GetRange(null, new Page(0, 50))).Data);
        var text = await File.ReadAllTextAsync(Path.Combine(Projects, Folders()[0], "project.yaml"));
        Assert.StartsWith($"formatVersion: {FormatVersions.Current}\n", text);
    }

    [Fact]
    public async Task Two_folders_of_one_project_are_read_together_reported_and_deleted_together()
    {
        var container = Open();
        var projects = container.Resolve<IProjectStorage>();
        var statuses = container.Resolve<IStatusStorage>();
        var index = container.Resolve<WorkspaceIndex>();
        var project = NewProject("Test");
        var version = await projects.Add(project);
        var status = NewStatus(project.Id, "Todo");
        await statuses.Add(status);

        var main = $"test-{Id8(project.Id)}";
        var second = $"old-name-{Id8(project.Id)}";
        var file = Directory.GetFiles(Path.Combine(Projects, main, "statuses")).Single();
        Directory.CreateDirectory(Path.Combine(Projects, second, "statuses"));
        File.Move(file, Path.Combine(Projects, second, "statuses", Path.GetFileName(file)));
        await index.Rescan();

        Assert.Equal("Todo", (await statuses.GetById(project.Id, status.Id))!.Name);
        Assert.Single((await statuses.GetRange(project.Id, new Page(0, 50))).Data);
        var problem = Assert.Single((await index.GetProblems(new Page(0, 50))).Data);
        Assert.Equal(new WorkspaceProblem($"projects/{second}", ProjectFolders.SecondFolderReason(main)), problem);

        Assert.True(await projects.Delete(project.Id, version));
        Assert.Empty(Folders());
        Assert.Empty((await index.GetProblems(new Page(0, 50))).Data);
    }

    [Fact]
    public async Task Migrate_renames_a_legacy_folder_and_merges_a_second_folder_without_losing_files()
    {
        var container = Open();
        var projects = container.Resolve<IProjectStorage>();
        var statuses = container.Resolve<IStatusStorage>();
        var project = NewProject("Legacy");
        await projects.Add(project);
        await statuses.Add(NewStatus(project.Id, "Todo"));
        var named = $"legacy-{Id8(project.Id)}";
        Directory.Move(Path.Combine(Projects, named), Path.Combine(Projects, project.Id.ToString()));
        // Вторая папка: задача-файл, которого нет в основной, и файл с тем же именем, но другим содержимым.
        var second = Path.Combine(Projects, $"old-{Id8(project.Id)}", "statuses");
        Directory.CreateDirectory(second);
        await File.WriteAllTextAsync(Path.Combine(second, "extra-00000001.yaml"), "x");
        var clash = Path.GetFileName(Directory.GetFiles(Path.Combine(Projects, project.Id.ToString(), "statuses")).Single());
        await File.WriteAllTextAsync(Path.Combine(second, clash), "theirs");

        var migration = container.Resolve<IFileMigration>();
        var dry = await migration.Run(new MigrationOptions(DryRun: true));
        Assert.Equal([new MigrationRename($"projects/{project.Id}", $"projects/{named}")], dry.RenamedFolders);
        Assert.Equal([new MigrationRename($"projects/old-{Id8(project.Id)}", $"projects/{named}")], dry.MergedFolders);
        Assert.Contains(dry.Unreadable, x => x.Path == $"projects/old-{Id8(project.Id)}/statuses/{clash}");
        Assert.True(dry.NeedsAttention);
        Assert.Contains(project.Id.ToString(), Folders());

        await migration.Run(new MigrationOptions());
        Assert.Equal([named, $"old-{Id8(project.Id)}"], Folders());
        Assert.True(File.Exists(Path.Combine(Projects, named, "statuses", "extra-00000001.yaml")));
        Assert.Equal("theirs", await File.ReadAllTextAsync(Path.Combine(second, clash)));
        Assert.Equal("Legacy", (await projects.GetById(project.Id))!.Name);
        Assert.StartsWith($"formatVersion: {FormatVersions.Current}\n", await File.ReadAllTextAsync(Path.Combine(Projects, named, "project.yaml")));
    }

    [Fact]
    public async Task A_reader_in_another_process_sees_the_project_while_it_is_renamed_again_and_again()
    {
        var writer = Open();
        var reader = Open();
        var projects = writer.Resolve<IProjectStorage>();
        var project = NewProject("Start");
        var version = await projects.Add(project);
        var status = NewStatus(project.Id, "Todo");
        await writer.Resolve<IStatusStorage>().Add(status);

        using var stop = new CancellationTokenSource();
        var reads = Task.Run(async () =>
        {
            var count = 0;
            var readProjects = reader.Resolve<IProjectStorage>();
            var readStatuses = reader.Resolve<IStatusStorage>();
            while (!stop.IsCancellationRequested)
            {
                Assert.NotNull(await readProjects.GetById(project.Id));
                Assert.NotNull(await readStatuses.GetById(project.Id, status.Id));
                count++;
            }
            return count;
        });

        for (var i = 0; i < 30; i++)
        {
            version = (await projects.Update(project with { Name = $"Name {i}" }, version))!;
            await writer.Resolve<IStatusStorage>().Add(NewStatus(project.Id, $"Status {i}"));
        }
        await stop.CancelAsync();
        Assert.True(await reads > 0);
        Assert.Equal([$"name-29-{Id8(project.Id)}"], Folders());
        Assert.Equal(31, (await writer.Resolve<IStatusStorage>().GetRange(project.Id, new Page(0, 100))).TotalCount);
    }
}
