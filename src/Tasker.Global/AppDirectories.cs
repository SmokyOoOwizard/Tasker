namespace Tasker.Global;

/// <summary>
/// Каталоги данных самого Tasker (не рабочих папок): одни и те же для десктопа, командной строки и демона MCP —
/// поэтому настройки и состояние демона у них общие. <c>TASKER_HOME</c> переопределяет каталог (тесты, портативная установка).
/// </summary>
public static class AppDirectories
{
    public const string HomeVariable = "TASKER_HOME";

    /// <summary>~/Library/Application Support/Tasker (macOS), %LOCALAPPDATA%\Tasker (Windows), ~/.local/share/Tasker (Linux).</summary>
    public static string Data =>
        AppEnvironment.Get(HomeVariable) is { Length: > 0 } home
            ? Path.GetFullPath(UserPath.Expand(home)!)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Tasker");

    public static string Logs => Path.Combine(Data, "logs");

    /// <summary>Состояние демона MCP: блокировка единственного экземпляра и файл с адресом.</summary>
    public static string Daemon => Path.Combine(Data, "mcp");
}
