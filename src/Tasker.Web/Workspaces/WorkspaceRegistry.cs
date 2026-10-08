using Tasker.Storage.Files.Workspaces;
using System.Text;
using Autofac;
using Serilog;
using Tasker.Core;
using Tasker.Core.Dto;
using Tasker.Core.Workspace;
using Tasker.Mcp;
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Storage.Files;

namespace Tasker.Web.Workspaces;

/// <summary>
/// Открытые рабочие области десктопа (вкладки) — единая точка синхронизации в пределах процесса.
/// <para>
/// Папка открывается один раз, сколько бы вкладок её ни показывали: вкладки получают <see cref="WorkspaceLease"/>
/// на общий экземпляр — одни хранилища и одни блокировки записи, поэтому гонок между вкладками нет,
/// а конфликт правок ловит проверка версий (409 <c>modified</c>). Закрылась последняя вкладка — область закрывается.
/// </para>
/// <para>
/// У каждой области свой дочерний контейнер Autofac с модулем хранилища (<see cref="FileStorageModule"/>
/// или <see cref="DbStorageModule"/> для SQLite); сервисы Core — общие регистрации корневого контейнера.
/// Запросы REST и интерфейса попадают в область по адресу <c>/w/{key}/…</c>, где key — имя папки (см. <see cref="WorkspaceRouting"/>);
/// MCP — по одному адресу <c>/mcp</c>, область там — аргумент вызова: то же имя (см. <see cref="McpWorkspaceCatalog"/>).
/// </para>
/// </summary>
public sealed class WorkspaceRegistry(ILifetimeScope root) : IAsyncDisposable
{
    public const string ScopeTag = "workspace";

    // Открытые области: по id (хэш пути — одна папка открывается один раз) и по ключу адреса.
    private readonly Dictionary<string, Workspace> _open = new();
    private readonly Dictionary<string, Workspace> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Открывает область или берёт уже открытую. Освобождать — <see cref="WorkspaceLease.DisposeAsync"/>.</summary>
    public async Task<WorkspaceLease> Open(WorkspaceLocation location, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_open.TryGetValue(location.Id, out var workspace))
            {
                workspace = await Create(location, UniqueKey(location), ct);
                _open.Add(location.Id, workspace);
                _byKey.Add(workspace.Key, workspace);
                Log.Information("Workspace opened: {Location} (name {Key}, address {BasePath})", location, workspace.Key, WorkspaceRouting.BasePath(workspace.Key));
            }

            workspace.References++;
            return new WorkspaceLease(this, workspace);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Открытые области.</summary>
    public IReadOnlyList<WorkspaceLocation> Opened
    {
        get
        {
            _gate.Wait();
            try
            {
                return _open.Values.Select(x => x.Location).ToArray();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <param name="key">Ключ из адреса <c>/w/{key}/…</c>, без учёта регистра.</param>
    internal Workspace? Find(string key)
    {
        _gate.Wait();
        try
        {
            return _byKey.GetValueOrDefault(key);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Открытая область по имени (ключу) без учёта регистра или по каноническому пути папки (файла SQLite) — так её называет агент MCP.
    /// Относительный путь не принимается: «имя» без разделителей — только ключ.
    /// </summary>
    internal Workspace? FindByNameOrPath(string nameOrPath)
    {
        _gate.Wait();
        try
        {
            if (_byKey.TryGetValue(nameOrPath, out var byKey))
                return byKey;
            if (!Path.IsPathRooted(nameOrPath))
                return null;

            try
            {
                return _open.GetValueOrDefault(WorkspaceLocation.Files(nameOrPath).Id)
                    ?? _open.GetValueOrDefault(WorkspaceLocation.Sqlite(nameOrPath).Id);
            }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Открытые области с их ключами (именами для агента).</summary>
    internal IReadOnlyList<(string Key, WorkspaceLocation Location)> OpenedWithKeys
    {
        get
        {
            _gate.Wait();
            try
            {
                return _open.Values.Select(x => (x.Key, x.Location)).ToArray();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    internal async Task Release(Workspace workspace)
    {
        await _gate.WaitAsync();
        try
        {
            if (--workspace.References > 0)
                return;

            _open.Remove(workspace.Location.Id);
            _byKey.Remove(workspace.Key);
        }
        finally
        {
            _gate.Release();
        }

        await workspace.DisposeAsync();
        Log.Information("Workspace closed: {Location}", workspace.Location);
    }

    public async ValueTask DisposeAsync()
    {
        Workspace[] all;
        await _gate.WaitAsync();
        try
        {
            all = _open.Values.ToArray();
            _open.Clear();
            _byKey.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var workspace in all)
            await workspace.DisposeAsync();
    }

    /// <summary>
    /// Ключ — имя папки (или файла БД): оно в адресе интерфейса и в аргументе <c>workspace</c> MCP, которое прописывают агенту,
    /// и оно не меняется между запусками. У одновременно открытых папок с одинаковым именем — суффикс: «Tasker-2».
    /// Вызывается под блокировкой реестра.
    /// </summary>
    private string UniqueKey(WorkspaceLocation location)
    {
        var slug = new StringBuilder();
        foreach (var c in location.Name.Trim())
            slug.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');

        var name = slug.ToString().Trim('-', '.');
        if (name.Length == 0)
            name = "workspace";

        var key = name;
        for (var i = 2; _byKey.ContainsKey(key); i++)
            key = $"{name}-{i}";
        return key;
    }

    private async Task<Workspace> Create(WorkspaceLocation location, string key, CancellationToken ct)
    {
        ILifetimeScope? scope = null;
        try
        {
            scope = root.BeginLifetimeScope(ScopeTag, builder =>
            {
                builder.RegisterInstance(location);
                builder.RegisterType<LocalMcpAgent>().AsSelf().SingleInstance();
                builder.RegisterType<WorkspaceEvents>().AsSelf().SingleInstance();
                builder.RegisterType<WorkspaceLockNotifier>().As<Tasker.Core.Locks.ILockNotifier>().SingleInstance();

                if (location.Kind == WorkspaceKind.Files)
                    builder.RegisterModule(new FileStorageModule(location.Path));
                else
                    builder.RegisterModule(new DbStorageModule(new DbConfigs { SqliteFile = location.Path }));
            });

            // События — до запуска хранилища: первая сверка индекса уже может что-то изменить.
            scope.Resolve<WorkspaceEvents>();

            var lifecycle = scope.ResolveOptional<IStorageLifecycle>();
            if (lifecycle != null)
                await lifecycle.Start(ct);

            return new Workspace(location, key, scope, lifecycle);
        }
        catch
        {
            if (scope != null)
                await scope.DisposeAsync();
            throw;
        }
    }
}

/// <summary>Ссылка вкладки на открытую область. Освобождается один раз; последняя закрывает область.</summary>
public sealed class WorkspaceLease : IAsyncDisposable
{
    private readonly WorkspaceRegistry _registry;
    private readonly Workspace _workspace;
    private int _disposed;

    internal WorkspaceLease(WorkspaceRegistry registry, Workspace workspace)
    {
        _registry = registry;
        _workspace = workspace;
    }

    public WorkspaceLocation Location => _workspace.Location;

    /// <summary>Ключ области — имя папки (см. <see cref="WorkspaceRegistry"/>).</summary>
    public string Key => _workspace.Key;

    /// <summary>Путь приложения внутри хоста: <c>/w/{key}</c>.</summary>
    public string BasePath => WorkspaceRouting.BasePath(Key);

    /// <summary>
    /// Сверяет индекс папки с файлами (есть только у папок). Десктоп вызывает при активации окна, как Unity
    /// при возврате в редактор: пока окно было неактивно, папку могли поменять (git pull), а watcher — пропустить событие.
    /// </summary>
    public async Task Rescan(CancellationToken ct = default)
    {
        if (_disposed != 0 || _workspace.Scope.ResolveOptional<IWorkspaceIndex>() is not { } index)
            return;

        try
        {
            await index.Rescan(ct);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Workspace {Location}: rescan failed", Location);
        }
    }

    /// <summary>
    /// Явная сверка по просьбе пользователя (<c>tasker sync</c>, git-хук): в отличие от <see cref="Rescan"/> не глотает ошибки
    /// и возвращает файлы, которые не удалось прочитать (например, конфликты слияния git) — их показывают сразу.
    /// </summary>
    /// <returns>null — у области нет индекса (SQLite): сверять нечего.</returns>
    public async Task<ListDto<WorkspaceProblem>?> Sync(int problemsLimit, CancellationToken ct = default)
    {
        if (_disposed != 0 || _workspace.Scope.ResolveOptional<IWorkspaceIndex>() is not { } index)
            return null;

        await index.Rescan(ct);
        return await index.GetProblems(Page.Of(0, problemsLimit), ct);
    }

    /// <summary>
    /// Сервис из контейнера открытой области — тот же экземпляр, что у вкладки и MCP: хранилища, уже загруженный индекс.
    /// Не вызывать после <see cref="DisposeAsync"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Аренда уже освобождена.</exception>
    public T Resolve<T>() where T : notnull
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _workspace.Scope.Resolve<T>();
    }

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) == 0 ? new ValueTask(_registry.Release(_workspace)) : ValueTask.CompletedTask;
}

/// <summary>Открытая область: контейнер с хранилищем, блокировка папки и запросы, которые сейчас в ней выполняются.</summary>
internal sealed class Workspace(WorkspaceLocation location, string key, ILifetimeScope scope, IStorageLifecycle? lifecycle)
    : IAsyncDisposable
{
    // Сколько ждать запросы, которые ещё выполняются при закрытии области.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _closing = new();
    private readonly object _sync = new();
    private int _requests;
    private TaskCompletionSource? _drained;

    public WorkspaceLocation Location { get; } = location;

    /// <summary>Часть адреса: <c>/w/{key}/…</c>.</summary>
    public string Key { get; } = key;
    public ILifetimeScope Scope { get; } = scope;

    /// <summary>Вкладки, которые держат область. Меняется под блокировкой реестра.</summary>
    public int References { get; set; }

    /// <summary>Отменяется при закрытии области — долгие запросы (поток событий) завершаются по нему.</summary>
    public CancellationToken Closing => _closing.Token;

    /// <summary>Начало запроса; false — область уже закрывается.</summary>
    public bool TryEnter()
    {
        lock (_sync)
        {
            if (_closing.IsCancellationRequested)
                return false;

            _requests++;
            return true;
        }
    }

    public void Exit()
    {
        lock (_sync)
        {
            if (--_requests == 0)
                _drained?.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task drained;
        lock (_sync)
        {
            if (_closing.IsCancellationRequested)
                return;

            _closing.Cancel();
            _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_requests == 0)
                _drained.TrySetResult();
            drained = _drained.Task;
        }

        if (await Task.WhenAny(drained, Task.Delay(DrainTimeout)) != drained)
            Log.Warning("Workspace {Location}: requests did not finish in {Timeout}, closing anyway", Location, DrainTimeout);

        if (lifecycle != null)
        {
            using var timeout = new CancellationTokenSource(DrainTimeout);
            await lifecycle.Stop(timeout.Token);
        }

        await Scope.DisposeAsync();
        _closing.Dispose();
    }
}
