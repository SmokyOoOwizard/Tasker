using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Serilog;
using Tasker.Global;
using Tasker.Mcp;
using Tasker.Web;
using Tasker.Web.Workspaces;

namespace Tasker.Daemon.Host;

/// <summary>Какая это сборка демона: версия и короткий идентификатор (меняется с каждой сборкой, в отличие от версии).</summary>
internal static class DaemonBuild
{
    public static string Version { get; } =
        typeof(DaemonBuild).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } version
            ? version.Split('+')[0]
            : typeof(DaemonBuild).Assembly.GetName().Version?.ToString() ?? "unknown";

    public static string Id { get; } = typeof(DaemonBuild).Module.ModuleVersionId.ToString("N")[..8];
}

/// <summary>
/// Рабочий процесс демона под супервизором (<see cref="Supervisor"/>): тот же сервер MCP, что и в <see cref="McpDaemon.Run"/>, но слушающий сокет
/// ему дали готовым (наследуемый дескриптор), а принимает он из него, только когда супервизор открыл затвор (<see cref="AcceptGate"/>):
/// первый процесс — сразу, процесс, пришедший на смену, — когда открыл все области.
/// </summary>
internal static class McpWorker
{
    private static readonly TimeSpan HelloWait = TimeSpan.FromSeconds(10);

    /// <summary>Сколько ждать тишины (нет вызовов в работе), прежде чем старый процесс выйдет; дольше — соединения всё ещё могут прийти.</summary>
    internal static TimeSpan DrainQuiet =>
        int.TryParse(AppEnvironment.Get("TASKER_MCP_DRAIN_QUIET_MS"), out var ms) && ms >= 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromSeconds(2);

    /// <summary>Сколько старый процесс доделывает начатые вызовы (массовая правка ждёт блокировок до 30 с); потом остальные обрываются.</summary>
    internal static TimeSpan DrainTimeout =>
        int.TryParse(AppEnvironment.Get("TASKER_MCP_DRAIN_TIMEOUT_MS"), out var ms) && ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromSeconds(60);

    /// <returns>Код выхода: 0 — остановлен штатно, 1 — не запустился.</returns>
    /// <param name="listenFd">Дескриптор слушающего сокета из <c>--listen-fd</c> (Unix); null — сокет придёт в <c>hello</c> (Windows).</param>
    public static async Task<int> Run(int? listenFd, bool console, TextWriter error, CancellationToken ct = default)
    {
        McpDaemon.ConfigureLogging(detached: !console, toStandardError: true);
        if (listenFd is { } inherited && !OperatingSystem.IsWindows())
            UnixFd.SetInheritable(inherited, false); // процессы, которые запустит сам сервер, сокет не получают

        // Обмен с супервизором — всегда UTF-8 (кодировка консоли по умолчанию в Windows — кодовая страница).
        var utf8 = new System.Text.UTF8Encoding(false);
        var link = new WorkerLink(new StreamReader(Console.OpenStandardInput(), utf8), new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n" });
        try
        {
            var hello = await Task.Run(() => link.ReadHello(HelloWait), CancellationToken.None);
            Log.Information("Starting the MCP server worker {Version} (build {Build}, pid {Pid})", DaemonBuild.Version, DaemonBuild.Id, Environment.ProcessId);

            var store = new SettingsStore();
            var settings = store.Load();
            var port = hello.Port ?? settings.Mcp.Port;
            var token = hello.Token ?? throw new InvalidOperationException("The supervisor did not send the control secret");
            var listenHandle = ListenerHandoff.Import(hello.ListenSocket, listenFd);
            if (listenFd is null && !OperatingSystem.IsWindows() && ListenerHandoff.Parse(hello.ListenSocket) is { Fd: { } fromMessage })
                UnixFd.SetInheritable(fromMessage, false);

            var gate = new AcceptGate();
            var drain = new DrainState();
            var builder = TaskerWebApp.CreateBuilder<DaemonModule>([], TaskerMode.Local);
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.ListenHandle(listenHandle);
                // Только локальные клиенты: защита от медленной отправки тела (240 Б/с, 5 с) не нужна и оборвала бы вызов, который клиент досылает долго.
                k.Limits.MinRequestBodyDataRate = null;
            });
            builder.Services.Replace(ServiceDescriptor.Singleton<IConnectionListenerFactory>(sp =>
                new GatedListenerFactory(ActivatorUtilities.CreateInstance<SocketTransportFactory>(sp), gate)));
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

            var state = new DaemonState(port, port)
            {
                Pid = hello.SupervisorPid ?? Environment.ProcessId,
                StartedAt = hello.SupervisorStartedAt ?? DateTimeOffset.UtcNow
            };

            var app = builder.Build();
            McpDaemon.ConfigureCatalog(app, state);
            var lifetime = app.Lifetime;

            await using var sync = new WorkspaceSync(app.Services.GetRequiredService<WorkspaceRegistry>(), state, port);
            var hooks = new McpDaemon.ControlHooks
            {
                Drain = drain,
                Stop = () => link.Send(new WireMessage { Type = "request", Op = "stop" }),
                Upgrade = async (request, requestCt) =>
                {
                    var require = state.Snapshot().Workspaces.Where(x => x.State == WorkspaceState.Open).Select(x => x.Path).ToArray();
                    try
                    {
                        var reply = await link.Request("upgrade", m => { m.Upgrade = request; m.Require = require; },
                            TimeSpan.FromSeconds(request.TimeoutSeconds + 30), requestCt);
                        return reply.Result ?? new UpgradeResult(reply.Ok ?? false, reply.Message ?? "The supervisor sent no result");
                    }
                    catch (Exception e) when (e is TimeoutException or IOException)
                    {
                        return new UpgradeResult(false, $"The supervisor did not answer the upgrade request: {e.Message}");
                    }
                }
            };

            app.Use((context, next) => drain.Invoke(context, () => next(context)));
            app.MapTaskerMcpHost(routes => McpDaemon.MapControl(routes, state, store, token, sync, hooks));
            sync.Prepare(settings.Mcp.Workspaces);

            // Команды супервизора слушаем сразу: activate может прийти раньше, чем сервер стартует. Канал закрыт — супервизора нет.
            // Console.In читает синхронно, поэтому — на своём потоке пула, а не в потоке запуска.
            var listening = Task.Run(() => link.Listen(message => Handle(message, gate, drain, state, lifetime, link, ct)), CancellationToken.None);
            _ = listening.ContinueWith(_ =>
            {
                Log.Warning("The supervisor closed the pipe: stopping");
                lifetime.StopApplication();
            }, TaskScheduler.Default);

            await app.StartAsync(ct);
            Log.Information("MCP server worker is up (pid {Pid}); it accepts calls when the supervisor opens the gate", Environment.ProcessId);

            using var watch = store.Watch(changed =>
            {
                Log.Information("Settings changed: {Count} workspaces", changed.Mcp.Workspaces.Count);
                _ = sync.Apply(changed.Mcp.Workspaces);
            });
            using var retry = new Timer(_ => _ = sync.Retry(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            await sync.Apply(settings.Mcp.Workspaces);

            // Области открыты (или не открылись): сообщаем, что можно принимать вызовы.
            link.Send(new WireMessage
            {
                Type = "ready",
                Version = DaemonBuild.Version,
                Build = DaemonBuild.Id,
                Workspaces = state.Snapshot().Workspaces
            });
            Log.Information("MCP server worker is ready: {Url}, the workspace is the 'workspace' argument of a tool (pid {Pid})", McpRegistration.LocalUrl(port), Environment.ProcessId);

            try
            {
                await app.WaitForShutdownAsync(ct);
            }
            catch (OperationCanceledException)
            {
            }
            Log.Information("Stopping the MCP server worker");
            await sync.DisposeAsync();
            await app.StopAsync(CancellationToken.None);
            return 0;
        }
        catch (Exception e)
        {
            Log.Fatal(e, "The MCP server worker crashed");
            error.WriteLine($"The MCP server did not start: {e.Message}");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static async Task Handle(WireMessage message, AcceptGate gate, DrainState drain, DaemonState state, IHostApplicationLifetime lifetime, WorkerLink link, CancellationToken ct)
    {
        switch (message.Type)
        {
            case "activate":
                gate.Open();
                Log.Information("Accepting calls");
                link.Send(new WireMessage { Type = "active" });
                break;

            case "drain":
                // Не ждём здесь: управление должно ответить на запрос о замене, а он сам — вызов в работе.
                _ = Task.Run(() => Drain(gate, drain, lifetime, ct), CancellationToken.None);
                break;

            case "stop":
                Log.Information("Stop requested by the supervisor");
                lifetime.StopApplication();
                break;

            case "workers":
                state.SetWorkers(message.Workers ?? []);
                break;
        }

        await Task.CompletedTask;
    }

    // Старый процесс при замене: перестаёт принимать, доделывает начатое и выходит. Новое соединение к нему больше не придёт —
    // слушающий сокет общий, и принимает из него новый процесс.
    private static async Task Drain(AcceptGate gate, DrainState drain, IHostApplicationLifetime lifetime, CancellationToken ct)
    {
        try
        {
            gate.Close();
            drain.BeginDrain();
            Log.Information("Draining: no new connections are accepted, {Count} call(s) in progress", drain.Active);
            var left = await drain.WaitQuiet(DrainQuiet, DrainTimeout, ct);
            drain.BeginClosing();
            Log.Information("Drained ({Left} call(s) aborted): the process exits", left);
        }
        catch (Exception e)
        {
            Log.Error(e, "Draining failed: the process exits anyway");
        }
        finally
        {
            lifetime.StopApplication();
        }
    }
}
