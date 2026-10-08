using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tasker.Core;
using Tasker.Core.IO;

namespace Tasker.Storage.Files.Index;

/// <summary>Жизненный цикл без слежения за файлами (короткая команда): только первая сверка индекса при открытии.</summary>
internal sealed class IndexOnlyLifecycle(WorkspaceIndex index) : IStorageLifecycle
{
    public Task Start(CancellationToken ct) => index.EnsureReady();
}

/// <summary>
/// Держит <see cref="WorkspaceIndex"/> в актуальном состоянии, пока папка открыта:
/// правки руками, git pull, checkout, merge. Один на папку — все вкладки и окна с ней работают
/// через один индекс.
/// <para>
/// События не обрабатываются по одному: пути копятся и разбираются пачкой, когда файловая система
/// затихнет на <see cref="Quiet"/> (но не реже <see cref="MaxDelay"/>). Checkout на сотни файлов —
/// одна транзакция индекса. Переполнение буфера событий — полная сверка.
/// </para>
/// </summary>
internal sealed class WorkspaceWatcher(TaskerDirectory directory, WorkspaceIndex index, ILogger<WorkspaceWatcher> log)
    : IStorageLifecycle, IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(2);

    // Столько путей в пачке — дешевле сверить всё, чем разбирать каждый.
    private const int FullRescanThreshold = 500;

    private readonly ConcurrentDictionary<string, byte> dirty = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource stopping = new();
    private FileSystemWatcher? watcher;
    private Task? loop;
    private long eventCount;
    private int fullRescan;

    public async Task Start(CancellationToken ct)
    {
        // Первая сверка — до того, как хост начнёт отвечать: списки сразу актуальны.
        await index.EnsureReady();

        watcher = new FileSystemWatcher(directory.Root)
        {
            IncludeSubdirectories = true,
            // Windows хранит события в буфере (по умолчанию 8 КБ — примерно сотня событий): checkout на сотни файлов его переполнит.
            // 64 КБ — максимум; при переполнении всё равно придёт Error и будет полная сверка.
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Created += (_, e) => Mark(e.FullPath);
        watcher.Changed += (_, e) => Mark(e.FullPath);
        watcher.Deleted += (_, e) => Mark(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            Mark(e.OldFullPath);
            Mark(e.FullPath);
        };
        watcher.Error += (_, e) =>
        {
            log.LogWarning(e.GetException(), "File watcher lost events, rescanning the workspace");
            Interlocked.Exchange(ref fullRescan, 1);
            Signal();
        };
        watcher.EnableRaisingEvents = true;

        loop = Task.Run(() => Loop(stopping.Token), CancellationToken.None);
    }

    public async Task Stop(CancellationToken ct)
    {
        if (watcher != null)
            watcher.EnableRaisingEvents = false;
        await stopping.CancelAsync();
        try
        {
            if (loop != null)
                await loop.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Не дождались — пачка догонится сверкой при следующем открытии.
        }
    }

    public void Dispose()
    {
        watcher?.Dispose();
        stopping.Dispose();
    }

    private void Mark(string path)
    {
        // Сам индекс лежит в .tasker/.cache — его записи событиями не считаем.
        // Регистр букв пути на Windows не важен (событие может прийти с другим регистром, чем у корня наблюдателя).
        if (PathRules.IsInside(directory.Cache, path))
            return;

        dirty[path] = 0;
        Interlocked.Increment(ref eventCount);
        Signal();
    }

    private void Signal()
    {
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Уже разбудили.
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await signal.WaitAsync(ct);

                var started = DateTime.UtcNow;
                long seen;
                do
                {
                    seen = Interlocked.Read(ref eventCount);
                    await Task.Delay(Quiet, ct);
                } while (Interlocked.Read(ref eventCount) != seen && DateTime.UtcNow - started < MaxDelay);

                var paths = new List<string>();
                foreach (var path in dirty.Keys)
                {
                    if (dirty.TryRemove(path, out _))
                        paths.Add(path);
                }

                if (Interlocked.Exchange(ref fullRescan, 0) == 1 || paths.Count > FullRescanThreshold)
                    await index.RescanExternal(ct);
                else if (paths.Count > 0)
                    await index.RefreshExternal(paths, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Индекс догонит при следующем событии или сверке — приложение продолжает работать.
                log.LogError(e, "Failed to update the workspace index");
            }
        }
    }
}
