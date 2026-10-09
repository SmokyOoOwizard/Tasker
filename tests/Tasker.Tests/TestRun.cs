using System.Runtime.CompilerServices;

namespace Tasker.Tests;

/// <summary>
/// Настройки прогона тестов из переменных окружения: <c>TASKER_BIN</c> (исполняемый файл консоли, которую проверяем) и
/// <c>TASKER_MCPD_BIN</c> (программа демона). Они читаются один раз при загрузке сборки и убираются из окружения процесса тестов,
/// чтобы не достаться дочерним процессам: сам <c>tasker</c> предупреждает о неизвестных переменных <c>TASKER_*</c>, а запускать его
/// могут не только тесты напрямую, но и git-хуки, стенд и скрипты.
/// </summary>
internal static class TestRun
{
    /// <summary>Путь к консоли <c>tasker</c>; null — тесты гоняют свою сборку (<c>dotnet tasker.dll</c>).</summary>
    public static string? TaskerBin { get; private set; }

    /// <summary>Путь к демону <c>tasker-mcpd</c>; null — он лежит рядом с консолью.</summary>
    public static string? TaskerMcpdBin { get; private set; }

    [ModuleInitializer]
    internal static void Capture()
    {
        TaskerBin = Take("TASKER_BIN");
        TaskerMcpdBin = Take("TASKER_MCPD_BIN");
    }

    private static string? Take(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, null);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
