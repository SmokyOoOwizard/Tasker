using System.Net.Http.Json;
using System.Text.Json;
using Tasker.Core;

namespace Tasker.Daemon;

/// <summary>
/// Обращения к запущенному демону MCP (<c>tasker-mcpd</c>): статус и готовность. Секрет управления берётся из <c>daemon.json</c>.
/// Десктоп по статусу видит, работает ли демон, и находит у него MCP открытой области.
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
            return await Retrying(async () =>
            {
                using var http = Create(info);
                return await http.GetFromJsonAsync<DaemonStatus>("/daemon/status", TaskerJson.Options, ct);
            }, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Выполняет запрос и, если соединение оборвалось (демон как раз заменяет рабочий процесс: раньше ответа соединение закрыли),
    /// повторяет его один раз после короткой паузы. Только для запросов, которые безопасно повторить.
    /// </summary>
    public static async Task<T> Retrying<T>(Func<Task<T>> request, CancellationToken ct = default)
    {
        try
        {
            return await request();
        }
        catch (HttpRequestException)
        {
            await Task.Delay(RetryPause, ct);
            return await request();
        }
    }

    /// <summary>Пауза перед единственным повтором запроса, оборвавшегося во время замены процесса демона.</summary>
    public static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(300);

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
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
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
}
