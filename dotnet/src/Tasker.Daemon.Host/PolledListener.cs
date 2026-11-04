using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging;

namespace Tasker.Daemon.Host;

internal sealed class PolledListenerFactory(ILoggerFactory loggers) : IConnectionListenerFactory, IConnectionListenerFactorySelector
{
    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is not FileHandleEndPoint handle)
            throw new NotSupportedException($"Only the listening socket handed over by the supervisor can be bound, got {endpoint}");

        var socket = new Socket(new SafeSocketHandle((IntPtr)(long)handle.FileHandle, ownsHandle: true)) { Blocking = false };
        var contexts = new SocketConnectionContextFactory(new SocketConnectionFactoryOptions(), loggers.CreateLogger<PolledListener>());
        return ValueTask.FromResult<IConnectionListener>(new PolledListener(socket, contexts));
    }

    public bool CanBind(EndPoint endpoint) => endpoint is FileHandleEndPoint;
}

internal sealed class PolledListener(Socket socket, SocketConnectionContextFactory contexts) : IConnectionListener
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public EndPoint EndPoint { get; } = socket.LocalEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await Task.Run(() => socket.Poll(PollInterval, SelectMode.SelectRead), CancellationToken.None))
                continue;

            Socket accepted;
            try
            {
                accepted = socket.Accept();
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.WouldBlock or SocketError.ConnectionReset or SocketError.ConnectionAborted)
            {
                continue;
            }

            accepted.Blocking = true;
            accepted.NoDelay = true;
            return contexts.Create(accepted);
        }
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        contexts.Dispose();
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
