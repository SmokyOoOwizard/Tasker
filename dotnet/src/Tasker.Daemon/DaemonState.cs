namespace Tasker.Daemon;

/// <summary>Состояние демона MCP.</summary>
public enum DaemonState
{
    /// <summary>Не работает: его остановили (<c>tasker mcp stop</c>) или ещё не запускали.</summary>
    Stopped,

    Starting,
    Running,

    Failed
}
