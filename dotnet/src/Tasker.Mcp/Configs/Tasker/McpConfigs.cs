using Tasker.Configs;

namespace Tasker.Mcp.Configs.Tasker;

/// <summary>MCP-сервер.</summary>
public class McpConfigs : AConfigs
{
    /// <summary>
    /// Десктоп: порт MCP на 127.0.0.1, если нужен не тот, что в глобальных настройках (<c>tasker mcp port</c>, по умолчанию 5719).
    /// <c>TASKER_MCP_CONFIGS_PORT</c> или <c>--mcpport=5719</c>. Не задан — порт из глобальных настроек.
    /// На сервере MCP доступен на его обычном адресе, по пути <c>/mcp</c>.
    /// </summary>
    [ConfigAlias("mcpport")]
    public int? Port { get; set; }
}
