using Tasker.Storage.Files.Workspaces;
using Autofac;
using Autofac.Core.Lifetime;
using Autofac.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Mcp;

namespace Tasker.Web.Workspaces;

/// <summary>
/// Десктоп: запрос к <c>/w/{key}/…</c> выполняется в рабочей области <c>key</c> — это имя папки
/// (у одновременно открытых папок с одинаковым именем — с суффиксом, «Tasker-2»). Префикс уходит в PathBase,
/// дальше всё как на сервере: <c>/w/{key}/api/…</c> — REST, <c>/w/{key}/mcp</c> — MCP, остальное — фронтенд.
/// Сервисы запроса берутся из контейнера области — так API видит хранилище именно этой папки.
/// <para>
/// Без префикса хранилища нет: отвечают только <c>/api/health</c> и статика фронтенда,
/// остальное API и MCP — 404 с подсказкой.
/// </para>
/// </summary>
internal static class WorkspaceRouting
{
    private const string Prefix = "/w";

    public static string BasePath(string key) => $"{Prefix}/{key}";

    public static void UseWorkspaces(this IApplicationBuilder app)
    {
        var registry = app.ApplicationServices.GetRequiredService<WorkspaceRegistry>();

        app.Use(async (context, next) =>
        {
            var request = context.Request;

            if (!TrySplit(request.Path, out var id, out var rest))
            {
                if (NeedsWorkspace(request.Path))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsJsonAsync(new { error = "No workspace in the address: use /w/{folder}/api/…" });
                    return;
                }

                await next(context);
                return;
            }

            if (rest.StartsWithSegments(McpRegistration.Path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { error = $"MCP has no per-workspace address: use {McpRegistration.Path} and pass the folder in the 'workspace' argument of the tool" });
                return;
            }

            var workspace = registry.Find(id);
            if (workspace == null || !workspace.TryEnter())
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { error = $"Workspace '{id}' is not open" });
                return;
            }

            var originalBase = request.PathBase;
            var originalPath = request.Path;
            var originalServices = context.RequestServices;
            try
            {
                await using var scope = workspace.Scope.BeginLifetimeScope(MatchingScopeLifetimeTags.RequestLifetimeScopeTag);
                request.PathBase = originalBase.Add(BasePath(workspace.Key));
                request.Path = rest;
                context.RequestServices = new AutofacServiceProvider(scope);
                context.Features.Set(new WorkspaceFeature(workspace.Location, workspace.Key, workspace.Closing));

                await next(context);
            }
            finally
            {
                context.RequestServices = originalServices;
                request.PathBase = originalBase;
                request.Path = originalPath;
                workspace.Exit();
            }
        });
    }

    private static bool NeedsWorkspace(PathString path) =>
        path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/health");

    // /w/{key}/rest → (key, /rest); /w/{key} → (key, /).
    private static bool TrySplit(PathString path, out string id, out PathString rest)
    {
        id = "";
        rest = PathString.Empty;
        if (!path.StartsWithSegments(Prefix, out var remaining) || !remaining.HasValue)
            return false;

        var value = remaining.Value!;
        var slash = value.IndexOf('/', 1);
        id = slash < 0 ? value[1..] : value[1..slash];
        rest = slash < 0 ? "/" : value[slash..];
        return id.Length > 0;
    }
}

/// <summary>Рабочая область текущего запроса.</summary>
/// <param name="Key">Имя области (ключ адреса <c>/w/{key}</c> и аргумент <c>workspace</c> MCP).</param>
/// <param name="Closing">Отменяется, когда область закрывают, — для долгих запросов.</param>
public sealed record WorkspaceFeature(WorkspaceLocation Location, string Key, CancellationToken Closing);
