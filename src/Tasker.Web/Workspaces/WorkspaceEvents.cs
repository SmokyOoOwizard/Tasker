using Tasker.Storage.Files.Workspaces;
using System.Collections.Concurrent;
using System.Text.Json;
using Tasker.Core;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Tasker.Core.Workspace;
using Tasker.Storage.Files;

namespace Tasker.Web.Workspaces;

/// <summary>
/// Что изменилось в рабочей области: <c>entity</c> — project, task, taskType, linkType, field, enum, status, statusSet, board, series, user,
/// <c>lock</c> (у сущности с этим id взяли или сняли блокировку на время правки; вид сущности в событии не указан)
/// или <c>unknown</c> (что-то изменилось — перечитать всё); id — если известен.
/// </summary>
public sealed record WorkspaceChange(string Entity, Guid? ProjectId, Guid? Id);

/// <summary>
/// Уведомления вкладок об изменениях в рабочей области — чтобы две вкладки (и два окна) одной папки
/// видели правки друг друга, а также правки агентов через MCP, руками и через git.
/// <list type="bullet">
/// <item>Папка: изменения индекса (<see cref="IWorkspaceIndex.Changed"/>) — он видит любые изменения файлов,
/// откуда бы они ни пришли, а событие приходит, когда списки уже их показывают.</item>
/// <item>SQLite: снаружи БД не следим — событие <c>unknown</c> после каждого успешного изменяющего запроса к области.</item>
/// </list>
/// Одинаковые события за <see cref="Debounce"/> склеиваются. Клиенты получают их потоком SSE: <c>GET /w/{key}/api/events</c>.
/// </summary>
public sealed class WorkspaceEvents : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);
    
    // Столько файлов сразу (checkout, полная сверка) — проще сказать «перечитайте всё».
    private const int ManyFiles = 200;

    private readonly IWorkspaceIndex? _index;
    private readonly ConcurrentDictionary<Channel<WorkspaceChange>, byte> _subscribers = new();
    private readonly ConcurrentDictionary<WorkspaceChange, byte> _pending = new();
    private readonly Timer _flush;

    /// <param name="index">Индекс папки; у рабочей области в SQLite его нет.</param>
    public WorkspaceEvents(IWorkspaceIndex? index = null)
    {
        _index = index;
        _flush = new Timer(_ => Flush());
        if (_index != null)
            _index.Changed += OnIndexChanged;
    }

    /// <summary>Изменение, которое не видно в файлах (рабочая область в SQLite).</summary>
    public void Publish(WorkspaceChange change)
    {
        _pending.TryAdd(change, 0);
        _flush.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Блокировку на время правки взяли или сняли: вкладкам перечитать блокировку сущности <paramref name="id"/>.</summary>
    public void PublishLock(Guid id) => Publish(new WorkspaceChange("lock", null, id));

    /// <summary>Поток SSE до отключения клиента, закрытия рабочей области или остановки приложения.</summary>
    public async Task Stream(HttpContext context, CancellationToken workspaceClosing)
    {
        // Без остановки приложения хост ждал бы открытые потоки до конца ShutdownTimeout.
        var appStopping = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, workspaceClosing, appStopping);
        var channel = Channel.CreateBounded<WorkspaceChange>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _subscribers.TryAdd(channel, 0);

        try
        {
            var response = context.Response;
            response.Headers.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            await response.WriteAsync(": connected\n\n", stop.Token);
            await response.Body.FlushAsync(stop.Token);

            while (!stop.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                wait.CancelAfter(Heartbeat);
                try
                {
                    var change = await channel.Reader.ReadAsync(wait.Token);
                    await response.WriteAsync($"data: {JsonSerializer.Serialize(change, TaskerJson.Options)}\n\n", stop.Token);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    // Комментарий SSE раз в Heartbeat: соединение не считается простаивающим.
                    await response.WriteAsync(": ping\n\n", stop.Token);
                }

                await response.Body.FlushAsync(stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Клиент отключился или область закрылась.
        }
        finally
        {
            _subscribers.TryRemove(channel, out _);
        }
    }

    public void Dispose()
    {
        if (_index != null)
            _index.Changed -= OnIndexChanged;
        _flush.Dispose();
    }

    private void OnIndexChanged(IReadOnlyList<WorkspaceFileChange> files)
    {
        // Пустой путь — вся папка (полная сверка).
        if (files.Count > ManyFiles || files.Any(x => x.Path == ""))
        {
            Publish(new WorkspaceChange("unknown", null, null));
            return;
        }

        foreach (var file in files)
        {
            if (Parse(file) is { } change)
                Publish(change);
        }
    }

    private void Flush()
    {
        foreach (var change in _pending.Keys)
        {
            _pending.TryRemove(change, out _);
            foreach (var subscriber in _subscribers.Keys)
                subscriber.Writer.TryWrite(change);
        }
    }

    /// <summary>
    /// Изменённый файл → что изменилось (раскладка — см. <see cref="TaskerDirectory"/>). Вид сущности и проект — из пути, а id — от индекса,
    /// из содержимого файла: сущности проекта называются по названию (<c>исправить-вход-3f2a9c1e.yaml</c>), и id в имени только начало.
    /// Папка целиком (<c>tasks/</c>) — без id.
    /// </summary>
    private static WorkspaceChange? Parse(WorkspaceFileChange file)
    {
        var parts = file.Path.Split('/');
        var id = file.Id;
        switch (parts)
        {
            case ["users", _]:
                return new WorkspaceChange("user", null, id);

            case ["projects", var project] when Guid.TryParse(project, out var projectId):
                return new WorkspaceChange("project", projectId, projectId);

            case ["projects", var project, var item] when Guid.TryParse(project, out var projectId):
                return item == ProjectDirectory.ProjectFileName
                    ? new WorkspaceChange("project", projectId, projectId)
                    // Папка tasks/ и т. п. целиком.
                    : new WorkspaceChange(EntityOf(item), projectId, null);

            case ["projects", var project, var folder, _] when Guid.TryParse(project, out var projectId):
                return new WorkspaceChange(EntityOf(folder), projectId, id);

            default:
                return null;
        }
    }

    private static string EntityOf(string folder) => folder switch
    {
        "tasks" => "task",
        "task-types" => "taskType",
        "statuses" => "status",
        "status-sets" => "statusSet",
        "boards" => "board",
        "series" => "series",
        "link-types" => "linkType",
        "fields" => "field",
        "enums" => "enum",
        _ => "unknown"
    };
}

/// <summary>Блокировки правки сообщают вкладкам о себе событием <c>lock</c> (REST и MCP одинаково).</summary>
internal sealed class WorkspaceLockNotifier(WorkspaceEvents events) : Tasker.Core.Locks.ILockNotifier
{
    public void Changed(Tasker.Core.Locks.LockedEntity entity, Guid id) => events.PublishLock(id);
}

internal static class WorkspaceEventsEndpoint
{
    /// <summary>Десктоп: <c>GET /api/events</c> внутри рабочей области — поток SSE <see cref="WorkspaceChange"/>.</summary>
    public static void MapWorkspaceEvents(this IEndpointRouteBuilder api) =>
        api.MapGet("/events", (HttpContext context) =>
        {
            var workspace = context.Features.Get<WorkspaceFeature>()!;
            return context.RequestServices.GetRequiredService<WorkspaceEvents>().Stream(context, workspace.Closing);
        });

    /// <summary>
    /// Рабочая область в SQLite: после успешного изменяющего запроса — событие <c>unknown</c>
    /// (изменения в файлах ловит <see cref="FileSystemWatcher"/>, здесь их не дублируем).
    /// </summary>
    public static void UseSqliteChangeEvents(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            await next(context);

            if (context.Features.Get<WorkspaceFeature>() is { Location.Kind: WorkspaceKind.Sqlite }
                && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                // Блокировки правки данных не меняют и о себе сообщают отдельным событием lock.
                && context.Request.Path.Value?.EndsWith("/lock", StringComparison.Ordinal) != true
                && context.Response.StatusCode is >= 200 and < 300)
            {
                context.RequestServices.GetRequiredService<WorkspaceEvents>().Publish(new WorkspaceChange("unknown", null, null));
            }
        });
}
