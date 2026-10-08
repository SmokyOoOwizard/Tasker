using Autofac;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core;
using Tasker.Core.Locks;
using Tasker.Core.Projects;
using Tasker.Core.Users;
using Tasker.Global;
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
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
/// Открытая на время команды рабочая область — папка (файлы в <c>.tasker</c>) или файл SQLite.
/// С той же папкой одновременно могут работать десктоп, демон MCP и другие команды: кэш и запись файлов
/// защищены короткими блокировками, а не блокировкой всей папки.
/// </summary>
internal sealed class Session : IAsyncDisposable
{
    private readonly IContainer _container;
    private readonly IStorageLifecycle? _lifecycle;

    private Session(IContainer container, IStorageLifecycle? lifecycle, ILifetimeScope scope)
    {
        _container = container;
        _lifecycle = lifecycle;
        Scope = scope;
    }

    public ILifetimeScope Scope { get; }

    public T Get<T>() where T : notnull => Scope.Resolve<T>();

    /// <summary>Сервис, который есть не у всякой области (например, миграция файлов — только у папок); null — нет.</summary>
    public T? GetOptional<T>() where T : class => Scope.ResolveOptional<T>();

    /// <summary>Рабочая область из параметров: файл SQLite или папка (по умолчанию текущая). Папка должна существовать.</summary>
    public static WorkspaceLocation Locate(WorkspaceSettings settings)
    {
        if (settings.Folder != null && settings.SqliteFile != null)
            throw new CliException("Use either --workspace or --sqlite, not both");

        var location = settings.SqliteFile != null
            ? WorkspaceLocation.Sqlite(settings.SqliteFile)
            : WorkspaceLocation.Files(settings.Folder ?? Directory.GetCurrentDirectory());

        if (location.Kind == WorkspaceKind.Files && !Directory.Exists(location.Path))
            throw new CliException($"Folder not found: {location.Path}");
        return location;
    }

    private static bool Exists(WorkspaceLocation location) => location.Kind == WorkspaceKind.Files
        ? Directory.Exists(new TaskerDirectory(location.Path).Projects)
        : File.Exists(location.Path);

    /// <param name="forCompletion">
    /// Открытие для автодополнения: только чтение существующей области — не создаёт ни папки <c>.tasker</c>, ни файла SQLite
    /// и не запускает слежение за папкой (команда живёт доли секунды). Нет области — <see cref="CliException"/>.
    /// </param>
    /// <param name="watch">
    /// Следить за файлами папки, пока область открыта (<c>IWorkspaceIndex.Changed</c>): нужно долгой работе. Команде — нет: слежение
    /// на macOS стоит десятков миллисекунд на запуск и остановку, а индекс сверяется с файлами при открытии и после своих записей.
    /// </param>
    public static async Task<Session> Open(WorkspaceSettings settings, CancellationToken ct, bool forCompletion = false, bool watch = false)
    {
        var location = Locate(settings);
        if (forCompletion && !Exists(location))
            throw new CliException($"No workspace at {location.Path}");

        IContainer? container = null;
        IStorageLifecycle? lifecycle = null;
        try
        {
            var builder = new ContainerBuilder();
            builder.RegisterModule(new CoreModule(lazy: true));
            // Логи хранилища в консоли не нужны: команда печатает только результат.
            builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
            builder.RegisterInstance(new UserOptions(RequireCredentials: false));
            builder.RegisterType<OpenProjectAccess>().As<IProjectAccess>().SingleInstance();
            builder.RegisterType<NoCurrentUser>().As<ICurrentUser>().SingleInstance();
            // Консоль — отдельный держатель блокировок: блокировка интерфейса останавливает и команды.
            builder.RegisterInstance(new SettingsLocalEditor(new SettingsStore(), "cli", " (console)")).As<ILocalEditor>();

            if (location.Kind == WorkspaceKind.Files)
                builder.RegisterModule(new FileStorageModule(location.Path, watchFiles: watch, lazy: true));
            else
                builder.RegisterModule(new DbStorageModule(new DbConfigs { SqliteFile = location.Path }));

            PerfTrace.Mark("container-register");
            container = builder.Build();
            PerfTrace.Mark("container-build");
            lifecycle = container.ResolveOptional<IStorageLifecycle>();
            // Индекс папки строится при первом обращении сам; слежение за ней нужно только долгой работе (демон, десктоп).
            if (lifecycle != null && !(forCompletion && location.Kind == WorkspaceKind.Files))
                await lifecycle.Start(ct);

            PerfTrace.Mark("lifecycle-start");
            return new Session(container, lifecycle, container.BeginLifetimeScope());
        }
        catch
        {
            if (lifecycle != null)
                await lifecycle.Stop(CancellationToken.None);
            if (container != null)
                await container.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Scope.DisposeAsync();
        if (_lifecycle != null)
            await _lifecycle.Stop(CancellationToken.None);
        await _container.DisposeAsync();
    }
}

internal record WorkspaceSettings(string? Folder, string? SqliteFile);
