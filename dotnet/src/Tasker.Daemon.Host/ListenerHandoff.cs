using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tasker.Global;

namespace Tasker.Daemon.Host;

/// <summary>
/// Как слушающий сокет супервизора попадает в рабочий процесс. На Unix — наследованием дескриптора (<c>--listen-fd</c>).
/// У класса Process в .NET на Windows произвольный дескриптор унаследовать нельзя, там сокет <em>дублируют в процесс-приёмник</em>
/// (<c>WSADuplicateSocket</c>): супервизор запускает рабочий процесс, получает его номер, делает описание сокета и передаёт его в сообщении
/// <c>hello</c>, рабочий процесс собирает из него свой дескриптор того же сокета (<c>WSASocket</c>). Очередь соединений у обоих общая,
/// поэтому всё остальное (затвор приёма, замена на лету) работает как на Unix. Исходный сокет супервизор не закрывает никогда
/// (по правилам WinSock он нужен, пока приёмник не собрал свой).
/// </summary>
internal interface IListenerHandoff
{
    /// <summary>Что дописать рабочему процессу в командную строку.</summary>
    string[] WorkerArguments(Socket listener);

    /// <summary>Подготовка сокета после открытия (на Unix — снять close-on-exec).</summary>
    void Prepare(Socket listener);

    /// <summary>Описание сокета для рабочего процесса <paramref name="workerPid"/> (поле <c>hello</c>) или null, если оно не нужно.</summary>
    string? Export(Socket listener, int workerPid);
}

/// <summary>Описание сокета в <c>hello</c>: <c>wsa:&lt;base64&gt;</c> (Windows) или <c>fd:&lt;номер&gt;</c> (то же, что <c>--listen-fd</c>, но сообщением).</summary>
internal static class ListenerHandoff
{
    public const string FdPrefix = "fd:";
    public const string WsaPrefix = "wsa:";

    /// <summary>Размер <c>WSAPROTOCOL_INFOW</c> в байтах: одинаков на x86, x64 и arm64.</summary>
    public const int ProtocolInfoSize = 628;

    /// <summary>Переменная <c>TASKER_MCP_HANDOFF=message</c> заставляет супервизор Unix передавать сокет сообщением, как на Windows (для проверки этого пути на macOS и Linux).</summary>
    public const string ModeVariable = "TASKER_MCP_HANDOFF";

    public const string HandoffArgument = "--listen-handoff";
    public const string FdArgument = "--listen-fd";

    public static IListenerHandoff ForThisSystem() =>
        OperatingSystem.IsWindows() ? new WindowsHandoff()
        : string.Equals(AppEnvironment.Get(ModeVariable), "message", StringComparison.OrdinalIgnoreCase) ? new UnixMessageHandoff()
        : new UnixInheritHandoff();

    public static string FormatFd(int fd) => FdPrefix + fd;

    public static string FormatWsa(byte[] protocolInfo) =>
        protocolInfo.Length == ProtocolInfoSize
            ? WsaPrefix + Convert.ToBase64String(protocolInfo)
            : throw new ArgumentException($"WSAPROTOCOL_INFOW must be {ProtocolInfoSize} bytes, got {protocolInfo.Length}", nameof(protocolInfo));

    /// <summary>Разбирает поле <c>hello</c>; null — оно пусто или неизвестного вида (сокет рабочий процесс получил иначе).</summary>
    public static (int? Fd, byte[]? ProtocolInfo)? Parse(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return null;

        if (description.StartsWith(FdPrefix, StringComparison.Ordinal) && int.TryParse(description.AsSpan(FdPrefix.Length), out var fd) && fd >= 0)
            return (fd, null);

        if (description.StartsWith(WsaPrefix, StringComparison.Ordinal))
        {
            try
            {
                var bytes = Convert.FromBase64String(description[WsaPrefix.Length..]);
                return bytes.Length == ProtocolInfoSize ? (null, bytes) : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Дескриптор (Unix) или значение SOCKET (Windows), с которым Kestrel откроет слушатель.</summary>
    public static ulong Import(string? description, int? listenFd)
    {
        if (Parse(description) is { } parsed)
        {
            if (parsed.Fd is { } fd)
                return (ulong)fd;
            if (parsed.ProtocolInfo is { } info)
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("A WinSock socket description cannot be used outside Windows");
                return WindowsSockets.Import(info);
            }
        }

        return listenFd is { } inherited && inherited >= 0
            ? (ulong)inherited
            : throw new InvalidOperationException("The supervisor did not give the worker a listening socket");
    }

    private sealed class UnixInheritHandoff : IListenerHandoff
    {
        public string[] WorkerArguments(Socket listener) => [FdArgument, ((int)listener.Handle).ToString()];

        public void Prepare(Socket listener) => UnixFd.SetInheritable((int)listener.Handle, true);

        public string? Export(Socket listener, int workerPid) => null;
    }

    // Тот же наследуемый дескриптор, но его номер приходит сообщением и аргумент <c>--listen-fd</c> не нужен: путь рабочего процесса — как на Windows.
    private sealed class UnixMessageHandoff : IListenerHandoff
    {
        public string[] WorkerArguments(Socket listener) => [HandoffArgument];

        public void Prepare(Socket listener) => UnixFd.SetInheritable((int)listener.Handle, true);

        public string? Export(Socket listener, int workerPid) => FormatFd((int)listener.Handle);
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsHandoff : IListenerHandoff
    {
        public string[] WorkerArguments(Socket listener) => [HandoffArgument];

        public void Prepare(Socket listener)
        {
        }

        public string? Export(Socket listener, int workerPid) => FormatWsa(WindowsSockets.Export(listener, workerPid));
    }
}

/// <summary>Вызовы WinSock: дублирование слушающего сокета в другой процесс. Единственное место с этими вызовами.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSockets
{
    private const int FromProtocolInfo = -1;
    private const uint WsaFlagOverlapped = 0x01;
    private const uint WsaFlagNoHandleInherit = 0x80;

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSADuplicateSocketW(nuint socket, uint processId, byte[] protocolInfo);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern nuint WSASocketW(int addressFamily, int type, int protocol, byte[] protocolInfo, uint group, uint flags);

    [DllImport("ws2_32.dll")]
    private static extern int WSAGetLastError();

    /// <summary>Описание сокета для процесса <paramref name="processId"/>; исходный сокет остаётся открытым (в отличие от <c>Socket.DuplicateAndClose</c>).</summary>
    public static byte[] Export(Socket socket, int processId)
    {
        var info = new byte[ListenerHandoff.ProtocolInfoSize];
        if (WSADuplicateSocketW((nuint)(nint)socket.Handle, (uint)processId, info) != 0)
            throw new Win32Exception(WSAGetLastError(), $"WSADuplicateSocket for process {processId} failed");
        return info;
    }

    /// <summary>Собирает дескриптор того же сокета в этом процессе. Закроет его Kestrel (слушатель принимает владение).</summary>
    public static ulong Import(byte[] protocolInfo)
    {
        // WinSock инициализирует .NET при первом сокете: без него WSASocket вернёт WSANOTINITIALISED.
        using (new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
        }

        var handle = WSASocketW(FromProtocolInfo, FromProtocolInfo, FromProtocolInfo, protocolInfo, 0, WsaFlagOverlapped | WsaFlagNoHandleInherit);
        return handle == nuint.MaxValue
            ? throw new Win32Exception(WSAGetLastError(), "WSASocket from the protocol information of the supervisor failed")
            : handle;
    }
}
