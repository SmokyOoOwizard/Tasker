using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Tasker.Core;

namespace Tasker.Daemon;

/// <summary>
/// Обращения к запущенному демону MCP (<c>tasker-mcpd</c>): статус, готовность и сверка области.
/// Секрет управления берётся из <c>daemon.json</c>. Десктоп по статусу видит, работает ли демон, и находит у него MCP открытой области.
/// </summary>
public static class DaemonClient
{
    /// <summary>Заголовок с секретом управления демоном (из <c>daemon.json</c>).</summary>
    public const string ControlHeader = "X-Tasker-Control";

    /// <summary>Заголовок вызова MCP демона: id агента области; без него вызов идёт от локального агента области.</summary>
    public const string AgentHeader = "X-Tasker-Agent";

    /// <returns>null — демон не работает или не отвечает.</returns>
    public static async Task<DaemonStatus?> GetStatus(CancellationToken ct = default)
    {
        if (!DaemonFiles.IsRunning() || DaemonFiles.ReadInfo() is not { } info)
            return null;

        try
        {
            using var http = Create(info);
            return await http.GetFromJsonAsync<DaemonStatus>("/daemon/status", TaskerJson.Options, ct);
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Готов ли демон принимать вызовы: ответ <c>/ready</c> (200). null — не отвечает.</summary>
    public static async Task<bool?> IsReady(CancellationToken ct = default)
    {
        if (DaemonFiles.ReadInfo() is not { } info)
            return null;

        try
        {
            using var http = Create(info);
            using var response = await http.GetAsync("/ready", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException)
        {
            return null;
        }
    }

    private static HttpClient Create(DaemonInfo info)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{info.Port}"), Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Add(ControlHeader, info.Token);
        return http;
    }

    /// <summary>
    /// Просит демон сверить кэш области с файлами и ждёт результата.
    /// </summary>
    /// <returns>null — демон не работает или эту область не обслуживает (сверять нужно самому).</returns>
    public static async Task<SyncResult?> Sync(Tasker.Storage.Files.Workspaces.WorkspaceLocation location, CancellationToken ct = default)
    {
        if (!DaemonFiles.IsRunning() || DaemonFiles.ReadInfo() is not { } info)
            return null;

        try
        {
            using var http = Create(info);
            http.Timeout = TimeSpan.FromMinutes(5);
            using var response = await http.PostAsJsonAsync("/daemon/sync", new SyncRequest(location.Path), TaskerJson.Options, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<SyncResult>(TaskerJson.Options, ct) : null;
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public static async Task<SearchResult?> Search(SearchRequest request, CancellationToken ct = default)
    {
        if (!DaemonFiles.IsRunning() || DaemonFiles.ReadInfo() is not { } info)
            return null;

        try
        {
            using var http = Create(info);
            http.Timeout = TimeSpan.FromMinutes(1);
            using var response = await http.PostAsJsonAsync("/daemon/search", request, TaskerJson.Options, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<SearchResult>(TaskerJson.Options, ct) : null;
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Просит демон остановиться. false — демон не работает или не принял запрос.</summary>
    public static async Task<bool> RequestStop(CancellationToken ct = default)
    {
        if (!DaemonFiles.IsRunning() || DaemonFiles.ReadInfo() is not { } info)
            return false;

        try
        {
            using var http = Create(info);
            using var response = await http.PostAsync("/daemon/stop", null, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or TaskCanceledException)
        {
            return false;
        }
    }
}
