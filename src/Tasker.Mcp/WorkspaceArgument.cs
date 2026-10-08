using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tasker.Core.Users;

namespace Tasker.Mcp;

/// <summary>
/// Рабочая область — аргумент вызова. Адрес MCP один (<see cref="McpRegistration.Path"/>) на все области; у каждого инструмента
/// (кроме <c>list_workspaces</c>) есть необязательный <c>workspace</c> — имя области или её путь. Если разрешена ровно одна область,
/// его можно не передавать (как <see cref="DefaultProject"/> с <c>projectId</c>); если несколько — <c>[invalid]</c> с перечнем.
/// Сервер остаётся без состояния: никаких «выбрать область» и сессий, область определяется каждым вызовом заново.
/// <para>
/// Как и <see cref="DefaultProject"/>, делается в одном месте: (1) схема каждого инструмента получает <c>workspace</c>
/// (<see cref="AddTo"/>), (2) фильтр вызова (<see cref="Filter"/>) убирает аргумент, открывает область и подменяет сервисы вызова
/// на контейнер этой области. Сами инструменты не знают о областях.
/// </para>
/// <para>
/// Агент в области: заголовок <see cref="McpRegistration.AgentHeader"/> (агент этой области) или её локальный агент.
/// </para>
/// </summary>
internal static class WorkspaceArgument
{
    public const string Argument = "workspace";

    /// <summary>Инструмент, которому область не нужна: он сам показывает области.</summary>
    public const string ListTool = "list_workspaces";

    public static bool Takes(Tool tool) =>
        tool.Name != ListTool
        && tool.InputSchema.ValueKind == JsonValueKind.Object
        && tool.InputSchema.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty(Argument, out _);

    /// <summary>Схема инструмента: добавляет необязательный <c>workspace</c>. Идемпотентна.</summary>
    public static void AddTo(Tool tool)
    {
        if (tool.Name == ListTool)
            return;

        var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
        if (schema["properties"] is not JsonObject properties)
            schema["properties"] = properties = new JsonObject();

        properties[Argument] = new JsonObject
        {
            ["type"] = "string",
            ["description"] =
                "Workspace (folder) to work in: its name or path from list_workspaces. " +
                "Optional if exactly one workspace is available; with several it is required. " +
                "A workspace that is still opening is waited for a few seconds; if it is still not ready the call fails with [unavailable] (retry later), " +
                "and with [failed] if it could not be opened."
        };
        tool.InputSchema = JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>Фильтр вызова: открывает область из аргумента и отвечает понятной ошибкой, если выбрать её нельзя.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, ct) =>
        {
            if (context.MatchedPrimitive is not McpServerTool tool || !Takes(tool.ProtocolTool))
                return await next(context, ct);

            var services = (context.Services ?? context.Server.Services)!;
            var catalog = services.GetRequiredService<IMcpWorkspaces>();

            if (!TryTake(context.Params, out var requested))
                return ErrorText.Error($"[invalid] {Argument} must be a string: the workspace name or path from {ListTool}");

            var (scope, failure) = await Choose(catalog, requested, ct);
            if (scope == null)
                return failure!;

            await using (scope)
            {
                var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
                if (http != null)
                {
                    if (await AgentOf(http, scope, ct) is not { } agentId)
                    {
                        return ErrorText.Error(
                            $"[forbidden] {McpRegistration.AgentHeader}: no agent with id '{http.Request.Headers[McpRegistration.AgentHeader]}' in workspace '{scope.Name}'");
                    }

                    http.User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(AgentTokenAuthenticationHandler.UserIdClaim, agentId.ToString())], "LocalAgent"));
                }

                context.Services = scope.Services;
                return await next(context, ct);
            }
        };

    /// <summary>Сколько вызов ждёт открытия области (демон открывает их в фоне после старта), прежде чем отдать <c>[unavailable]</c>.</summary>
    internal static TimeSpan OpenWait { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("TASKER_MCP_OPEN_WAIT_MS"), out var ms) && ms >= 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromSeconds(5);

    private static readonly TimeSpan OpenPoll = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Открывает область по аргументу или (аргумента нет) единственную разрешённую; иначе — ошибка для агента.
    /// Область, которая ещё открывается, вызов ждёт до <see cref="OpenWait"/>: после запуска демона это секунды, а не ошибка агента.
    /// </summary>
    private static async Task<(McpWorkspaceScope? Scope, CallToolResult? Failure)> Choose(IMcpWorkspaces catalog, string? requested, CancellationToken ct)
    {
        var all = catalog.List();

        if (requested == null)
        {
            if (all.Count == 0)
            {
                return (null, ErrorText.Error(
                    $"[invalid] {Argument} is required: no workspace is available to MCP (allow one with 'tasker mcp workspace add <path>' or open a folder in the Tasker desktop app)"));
            }

            if (all.Count > 1)
            {
                return (null, ErrorText.Error(
                    $"[invalid] {Argument} is required: {all.Count} workspaces are available ({string.Join(", ", all.Select(x => x.Name))}); {ListTool} shows them"));
            }

            requested = all[0].Path;
        }

        var deadline = DateTime.UtcNow + OpenWait;
        while (true)
        {
            if (catalog.Enter(requested) is { } scope)
                return (scope, null);

            // Разрешена, но не открыта (открывается или не открылась): область «есть», но работать в ней пока нельзя.
            var unavailable = catalog.List().FirstOrDefault(x =>
                string.Equals(x.Name, requested, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Path, requested, StringComparison.Ordinal));
            if (unavailable == null)
            {
                // Неизвестная и не разрешённая — одинаковый ответ: по нему нельзя узнать, какие ещё папки есть на машине.
                return (null, ErrorText.Error($"[not_found] Workspace '{requested}' not found; {ListTool} shows the available ones"));
            }

            if (unavailable.Status == McpWorkspaceStatus.Failed)
            {
                return (null, ErrorText.Error(
                    $"[failed] Workspace '{unavailable.Name}' could not be opened"
                    + (unavailable.Error == null ? "" : $": {unavailable.Error}")
                    + "; fix the folder (the server retries on its own every 30 s) or check status in " + ListTool));
            }

            // Opening (или уже Open, но Enter не удался из-за закрытия) — ждём.
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return (null, ErrorText.Error(
                    $"[unavailable] Workspace '{unavailable.Name}' is opening, retry in {RetryAfterSeconds} s (this is temporary, not an error in the call)"));
            }

            await Task.Delay(left < OpenPoll ? left : OpenPoll, ct);
        }
    }

    /// <summary>Через сколько секунд стоит повторить вызов, когда область ещё открывается.</summary>
    internal const int RetryAfterSeconds = 2;

    /// <summary>Агент вызова: из заголовка (агент этой области) или локальный агент области; null — в заголовке чужой id.</summary>
    private static async Task<Guid?> AgentOf(HttpContext http, McpWorkspaceScope scope, CancellationToken ct)
    {
        var header = http.Request.Headers[McpRegistration.AgentHeader].ToString();
        if (string.IsNullOrEmpty(header))
            return await scope.Services.GetRequiredService<LocalMcpAgent>().GetId(scope.Services, ct);

        return Guid.TryParse(header, out var id)
            && await scope.Services.GetRequiredService<IUserStorage>().GetById(id, ct) is { IsAgent: true }
            ? id
            : null;
    }

    /// <summary>Забирает <c>workspace</c> из аргументов (инструмент его не знает). false — значение не строка.</summary>
    private static bool TryTake(CallToolRequestParams? request, out string? value)
    {
        value = null;
        if (request?.Arguments is not { } arguments || !arguments.Remove(Argument, out var raw))
            return true;

        switch (raw.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return true;
            case JsonValueKind.String:
                value = string.IsNullOrWhiteSpace(raw.GetString()) ? null : raw.GetString()!.Trim();
                return true;
            default:
                return false;
        }
    }
}
