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

namespace Tasker.Tests;

/// <summary>Папка (<paramref name="Folder"/>) или файл SQLite (<paramref name="SqliteFile"/>) рабочей области.</summary>
internal record WorkspaceSettings(string? Folder, string? SqliteFile);

/// <summary>
/// Рабочая область, открытая общим .NET-кодом хранилищ (тем же, что у десктопа и сервера) внутри процесса тестов: тест работает
/// с ней одновременно с процессами консоли <c>tasker</c>. Без входа и проверок прав, держатель блокировок — локальный.
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

    private sealed class NoCurrentUser : ICurrentUser
    {
        public Guid? Id => null;
    }

    /// <param name="watch">Следить за файлами папки, пока область открыта (<c>IWorkspaceIndex.Changed</c>).</param>
    public static async Task<Session> Open(WorkspaceSettings settings, CancellationToken ct, bool watch = false)
    {
        var location = settings.SqliteFile != null
            ? WorkspaceLocation.Sqlite(settings.SqliteFile)
            : WorkspaceLocation.Files(settings.Folder ?? throw new ArgumentException("No folder and no SQLite file", nameof(settings)));

        IContainer? container = null;
        IStorageLifecycle? lifecycle = null;
        try
        {
            var builder = new ContainerBuilder();
            builder.RegisterModule(new CoreModule(lazy: true));
            builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
            builder.RegisterInstance(new UserOptions(RequireCredentials: false));
            builder.RegisterType<OpenProjectAccess>().As<IProjectAccess>().SingleInstance();
            builder.RegisterType<NoCurrentUser>().As<ICurrentUser>().SingleInstance();
            builder.RegisterInstance(new SettingsLocalEditor(new SettingsStore(), "cli", " (console)")).As<ILocalEditor>();

            if (location.Kind == WorkspaceKind.Files)
                builder.RegisterModule(new FileStorageModule(location.Path, watchFiles: watch, lazy: true));
            else
                builder.RegisterModule(new DbStorageModule(new DbConfigs { SqliteFile = location.Path }));

            container = builder.Build();
            lifecycle = container.ResolveOptional<IStorageLifecycle>();
            if (lifecycle != null)
                await lifecycle.Start(ct);
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
