using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Tasker.Daemon;
using Tasker.Global;
using Tasker.Mcp;
using Tasker.Web;
using Tasker.Web.Workspaces;

namespace Tasker.Daemon.Host;

/// <summary>
/// Демон MCP: процесс, который держит MCP-сервер для рабочих областей из глобальных настроек.
/// Адрес один — <c>/mcp</c>; область агент указывает аргументом вызова.
/// Запускается командой <c>tasker mcp run</c> (её же выполняют launchd и systemd при автозапуске).
/// <para>
/// Два режима. <see cref="Supervisor"/> (по умолчанию, Unix) — процесс, которым владеют служба и <c>daemon.lock</c>: он держит слушающий сокет
/// и запускает <em>рабочие процессы</em> (<see cref="RunWorker"/>), которые сервер и составляют; так процесс сервера можно заменить на лету
/// (<c>tasker mcp upgrade</c>). <see cref="Run"/> (<c>--single</c>, Windows) — всё в одном процессе, как раньше: его заменяет только перезапуск.
/// </para>
/// <list type="bullet">
/// <item>Один экземпляр на пользователя: <c>daemon.lock</c> держится, пока процесс жив.</item>
/// <item>Список областей и порт берёт из <see cref="SettingsStore"/>; изменение списка применяет на лету,
/// смена порта требует перезапуска.</item>
/// <item>Управление — <c>/daemon/status</c> и <c>/daemon/stop</c>; нужен секрет из <c>daemon.json</c>, а его читает только владелец.</item>
/// </list>
/// </summary>
public static class McpDaemon
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PortWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PortPoll = TimeSpan.FromMilliseconds(200);

    /// <param name="detached">Запущен в фоне: отвязаться от терминала и писать только в файл.</param>
    /// <returns>Код выхода: 0 — остановлен штатно, 1 — не запустился.</returns>
    public static async Task<int> Run(bool detached, TextWriter error, CancellationToken ct = default)
    {
        using var instance = DaemonFiles.TryHold();
        if (instance == null)
        {
            error.WriteLine("The MCP server is already running: see 'tasker mcp status'");
            return 1;
        }

        if (detached)
            Detach();
        ConfigureLogging(detached);

        try
        {
            Log.Information("Starting the MCP server");
            var store = new SettingsStore();
            var settings = store.Load();
            var port = settings.Mcp.Port;
            // Десктоп мог занять порт раньше: он видит блокировку демона и отпускает порт сам — даём ему на это время.
            if (!await WaitForPort(port, ct))
            {
                var message = $"Port {port} is busy: another program (for example Tasker desktop) listens on it. Free it or change the port with 'tasker mcp port <number>'";
                Log.Error(message);
                error.WriteLine(message);
                return 1;
            }

            var builder = TaskerWebApp.CreateBuilder<DaemonModule>([], TaskerMode.Local);
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

            var state = new DaemonState(port, port);
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            var app = builder.Build();
            ConfigureCatalog(app, state);
            await using var sync = new WorkspaceSync(app.Services.GetRequiredService<WorkspaceRegistry>(), state, port);
            app.MapTaskerMcpHost(routes => MapControl(routes, state, store, token, sync, new ControlHooks()));
            sync.Prepare(settings.Mcp.Workspaces);
            await app.StartAsync(ct);

            // Windows: остановка именованным событием (сигналов нет); на Unix Listen возвращает null.
            using var stopEvent = ShutdownSignal.Listen(() =>
            {
                Log.Information("Stop requested through the stop event");
                app.Lifetime.StopApplication();
            });

            DaemonFiles.WriteInfo(new DaemonInfo(state.Pid, port, token, state.StartedAt));
            Log.Information("MCP server is running: {Url}, the workspace is the 'workspace' argument of a tool (pid {Pid})", McpRegistration.LocalUrl(port), state.Pid);

            using var watch = store.Watch(changed =>
            {
                Log.Information("Settings changed: {Count} workspaces", changed.Mcp.Workspaces.Count);
                _ = sync.Apply(changed.Mcp.Workspaces);
            });
            using var retry = new Timer(_ => _ = sync.Retry(), null, RetryEvery, RetryEvery);
            await sync.Apply(settings.Mcp.Workspaces);

            try
            {
                await app.WaitForShutdownAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C в терминале — штатная остановка.
            }
            Log.Information("Stopping the MCP server");
            await sync.DisposeAsync();
            await app.StopAsync(CancellationToken.None);
            DaemonFiles.DeleteInfo(state.Pid);
            return 0;
        }
        catch (Exception e)
        {
            Log.Fatal(e, "The MCP server crashed");
            error.WriteLine($"The MCP server did not start: {e.Message}");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>Агент видит и области, которые разрешены, но ещё не открылись (list_workspaces), — их знает состояние демона.</summary>
    internal static void ConfigureCatalog(WebApplication app, DaemonState state) =>
        app.Services.GetRequiredService<McpWorkspaceCatalog>().Unavailable = () => state.Snapshot().Workspaces
            .Where(x => x.State != WorkspaceState.Open)
            .Select(x => new McpWorkspace(
                System.IO.Path.GetFileName(x.Path.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : x.Path,
                x.Path,
                x.State == WorkspaceState.Opening ? McpWorkspaceStatus.Opening : McpWorkspaceStatus.Failed,
                Error: x.Error))
            .ToArray();

    /// <summary>Что управление демоном делает иначе у рабочего процесса под супервизором: замена на лету и остановка через него.</summary>
    internal sealed class ControlHooks
    {
        /// <summary>null — демон в одном процессе: заменить на лету его нельзя.</summary>
        public Func<UpgradeRequest, CancellationToken, Task<UpgradeResult>>? Upgrade { get; init; }

        /// <summary>null — остановить свой процесс (<see cref="IHostApplicationLifetime.StopApplication"/>).</summary>
        public Action? Stop { get; init; }

        public DrainState? Drain { get; init; }
    }

    internal static void MapControl(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder routes, DaemonState state, SettingsStore store, string token, WorkspaceSync sync, ControlHooks hooks)
    {
        bool Authorized(HttpContext context) =>
            context.Request.Headers[DaemonClient.ControlHeader].ToString() is { Length: > 0 } given
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token));

        routes.MapGet("/daemon/status", (HttpContext context) =>
        {
            if (!Authorized(context))
                return Results.Unauthorized();

            // Порт из настроек — свежий: если его изменили после запуска, статус подскажет про перезапуск.
            GlobalSettings? current = null;
            try
            {
                current = store.Load();
            }
            catch (SettingsException)
            {
            }
            return Results.Ok(state.Snapshot(current));
        });

        // Сверить индекс области с файлами сейчас — после git pull, checkout и т. п. Возвращается, когда кэш актуален.
        routes.MapPost("/daemon/sync", async (HttpContext context, SyncRequest? request, CancellationToken ct) =>
        {
            if (!Authorized(context))
                return Results.Unauthorized();
            if (request is not { Path.Length: > 0 })
                return Results.BadRequest(new { error = "Body {\"kind\", \"path\"} is required" });

            var location = request.Kind == Tasker.Storage.Files.Workspaces.WorkspaceKind.Files
                ? Tasker.Storage.Files.Workspaces.WorkspaceLocation.Files(request.Path)
                : Tasker.Storage.Files.Workspaces.WorkspaceLocation.Sqlite(request.Path);
            return await sync.Sync(location, ct) is { } result
                ? Results.Ok(result)
                : Results.NotFound(new { error = $"The daemon does not serve {location.Path}" });
        });

        routes.MapPost("/daemon/stop", (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            if (!Authorized(context))
                return Results.Unauthorized();

            Log.Information("Stop requested");
            if (hooks.Stop != null)
                hooks.Stop();
            else
                lifetime.StopApplication();
            return Results.Ok(new { stopping = true });
        });

        // Заменить рабочий процесс на лету: новый поднимается рядом, старый заканчивает начатое. Решает супервизор.
        routes.MapPost("/daemon/upgrade", async (HttpContext context, UpgradeRequest? request, CancellationToken ct) =>
        {
            if (!Authorized(context))
                return Results.Unauthorized();
            if (hooks.Upgrade == null)
                return Results.Conflict(new { error = "The MCP server runs as a single process and cannot be replaced on the fly: restart it" });
            if (request is not { File.Length: > 0 })
                return Results.BadRequest(new { error = "Body {\"file\", \"arguments\", \"timeoutSeconds\"} is required" });
            if (!File.Exists(request.File))
                return Results.Ok(new UpgradeResult(false, $"The program of the new server process is not found: {request.File}"));

            Log.Information("Upgrade requested: {File} {Arguments}", request.File, string.Join(' ', request.Arguments));
            return Results.Ok(await hooks.Upgrade(request, ct));
        });

        // Готов принимать вызовы: все области открыты (или не открылись) и процесс не закрывается. Иначе 503 с Retry-After.
        routes.MapGet("/ready", () =>
        {
            var workspaces = state.Snapshot().Workspaces;
            var ready = workspaces.All(x => x.State != WorkspaceState.Opening) && hooks.Drain is not { Draining: true };
            return ready
                ? Results.Ok(new { ready = true, pid = Environment.ProcessId })
                : Results.Json(new { ready = false, opening = workspaces.Count(x => x.State == WorkspaceState.Opening), draining = hooks.Drain?.Draining ?? false },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        // Процесс жив и отвечает (в отличие от /ready не говорит, открыты ли области).
        routes.MapGet("/health", () => Results.Ok(new { status = "ok", mode = "McpDaemon", pid = Environment.ProcessId }));
    }

    // Файл журнала общий для супервизора и рабочих процессов (на время замены их два): shared — без исключительной блокировки.
    // Консоль — stderr: в stdout рабочего процесса идёт обмен с супервизором.
    internal static void ConfigureLogging(bool detached, bool toStandardError = false)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(AppDirectories.Logs, "mcp-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7, shared: true);
        if (!detached)
            configuration = toStandardError ? configuration.WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose) : configuration.WriteTo.Console();
        Log.Logger = configuration.CreateLogger();
    }

    /// <summary>Порт свободен сейчас или освободится за <see cref="PortWait"/> (десктоп уступает его демону на лету).</summary>
    internal static async Task<bool> WaitForPort(int port, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + PortWait;
        var announced = false;
        while (!IsPortFree(port))
        {
            if (DateTime.UtcNow >= deadline)
                return false;
            if (!announced)
            {
                Log.Information("Port {Port} is busy: waiting up to {Seconds:0} s for the desktop to release it", port, PortWait.TotalSeconds);
                announced = true;
            }
            await Task.Delay(PortPoll, ct);
        }
        return true;
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

    // Новый сеанс: закрытие терминала (SIGHUP) не убьёт фоновый демон.
    internal static void Detach()
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            setsid();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();
}
