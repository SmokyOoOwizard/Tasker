namespace Tasker.Daemon;

/// <summary>
/// Какой режим демона выбрать на этой системе. На macOS и Linux по умолчанию — супервизор (рабочие процессы делят слушающий сокет,
/// <c>tasker mcp upgrade</c> заменяет процесс без простоя). На Windows тот же режим готов, но проверен только чек-листом со страницы вики «Windows»,
/// поэтому по умолчанию демон остаётся одним процессом (<c>--single</c>); супервизор включают флагом <c>--supervised</c>
/// или переменной <see cref="SupervisorVariable"/>.
/// </summary>
public static class DaemonPlatform
{
    /// <summary>Включает режим супервизора там, где он не по умолчанию (Windows): <c>1</c>, <c>true</c>, <c>yes</c> или <c>on</c>.</summary>
    public const string SupervisorVariable = "TASKER_MCP_SUPERVISOR";

    /// <param name="single">Явно запрошен один процесс (<c>--single</c>): сильнее всего остального.</param>
    /// <param name="supervised">Явно запрошен супервизор (<c>--supervised</c>).</param>
    /// <param name="variable">Значение <see cref="SupervisorVariable"/>.</param>
    public static bool UseSupervisor(bool isWindows, bool single, bool supervised, string? variable)
    {
        if (single)
            return false;
        return !isWindows || supervised || IsOn(variable);
    }

    public static bool IsOn(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
