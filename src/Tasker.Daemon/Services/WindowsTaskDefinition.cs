using System.Security;
using System.Text;

namespace Tasker.Daemon.Services;

/// <summary>
/// Чистые функции Windows-автозапуска (без вызова ОС — проверяются на любой системе): XML-определение задачи Планировщика,
/// командная строка запуска без окна, разбор вывода PowerShell. Вызовы <c>schtasks.exe</c> — в <see cref="WindowsTaskService"/>.
/// </summary>
internal static class WindowsTaskDefinition
{
    /// <summary>Перезапуск при падении: каждую минуту (минимум Планировщика), до 999 раз подряд.</summary>
    public const string RestartInterval = "PT1M";

    public const int RestartCount = 999;

    /// <summary>
    /// Запуск без окна: <c>conhost.exe --headless</c> (Windows 10 1809 и новее) создаёт консоль без окна, поэтому окно
    /// консоли не мелькает, а процесс остаётся единственным действием задачи: Планировщик видит его завершение, код выхода
    /// (перезапуск при падении) и умеет остановить (<c>schtasks /end</c>). Без скриптовых обёрток (wscript/VBS отключаемы).
    /// </summary>
    public const string HeadlessHost = @"%SystemRoot%\System32\conhost.exe";

    /// <summary>
    /// Действие задачи. Без переменных окружения — <c>conhost --headless "tasker-mcpd.exe" --detached</c>; с ними (Планировщик
    /// переменных не умеет) — через <c>cmd /d /s /c "set "K=V" &amp;&amp; "tasker-mcpd.exe" --detached"</c>.
    /// </summary>
    /// <exception cref="ServiceException">Значение переменной содержит <c>%</c> или <c>"</c>: cmd подставит или порвёт его.</exception>
    public static (string Command, string Arguments) Action(IReadOnlyList<string> daemon, IReadOnlyDictionary<string, string> environment)
    {
        var program = string.Join(' ', daemon.Select(QuoteArgument));
        if (environment.Count == 0)
            return (HeadlessHost, "--headless " + program);

        var sets = new StringBuilder();
        foreach (var (key, value) in environment)
        {
            if (value.Contains('%') || value.Contains('"') || key.Any(c => c is '%' or '"' or '=' or '&' or ' '))
                throw new ServiceException($"Autostart on Windows cannot pass {key}: the value must not contain % or \"");
            sets.Append($"set \"{key}={value}\" && ");
        }

        return (HeadlessHost, $"--headless cmd.exe /d /s /c \"{sets}{program}\"");
    }

    /// <summary>Аргумент по правилам разбора командной строки Windows (CommandLineToArgvW): пробелы, кавычки и обратные косые.</summary>
    public static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"'))
            return value;

        var text = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            if (c == '"')
                text.Append('\\', slashes * 2 + 1).Append('"');
            else
                text.Append('\\', slashes).Append(c);
            slashes = 0;
        }

        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>
    /// XML задачи Планировщика (схема 1.2) для <c>schtasks /create /xml</c>: вход текущего пользователя, без прав администратора,
    /// одна копия, работает от батареи и без остановки при простое, перезапуск при падении, без ограничения по времени.
    /// </summary>
    /// <param name="userId">Пользователь в виде <c>DOMAIN\name</c>.</param>
    /// <param name="workingDirectory">Рабочий каталог; без кавычек (Планировщик их не разбирает).</param>
    public static string Xml(string name, string userId, string command, string arguments, string workingDirectory)
    {
        static string E(string value) => SecurityElement.Escape(value) ?? "";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{E(name)}: MCP server of Tasker (starts at logon, restarts on failure)</Description>
                <URI>\{E(name)}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{E(userId)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{E(userId)}</UserId>
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
                  <Interval>{RestartInterval}</Interval>
                  <Count>{RestartCount}</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{E(command)}</Command>
                  <Arguments>{E(arguments)}</Arguments>
                  <WorkingDirectory>{E(workingDirectory)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>

            """.ReplaceLineEndings("\r\n");
    }

    /// <summary>Скрипт PowerShell, который печатает состояние задачи (<c>Running</c>, <c>Ready</c>, <c>Disabled</c>) — имена не переводятся, в отличие от вывода schtasks.</summary>
    public static string StateScript(string name) =>
        $"(Get-ScheduledTask -TaskName '{name.Replace("'", "''")}' -ErrorAction Stop).State";

    /// <summary>Задача выполняется: по выводу <see cref="StateScript"/>.</summary>
    public static bool IsRunning(string output) => output.Trim().Equals("Running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Имя файла для определения задачи: без символов, недопустимых в имени файла.</summary>
    public static string FileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '\\' or '/' ? '_' : c)) + ".xml";
}
