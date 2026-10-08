using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Serilog;

namespace Tasker.Daemon.Host;

/// <summary>
/// Затвор приёма соединений рабочего процесса. Слушающий сокет общий (его открыл супервизор, он никогда не закрывается),
/// а <em>принимает</em> из него тот процесс, у которого затвор открыт: новый открывает его, когда готов, старый закрывает
/// и заканчивает начатое. Соединения, которых никто пока не принял, ждут в очереди ядра — отказов при замене нет.
/// </summary>
internal sealed class AcceptGate
{
    private readonly object _sync = new();
    private TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource _closing = new();

    public bool IsOpen
    {
        get
        {
            lock (_sync)
                return _opened.Task.IsCompleted;
        }
    }

    public void Open()
    {
        lock (_sync)
        {
            if (_opened.Task.IsCompleted)
                return;

            _closing = new CancellationTokenSource();
            _opened.TrySetResult();
        }
    }

    /// <summary>Перестаёт принимать новые соединения; принятые продолжают работать.</summary>
    public void Close()
    {
        lock (_sync)
        {
            if (!_opened.Task.IsCompleted)
                return;

            _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _closing.Cancel();
        }
    }

    internal (Task Opened, CancellationToken Closing) Snapshot()
    {
        lock (_sync)
            return (_opened.Task, _closing.Token);
    }
}

/// <summary>Слушатель Kestrel с затвором: пока затвор закрыт, соединения не принимаются (<see cref="IConnectionListener.AcceptAsync"/> ждёт).</summary>
internal sealed class GatedListenerFactory(IConnectionListenerFactory inner, AcceptGate gate) : IConnectionListenerFactory, IConnectionListenerFactorySelector
{
    public async ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default) =>
        new GatedListener(await inner.BindAsync(endpoint, cancellationToken), gate);

    public bool CanBind(EndPoint endpoint) => inner is not IConnectionListenerFactorySelector selector || selector.CanBind(endpoint);
}

internal sealed class GatedListener(IConnectionListener inner, AcceptGate gate) : IConnectionListener
{
    private readonly CancellationTokenSource _unbound = new();

    public EndPoint EndPoint => inner.EndPoint;

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _unbound.Token);
        try
        {
            while (true)
            {
                var (opened, closing) = gate.Snapshot();
                if (!opened.IsCompleted)
                {
                    await opened.WaitAsync(stop.Token);
                    continue;
                }

                // Затвор закроют — незавершённый приём снимается (приём, который уже получил соединение, его отдаёт: оно обслужится).
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, closing);
                try
                {
                    var accepted = await inner.AcceptAsync(linked.Token);
                    if (accepted != null || stop.IsCancellationRequested || !closing.IsCancellationRequested)
                        return accepted;
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException) when (_unbound.IsCancellationRequested)
        {
            return null; // слушатель закрыт: приёмный цикл Kestrel заканчивается
        }
    }

    // Слушающий сокет общий: закрыть или завершить (shutdown) его здесь значит оставить без приёма и нового процесса.
    // Поэтому только прекращаем приём; процесс выходит — дескриптор закрывается вместе с ним, а сокет живёт у супервизора и нового процесса.
    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        _unbound.Cancel();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Состояние завершения старого процесса: считает вызовы в работе, а на закрытии отвечает повторяемой ошибкой.
/// Вызовы, пришедшие по уже открытым соединениям, обслуживаются как обычно — но ответ закрывает соединение (клиент придёт к новому процессу).
/// </summary>
internal sealed class DrainState
{
    private int _active;
    private long _lastActivityTicks = Environment.TickCount64;

    public bool Draining { get; private set; }

    /// <summary>Последняя фаза: процесс закрывается, новые вызовы получают 503 с <c>Retry-After</c>.</summary>
    public bool Closing { get; private set; }

    public int Active => Volatile.Read(ref _active);

    public TimeSpan Idle => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks));

    public void BeginDrain()
    {
        Draining = true;
        // Отсчёт тишины — с этого момента: соединение, принятое за миг до закрытия затвора, ещё пришлёт свой вызов.
        Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);
    }

    public void BeginClosing() => Closing = true;

    public async Task Invoke(HttpContext context, Func<Task> next)
    {
        if (Closing)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "1";
            context.Response.Headers.Connection = "close";
            await context.Response.WriteAsJsonAsync(new { error = "[unavailable] The MCP server process is being replaced: repeat the request" });
            return;
        }

        Interlocked.Increment(ref _active);
        context.Response.OnStarting(() =>
        {
            if (Draining)
                context.Response.Headers.Connection = "close";
            return Task.CompletedTask;
        });
        try
        {
            await next();
        }
        finally
        {
            Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>Ждёт, пока вызовов в работе не будет <paramref name="quiet"/> подряд, но не дольше <paramref name="timeout"/>.</summary>
    /// <returns>Сколько вызовов осталось в работе (0 — всё закончено).</returns>
    public async Task<int> WaitQuiet(TimeSpan quiet, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (Active == 0 && Idle >= quiet)
                return 0;
            if (DateTime.UtcNow >= deadline)
            {
                Log.Warning("{Count} call(s) are still running after {Seconds:0} s: they are aborted", Active, timeout.TotalSeconds);
                return Active;
            }

            await Task.Delay(50, ct);
        }
    }
}
