using System.Text;
using System.Xml.Linq;
using Tasker.Daemon.Services;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// Автозапуск через Планировщик заданий Windows: определение задачи, командная строка и команды schtasks проверяются без
/// настоящего Планировщика (на любой системе); настоящий Планировщик — только <see cref="WindowsFactAttribute"/>-тест.
/// </summary>
public class WindowsTaskServiceTests : IDisposable
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private readonly IsolatedHome _home = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _runner = new();

    public WindowsTaskServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        _home.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Calls { get; } = [];
        public Dictionary<string, ProcessResult> Results { get; } = new();

        public Task<ProcessResult> Run(string file, params string[] arguments)
        {
            var line = $"{file} {string.Join(' ', arguments)}";
            Calls.Add(line);
            return Task.FromResult(Results.FirstOrDefault(x => line.StartsWith(x.Key)).Value ?? new ProcessResult(0, "", ""));
        }
    }

    private WindowsTaskService Service(string name = "Tasker MCP Test") => new(_runner, _directory, name, @"PC\user");

    // ---- XML (golden) ----

    [Fact]
    public void Task_xml_matches_the_golden_sample()
    {
        var xml = WindowsTaskDefinition.Xml(
            "Tasker MCP", @"PC\Артём", @"%SystemRoot%\System32\conhost.exe",
            "--headless \"C:\\Users\\Артём Иванов\\tasker\\tasker-mcpd.exe\" --detached", @"C:\Users\Артём Иванов\AppData\Local\Tasker");

        const string expected = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Tasker MCP: MCP server of Tasker (starts at logon, restarts on failure)</Description>
                <URI>\Tasker MCP</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>PC\Артём</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>PC\Артём</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>999</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>%SystemRoot%\System32\conhost.exe</Command>
                  <Arguments>--headless &quot;C:\Users\Артём Иванов\tasker\tasker-mcpd.exe&quot; --detached</Arguments>
                  <WorkingDirectory>C:\Users\Артём Иванов\AppData\Local\Tasker</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>

            """;
        Assert.Equal(expected.ReplaceLineEndings("\r\n"), xml);
    }

    [Fact]
    public void Task_xml_is_well_formed_and_escapes_special_characters()
    {
        var xml = WindowsTaskDefinition.Xml("A&B <c>", @"PC\u&", "cmd", "a \"b\" & <c>", @"C:\x&y");

        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("a \"b\" & <c>", root.Descendants(Ns + "Arguments").Single().Value);
        Assert.Equal(@"C:\x&y", root.Descendants(Ns + "WorkingDirectory").Single().Value);
        Assert.Equal(@"\A&B <c>", root.Descendants(Ns + "URI").Single().Value);
    }

    [Fact]
    public void Task_runs_for_the_current_user_without_elevation_and_without_a_window()
    {
        var root = XDocument.Parse(Service().Xml()).Root!;

        Assert.Equal(@"PC\user", root.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value);
        Assert.Equal("LeastPrivilege", root.Descendants(Ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", root.Descendants(Ns + "LogonType").Single().Value);
        Assert.Equal("IgnoreNew", root.Descendants(Ns + "MultipleInstancesPolicy").Single().Value);
        Assert.Equal("false", root.Descendants(Ns + "StopIfGoingOnBatteries").Single().Value);
        Assert.Equal("PT0S", root.Descendants(Ns + "ExecutionTimeLimit").Single().Value);

        var exec = root.Descendants(Ns + "Exec").Single();
        Assert.EndsWith("conhost.exe", exec.Element(Ns + "Command")!.Value);
        var arguments = exec.Element(Ns + "Arguments")!.Value;
        Assert.StartsWith("--headless ", arguments);
        Assert.Contains(" --detached", arguments);
        Assert.Contains("tasker-mcpd", arguments);
    }

    // ---- командная строка ----

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData(@"C:\Program Files\x.exe", "\"C:\\Program Files\\x.exe\"")]
    [InlineData(@"C:\Артём\x y\", "\"C:\\Артём\\x y\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    public void Arguments_are_quoted_by_windows_rules(string value, string expected) =>
        Assert.Equal(expected, WindowsTaskDefinition.QuoteArgument(value));

    [Fact]
    public void Action_runs_the_daemon_headless()
    {
        var (command, arguments) = WindowsTaskDefinition.Action([@"C:\Tasker App\tasker-mcpd.exe", "--detached"], new Dictionary<string, string>());

        Assert.Equal(@"%SystemRoot%\System32\conhost.exe", command);
        Assert.Equal("--headless \"C:\\Tasker App\\tasker-mcpd.exe\" --detached", arguments);
    }

    [Fact]
    public void Action_passes_environment_through_cmd()
    {
        var (_, arguments) = WindowsTaskDefinition.Action(
            [@"C:\Tasker App\tasker-mcpd.exe", "--detached"], new Dictionary<string, string> { ["TASKER_HOME"] = @"C:\Данные & тест" });

        Assert.Equal("--headless cmd.exe /d /s /c \"set \"TASKER_HOME=C:\\Данные & тест\" && \"C:\\Tasker App\\tasker-mcpd.exe\" --detached\"", arguments);
    }

    [Theory]
    [InlineData("C:\\%TEMP%")]
    [InlineData("C:\\a\"b")]
    public void Action_refuses_environment_that_cmd_would_corrupt(string value)
    {
        var error = Assert.Throws<ServiceException>(() =>
            WindowsTaskDefinition.Action(["x.exe"], new Dictionary<string, string> { ["TASKER_HOME"] = value }));

        Assert.Contains("TASKER_HOME", error.Message);
    }

    [Fact]
    public async Task Task_carries_the_data_directory_when_it_is_overridden()
    {
        var service = Service();
        await service.Enable();

        var arguments = XDocument.Parse(await File.ReadAllTextAsync(service.DefinitionPath)).Root!.Descendants(Ns + "Arguments").Single().Value;
        Assert.Contains($"set \"TASKER_HOME={_home.Path}\"", arguments);
    }

    // ---- разбор состояния ----

    [Theory]
    [InlineData("Running\r\n", true)]
    [InlineData("running", true)]
    [InlineData("Ready\r\n", false)]
    [InlineData("Disabled", false)]
    [InlineData("", false)]
    public void State_is_read_from_powershell_output(string output, bool running) =>
        Assert.Equal(running, WindowsTaskDefinition.IsRunning(output));

    [Fact]
    public void State_script_escapes_quotes_in_the_task_name() =>
        Assert.Equal("(Get-ScheduledTask -TaskName 'It''s' -ErrorAction Stop).State", WindowsTaskDefinition.StateScript("It's"));

    [Theory]
    [InlineData("Tasker MCP", "Tasker MCP.xml")]
    [InlineData(@"a\b/c", "a_b_c.xml")]
    public void Definition_file_name_is_safe(string name, string expected) =>
        Assert.Equal(expected, WindowsTaskDefinition.FileName(name));

    // ---- команды schtasks ----

    [Fact]
    public async Task Enable_writes_utf16_definition_creates_and_runs_the_task()
    {
        var service = Service();

        await service.Enable();

        Assert.True(service.IsEnabled);
        var bytes = await File.ReadAllBytesAsync(service.DefinitionPath);
        Assert.Equal([0xFF, 0xFE], bytes[..2]); // UTF-16 LE с меткой порядка байтов
        Assert.Contains("encoding=\"UTF-16\"", Encoding.Unicode.GetString(bytes));
        Assert.Equal(
            [
                $"schtasks /create /tn Tasker MCP Test /xml {service.DefinitionPath} /f",
                "schtasks /end /tn Tasker MCP Test",
                "schtasks /run /tn Tasker MCP Test",
            ],
            _runner.Calls);
    }

    [Fact]
    public async Task Enable_reports_a_failing_schtasks()
    {
        _runner.Results["schtasks /create"] = new ProcessResult(1, "", "ERROR: Access is denied.");

        var error = await Assert.ThrowsAsync<ServiceException>(() => Service().Enable());

        Assert.Contains("schtasks /create failed (1)", error.Message);
        Assert.Contains("Access is denied", error.Message);
    }

    [Fact]
    public async Task Disable_ends_deletes_the_task_and_removes_the_definition()
    {
        var service = Service();
        await service.Enable();
        _runner.Calls.Clear();

        await service.Disable();

        Assert.False(service.IsEnabled);
        Assert.Equal(["schtasks /end /tn Tasker MCP Test", "schtasks /delete /tn Tasker MCP Test /f"], _runner.Calls);
    }

    [Fact]
    public async Task Disable_is_harmless_when_nothing_was_enabled()
    {
        _runner.Results["schtasks"] = new ProcessResult(1, "", "ERROR: The system cannot find the file specified.");

        await Service().Disable();

        Assert.False(Service().IsEnabled);
    }

    [Fact]
    public async Task Start_runs_the_task_and_stop_ends_it_keeping_autostart()
    {
        var service = Service();
        await service.Enable();
        _runner.Calls.Clear();

        await service.Start();
        await service.Stop();

        Assert.True(service.IsEnabled);
        Assert.Equal(["schtasks /run /tn Tasker MCP Test", "schtasks /end /tn Tasker MCP Test"], _runner.Calls);
    }

    [Fact]
    public async Task Stop_of_a_stopped_task_is_not_an_error()
    {
        _runner.Results["schtasks /end"] = new ProcessResult(1, "", "ERROR: There is no running instance of the task.");
        _runner.Results["powershell"] = new ProcessResult(0, "Ready\r\n", "");

        await Service().Stop();
    }

    [Fact]
    public async Task Stop_reports_a_failure_while_the_task_is_running()
    {
        _runner.Results["schtasks /end"] = new ProcessResult(1, "", "ERROR: Access is denied.");
        _runner.Results["powershell"] = new ProcessResult(0, "Running\r\n", "");

        var error = await Assert.ThrowsAsync<ServiceException>(() => Service().Stop());

        Assert.Contains("schtasks /end failed", error.Message);
    }

    [Fact]
    public async Task Start_without_autostart_is_an_error()
    {
        var error = await Assert.ThrowsAsync<ServiceException>(() => Service().Start());

        Assert.Contains("mcp autostart enable", error.Message);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Is_loaded_follows_the_task_state()
    {
        var service = Service();

        _runner.Results["powershell"] = new ProcessResult(0, "Running\r\n", "");
        Assert.True(await service.IsLoaded());

        _runner.Results["powershell"] = new ProcessResult(0, "Ready\r\n", "");
        Assert.False(await service.IsLoaded());

        _runner.Results["powershell"] = new ProcessResult(1, "", "Get-ScheduledTask : No MSFT_ScheduledTask objects found");
        Assert.False(await service.IsLoaded());
        Assert.Contains("-TaskName 'Tasker MCP Test'", _runner.Calls[0]);
    }

    [Fact]
    public void Default_name_and_directory_follow_the_overrides()
    {
        using var dir = Tasker.Global.AppEnvironment.Override("TASKER_SERVICE_DIR", _directory);
        using var label = Tasker.Global.AppEnvironment.Override("TASKER_SERVICE_LABEL", "Tasker MCP Custom");

        var service = new WindowsTaskService(_runner);

        Assert.Equal("Tasker MCP Custom", service.TaskName);
        Assert.Equal(Path.Combine(_directory, "Tasker MCP Custom.xml"), service.DefinitionPath);
    }

    // ---- настоящий Планировщик (только Windows) ----

    [WindowsFact]
    public async Task Real_task_scheduler_creates_queries_and_deletes_an_isolated_task()
    {
        var name = "Tasker MCP test " + Guid.NewGuid().ToString("N")[..8];
        var system = new SystemProcessRunner();
        var service = new WindowsTaskService(system, _directory, name);

        try
        {
            // Определение без запуска демона: создаём только задачу.
            await File.WriteAllTextAsync(service.DefinitionPath, service.Xml(), new UnicodeEncoding(false, true));
            var create = await system.Run("schtasks", "/create", "/tn", name, "/xml", service.DefinitionPath, "/f");
            Assert.True(create.Success, create.Error + create.Output);

            var query = await system.Run("schtasks", "/query", "/tn", name, "/xml");
            Assert.True(query.Success, query.Error);
            Assert.Contains("LogonTrigger", query.Output);
            Assert.False(await service.IsLoaded());
        }
        finally
        {
            await system.Run("schtasks", "/delete", "/tn", name, "/f");
        }
    }
}
