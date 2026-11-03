using System.Text;
using Tasker.Global;

namespace Tasker.Daemon.Services;

/// <summary>
/// Windows: задача Планировщика заданий для текущего пользователя (не служба Windows, права администратора не нужны).
/// Триггер — вход пользователя, перезапуск при падении, одна копия, без окна (<see cref="WindowsTaskDefinition"/>). Управление —
/// <c>schtasks.exe</c> с XML-определением (UTF-16). Определение лежит в <c>%LOCALAPPDATA%\Tasker\service\&lt;имя&gt;.xml</c>: оно же
/// признак «автозапуск включён». <c>TASKER_SERVICE_DIR</c> и <c>TASKER_SERVICE_LABEL</c> переопределяют каталог и имя задачи.
/// Задача держит <c>tasker-mcpd</c> (супервизор); <c>schtasks /end</c> останавливает его, и Планировщик его не поднимает снова:
/// завершение по запросу — не падение.
/// </summary>
public sealed class WindowsTaskService(IProcessRunner runner, string? directory = null, string? taskName = null, string? userId = null) : IServiceManager
{
    private readonly string _directory = directory
        ?? AppEnvironment.Get("TASKER_SERVICE_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Tasker", "service");

    private readonly string _userId = userId ?? $"{Environment.UserDomainName}\\{Environment.UserName}";

    public string TaskName { get; } = taskName ?? AppEnvironment.Get("TASKER_SERVICE_LABEL") ?? "Tasker MCP";

    public string Name => "Task Scheduler";

    public string DefinitionPath => Path.Combine(_directory, WindowsTaskDefinition.FileName(TaskName));

    public bool IsEnabled => File.Exists(DefinitionPath);

    public async Task Enable()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(AppDirectories.Data);
        // schtasks /create /xml читает UTF-16 (с меткой порядка байтов): так работают кириллица и пробелы в путях.
        await File.WriteAllTextAsync(DefinitionPath, Xml(), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        Check(await runner.Run("schtasks", "/create", "/tn", TaskName, "/xml", DefinitionPath, "/f"), "schtasks /create");
        // Триггер срабатывает только при следующем входе: запускаем сейчас (старую копию, если была, останавливаем).
        await runner.Run("schtasks", "/end", "/tn", TaskName);
        Check(await runner.Run("schtasks", "/run", "/tn", TaskName), "schtasks /run");
    }

    public async Task Disable()
    {
        await runner.Run("schtasks", "/end", "/tn", TaskName);
        await runner.Run("schtasks", "/delete", "/tn", TaskName, "/f"); // нет задачи — не ошибка
        if (File.Exists(DefinitionPath))
            File.Delete(DefinitionPath);
    }

    public async Task Start()
    {
        if (!IsEnabled)
            throw new ServiceException("Autostart is not enabled: run 'tasker mcp autostart enable'");

        Check(await runner.Run("schtasks", "/run", "/tn", TaskName), "schtasks /run");
    }

    public async Task Stop()
    {
        var result = await runner.Run("schtasks", "/end", "/tn", TaskName);
        // «Задача не выполняется» — уже остановлена.
        if (!result.Success && await IsLoaded())
            Check(result, "schtasks /end");
    }

    public async Task<bool> IsLoaded()
    {
        var result = await runner.Run("powershell", "-NoProfile", "-NonInteractive", "-Command", WindowsTaskDefinition.StateScript(TaskName));
        return result.Success && WindowsTaskDefinition.IsRunning(result.Output);
    }

    internal string Xml()
    {
        var (command, arguments) = WindowsTaskDefinition.Action(ServiceManagers.DaemonCommand(), ServiceManagers.Environment());
        // Рабочий каталог — каталог данных Tasker (создаётся при включении); пути демона абсолютные.
        return WindowsTaskDefinition.Xml(TaskName, _userId, command, arguments, AppDirectories.Data);
    }

    private static void Check(ProcessResult result, string what)
    {
        if (!result.Success)
            throw new ServiceException($"{what} failed ({result.ExitCode}): {(result.Error + result.Output).Trim()}");
    }
}
