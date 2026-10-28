using System;
using System.IO;
using System.Threading;
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

    // Локальный in-process хост того же веб-слоя, что и у Tasker.Server: только интерфейс и REST вкладок, без MCP.
    // Слушает только loopback на свободном порту (с Vite — на постоянном, см. DevApiPort) и живёт вместе с приложением.
    private static WebApplication StartLocalHost(string[] args)
    {
        var builder = TaskerWebApp.CreateBuilder<DesktopModule>(args, TaskerMode.Local);
        builder.WebHost.UseUrls($"http://127.0.0.1:{(DesktopModule.FrontendDevUrl == null ? 0 : DesktopModule.DevApiPort)}");

        // MCP обслуживает демон tasker-mcpd: десктоп запускает его, если он не работает, и показывает его состояние.
        builder.Services.AddSingleton<IDaemonMcp, DaemonMcpLocator>();
        // Не ждём 30 с по умолчанию, пока WebView закроет keep-alive соединения.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));

        var app = builder.Build();
        app.MapTasker();
        app.StartAsync().GetAwaiter().GetResult();

        // Интерфейс не ждёт демона: окно открывается сразу, состояние демона видно в трее и во вкладке «Агенты».
        var daemon = app.Services.GetRequiredService<DesktopDaemon>();
        _ = Task.Run(() => StartDaemon(daemon, app.Lifetime.ApplicationStopping));
        return app;
    }

    /// <summary>Демон MCP не работает — запускаем его (как <c>tasker mcp start</c>) и ждём готовности. Закрытие десктопа его не остановит.</summary>
    private static async Task StartDaemon(DesktopDaemon daemon, CancellationToken ct)
    {
        try
        {
            var result = await daemon.Start(ct);
            if (result.Status is { } status)
            {
                Log.Information(result.Started
                        ? "MCP: started the MCP server (tasker-mcpd), pid {Pid}: {Url}"
                        : "MCP: served by the running MCP server (tasker-mcpd), pid {Pid}: {Url}",
                    status.Pid, status.McpUrl);
            }
            else
            {
                Log.Error("MCP is unavailable: {Error}", result.Error);
            }
        }
        catch (OperationCanceledException)
        {
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
