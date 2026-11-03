using System.Reflection;
using Tasker.Core;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tasker.Core.Agents;
using Tasker.Core.Users;

namespace Tasker.Mcp;

/// <summary>
/// MCP-сервер Tasker (Streamable HTTP, без сессий) по адресу <see cref="Path"/> на том же хосте, что и REST.
/// <list type="bullet">
/// <item>Сервер: одна область, только с токеном агента (<see cref="AgentTokenAuthenticationHandler"/>).</item>
/// <item>Десктоп и демон: без токена, от имени локального агента; адрес один на все области, область — аргумент
/// вызова (<see cref="WorkspaceArgument"/>).</item>
/// </list>
/// </summary>
public static class McpRegistration
{
    public const string Path = "/mcp";

    /// <summary>Адрес MCP на 127.0.0.1 (десктоп и демон): один на все рабочие области.</summary>
    public static string LocalUrl(int port) => $"http://127.0.0.1:{port}{Path}";

    /// <summary>Десктоп и демон: id агента области, от имени которого работает MCP. Без заголовка — локальный агент области.</summary>
    public const string AgentHeader = "X-Tasker-Agent";

    /// <param name="requireAgentToken">
    /// Сервер: вход по токену агента, область одна. Иначе (десктоп, демон) — область выбирается аргументом вызова
    /// (<see cref="IMcpWorkspaces"/> должен быть зарегистрирован в контейнере).
    /// </param>
    public static void AddTaskerMcp(this IServiceCollection services, bool requireAgentToken)
    {
        var workspaceArgument = !requireAgentToken;

        // Фильтры вызова: первый добавленный — самый внешний. Внешний — ошибки (в том числе фильтров ниже) без префикса SDK;
        // затем область (подменяет сервисы вызова на контейнер области); и уже в ней — проект по умолчанию.
        services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "tasker", Version = "1.0.0" })
            .WithHttpTransport(o => o.Stateless = true)
            .WithRequestFilters(filters =>
            {
                filters.AddCallToolFilter(ErrorText.Filter);
                if (workspaceArgument)
                    filters.AddCallToolFilter(WorkspaceArgument.Filter);
                // projectId необязателен, когда в области один проект (см. DefaultProject).
                filters.AddCallToolFilter(DefaultProject.Filter);
            });

        // Как WithToolsFromAssembly, но со своим ToolServices: см. там, почему нельзя отдать SDK контейнер Autofac как есть.
        var methods = typeof(McpRegistration).Assembly.GetTypes()
            .Where(x => x.IsDefined(typeof(McpServerToolTypeAttribute), inherit: false))
            // list_workspaces — только там, где область выбирается аргументом.
            .Where(x => workspaceArgument || x != typeof(Tools.WorkspaceTools))
            .SelectMany(x => x.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(x => x.IsDefined(typeof(McpServerToolAttribute), inherit: false));

        foreach (var method in methods)
        {
            services.AddSingleton(sp =>
            {
                var tool = McpServerTool.Create(method, target: null, new McpServerToolCreateOptions
                {
                    Services = new ToolServices(sp),
                    SerializerOptions = TaskerJson.Options
                });
                DefaultProject.MakeOptional(tool.ProtocolTool);
                if (workspaceArgument)
                    WorkspaceArgument.AddTo(tool.ProtocolTool);
                return tool;
            });
        }

        if (requireAgentToken)
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, AgentTokenAuthenticationHandler>(AgentTokenAuthenticationHandler.SchemeName, null);
        }
    }

    public static void MapTaskerMcp(this IEndpointRouteBuilder app, bool requireAgentToken)
    {
        var mcp = app.MapMcp(Path);

        // На проводе без \uXXXX: SDK кодирует конверт JSON-RPC своими опциями, которые не настроить (см. WireJson).
        WireJson.Unescape(mcp);

        // Только схема токена агента: JWT людей в MCP не принимается (и наоборот — токен агента в REST).
        if (requireAgentToken)
            mcp.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = AgentTokenAuthenticationHandler.SchemeName });
    }
}

/// <summary>
/// Десктоп: порт MCP на 127.0.0.1; null — порт был занят или десктоп уступил его демону, MCP недоступен.
/// Порт может обнулиться на лету, когда стартует демон MCP.
/// </summary>
public sealed class LocalMcpAddress(int? port)
{
    private int _port = port ?? 0;

    public int? Port => Volatile.Read(ref _port) is var value and > 0 ? value : null;

    /// <summary>Десктоп перестал слушать этот порт.</summary>
    public void Release() => Volatile.Write(ref _port, 0);
}
