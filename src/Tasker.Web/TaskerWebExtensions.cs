using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Serilog;
using Tasker.Mcp;
using Tasker.Web.Workspaces;

namespace Tasker.Web;

/// <summary>
/// Общий веб-слой: API и встроенный фронтенд.
/// Подключается одинаково и в Tasker.Server, и в Tasker.Desktop.
/// </summary>
public static class TaskerWebExtensions
{
    public static WebApplication MapTasker(this WebApplication app)
    {
        var mode = app.Services.GetRequiredService<TaskerHost>().Mode;
        var frontend = CreateFrontendProvider();

        if (mode == TaskerMode.Local)
        {
            app.UseLoopbackOnly();
            app.UseWorkspaces();
            app.UseSqliteChangeEvents();
        }

        app.Use(TaskerApi.HandleErrors);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontend });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = frontend });

        // Явно и после UseWorkspaces: маршрут выбирается по пути уже без префикса /w/{key}.
        app.UseRouting();

        if (mode == TaskerMode.Server)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        var api = app.MapGroup("/api");
        api.MapGet("/health", () => Results.Ok(new { status = "ok", mode = mode.ToString() }));
        api.MapTaskerApi(mode);
        if (mode == TaskerMode.Local)
            api.MapWorkspaceEvents();

        app.MapTaskerMcp(requireAgentToken: mode == TaskerMode.Server);

        // Неизвестный путь под /api — 404 JSON-клиенту. Без этого GET попадал бы в SPA-fallback
        // (200 с index.html), а POST/PATCH/DELETE — в 405 от него же.
        api.Map("{*path}", () => Results.NotFound(new { error = "Not found" }));

        // SPA: все неизвестные не-API маршруты отдают index.html фронтенда.
        app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = frontend });

        return app;
    }

    /// <summary>
    /// Демон MCP (<c>tasker mcp run</c>): только MCP рабочих областей — один адрес <c>/mcp</c>, область — аргумент вызова — без REST и фронтенда.
    /// Работает как десктоп (режим Local): без токена, только запросы с этой машины, от имени локального агента области.
    /// </summary>
    /// <param name="mapExtra">Дополнительные маршруты хоста (управление демоном) — они не привязаны к рабочей области.</param>
    public static WebApplication MapTaskerMcpHost(this WebApplication app, Action<IEndpointRouteBuilder>? mapExtra = null)
    {
        app.UseLoopbackOnly();
        app.UseWorkspaces();
        app.UseRouting();

        app.MapGet("/api/health", () => Results.Ok(new { status = "ok", mode = "McpDaemon" }));
        mapExtra?.Invoke(app);
        app.MapTaskerMcp(requireAgentToken: false);

        return app;
    }

    // Если frontend/dist не был собран перед сборкой .NET (свежий clone, worktree),
    // манифеста встроенных файлов нет — API всё равно поднимаем, просто без страниц.
    private static IFileProvider CreateFrontendProvider()
    {
        var assembly = typeof(TaskerWebExtensions).Assembly;
        if (!assembly.GetManifestResourceNames().Contains("Microsoft.Extensions.FileProviders.Embedded.Manifest.xml"))
        {
            Log.Warning("Frontend is not embedded: run `npm run build` in frontend/ before building .NET");
            return new NullFileProvider();
        }

        return new ManifestEmbeddedFileProvider(assembly, "wwwroot");
    }
}
