using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core;
using Tasker.Core.Workspace;
using Tasker.Storage.Files.Index;
using Tasker.Storage.Files.Storages;

namespace Tasker.Storage.Files;

public sealed class FileWorkspaceStorage : IDisposable
{
    private readonly WorkspaceIndex _index;

    private FileWorkspaceStorage(TaskerDirectory directory, WorkspaceIndex index)
    {
        Directory = directory;
        _index = index;
        Storage = new StorageSet(
            new ProjectStorage(directory, index),
            new TaskStorage(directory, index),
            new StatusStorage(directory, index),
            new StatusSetStorage(directory, index),
            new TaskTypeStorage(directory, index),
            new SeriesStorage(directory, index),
            new WriteScope(directory, index),
            new LinkTypeStorage(directory, index),
            new FieldStorage(directory, index),
            new FieldEnumStorage(directory, index),
            new BoardStorage(directory, index),
            new UserStorage(directory, index),
            new EditLockStorage(directory),
            new TaskCommentStorage(directory, index));
        Lifecycle = new IndexOnlyLifecycle(index);
        Migration = new FileMigration(directory, index);
    }

    public TaskerDirectory Directory { get; }
    public StorageSet Storage { get; }
    public IWorkspaceIndex Index => _index;
    public IStorageLifecycle Lifecycle { get; }
    public IFileMigration Migration { get; }

    public static FileWorkspaceStorage Open(string workspacePath)
    {
        var directory = new TaskerDirectory(workspacePath);
        directory.EnsureCreated();
        return new FileWorkspaceStorage(directory, new WorkspaceIndex(directory, NullLogger<WorkspaceIndex>.Instance));
    }

    public void Dispose() => _index.Dispose();
}
