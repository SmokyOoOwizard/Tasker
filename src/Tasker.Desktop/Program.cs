using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Tasker.Configs;
using Tasker.Daemon;
using Tasker.Global;
using Tasker.Mcp;
using Tasker.Mcp.Configs.Tasker;
using Tasker.Web;

namespace Tasker.Desktop;

internal static class Program
{
    // ~/Library/Application Support/Tasker (macOS), %LOCALAPPDATA%\Tasker (Windows): общий у десктопа, командной строки и демона MCP.
    private static readonly string DataDirectory = AppDirectories.Data;

    [STAThread]
    public static int Main(string[] args)
    {
        // Второй запуск передаёт свои папки первому и завершается — до логов в общий файл и до портов.
        var startup = StartupRequest.FromArgs(args);
        using var instance = SingleInstance.Acquire(DataDirectory, startup);
        if (instance == null)
        {
            Console.WriteLine("Tasker is already running: the request is passed to it");
            // У первого запуска опечатки попадут в журнал при старте хоста; второй запуск до него не дойдёт — скажем сразу.
            foreach (var warning in ConfigDiagnostics.Check(args, ConfigsParser.GetConfigs(args)))
                Console.Error.WriteLine($"Warning: {warning}");
            return 0;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .WriteTo.Console()
            // Скользящее окно в сутки: файл на каждый час, файлы старше 23 часов удаляются
            // при открытии следующего — самая старая запись всегда моложе суток.
            .WriteTo.File(
                Path.Combine(DataDirectory, "logs", "tasker-.log"),
                rollingInterval: RollingInterval.Hour,
                retainedFileCountLimit: null,
                retainedFileTimeLimit: TimeSpan.FromHours(23)
            )
            .CreateLogger();

        try
        {
            Log.Information("Starting Tasker.Desktop");
            var host = StartLocalHost(args);

            var app = host.Services.GetRequiredService<App>();
            app.Startup = startup;
            instance.Requested += app.OnAnotherLaunch;

            // Последнее окно при закрытии прячется в трей — выходим только явно (меню, трей, Dock, Cmd+Q).
            BuildAvaloniaApp(app).StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

            // На главном потоке остался SynchronizationContext Avalonia, а её диспетчер уже
            // остановлен: продолжения StopAsync ушли бы в него и зависли. Останавливаем вне UI.
            Task.Run(() => host.StopAsync()).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception e)
        {
            Log.Fatal(e, "Tasker.Desktop crashed");
            return 1;
        }
        finally
        {
            Log.Information("Tasker.Desktop stopped");
            Log.CloseAndFlush();
        }
    }

    // Локальный in-process хост того же веб-слоя, что и у Tasker.Server.
    // Слушает только loopback на свободном порту и живёт вместе с приложением.
    private static WebApplication StartLocalHost(string[] args)
    {
        int? explicitMcpPort = null;
        var builder = TaskerWebApp.CreateBuilder<DesktopModule>(args, TaskerMode.Local, configs =>
            explicitMcpPort = configs.Get<McpConfigs>().Port);

        // Интерфейс — на случайном порту; MCP — ещё и на постоянном, чтобы адрес можно было прописать агенту.
        // Порт — из --mcpport или из глобальных настроек (общих с командной строкой и демоном MCP).
        // Демон MCP уже работает — он держит MCP всех разрешённых областей, и десктоп свой не поднимает.
        // Порт занят (например, запущен второй Tasker) — приложение работает, но без MCP.
        var daemon = Task.Run(() => DaemonClient.GetStatus()).GetAwaiter().GetResult();
        var plan = DesktopMcpPlan.Decide(explicitMcpPort, LoadMcpSettings(), daemon, IsPortFree);
        if (plan.Error)
            Log.Error(plan.Message);
        else
            Log.Information(plan.Message);

        if (plan.Port is { } mcpPort)
            Log.Information("MCP: {Url} (no token; the open tabs are the workspaces, chosen by the 'workspace' argument; agent from the {Header} header or the local agent)", McpRegistration.LocalUrl(mcpPort), McpRegistration.AgentHeader);

        // Адреса — настройкой Kestrel из памяти: порт MCP можно отпустить на лету, когда стартует демон.
        var address = new LocalMcpAddress(plan.Port);
        var endpoints = LocalEndpoints.Add(builder, plan.Port, address);
        builder.Services.AddSingleton(address);
        builder.Services.AddSingleton<IExternalMcp, DaemonMcpLocator>();
        // Не ждём 30 с по умолчанию, пока WebView закроет keep-alive соединения.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));

        var app = builder.Build();
        app.MapTasker();
        app.StartAsync().GetAwaiter().GetResult();

        // Демон может стартовать позже десктопа: тогда порт MCP отпускаем, иначе демон не запустится.
        if (plan.Port != null)
            _ = endpoints.YieldToDaemon(app.Lifetime.ApplicationStopping);
        return app;
    }

    private static McpSettings LoadMcpSettings()
    {
        try
        {
            return new SettingsStore().Load().Mcp;
        }
        catch (SettingsException e)
        {
            Log.Warning("{Message}: using the default MCP settings", e.Message);
            return new McpSettings();
        }
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => Configure(AppBuilder.Configure<App>());

    private static AppBuilder BuildAvaloniaApp(App app)
        => Configure(AppBuilder.Configure(() => app));

    private static AppBuilder Configure(AppBuilder builder)
        => builder
            .UsePlatformDetect()
            // Без стандартного меню macOS с пунктом «About Avalonia».
            .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
