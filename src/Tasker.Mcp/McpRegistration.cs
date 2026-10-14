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
/// MCP-сервер Tasker на сервере (Streamable HTTP, без сессий) по адресу <see cref="Path"/> на том же хосте, что и REST:
/// одна область, вход только по токену агента (<see cref="AgentTokenAuthenticationHandler"/>). Локальный MCP (область — аргумент
/// вызова) обслуживает демон <c>tasker-mcpd</c>; десктоп своего MCP не поднимает.
/// </summary>
public static class McpRegistration
{
    public const string Path = "/mcp";

    public static void AddTaskerMcp(this IServiceCollection services)
    {
        // Фильтры вызова: первый добавленный — самый внешний. Внешний — ошибки (в том числе фильтров ниже) без префикса SDK;
        // затем проект по умолчанию.
        services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "tasker", Version = "1.0.0" })
            .WithHttpTransport(o => o.Stateless = true)
            .WithRequestFilters(filters =>
            {
                filters.AddCallToolFilter(ErrorText.Filter);
                // projectId необязателен, когда у агента один проект (см. DefaultProject).
                filters.AddCallToolFilter(DefaultProject.Filter);
            });

        // Как WithToolsFromAssembly, но со своим ToolServices: см. там, почему нельзя отдать SDK контейнер Autofac как есть.
        var methods = typeof(McpRegistration).Assembly.GetTypes()
            .Where(x => x.IsDefined(typeof(McpServerToolTypeAttribute), inherit: false))
            .SelectMany(x => x.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(x => x.IsDefined(typeof(McpServerToolAttribute), inherit: false));

        foreach (var method in methods)
        {
            services.AddSingleton(sp =>
            {
                var tool = McpServerTool.Create(method, target: null, new McpServerToolCreateOptions
                {
                    Services = new ToolServices(sp),
                    SerializerOptions = TaskerJson.Options,
                    SchemaCreateOptions = NestedRequired.SchemaOptions
                });
                DefaultProject.MakeOptional(tool.ProtocolTool);
                return tool;
            });
        }

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, AgentTokenAuthenticationHandler>(AgentTokenAuthenticationHandler.SchemeName, null);
    }

    public static void MapTaskerMcp(this IEndpointRouteBuilder app)
    {
        var mcp = app.MapMcp(Path);

        // На проводе без \uXXXX: SDK кодирует конверт JSON-RPC своими опциями, которые не настроить (см. WireJson).
        WireJson.Unescape(mcp);

        // Только схема токена агента: JWT людей в MCP не принимается (и наоборот — токен агента в REST).
        mcp.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = AgentTokenAuthenticationHandler.SchemeName });
    }
}
