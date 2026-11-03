using Serilog;
using Tasker.Core.Links;
using Tasker.Core.Projects;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;
using Tasker.Core.Workspace;
using Tasker.Daemon;
using Tasker.Global;
using Tasker.Mcp;
using Tasker.Storage.Files.Workspaces;
using Tasker.Web.Workspaces;

namespace Tasker.Daemon.Host;

/// <summary>
/// Держит открытыми ровно те рабочие области, что перечислены в настройках: добавили область командой
/// или из десктопа — демон открывает её, не перезапускаясь; убрали — закрывает. Область, которую открыть
/// не удалось (папку удалили, диск не подключён), не мешает остальным и пробуется снова при следующем
/// изменении настроек и по таймеру.
/// </summary>
internal sealed class WorkspaceSync(WorkspaceRegistry registry, DaemonState state, int port) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (WorkspaceLease Lease, WorkspaceEntry Entry)> _open = new();
    private readonly Dictionary<string, string> _failed = new();
    // Последнее сообщенное состояние серий по областям: в лог пишем, только когда оно изменилось.
    private readonly Dictionary<string, string> _reported = new();
    private IReadOnlyList<WorkspaceEntry> _desired = [];
    private bool _disposed;

    /// <summary>
    /// Сразу помечает области из настроек как открывающиеся — до того, как хост начнёт отвечать:
    /// иначе статус на миг показал бы «областей нет», и запуск счёл бы демон готовым раньше времени.
    /// </summary>
    public void Prepare(IReadOnlyList<WorkspaceEntry> desired)
    {
        _desired = desired;
        Publish(opening: desired.Select(x => x.Location.Id).ToHashSet());
    }

    /// <summary>Приводит открытые области к списку из настроек.</summary>
    public async Task Apply(IReadOnlyList<WorkspaceEntry> desired)
    {
        await _gate.WaitAsync();
        try
        {
            _desired = desired;
            if (!_disposed)
                await Reconcile();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Сверяет индекс открытой области с файлами сейчас (по просьбе <c>tasker sync</c> или git-хука).
    /// Сверка идёт вне блокировки списка областей: долгая сверка не задерживает их открытие и закрытие.
    /// </summary>
    /// <returns>null — эта область не открыта в демоне.</returns>
    public async Task<SyncResult?> Sync(WorkspaceLocation location, CancellationToken ct)
    {
        WorkspaceLease lease;
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed || !_open.TryGetValue(location.Id, out var open))
                return null;
            lease = open.Lease;
        }
        finally
        {
            _gate.Release();
        }

        var problems = await lease.Sync(SyncResult.ProblemsShown, ct);
        var series = await ComputeSeries(lease, ct);
        Report(location.Id, location.Path, series);
        return new SyncResult(location.Path, problems?.TotalCount ?? 0, problems?.Data ?? []) { Series = series };
    }

    /// <summary>Сводка по сериям через контейнер уже открытой области (хранилища и индекс — те же, второй раз не грузятся).</summary>
    internal static async Task<ProjectSeriesHealth[]> ComputeSeries(WorkspaceLease lease, CancellationToken ct)
    {
        try
        {
            return await SeriesReports.Compute(lease.Resolve<IProjectStorage>(), lease.Resolve<ITaskStorage>(), lease.Resolve<ISeriesStorage>(),
                lease.Resolve<ILinkTypeStorage>(), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [new ProjectSeriesHealth(Guid.Empty, "(workspace)", [], [], 0, 0, $"cannot check the series: {e.Message}")];
        }
    }

    /// <summary>
    /// Пишет в лог, что не так с сериями: дубликат префикса — Error («требуется переименование»), остальное — Warning.
    /// Повторяет сообщение, только когда состояние области изменилось.
    /// </summary>
    private void Report(string id, string path, ProjectSeriesHealth[] series)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var linkWarnings = new List<string>();
        foreach (var project in series)
        {
            foreach (var conflict in project.PrefixConflicts)
                errors.Add($"project '{project.ProjectName}': series prefix '{conflict.Prefix}' is used by several series: {string.Join(", ", conflict.SeriesIds)}: {SeriesReports.PrefixHint}");
            foreach (var conflict in project.NumberConflicts)
                warnings.Add($"project '{project.ProjectName}': {conflict.Reference} is used by tasks {string.Join(", ", conflict.TaskIds)}");
            if (project.TasksWithInvalidSeries > 0)
                warnings.Add($"project '{project.ProjectName}': {project.TasksWithInvalidSeries} task(s) refer to a missing series (run 'tasker cleanup')");
            if (project.UnreadableSeriesFiles > 0)
                warnings.Add($"project '{project.ProjectName}': {project.UnreadableSeriesFiles} series file(s) cannot be read");
            foreach (var line in SeriesReports.DescribeLinks(project))
                linkWarnings.Add($"project '{project.ProjectName}': {line}");
            if (project.Error != null)
                warnings.Add($"project '{project.ProjectName}': {project.Error}");
        }

        var signature = string.Join('\n', errors.Concat(warnings).Concat(linkWarnings));
        lock (_reported)
        {
            if (_reported.GetValueOrDefault(id, "") == signature)
                return;
            _reported[id] = signature;
        }

        if (errors.Count > 0)
            Log.Error("Workspace {Path}: series rename required: {Problems}", path, string.Join("; ", errors));
        if (warnings.Count > 0)
            Log.Warning("Workspace {Path}: series need attention: {Problems}", path, string.Join("; ", warnings));
        if (linkWarnings.Count > 0)
            Log.Warning("Workspace {Path}: links need attention: {Problems}", path, string.Join("; ", linkWarnings));
    }

    /// <summary>Повторяет открытие областей, которые не открылись.</summary>
    public async Task Retry()
    {
        await _gate.WaitAsync();
        try
        {
            if (_failed.Count > 0 && !_disposed)
                await Reconcile();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var (lease, _) in _open.Values)
                await lease.DisposeAsync();
            _open.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task Reconcile()
    {
        var wanted = _desired.Select(x => (Entry: x, Location: x.Location)).DistinctBy(x => x.Location.Id).ToArray();
        var ids = wanted.Select(x => x.Location.Id).ToHashSet();

        foreach (var id in _open.Keys.Where(x => !ids.Contains(x)).ToArray())
        {
            Log.Information("Workspace removed from MCP: {Path}", _open[id].Entry.Path);
            await _open[id].Lease.DisposeAsync();
            _open.Remove(id);
            lock (_reported)
                _reported.Remove(id);
        }
        foreach (var id in _failed.Keys.Where(x => !ids.Contains(x)).ToArray())
            _failed.Remove(id);

        Publish(opening: wanted.Where(x => !_open.ContainsKey(x.Location.Id)).Select(x => x.Location.Id).ToHashSet());

        var opened = new List<WorkspaceLocation>();
        foreach (var (entry, location) in wanted.Where(x => !_open.ContainsKey(x.Location.Id)))
        {
            try
            {
                // Не создаём .tasker в папке, которой уже нет: открытие делает недостающие каталоги.
                if (location.Kind == WorkspaceKind.Files ? !Directory.Exists(location.Path) : !File.Exists(location.Path))
                    throw new DirectoryNotFoundException($"{location.Path} does not exist");

                // Тестовый крючок: искусственно медленное открытие области (проверка ожидания открытия в вызове MCP).
                if (int.TryParse(AppEnvironment.Get("TASKER_MCP_OPEN_DELAY_MS"), out var delay) && delay > 0)
                    await Task.Delay(delay);

                var lease = await registry.Open(location);
                _open[location.Id] = (lease, entry);
                _failed.Remove(location.Id);
                opened.Add(location);
                Log.Information("Workspace available through MCP: {Path}, argument workspace=\"{Key}\" (address {Url})", location.Path, lease.Key, McpRegistration.LocalUrl(port));
            }
            catch (Exception e)
            {
                _failed[location.Id] = e.Message;
                Log.Warning("Cannot open workspace {Path}: {Error}", entry.Path, e.Message);
            }

            Publish(opening: wanted.Where(x => !_open.ContainsKey(x.Location.Id) && !_failed.ContainsKey(x.Location.Id)).Select(x => x.Location.Id).ToHashSet());
        }

        Publish(opening: []);

        // Состояние серий сообщаем после открытия, когда статус уже опубликован: сверка серий не задерживает готовность.
        foreach (var location in opened)
        {
            try
            {
                Report(location.Id, location.Path, await ComputeSeries(_open[location.Id].Lease, CancellationToken.None));
            }
            catch (Exception e)
            {
                Log.Warning(e, "Workspace {Path}: cannot check the series", location.Path);
            }
        }
    }

    private void Publish(HashSet<string> opening) =>
        state.SetWorkspaces(_desired
            .Select(x => (Entry: x, Id: x.Location.Id))
            .DistinctBy(x => x.Id)
            .Select(x => _open.TryGetValue(x.Id, out var open)
                ? new WorkspaceStatus(x.Entry.Kind, x.Entry.Path, WorkspaceState.Open, open.Lease.Key, null)
                : _failed.TryGetValue(x.Id, out var error)
                    ? new WorkspaceStatus(x.Entry.Kind, x.Entry.Path, WorkspaceState.Failed, null, error)
                    : new WorkspaceStatus(x.Entry.Kind, x.Entry.Path, opening.Contains(x.Id) ? WorkspaceState.Opening : WorkspaceState.Failed, null, null))
            .ToArray());
}
