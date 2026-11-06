using System.Text.Json;
using System.Text.Json.Serialization;
using Autofac;
using Tasker.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Core.Auth;
using Tasker.Core.Locks;
using Tasker.Core.Projects;
using Tasker.Core.Users;
using Tasker.Global;
using Tasker.Mcp;
using Tasker.Web.Configs.Tasker;
using Tasker.Web.Graph;
using Tasker.Web.Workspaces;

namespace Tasker.Web.Auth;

/// <summary>Всё, что отличается у сервера, десктопа и демона MCP: вход, текущий пользователь, доступ к проектам, MCP.</summary>
internal static class ModeRegistration
{
    /// <summary>Сервисы ASP.NET Core — до Build().</summary>
    public static void AddTaskerMode(this IServiceCollection services, TaskerMode mode, bool localMcp)
    {
        // Общие настройки JSON (TaskerJson): enum строками, Unicode без \uXXXX.
        services.ConfigureHttpJsonOptions(o => TaskerJson.Configure(o.SerializerOptions));

        // Текущий пользователь берётся из запроса; на десктопе входа нет, у демона это локальный агент MCP области.
        services.AddHttpContextAccessor();

        if (mode == TaskerMode.Server)
        {
            // JWT людей — схема по умолчанию для REST; токен агента — отдельная схема только для /mcp.
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
            services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<TokenService>((bearer, tokens) => tokens.Configure(bearer));
            services.AddAuthorization();
        }

        services.AddTaskerGraph();

        if (mode == TaskerMode.Server || localMcp)
            services.AddTaskerMcp(requireAgentToken: mode == TaskerMode.Server);
    }

    /// <summary>Регистрации Autofac.</summary>
    public static void RegisterTaskerMode(this ContainerBuilder builder, TaskerMode mode, AuthConfigs auth, bool localMcp)
    {
        builder.RegisterInstance(new TaskerHost(mode));
        builder.RegisterType<HttpCurrentUser>().As<ICurrentUser>().InstancePerLifetimeScope();

        if (mode == TaskerMode.Local)
        {
            var settings = new SettingsStore();
            builder.RegisterInstance(new SettingsLocalEditor(settings, CurrentUserEditor.LocalKey)).As<ILocalEditor>();
            builder.RegisterInstance(new SettingsLocalPerson(settings, TimeProvider.System)).As<ILocalPerson>();
            builder.RegisterInstance(new UserOptions(RequireCredentials: false));
            builder.RegisterType<OpenProjectAccess>().As<IProjectAccess>().SingleInstance();
            builder.RegisterType<WorkspaceRegistry>().AsSelf().SingleInstance();
            // Демон: область MCP — аргумент вызова, какие области доступны агентам, знает реестр.
            if (localMcp)
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
