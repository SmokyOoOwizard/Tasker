using Tasker.Global;

namespace Tasker.Daemon;

/// <summary>Что делает десктоп с MCP при запуске: поднимает свой на порту или уступает демону.</summary>
/// <param name="Port">Порт собственного MCP десктопа; null — не поднимать.</param>
/// <param name="Message">Что писать в журнал.</param>
public sealed record DesktopMcpPlan(int? Port, string Message, bool Error = false)
{
    /// <summary>
    /// Демон работает — MCP всех разрешённых областей уже у него, десктоп его порт не занимает и своего не поднимает.
    /// Иначе — свой MCP на порту из <c>--mcpport</c> или из глобальных настроек; порт занят — приложение работает без MCP.
    /// </summary>
    public static DesktopMcpPlan Decide(int? explicitPort, McpSettings settings, DaemonStatus? daemon, Func<int, bool> isPortFree)
    {
        if (daemon != null)
        {
            return new DesktopMcpPlan(null,
                $"MCP is served by the MCP daemon on port {daemon.Port} (pid {daemon.Pid}): the desktop does not start its own");
        }

        var port = explicitPort ?? settings.Port;
        return isPortFree(port)
            ? new DesktopMcpPlan(port, $"MCP: own server on port {port}")
            : new DesktopMcpPlan(null, $"MCP port {port} is busy: MCP is unavailable (change the port with 'tasker mcp port' or --mcpport)", Error: true);
    }
}
