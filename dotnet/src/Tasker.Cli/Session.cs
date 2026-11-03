using Tasker.Core;
using Tasker.Core.Locks;
using Tasker.Core.Projects;
using Tasker.Core.Users;
using Tasker.Core.Workspace;
using Tasker.Global;
using Tasker.Storage.Files;
using Tasker.Storage.Files.Workspaces;

namespace Tasker.Cli;

/// <summary>Ошибка пользователя команды: сообщение выводится как есть, без стека.</summary>
internal class CliException(string message) : Exception(message);

/// <summary>Кто выполняет команды. В консоли, как на десктопе, входа нет: проверок прав нет.</summary>
internal sealed class NoCurrentUser : ICurrentUser
{
    public Guid? Id => null;
}

/// <summary>
/// Открытая на время команды рабочая область — папка (файлы в <c>.tasker</c>).
/// С той же папкой одновременно могут работать десктоп, демон MCP и другие команды: кэш и запись файлов
/// защищены короткими блокировками, а не блокировкой всей папки.
/// </summary>
internal sealed class Session : IAsyncDisposable
{
    private readonly CoreServices _services;
    private readonly FileWorkspaceStorage _files;

    private Session(FileWorkspaceStorage files)
    {
        _files = files;
        _services = new CoreServices(files.Storage, new NoCurrentUser(), new OpenProjectAccess(), new UserOptions(RequireCredentials: false),
            new SettingsLocalEditor(new SettingsStore(), "cli", " (console)"));
    }

    public T Get<T>() where T : class => GetOptional<T>() ?? throw new InvalidOperationException($"{typeof(T).Name} is not available in this workspace");

    /// <summary>Сервис, который есть не у всякой области (например, миграция файлов — только у папок); null — нет.</summary>
    public T? GetOptional<T>() where T : class
    {
        if (typeof(T) == typeof(IFileMigration))
            return (T?)_files.Migration;
        if (typeof(T) == typeof(IWorkspaceIndex))
            return (T?)_files.Index;
        return _services.Find<T>();
    }

    /// <summary>Рабочая область из параметров: папка (по умолчанию текущая). Папка должна существовать.</summary>
    public static WorkspaceLocation Locate(WorkspaceSettings settings)
    {
        var location = WorkspaceLocation.Files(settings.Folder ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(location.Path))
            throw new CliException($"Folder not found: {location.Path}");
        return location;
    }

    private static bool Exists(WorkspaceLocation location) => Directory.Exists(new TaskerDirectory(location.Path).Projects);

    /// <param name="forCompletion">
    /// Открытие для автодополнения: только чтение существующей области — не создаёт ни папки <c>.tasker</c>, ни файла SQLite
    /// и не запускает слежение за папкой (команда живёт доли секунды). Нет области — <see cref="CliException"/>.
    /// </param>
    public static async Task<Session> Open(WorkspaceSettings settings, CancellationToken ct, bool forCompletion = false)
    {
        var location = Locate(settings);
        if (forCompletion && !Exists(location))
            throw new CliException($"No workspace at {location.Path}");

        var files = FileWorkspaceStorage.Open(location.Path);
        try
        {
            var session = new Session(files);
            PerfTrace.Mark("storage-open");
            if (!forCompletion)
                await files.Lifecycle.Start(ct);

            PerfTrace.Mark("lifecycle-start");
            return session;
        }
        catch
        {
            files.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _files.Lifecycle.Stop(CancellationToken.None);
        _files.Dispose();
    }
}

internal record WorkspaceSettings(string? Folder);
