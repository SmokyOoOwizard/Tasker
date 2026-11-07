using Tasker.Storage.Files.Workspaces;
using System.Collections.Concurrent;
using Tasker.Core;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Tasker.Core.Workspace;
using Tasker.Storage.Files;

namespace Tasker.Web.Workspaces;

/// <summary>
/// Что изменилось в рабочей области: <c>entity</c> — project, task, taskType, linkType, field, enum, status, statusSet, board, series, comment, user,
/// <c>lock</c> (у сущности с этим id взяли или сняли блокировку на время правки; вид сущности в событии не указан)
/// или <c>unknown</c> (что-то изменилось — перечитать всё); id — если известен.
/// </summary>
public sealed record WorkspaceChange(string Entity, Guid? ProjectId, Guid? Id, Tasker.Core.Locks.EditHolder? Author = null);

/// <summary>
/// Уведомления вкладок об изменениях в рабочей области — чтобы две вкладки (и два окна) одной папки
/// видели правки друг друга, а также правки агентов через MCP, руками и через git.
/// Источник — изменения индекса (<see cref="IWorkspaceIndex.Changed"/>): он видит любые изменения файлов,
/// откуда бы они ни пришли, а событие приходит, когда списки уже их показывают.
/// Одинаковые события за <see cref="Debounce"/> склеиваются. Клиенты получают их подпиской GraphQL <c>workspaceChanges</c>: <c>POST /w/{key}/api/graphql</c>.
/// </summary>
public sealed class WorkspaceEvents : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    
    // Столько файлов сразу (checkout, полная сверка) — проще сказать «перечитайте всё».
    private const int ManyFiles = 200;

    private readonly IWorkspaceIndex _index;
    private readonly ConcurrentDictionary<Channel<WorkspaceChange>, byte> _subscribers = new();
    private readonly ConcurrentDictionary<WorkspaceChange, byte> _pending = new();
    private readonly Timer _flush;

    public WorkspaceEvents(IWorkspaceIndex index)
    {
        _index = index;
        _flush = new Timer(_ => Flush());
        _index.Changed += OnIndexChanged;
    }

    public void Publish(WorkspaceChange change)
    {
        if (change.Author == null && Tasker.Core.Locks.ChangeAuthor.Current is { } author)
            change = change with { Author = author };
        _pending.TryAdd(change, 0);
        _flush.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Блокировку на время правки взяли или сняли: вкладкам перечитать блокировку сущности <paramref name="id"/>.</summary>
    public void PublishLock(Guid id) => Publish(new WorkspaceChange("lock", null, id));

    public IAsyncEnumerable<WorkspaceChange> Watch(CancellationToken workspaceClosing, CancellationToken ct = default)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(workspaceClosing, ct);
        var channel = Channel.CreateBounded<WorkspaceChange>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _subscribers.TryAdd(channel, 0);
        stop.Token.Register(() => _subscribers.TryRemove(channel, out _));
        return Read(channel, stop);
    }

    private async IAsyncEnumerable<WorkspaceChange> Read(Channel<WorkspaceChange> channel, CancellationTokenSource stop)
    {
        try
        {
            while (true)
            {
                WorkspaceChange change;
                try
                {
                    change = await channel.Reader.ReadAsync(stop.Token);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                yield return change;
            }
        }
        finally
        {
            _subscribers.TryRemove(channel, out _);
            stop.Dispose();
        }
    }

    public void Dispose()
    {
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
    /// Изменённый файл → что изменилось (раскладка — см. <see cref="TaskerDirectory"/>). Вид сущности — из пути, а проект и id — от индекса,
    /// из содержимого файла: папка проекта и сущности называются по названию (<c>projects/мой-проект-3f2a9c1e/tasks/исправить-вход-3f2a9c1e.yaml</c>),
    /// и id в именах только начало. Папка целиком (<c>tasks/</c>) — без id; проект которой не узнать (папку удалили) — «перечитать всё».
    /// </summary>
    private static WorkspaceChange? Parse(WorkspaceFileChange file)
    {
        var parts = file.Path.Split('/');
        var id = file.Id;
        if (parts is ["users", _])
            return new WorkspaceChange("user", null, id);
        if (parts is not ["projects", _, ..] || parts.Length > 4)
            return null;
        if (file.ProjectId is not { } projectId)
            return new WorkspaceChange("unknown", null, null);

        return parts switch
        {
            ["projects", _] => new WorkspaceChange("project", projectId, projectId),
            ["projects", _, var item] => item == ProjectDirectory.ProjectFileName
                ? new WorkspaceChange("project", projectId, projectId)
                // Папка tasks/ и т. п. целиком.
                : new WorkspaceChange(EntityOf(item), projectId, null),
            ["projects", _, var folder, _] => new WorkspaceChange(EntityOf(folder), projectId, id),
            _ => null
        };
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
        "comments" => "comment",
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
    public static bool ChangesData(HttpRequest request) =>
        !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
        // Блокировки правки данных не меняют и о себе сообщают отдельным событием lock.
        && request.Path.Value?.EndsWith("/lock", StringComparison.Ordinal) != true
        && request.Path.Value?.EndsWith(Graph.GraphSetup.Path, StringComparison.Ordinal) != true;

    public static void UseChangeAuthors(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Features.Get<WorkspaceFeature>() == null
                || HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
                || context.Request.Path.Value?.EndsWith(Graph.GraphSetup.Path, StringComparison.Ordinal) == true)
            {
                await next(context);
                return;
            }

            var author = await context.RequestServices.GetRequiredService<Tasker.Core.Locks.IEditorIdentity>().Current(context.RequestAborted);
            using (Tasker.Core.Locks.ChangeAuthor.Begin(author))
                await next(context);
        });
}
