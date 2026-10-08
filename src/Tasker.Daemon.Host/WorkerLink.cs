using System.Collections.Concurrent;
using Serilog;

namespace Tasker.Daemon.Host;

/// <summary>Связь рабочего процесса с супервизором: команды приходят в stdin, события и запросы уходят в stdout (<see cref="WireMessage"/>).</summary>
internal sealed class WorkerLink(TextReader input, TextWriter output)
{
    private readonly object _write = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<WireMessage>> _requests = new();
    private long _nextId;

    /// <summary>Первое сообщение — <c>hello</c>: без него процесс не знает ни секрета управления, ни порта.</summary>
    public async Task<WireMessage> ReadHello(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var line = await input.ReadLineAsync(cts.Token) ?? throw new InvalidOperationException("The supervisor closed the pipe before it said hello");
        var message = WireMessage.Parse(line);
        return message is { Type: "hello" } ? message : throw new InvalidOperationException($"Expected hello from the supervisor, got: {line}");
    }

    /// <summary>Читает команды супервизора, пока он не закроет канал (его больше нет — вызывающий останавливает процесс).</summary>
    public async Task Listen(Func<WireMessage, Task> handle)
    {
        while (await input.ReadLineAsync() is { } line)
        {
            if (WireMessage.Parse(line) is not { } message)
                continue;

            if (message.Type == "reply")
            {
                if (_requests.TryRemove(message.Id, out var waiting))
                    waiting.TrySetResult(message);
                continue;
            }

            try
            {
                await handle(message);
            }
            catch (Exception e)
            {
                Log.Error(e, "Cannot handle the supervisor command {Type}", message.Type);
            }
        }

        foreach (var waiting in _requests.Values)
            waiting.TrySetException(new IOException("The supervisor is gone"));
    }

    public void Send(WireMessage message)
    {
        try
        {
            lock (_write)
            {
                output.WriteLine(WireMessage.Prefix + message.ToLine());
                output.Flush();
            }
        }
        catch (IOException)
        {
            // Супервизора нет — процесс узнает об этом по концу stdin.
        }
    }

    /// <summary>Запрос супервизору с ответом (<c>upgrade</c>).</summary>
    public async Task<WireMessage> Request(string op, Action<WireMessage> fill, TimeSpan timeout, CancellationToken ct)
    {
        var message = new WireMessage { Type = "request", Op = op, Id = Interlocked.Increment(ref _nextId) };
        fill(message);
        var waiting = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[message.Id] = waiting;
        try
        {
            Send(message);
            return await waiting.Task.WaitAsync(timeout, ct);
        }
        finally
        {
            _requests.TryRemove(message.Id, out _);
        }
    }
}
