using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Tasker.Desktop;

/// <summary>
/// Один экземпляр Tasker на пользователя. Первый запуск берёт эксклюзивную блокировку
/// <c>instance.lock</c> в каталоге данных и слушает unix-сокет <c>instance.sock</c> рядом.
/// Следующий запуск блокировку не получает: отправляет первому свой <see cref="StartupRequest"/>
/// (какие папки открыть; пустой — просто показать окно) и завершается.
/// <para>
/// Так все окна и вкладки живут в одном процессе, и папку синхронизирует один
/// <see cref="Tasker.Web.Workspaces.WorkspaceRegistry"/>.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly FileStream _lock;
    private readonly Socket _listener;
    private readonly string _socketPath;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(FileStream fileLock, Socket listener, string socketPath)
    {
        _lock = fileLock;
        _listener = listener;
        _socketPath = socketPath;
    }

    /// <summary>Запросы от следующих запусков. Вызывается не в UI-потоке.</summary>
    public event Action<StartupRequest>? Requested;

    /// <summary>
    /// Первый запуск — экземпляр, который принимает запросы; иначе запрос уже передан первому, и возвращается null.
    /// </summary>
    public static SingleInstance? Acquire(string dataDirectory, StartupRequest request)
    {
        Directory.CreateDirectory(dataDirectory);
        var socketPath = Path.Combine(dataDirectory, "instance.sock");

        FileStream fileLock;
        try
        {
            fileLock = new FileStream(Path.Combine(dataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Send(socketPath, request);
            return null;
        }

        // Блокировка наша — значит, сокет остался от упавшего процесса.
        File.Delete(socketPath);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen();

        var instance = new SingleInstance(fileLock, listener, socketPath);
        _ = instance.Listen();
        return instance;
    }

    // Первый экземпляр мог взять блокировку, но ещё не начать слушать сокет — пробуем несколько раз.
    private static void Send(string socketPath, StartupRequest request)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(new UnixDomainSocketEndPoint(socketPath));
                socket.Send(JsonSerializer.SerializeToUtf8Bytes(request));
                socket.Shutdown(SocketShutdown.Send);
                return;
            }
            catch (SocketException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    private async Task Listen()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptAsync(_stop.Token);
                using var stream = new NetworkStream(client);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var json = await reader.ReadToEndAsync(_stop.Token);

                if (JsonSerializer.Deserialize<StartupRequest>(json) is { } request)
                    Requested?.Invoke(request);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Warning(e, "Single instance: bad request from another launch");
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        File.Delete(_socketPath);
        _lock.Dispose();
    }
}
