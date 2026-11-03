using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Serilog;
using Tasker.Daemon;
using Tasker.Mcp;

namespace Tasker.Web;

/// <summary>
/// Адреса локального хоста десктопа: интерфейс на случайном порту и (если порт свободен) MCP на постоянном.
/// Заданы настройкой Kestrel из памяти, а не <c>UseUrls</c>: Kestrel перечитывает её на лету,
/// поэтому порт MCP можно отпустить, не останавливая хост (десктоп уступает порт стартовавшему демону).
/// </summary>
public sealed class LocalEndpoints : ConfigurationProvider, IConfigurationSource
{
    private const string Ui = "Kestrel:Endpoints:Ui:Url";
    private const string Mcp = "Kestrel:Endpoints:Mcp:Url";

    private readonly LocalMcpAddress _address;
    private int? _mcpPort;

    private LocalEndpoints(int? mcpPort, LocalMcpAddress address)
    {
        _mcpPort = mcpPort;
        _address = address;
        Fill();
    }

    /// <summary>Порт MCP, который хост ещё слушает; null — не слушает.</summary>
    public int? McpPort => _mcpPort;

    /// <summary>Подключает адреса к хосту; <paramref name="address"/> обнулится, когда порт будет отпущен.</summary>
    public static LocalEndpoints Add(WebApplicationBuilder builder, int? mcpPort, LocalMcpAddress address)
    {
        var endpoints = new LocalEndpoints(mcpPort, address);
        ((IConfigurationBuilder)builder.Configuration).Add(endpoints);
        return endpoints;
    }

    IConfigurationProvider IConfigurationSource.Build(IConfigurationBuilder builder) => this;

    /// <summary>Перестаёт слушать порт MCP и ждёт, пока он освободится. false — не освободился за <paramref name="timeout"/>.</summary>
    public async Task<bool> ReleaseMcp(TimeSpan timeout, CancellationToken ct = default)
    {
        if (_mcpPort is not { } port)
            return true;

        _mcpPort = null;
        _address.Release();
        Fill();
        OnReload();

        var deadline = DateTime.UtcNow + timeout;
        while (!IsFree(port))
        {
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(50, ct);
        }
        return true;
    }

    /// <summary>
    /// Ждёт, пока <paramref name="daemonRunning"/> не станет true, и отпускает порт MCP: демон держит MCP всех областей.
    /// Возвращается и без освобождения, если порта нет или ждать отменили.
    /// </summary>
    public async Task YieldToDaemon(Func<bool> daemonRunning, TimeSpan pollEvery, CancellationToken ct = default)
    {
        try
        {
            while (_mcpPort is { } port)
            {
                if (daemonRunning())
                {
                    if (await ReleaseMcp(TimeSpan.FromSeconds(5), ct))
                        Log.Information("The MCP daemon started: the desktop released MCP port {Port}, MCP is served by the daemon", port);
                    else
                        Log.Warning("The MCP daemon started, but port {Port} was not released in time", port);
                    return;
                }
                await Task.Delay(pollEvery, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Следит за блокировкой демона: <see cref="DaemonFiles.IsRunning"/>.</summary>
    public Task YieldToDaemon(CancellationToken ct = default) =>
        YieldToDaemon(DaemonFiles.IsRunning, TimeSpan.FromMilliseconds(500), ct);

    private void Fill()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [Ui] = "http://127.0.0.1:0" };
        if (_mcpPort is { } port)
            data[Mcp] = $"http://127.0.0.1:{port}";
        Data = data;
    }

    private static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
