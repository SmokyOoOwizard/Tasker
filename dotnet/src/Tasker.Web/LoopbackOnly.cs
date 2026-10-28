using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Tasker.Web;

/// <summary>
/// Десктоп: запросы только с этой машины. Хост слушает 127.0.0.1, поэтому другие устройства в сети
/// до него не достают; здесь — защита от DNS rebinding: страница в браузере на чужом домене, который
/// указывает на 127.0.0.1, пришлёт свой Host/Origin — такие запросы отклоняются.
/// Локальные программы на этой же машине доступ имеют (обычная модель для локального приложения).
/// </summary>
internal static class LoopbackOnly
{
    public static void UseLoopbackOnly(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var origin = request.Headers.Origin.ToString();

            if (!IsLoopback(request.Host.Host) || (origin.Length > 0 && !IsLoopbackOrigin(origin)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Only local requests are allowed" });
                return;
            }

            await next(context);
        });
    }

    private static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLoopback(uri.Host);

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
