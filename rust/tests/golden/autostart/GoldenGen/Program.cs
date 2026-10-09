using Tasker.Daemon.Services;
using Tasker.Global;

// Снимает файлы автозапуска .NET-генераторов (launchd/systemd/Task Scheduler) с поддельным исполнителем команд:
// файлы пишутся в <out>/<case>/, вызовы команд — в <out>/<case>/calls.txt. Аргументы: <out> <tasker_home_plain> <tasker_home_space>.
var outDir = args[0];
var homePlain = args[1];
var homeSpace = args[2];

// Launcher.DaemonCommand ищет tasker-mcpd.dll рядом с главной сборкой.
var binDir = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
File.WriteAllText(Path.Combine(binDir, "tasker-mcpd.dll"), "");

async Task Case(string name, string? home, Func<IProcessRunner, string, IServiceManager> make)
{
    var dir = Path.Combine(outDir, name);
    Directory.CreateDirectory(dir);
    var runner = new FakeRunner();
    using var _ = AppEnvironment.Override("TASKER_HOME", home);
    var service = make(runner, dir);
    await service.Enable();
    await File.WriteAllLinesAsync(Path.Combine(dir, "calls.txt"), runner.Calls);
}

await Case("launchd", homePlain, (r, d) => new LaunchdService(r, d, "com.tasker.golden-autostart"));
await Case("launchd-nohome", null, (r, d) => new LaunchdService(r, d, "com.tasker.golden-autostart"));
await Case("launchd-special", Path.Combine(homeSpace, "a&b <c>"), (r, d) => new LaunchdService(r, d, "com.tasker.golden-autostart"));
await Case("systemd", homePlain, (r, d) => new SystemdService(r, d, "tasker-golden.service"));
await Case("systemd-nohome", null, (r, d) => new SystemdService(r, d, "tasker-golden.service"));
await Case("systemd-space", homeSpace, (r, d) => new SystemdService(r, d, "tasker-golden.service"));
await Case("windows", homePlain, (r, d) => new WindowsTaskService(r, d, "Tasker MCP Golden", @"PC\Артём"));
await Case("windows-nohome", null, (r, d) => new WindowsTaskService(r, d, "Tasker MCP Golden", @"PC\Артём"));
Console.WriteLine("dotnet=" + Environment.ProcessPath);
Console.WriteLine("bin=" + binDir);

sealed class FakeRunner : IProcessRunner
{
    public List<string> Calls { get; } = [];
    public Task<ProcessResult> Run(string file, params string[] arguments)
    {
        Calls.Add(string.Join('\t', [file, .. arguments]));
        return Task.FromResult(new ProcessResult(0, "", ""));
    }
}
