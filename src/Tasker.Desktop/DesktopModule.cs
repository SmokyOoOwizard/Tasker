using System;
using System.Linq;
using Autofac;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Tasker.Core.Locks;
using Tasker.Global;
using Tasker.Mcp;

namespace Tasker.Desktop;

public class DesktopModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        // Резолвится лениво, уже после старта хоста, — тогда известен реальный порт.
        // Хост слушает два адреса: случайный порт для интерфейса и постоянный для MCP — берём первый.
        // Для разработки фронта с hot reload: TASKER_FRONTEND_DEV_URL=http://localhost:5173
        builder.Register(c =>
            {
                var devUrl = Environment.GetEnvironmentVariable("TASKER_FRONTEND_DEV_URL");
                var mcpPort = c.Resolve<LocalMcpAddress>().Port;
                var address = string.IsNullOrEmpty(devUrl)
                    ? c.Resolve<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                        .First(x => new Uri(x).Port != mcpPort)
                    : devUrl;
                return new FrontendAddress(new Uri(address));
            })
            .SingleInstance();

        // Кто правит из интерфейса: блокировки на время правки показывают другим это имя.
        builder.RegisterInstance(new SettingsLocalEditor(new SettingsStore(), "local")).As<ILocalEditor>();

        builder.RegisterType<App>().AsSelf().SingleInstance();
        builder.RegisterType<WindowManager>().AsSelf().SingleInstance();
    }
}
