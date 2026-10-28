using System.Text;
using Autofac;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core.Workspace;
using Tasker.Storage.Files;
using Tasker.Storage.Files.Storages;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Порядок ключей файла задачи: <c>description</c> последним. Запись <see cref="TaskFile"/> даёт те же байты, что golden-корпус
/// (его пишет и Rust), чтение принимает оба порядка, <c>tasker migrate</c> переставляет описание в файлах прежнего порядка.
/// </summary>
public class TaskFileKeyOrderTests : IDisposable
{
    private static readonly Guid GoldenProject = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));

    public TaskFileKeyOrderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string GoldenTasks()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var tasks = Path.Combine(directory.FullName, "tests", "golden", "workspace", ".tasker", "projects", "golden-11111111", "tasks");
            if (Directory.Exists(tasks))
                return tasks;
        }
        throw new DirectoryNotFoundException("tests/golden not found");
    }

    public static TheoryData<string> GoldenTaskFiles() =>
        new(Directory.GetFiles(GoldenTasks(), "*.yaml").Select(Path.GetFileName).Order(StringComparer.Ordinal)!);

    /// <summary>Прежний порядок: блок описания сразу после <c>title</c> (обратная перестановка к <see cref="TaskFile.ReorderKeys"/>).</summary>
    private static string OldOrder(string text)
    {
        var start = text.IndexOf("\ndescription:", StringComparison.Ordinal) + 1;
        var block = text[start..];
        text = text[..start];
        var title = text.IndexOf("\ntitle:", StringComparison.Ordinal) + 1;
        var afterTitle = text.IndexOf("\ntypeId:", title, StringComparison.Ordinal) + 1;
        return text[..afterTitle] + block + text[afterTitle..];
    }

    [Theory]
    [MemberData(nameof(GoldenTaskFiles))]
    public async Task Writing_a_golden_task_gives_the_same_bytes_and_both_key_orders_read_the_same(string name)
    {
        var golden = Path.Combine(GoldenTasks(), name);
        var bytes = await File.ReadAllBytesAsync(golden);
        var task = (await TaskFile.Read(GoldenProject, golden, CancellationToken.None))!;

        var written = Path.Combine(_root, name);
        await TaskFile.Write(written, task, CancellationToken.None);
        Assert.Equal(Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(await File.ReadAllBytesAsync(written)));

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Null(TaskFile.ReorderKeys(text));
        if (task.Description == null)
            return;

        var old = OldOrder(text);
        Assert.NotEqual(text, old);
        Assert.Equal(text, TaskFile.ReorderKeys(old));
        var oldPath = Path.Combine(_root, "old-" + name);
        await File.WriteAllTextAsync(oldPath, old);
        var fromOld = (await TaskFile.Read(GoldenProject, oldPath, CancellationToken.None))!;
        Assert.Equal(task.Description, fromOld.Description);
        Assert.Equal(task.Title, fromOld.Title);
        Assert.Equal(task.TypeId, fromOld.TypeId);
        Assert.Equal(task.Fields.Count, fromOld.Fields.Count);
    }

    [Fact]
    public void ReorderKeys_moves_only_the_description_block()
    {
        Assert.Equal(
            "formatVersion: 9\nid: 1\ntitle: x\ntypeId: 2\nfields:\n- id: 3\ndescription: |-\n  a\n\n  b\n",
            TaskFile.ReorderKeys("formatVersion: 9\nid: 1\ntitle: x\ndescription: |-\n  a\n\n  b\ntypeId: 2\nfields:\n- id: 3\n"));
        Assert.Equal("id: 1\r\ntypeId: 2\r\ndescription: 'a:\r\n  b'\r\n", TaskFile.ReorderKeys("id: 1\r\ndescription: 'a:\r\n  b'\r\ntypeId: 2"));
        Assert.Null(TaskFile.ReorderKeys("id: 1\ndescription: |+\n  a\n\n"));
        Assert.Null(TaskFile.ReorderKeys("id: 1\ndescription: a\n# note\n"));
        Assert.Null(TaskFile.ReorderKeys("id: 1\ndescriptionX: a\ntitle: b\n"));
        Assert.Null(TaskFile.ReorderKeys("title: |\n  description: a\nid: 1\n"));
    }

    [Fact]
    public async Task Migrate_rewrites_task_files_in_the_old_key_order()
    {
        var builder = new ContainerBuilder();
        builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterModule(new FileStorageModule(_root));
        using var container = builder.Build();
        var migration = container.Resolve<IFileMigration>();

        var project = Guid.NewGuid();
        var id = Guid.Parse("0b6f5f9e-5c1a-4d7e-9a4b-2f1d3c4e5a6b");
        var tasks = new TaskerDirectory(_root).Project(project).Tasks;
        Directory.CreateDirectory(tasks);
        var current = Path.Combine(tasks, "current-0b6f5f9e.yaml");
        var head = $"formatVersion: {FormatVersions.Current}\nid: {id}\ntitle: Current\n";
        var tail = "typeId: 9c8b7a6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d\nstatusId: 5d2c1a3b-7e4f-4a6b-8c9d-0e1f2a3b4c5d\n"
            + "createdAt: 2026-09-25T10:00:00.0000000+00:00\nupdatedAt: 2026-09-25T10:00:00.0000000+00:00\n";
        await File.WriteAllTextAsync(current, head + "description: |-\n  One\n  Two\n" + tail);

        var check = await migration.Run(new MigrationOptions(DryRun: true));
        Assert.True(check.NeedsAttention);
        Assert.Equal([$"projects/{project}/tasks/current-0b6f5f9e.yaml"], check.Reordered);
        Assert.Empty(check.Migrated);
        Assert.Equal(0, check.UpToDate);
        Assert.Equal(head + "description: |-\n  One\n  Two\n" + tail, await File.ReadAllTextAsync(current));

        var report = await migration.Run(new MigrationOptions());
        Assert.Single(report.Reordered);
        Assert.Equal(head + tail + "description: |-\n  One\n  Two\n", await File.ReadAllTextAsync(current));

        var again = await migration.Run(new MigrationOptions(DryRun: true));
        Assert.False(again.NeedsAttention);
        Assert.Equal(1, again.UpToDate);
    }
}
