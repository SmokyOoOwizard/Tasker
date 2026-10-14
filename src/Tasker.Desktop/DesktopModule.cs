using System;
using System.Linq;
using Autofac;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Tasker.Core.Locks;
using Tasker.Daemon;
using Tasker.Global;

namespace Tasker.Desktop;

public class DesktopModule : Module
{
    /// <summary>
    /// Для разработки фронта с hot reload: <c>TASKER_FRONTEND_DEV_URL=http://localhost:5173</c>. WebView открывает Vite, а тот
    /// проксирует API вкладок на хост десктопа — тогда хост слушает постоянный порт <see cref="DevApiPort"/>.
    /// </summary>
    public static string? FrontendDevUrl => Environment.GetEnvironmentVariable("TASKER_FRONTEND_DEV_URL") is { Length: > 0 } url ? url : null;

    /// <summary>Порт хоста десктопа при работе с Vite (<c>frontend/vite.config.ts</c> проксирует на него <c>/w/…/api</c>).</summary>
    public const int DevApiPort = 5720;

    protected override void Load(ContainerBuilder builder)
    {
        // Резолвится лениво, уже после старта хоста, — тогда известен реальный порт.
        builder.Register(c => new FrontendAddress(new Uri(FrontendDevUrl
                ?? c.Resolve<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First())))
            .SingleInstance();

        // Общие с консолью и демоном настройки (settings.json): имя для блокировок, области демона MCP.
        var settings = new SettingsStore();

        // Кто правит из интерфейса: блокировки на время правки показывают другим это имя.
        builder.RegisterInstance(new SettingsLocalEditor(settings, "local")).As<ILocalEditor>();

        // MCP — у демона tasker-mcpd: десктоп запускает его и отдаёт ему открытые папки.
        builder.RegisterInstance(new DesktopDaemon(DaemonLauncher.Find, settings)).AsSelf();

        builder.RegisterType<App>().AsSelf().SingleInstance();
        builder.RegisterType<WindowManager>().AsSelf().SingleInstance();
    }
}
