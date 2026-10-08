using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Serilog;
using Tasker.Global;

namespace Tasker.Daemon.Host;

/// <summary>
/// Супервизор демона: процесс, которым владеют служба (launchd, systemd) или <c>tasker mcp start</c>, и блокировка <c>daemon.lock</c>.
/// Он ничего не обслуживает сам. Его дело — слушающий сокет (открывает один раз и не закрывает, пока жив демон) и рабочие процессы,
/// которые принимают вызовы из этого сокета. Поэтому сервер можно заменить на лету, не трогая ни службу, ни порт, ни блокировку:
/// <list type="number">
/// <item>рядом со старым рабочим процессом запускается новый (того же сокета у него нет в очереди приёма — затвор закрыт);</item>
/// <item>новый открывает все области и сообщает <c>ready</c>; не успел или не открыл область, которая была открыта у старого, —
/// его снимают, старый как работал, так и работает;</item>
/// <item>новому открывают затвор (<c>activate</c>), старому закрывают (<c>drain</c>): он заканчивает начатые вызовы и выходит.</item>
/// </list>
/// Супервизор намеренно мал и не зависит от веб-слоя: он живёт до перезапуска демона и не обновляется на лету, поэтому обмен с рабочими
/// процессами (<see cref="WireMessage"/>) совместим в обе стороны. Рабочий процесс упал — супервизор запускает его снова (последней сборкой).
/// </summary>
internal sealed class Supervisor
{
    private static readonly TimeSpan PortWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan[] RestartBackoff = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private readonly object _sync = new();
    private readonly List<WorkerProcess> _workers = [];
    private readonly TaskCompletionSource<int> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _upgrade = new(1, 1);
    private readonly Socket _listener;
    private readonly string _token;
    private readonly int _port;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly bool _console;
    private readonly IListenerHandoff _handoff;
    private readonly IWorkerJob _job = WorkerJob.Create();
    private (string File, string[] Arguments) _command = Tasker.Daemon.Launcher.Command();
    private readonly List<DateTime> _failures = [];
    private bool _stopping;

    private Supervisor(Socket listener, IListenerHandoff handoff, int port, string token, bool console)
    {
        _handoff = handoff;
        _listener = listener;
        _port = port;
        _token = token;
        _console = console;
    }

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
            McpDaemon.Detach();
        McpDaemon.ConfigureLogging(detached);

        try
        {
            Log.Information("Starting the MCP server (supervisor, pid {Pid})", Environment.ProcessId);
            var port = new SettingsStore().Load().Mcp.Port;

            // Десктоп мог занять порт раньше: он видит блокировку демона и отпускает порт сам — даём ему на это время.
            var handoff = ListenerHandoff.ForThisSystem();
            var listener = await Bind(port, handoff, ct);
            if (listener == null)
            {
                var message = $"Port {port} is busy: another program (for example Tasker desktop) listens on it. Free it or change the port with 'tasker mcp port <number>'";
                Log.Error(message);
                error.WriteLine(message);
                return 1;
            }

            using (listener)
            {
                var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var supervisor = new Supervisor(listener, handoff, port, token, console: !detached);
                DaemonFiles.WriteInfo(new DaemonInfo(Environment.ProcessId, port, token, supervisor._startedAt));
                try
                {
                    return await supervisor.Serve(ct);
                }
                finally
                {
                    DaemonFiles.DeleteInfo(Environment.ProcessId);
                }
            }
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

    // Сокет, который получают рабочие процессы (наследованием дескриптора или дублированием в процесс, см. IListenerHandoff).
    private static async Task<Socket?> Bind(int port, IListenerHandoff handoff, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + PortWait;
        var announced = false;
        while (true)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // На Windows SO_REUSEADDR разрешает второму сокету занять тот же порт (не как на Unix): там порт берём исключительно.
                if (OperatingSystem.IsWindows())
                    socket.ExclusiveAddressUse = true;
                else
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                socket.Listen(512);
                handoff.Prepare(socket);
                return socket;
            }
            catch (SocketException)
            {
                socket.Dispose();
                if (DateTime.UtcNow >= deadline)
                    return null;
                if (!announced)
                {
                    Log.Information("Port {Port} is busy: waiting up to {Seconds:0} s for the desktop to release it", port, PortWait.TotalSeconds);
                    announced = true;
                }
                await Task.Delay(200, ct);
            }
        }
    }

    private async Task<int> Serve(CancellationToken ct)
    {
        using var signals = new ShutdownSignals(Stop);
        using var job = _job;
        using var cancelled = ct.Register(() => Stop("cancelled"));

        StartWorker(_command.File, _command.Arguments, activate: true);
        Log.Information("MCP server is running on port {Port} (supervisor pid {Pid})", _port, Environment.ProcessId);

        var code = await _finished.Task;
        await StopWorkers();
        Log.Information("MCP server stopped");
        return code;
    }

    private void Stop(string reason)
    {
        Log.Information("Stopping the MCP server: {Reason}", reason);
        lock (_sync)
            _stopping = true;
        _finished.TrySetResult(0);
    }

    // ---- рабочие процессы ----

    private WorkerProcess StartWorker(string file, string[] arguments, bool activate)
    {
        var worker = WorkerProcess.Start(file, arguments, _handoff.WorkerArguments(_listener), _console, _job);
        try
        {
            worker.Hello(_token, _port, Environment.ProcessId, _startedAt, _handoff.Export(_listener, worker.Pid));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            worker.Kill();
            throw new InvalidOperationException($"Cannot give the listening socket to the worker {worker.Pid}: {e.Message}", e);
        }

        worker.Message += message => OnMessage(worker, message);
        worker.Exited += () => OnExited(worker);
        lock (_sync)
            _workers.Add(worker);
        worker.Begin();
        if (activate)
        {
            worker.Role = WorkerRole.Active;
            worker.Send(new WireMessage { Type = "activate" });
        }

        Log.Information("Worker {Pid} started: {File} {Arguments}", worker.Pid, file, string.Join(' ', arguments));
        Broadcast();
        return worker;
    }

    private void Broadcast()
    {
        WorkerProcess[] workers;
        lock (_sync)
            workers = [.. _workers];

        var message = new WireMessage { Type = "workers", Workers = [.. workers.Select(x => x.Status())] };
        foreach (var worker in workers)
            worker.Send(message);
    }

    private WorkerProcess? Active
    {
        get
        {
            lock (_sync)
                return _workers.FirstOrDefault(x => x.Role == WorkerRole.Active);
        }
    }

    private void OnMessage(WorkerProcess worker, WireMessage message)
    {
        switch (message.Type)
        {
            case "ready":
                worker.Version = message.Version ?? "";
                worker.Build = message.Build ?? "";
                worker.Workspaces = message.Workspaces ?? [];
                worker.Ready.TrySetResult();
                Broadcast();
                break;

            case "active":
                worker.ActiveAck.TrySetResult();
                break;

            case "request" when message.Op == "stop":
                Stop("stop requested through the control interface");
                break;

            case "request" when message.Op == "upgrade" && message.Upgrade is { } request:
                _ = Task.Run(async () =>
                {
                    UpgradeResult result;
                    try
                    {
                        result = await Upgrade(request, message.Require ?? []);
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "Upgrade failed");
                        result = new UpgradeResult(false, $"The upgrade failed: {e.Message}");
                    }
                    worker.Send(new WireMessage { Type = "reply", Id = message.Id, Ok = result.Ok, Message = result.Message, Result = result });
                });
                break;
        }
    }

    private void OnExited(WorkerProcess worker)
    {
        bool replaceActive;
        lock (_sync)
        {
            _workers.Remove(worker);
            replaceActive = !_stopping && worker.Role == WorkerRole.Active && !worker.Replaced;
        }

        Log.Information("Worker {Pid} exited with code {Code}", worker.Pid, worker.ExitCode);
        Broadcast();
        if (replaceActive)
            _ = Task.Run(Restart);
    }

    // Рабочий процесс упал: запускаем снова той сборкой, что работала (после замены — новой). Падает без остановки — сдаёмся.
    private async Task Restart()
    {
        TimeSpan pause;
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            _failures.RemoveAll(x => now - x > TimeSpan.FromMinutes(1));
            _failures.Add(now);
            if (_failures.Count > RestartBackoff.Length)
            {
                Log.Fatal("The MCP server worker keeps crashing: giving up");
                _finished.TrySetResult(1);
                return;
            }

            pause = RestartBackoff[_failures.Count - 1];
        }

        Log.Warning("The MCP server worker exited unexpectedly: starting it again in {Seconds:0} s", pause.TotalSeconds);
        await Task.Delay(pause);
        lock (_sync)
        {
            if (_stopping)
                return;
        }

        try
        {
            StartWorker(_command.File, _command.Arguments, activate: true);
        }
        catch (Exception e)
        {
            Log.Error(e, "Cannot start the worker");
            _ = Task.Run(Restart);
        }
    }

    // ---- замена на лету ----

    private async Task<UpgradeResult> Upgrade(UpgradeRequest request, string[] require)
    {
        if (!_upgrade.Wait(0))
            return new UpgradeResult(false, "Another upgrade is in progress: wait for it to finish");

        try
        {
            if (Active is not { } old)
                return new UpgradeResult(false, "No worker accepts calls right now: wait for the server to start, or restart it");
            lock (_sync)
            {
                if (_stopping)
                    return new UpgradeResult(false, "The MCP server is stopping");
            }

            var timeout = TimeSpan.FromSeconds(Math.Max(1, request.TimeoutSeconds));
            Log.Information("Upgrading: starting a new worker next to {OldPid}", old.Pid);

            WorkerProcess fresh;
            try
            {
                fresh = StartWorker(request.File, request.Arguments, activate: false);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return Fail($"Cannot start the new MCP server process ({request.File}): {e.Message}", old);
            }

            // Ждём готовности нового: он открыл все области. Вышел раньше или не уложился — старый остаётся.
            var ready = await Task.WhenAny(fresh.Ready.Task, fresh.ExitedTask, Task.Delay(timeout));
            if (ready != fresh.Ready.Task)
            {
                var reason = ready == fresh.ExitedTask
                    ? $"the new MCP server process exited with code {fresh.ExitCode} before it was ready"
                    : $"the new MCP server process was not ready in {timeout.TotalSeconds:0} s";
                Remove(fresh);
                return Fail($"{Capitalize(reason)}: the running server is untouched. See the log in {AppDirectories.Logs}", old, fresh);
            }

            var missing = require
                .Select(path => (Path: path, Workspace: fresh.Workspaces.FirstOrDefault(x => x.Path == path)))
                .Where(x => x.Workspace is not { State: Daemon.WorkspaceState.Open })
                .ToArray();
            if (missing.Length > 0)
            {
                Remove(fresh);
                var details = string.Join("; ", missing.Select(x => $"{x.Path}: {x.Workspace?.Error ?? "not open"}"));
                return Fail($"The new MCP server process cannot open workspaces that are open now ({details}): the running server is untouched", old, fresh);
            }

            // Открываем затвор у нового раньше, чем закрываем у старого: соединения всё это время есть кому принять.
            fresh.Send(new WireMessage { Type = "activate" });
            if (await Task.WhenAny(fresh.ActiveAck.Task, fresh.ExitedTask, Task.Delay(TimeSpan.FromSeconds(10))) != fresh.ActiveAck.Task)
            {
                Remove(fresh);
                return Fail("The new MCP server process did not start to accept calls: the running server is untouched", old, fresh);
            }

            lock (_sync)
            {
                fresh.Role = WorkerRole.Active;
                old.Role = WorkerRole.Draining;
                old.Replaced = true;
                _command = (request.File, request.Arguments);
            }

            old.Send(new WireMessage { Type = "drain" });
            Broadcast();
            _ = Task.Run(() => KillIfStuck(old));

            Log.Information("Upgraded: worker {NewPid} (build {NewBuild}) accepts calls, {OldPid} (build {OldBuild}) finishes its calls", fresh.Pid, fresh.Build, old.Pid, old.Build);
            return new UpgradeResult(true,
                $"The MCP server process is replaced without downtime: {old.Pid} ({Describe(old)}) -> {fresh.Pid} ({Describe(fresh)}); the old process finishes the calls it has started",
                old.Pid, fresh.Pid, old.Build, fresh.Build);
        }
        finally
        {
            _upgrade.Release();
        }
    }

    private static string Describe(WorkerProcess worker) => $"{(worker.Version is { Length: > 0 } v ? v + ", " : "")}build {(worker.Build is { Length: > 0 } b ? b : "?")}";

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private UpgradeResult Fail(string message, WorkerProcess old, WorkerProcess? fresh = null)
    {
        Log.Warning("Upgrade rolled back: {Message}", message);
        fresh?.Kill();
        return new UpgradeResult(false, message, old.Pid, fresh?.Pid, old.Build, fresh?.Build);
    }

    private void Remove(WorkerProcess worker)
    {
        // Снятый при откате процесс не считается упавшим: заменять его нечем.
        worker.Replaced = true;
        worker.Kill();
        lock (_sync)
            _workers.Remove(worker);
        Broadcast();
    }

    // Старый процесс не вышел за срок завершения — снимаем (его вызовы к этому времени оборваны сервером).
    private async Task KillIfStuck(WorkerProcess old)
    {
        if (await Task.WhenAny(old.ExitedTask, Task.Delay(McpWorker.DrainTimeout + StopWait)) != old.ExitedTask)
        {
            Log.Warning("Worker {Pid} did not exit after draining: killed", old.Pid);
            old.Kill();
        }
    }

    // ---- остановка ----

    private async Task StopWorkers()
    {
        WorkerProcess[] workers;
        lock (_sync)
            workers = [.. _workers];

        foreach (var worker in workers)
            worker.Send(new WireMessage { Type = "stop" });

        var all = Task.WhenAll(workers.Select(x => x.ExitedTask));
        if (await Task.WhenAny(all, Task.Delay(StopWait)) != all)
        {
            foreach (var worker in workers.Where(x => !x.ExitedTask.IsCompleted))
            {
                Log.Warning("Worker {Pid} did not stop in {Seconds:0} s: killed", worker.Pid, StopWait.TotalSeconds);
                worker.Kill();
            }
            await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(2)));
        }
    }
}

/// <summary>Рабочий процесс глазами супервизора: сам процесс, канал связи с ним и то, что он о себе сообщил.</summary>
internal sealed class WorkerProcess
{
    private readonly Process _process;
    private readonly object _write = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WorkerProcess(Process process)
    {
        _process = process;
        Pid = process.Id;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public int Pid { get; }
    public DateTimeOffset StartedAt { get; }
    public WorkerRole Role { get; set; } = WorkerRole.Starting;

    /// <summary>Заменён или снят при откате: его выход не повод запускать рабочий процесс заново.</summary>
    public bool Replaced { get; set; }

    public string Version { get; set; } = "";
    public string Build { get; set; } = "";
    public Daemon.WorkspaceStatus[] Workspaces { get; set; } = [];
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ActiveAck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task ExitedTask => _exited.Task;
    public int ExitCode => _process.HasExited ? _process.ExitCode : -1;

    public event Action<WireMessage>? Message;
    public event Action? Exited;

    public WorkerStatus Status() => new(Pid, Version, Build, Role, StartedAt);

    public static WorkerProcess Start(string file, string[] arguments, string[] listenArguments, bool console, IWorkerJob job)
    {
        var utf8 = new System.Text.UTF8Encoding(false);
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            // Обмен — JSON с путями: на Windows кодировка по умолчанию — кодовая страница консоли, а не UTF-8.
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            // Фоновый супервизор без консоли: без этого у каждого рабочего процесса появилось бы своё окно.
            CreateNoWindow = !console
        };
        AppEnvironment.Apply(info);
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--worker");
        foreach (var argument in listenArguments)
            info.ArgumentList.Add(argument);
        if (console)
            info.ArgumentList.Add("--console");

        var process = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {file}");
        job.Assign(process);
        return new WorkerProcess(process);
    }

    /// <summary>Начинает читать вывод и следить за выходом — после того, как подписались на <see cref="Message"/> и <see cref="Exited"/>.</summary>
    public void Begin()
    {
        _ = Task.Run(() => Read(_process.StandardOutput));
        _ = Task.Run(async () =>
        {
            await _process.WaitForExitAsync();
            // Хвост вывода дочитан раньше выхода: сообщение «ready» перед самым выходом не теряется.
            await Task.Delay(50);
            _exited.TrySetResult();
            Exited?.Invoke();
        });
    }

    public void Hello(string token, int port, int supervisorPid, DateTimeOffset startedAt, string? listenSocket) =>
        Send(new WireMessage { Type = "hello", Token = token, Port = port, SupervisorPid = supervisorPid, SupervisorStartedAt = startedAt, ListenSocket = listenSocket });

    public void Send(WireMessage message)
    {
        try
        {
            lock (_write)
            {
                _process.StandardInput.WriteLine(message.ToLine());
                _process.StandardInput.Flush();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Процесс уже вышел: об этом узнает обработчик выхода.
        }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task Read(StreamReader output)
    {
        try
        {
            while (await output.ReadLineAsync() is { } line)
            {
                if (!line.StartsWith(WireMessage.Prefix, StringComparison.Ordinal))
                    continue;
                if (WireMessage.Parse(line[WireMessage.Prefix.Length..]) is { } message)
                    Message?.Invoke(message);
            }
        }
        catch (IOException)
        {
        }
    }
}
