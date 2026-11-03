using System.Text.Json;
using System.Text.Json.Serialization;
using Autofac;
using Tasker.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Core.Auth;
using Tasker.Core.Projects;
using Tasker.Core.Users;
using Tasker.Mcp;
using Tasker.Web.Configs.Tasker;
using Tasker.Web.Workspaces;

namespace Tasker.Web.Auth;

/// <summary>Всё, что отличается у сервера и десктопа: вход, текущий пользователь, доступ к проектам, MCP.</summary>
internal static class ModeRegistration
{
    /// <summary>Сервисы ASP.NET Core — до Build().</summary>
    public static void AddTaskerMode(this IServiceCollection services, TaskerMode mode)
    {
        // Общие настройки JSON (TaskerJson): enum строками, Unicode без \uXXXX.
        services.ConfigureHttpJsonOptions(o => TaskerJson.Configure(o.SerializerOptions));

        // Текущий пользователь берётся из запроса в обоих режимах: на десктопе — это локальный агент MCP.
        services.AddHttpContextAccessor();

        if (mode == TaskerMode.Server)
        {
            // JWT людей — схема по умолчанию для REST; токен агента — отдельная схема только для /mcp.
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
            services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<TokenService>((bearer, tokens) => tokens.Configure(bearer));
            services.AddAuthorization();
        }

        services.AddTaskerMcp(requireAgentToken: mode == TaskerMode.Server);
    }

    /// <summary>Регистрации Autofac.</summary>
    public static void RegisterTaskerMode(this ContainerBuilder builder, TaskerMode mode, AuthConfigs auth)
    {
        builder.RegisterInstance(new TaskerHost(mode));
        builder.RegisterType<HttpCurrentUser>().As<ICurrentUser>().InstancePerLifetimeScope();

        if (mode == TaskerMode.Local)
        {
            builder.RegisterInstance(new UserOptions(RequireCredentials: false));
            builder.RegisterType<OpenProjectAccess>().As<IProjectAccess>().SingleInstance();
            builder.RegisterType<WorkspaceRegistry>().AsSelf().SingleInstance();
            // Область MCP — аргумент вызова: какие области доступны агентам, знает реестр.
            builder.RegisterType<McpWorkspaceCatalog>().AsSelf().As<IMcpWorkspaces>().SingleInstance();
            return;
        }

        // Ключ подписи создаётся (или генерируется) сразу при старте, а не на первом запросе.
        builder.RegisterInstance(new TokenService(auth, TimeProvider.System)).As<TokenService>().As<IAccessTokenIssuer>();
        builder.RegisterInstance(new AuthSessionOptions(auth.RefreshTokenLifetime));
        builder.RegisterType<AuthService>().AsSelf().InstancePerLifetimeScope();

        builder.RegisterInstance(new UserOptions(RequireCredentials: true));
        builder.RegisterType<MemberProjectAccess>().As<IProjectAccess>().InstancePerLifetimeScope();
        builder.RegisterType<ProjectMemberService>().AsSelf().InstancePerLifetimeScope();
    }
}
