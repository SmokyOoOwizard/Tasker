#pragma warning disable CA1416 // вызовы Windows в тестах стоят за проверкой платформы (WindowsFact) или проверяют только раскладку структур
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Tasker.Daemon;
using Tasker.Daemon.Host;
using Tasker.Global;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Демон на Windows (TSK-117). Чистая логика (командная строка, окружение, имя события, выбор режима, описание сокета, раскладка структур
/// Win32) проверяется на любой системе; вызовы ОС — тестами <see cref="WindowsFactAttribute"/>, которые на других системах пропускаются
/// и ждут запуска на Windows (чек-лист в описании TSK-117 и на странице вики «Windows»).
/// </summary>
public class DaemonWindowsTests : IDisposable
{
    private readonly List<IDisposable> _env = [];

    public void Dispose()
    {
        foreach (var x in _env)
            x.Dispose();
    }

    // ---- чистые функции ----

    [Theory]
    [InlineData(new[] { "--detached" }, "\"C:\\Program Files\\tasker\\tasker-mcpd.exe\" --detached")]
    [InlineData(new[] { "a b", "" }, "\"C:\\Program Files\\tasker\\tasker-mcpd.exe\" \"a b\" \"\"")]
    [InlineData(new[] { "say \"hi\"" }, "\"C:\\Program Files\\tasker\\tasker-mcpd.exe\" \"say \\\"hi\\\"\"")]
    [InlineData(new[] { "C:\\dir with space\\" }, "\"C:\\Program Files\\tasker\\tasker-mcpd.exe\" \"C:\\dir with space\\\\\"")]
    [InlineData(new[] { "C:\\plain\\path" }, "\"C:\\Program Files\\tasker\\tasker-mcpd.exe\" C:\\plain\\path")]
    [InlineData(new[] { "back\\\\\"q" }, "\"C:\\Program Files\\tasker\\tasker-mcpd.exe\" \"back\\\\\\\\\\\"q\"")]
    public void The_command_line_is_quoted_so_that_Windows_splits_it_back_into_the_same_arguments(string[] arguments, string expected) =>
        Assert.Equal(expected, WindowsCommandLine.Build("C:\\Program Files\\tasker\\tasker-mcpd.exe", arguments));

    [Fact]
    public void The_environment_block_is_sorted_ignoring_case_skips_unset_variables_and_ends_with_two_nulls()
    {
        var block = WindowsCommandLine.EnvironmentBlock(
        [
            new("b", "2"), new("TASKER_HOME", "C:\\Users\\Артём\\data"), new("Path", "x;y"), new("gone", null), new("A", "")
        ]);

        Assert.Equal("A=\0b=2\0Path=x;y\0TASKER_HOME=C:\\Users\\Артём\\data\0\0", block);
    }

    [Fact]
    public void The_stop_event_name_depends_on_the_data_directory_only_and_stays_in_the_session_namespace()
    {
        var first = ShutdownSignal.EventName(Path.Combine(Path.GetTempPath(), "one", "mcp"));
        Assert.Equal(first, ShutdownSignal.EventName(Path.Combine(Path.GetTempPath(), "one", "mcp") + Path.DirectorySeparatorChar));
        Assert.NotEqual(first, ShutdownSignal.EventName(Path.Combine(Path.GetTempPath(), "two", "mcp")));
        Assert.StartsWith("Local\\tasker-mcp-stop-", first);
        Assert.True(first.Length < 100);
    }

    [Theory]
    [InlineData(false, false, false, null, true)]
    [InlineData(false, true, false, null, false)]
    [InlineData(false, true, true, "1", false)]
    [InlineData(true, false, false, null, false)]
    [InlineData(true, false, false, "0", false)]
    [InlineData(true, false, false, "1", true)]
    [InlineData(true, false, false, "True", true)]
    [InlineData(true, false, true, null, true)]
    [InlineData(true, true, true, "1", false)]
    public void The_supervisor_is_the_default_on_Unix_and_opt_in_on_Windows(bool windows, bool single, bool supervised, string? variable, bool expected) =>
        Assert.Equal(expected, DaemonPlatform.UseSupervisor(windows, single, supervised, variable));

    [Fact]
    public void A_recycled_process_number_is_not_taken_for_the_daemon()
    {
        var recorded = new DateTimeOffset(2026, 10, 7, 12, 0, 5, TimeSpan.Zero);

        Assert.True(DaemonProcessIdentity.StartMatches(recorded.AddSeconds(-3), recorded));
        Assert.True(DaemonProcessIdentity.StartMatches(recorded.AddMilliseconds(500), recorded));
        Assert.False(DaemonProcessIdentity.StartMatches(recorded.AddHours(-2), recorded)); // процесс старше демона: номер достался уже существовавшему
        Assert.False(DaemonProcessIdentity.StartMatches(recorded.AddMinutes(10), recorded)); // процесс запущен позже записи
    }

    [Fact]
    public void Find_returns_a_live_process_only_when_the_start_time_matches()
    {
        using var self = Process.GetCurrentProcess();
        var started = new DateTimeOffset(self.StartTime);

        using (var found = DaemonProcessIdentity.Find(new DaemonInfo(Environment.ProcessId, 1, "t", started.AddSeconds(1))))
            Assert.NotNull(found);
        Assert.Null(DaemonProcessIdentity.Find(new DaemonInfo(Environment.ProcessId, 1, "t", started.AddDays(-1))));
        Assert.Null(DaemonProcessIdentity.Find(new DaemonInfo(int.MaxValue - 7, 1, "t", started)));
    }

    [Fact]
    public void The_socket_description_survives_the_hello_message_and_garbage_is_ignored()
    {
        var info = Enumerable.Range(0, ListenerHandoff.ProtocolInfoSize).Select(x => (byte)x).ToArray();

        var wsa = ListenerHandoff.Parse(ListenerHandoff.FormatWsa(info));
        Assert.Equal(info, wsa!.Value.ProtocolInfo);
        Assert.Null(wsa.Value.Fd);
        Assert.Equal(42, ListenerHandoff.Parse(ListenerHandoff.FormatFd(42))!.Value.Fd);

        Assert.Null(ListenerHandoff.Parse(null));
        Assert.Null(ListenerHandoff.Parse(""));
        Assert.Null(ListenerHandoff.Parse("fd:-1"));
        Assert.Null(ListenerHandoff.Parse("fd:x"));
        Assert.Null(ListenerHandoff.Parse("wsa:not base64!"));
        Assert.Null(ListenerHandoff.Parse("wsa:" + Convert.ToBase64String(new byte[10])));
        Assert.Throws<ArgumentException>(() => ListenerHandoff.FormatWsa(new byte[10]));
    }

    [Fact]
    public void Without_any_socket_the_worker_refuses_to_start_instead_of_listening_on_nothing()
    {
        Assert.Throws<InvalidOperationException>(() => ListenerHandoff.Import(null, null));
        Assert.Equal(7UL, ListenerHandoff.Import(null, 7));
        Assert.Equal(9UL, ListenerHandoff.Import(ListenerHandoff.FormatFd(9), null));
    }

    [Fact]
    public void The_hello_message_carries_the_socket_description_and_old_peers_ignore_it()
    {
        var hello = new WireMessage { Type = "hello", Token = "t", Port = 5719, ListenSocket = "fd:5" };

        var parsed = WireMessage.Parse(hello.ToLine())!;
        Assert.Equal("fd:5", parsed.ListenSocket);

        // Сообщение без поля (супервизор старой сборки) читается: сокет тогда придёт по --listen-fd.
        Assert.Null(WireMessage.Parse("{\"type\":\"hello\",\"token\":\"t\"}")!.ListenSocket);
    }

    [Fact]
    public void The_job_object_structure_has_the_layout_Windows_expects()
    {
        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION: 144 байта на 64-разрядной системе, 112 на 32-разрядной.
        Assert.Equal(IntPtr.Size == 8 ? 144 : 112, Marshal.SizeOf<WindowsJob.ExtendedLimitInformationStruct>());
        Assert.Equal(IntPtr.Size == 8 ? 64 : 48, Marshal.SizeOf<WindowsJob.BasicLimitInformation>());
    }

    [Fact]
    public void The_stop_event_is_not_used_outside_Windows()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Null(ShutdownSignal.Listen(() => { }));
        Assert.False(ShutdownSignal.Raise());
    }

    // ---- рабочий процесс получает сокет сообщением (путь Windows, проверенный на Unix) ----

    [Fact]
    public async Task A_worker_that_gets_the_listening_socket_in_the_hello_message_serves_and_is_replaced_on_the_fly()
    {
        if (OperatingSystem.IsWindows())
            return; // на Windows тот же путь проверяет Windows_supervisor_*: сообщение там несёт описание WinSock

        using var daemon = new DaemonFixture();
        _env.Add(AppEnvironment.Override(ListenerHandoff.ModeVariable, "message"));
        _env.Add(AppEnvironment.Override("TASKER_MCP_DRAIN_QUIET_MS", "300"));
        await daemon.Workspace("ws", "Demo");

        var before = await daemon.Start();
        var worker = before.Workers.Single();
        var key = before.Workspaces[0].Key!;
        Assert.Equal(["Demo"], DaemonFixture.ProjectNames(await daemon.CallTool(key, "list_projects")));

        var upgrade = await daemon.Mcp("upgrade");
        Assert.True(upgrade.Code == 0, upgrade.Err + upgrade.Out);

        var after = (await daemon.Status())!;
        Assert.Equal(before.Pid, after.Pid);
        Assert.NotEqual(worker.Pid, after.Workers.Single().Pid);
        Assert.Equal(["Demo"], DaemonFixture.ProjectNames(await daemon.CallTool(key, "list_projects")));
        Assert.Equal(0, (await daemon.Mcp("stop")).Code);
        Assert.False(DaemonFiles.IsRunning());
    }

    // ---- вызовы Windows: выполняются только на Windows ----

    [WindowsFact]
    public async Task Windows_a_socket_duplicated_into_a_process_shares_the_accept_queue_and_the_original_stays_open()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(16);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        // Процесс-приёмник — этот же (в тесте рабочего процесса нет): дубликат — второй дескриптор того же сокета.
        var description = ListenerHandoffWindows.Export(listener, Environment.ProcessId);
        var imported = ListenerHandoffWindows.Import(description);
        Assert.NotEqual(0UL, imported);
        using var duplicate = new Socket(new System.Net.Sockets.SafeSocketHandle((IntPtr)imported, ownsHandle: true));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var accepted = await duplicate.AcceptAsync();
        Assert.True(accepted.Connected);
        Assert.Equal(port, ((IPEndPoint)duplicate.LocalEndPoint!).Port);
        Assert.True(listener.IsBound); // исходный сокет не закрыт (в отличие от Socket.DuplicateAndClose)
    }

    [WindowsFact]
    public async Task Windows_the_stop_event_wakes_the_listener_and_raising_without_a_listener_reports_false()
    {
        Assert.False(ShutdownSignal.Raise());

        var woken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var listener = ShutdownSignal.Listen(() => woken.TrySetResult()))
        {
            Assert.NotNull(listener);
            Assert.True(ShutdownSignal.Raise());
            await woken.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.False(ShutdownSignal.Raise());
    }

    [WindowsFact]
    public async Task Windows_a_process_in_the_job_object_is_killed_when_the_job_is_closed()
    {
        var job = WorkerJob.Create();
        var info = new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(info)!;
        job.Assign(process);

        job.Dispose();

        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.True(process.HasExited);
    }

    [WindowsFact]
    public async Task Windows_a_detached_process_runs_without_a_window_and_survives_the_caller_handles()
    {
        var marker = Path.Combine(Path.GetTempPath(), "tasker-detached-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            using var process = WindowsDetachedProcess.Start(cmd, ["/c", $"echo started> \"{marker}\""]);
            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

            Assert.True(File.Exists(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [WindowsFact]
    public async Task Windows_the_supervisor_starts_in_the_background_serves_upgrades_and_stops_through_the_stop_event()
    {
        using var daemon = new DaemonFixture();
        _env.Add(AppEnvironment.Override(DaemonPlatform.SupervisorVariable, "1"));
        _env.Add(AppEnvironment.Override("TASKER_MCP_DRAIN_QUIET_MS", "300"));
        await daemon.Workspace("ws", "Demo");

        var before = await daemon.Start();
        Assert.True(before.Supervised);
        var key = before.Workspaces[0].Key!;
        Assert.Equal(["Demo"], DaemonFixture.ProjectNames(await daemon.CallTool(key, "list_projects")));

        var upgrade = await daemon.Mcp("upgrade");
        Assert.True(upgrade.Code == 0, upgrade.Err + upgrade.Out);
        var after = (await daemon.Status())!;
        Assert.NotEqual(before.Workers.Single().Pid, after.Workers.Single().Pid);
        Assert.Equal(["Demo"], DaemonFixture.ProjectNames(await daemon.CallTool(key, "list_projects")));

        Assert.True(ShutdownSignal.Raise());
        for (var i = 0; i < 100 && DaemonFiles.IsRunning(); i++)
            await Task.Delay(100);
        Assert.False(DaemonFiles.IsRunning());
    }
}

/// <summary>Доступ тестов к вызовам WinSock без обращения к атрибутам платформы из каждого теста.</summary>
internal static class ListenerHandoffWindows
{
    public static byte[] Export(Socket socket, int pid) => WindowsSockets.Export(socket, pid);

    public static ulong Import(byte[] info) => WindowsSockets.Import(info);
}
