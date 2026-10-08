using System.Net.Http.Json;
using System.Text.Json;
using Tasker.Core;

namespace Tasker.Daemon;

/// <summary>Обращения к запущенному демону: статус и остановка. Секрет управления берётся из <c>daemon.json</c>.</summary>
public static class DaemonClient
{
    /// <summary>Заголовок с секретом управления демоном (из <c>daemon.json</c>).</summary>
    public const string ControlHeader = "X-Tasker-Control";

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

    /// <summary>
    /// Просит демон заменить рабочий процесс на лету и ждёт итога. Ошибка связи — тоже итог (<c>Ok = false</c>): старый процесс жив.
    /// </summary>
    /// <returns>null — демон не работает.</returns>
    public static async Task<UpgradeResult?> Upgrade(UpgradeRequest request, CancellationToken ct = default)
    {
        if (!DaemonFiles.IsRunning() || DaemonFiles.ReadInfo() is not { } info)
            return null;

        try
        {
            using var http = Create(info);
            // Замена — это запуск нового процесса и открытие областей: дольше обычных запросов.
            http.Timeout = TimeSpan.FromSeconds(request.TimeoutSeconds + 30);
            using var response = await http.PostAsJsonAsync("/daemon/upgrade", request, TaskerJson.Options, ct);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<UpgradeResult>(TaskerJson.Options, ct);

            return new UpgradeResult(false, $"The daemon refused the upgrade: HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}".Trim());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UpgradeResult(false, $"The daemon did not answer the upgrade request: {e.Message}");
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
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
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
            return await Retrying(async () =>
            {
                using var http = Create(info);
                http.Timeout = TimeSpan.FromMinutes(5);
                using var response = await http.PostAsJsonAsync("/daemon/sync", new SyncRequest(location.Kind, location.Path), TaskerJson.Options, ct);
                return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<SyncResult>(TaskerJson.Options, ct) : null;
            }, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
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
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static HttpClient Create(DaemonInfo info)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{info.Port}"), Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Add(ControlHeader, info.Token);
        return http;
    }
}
