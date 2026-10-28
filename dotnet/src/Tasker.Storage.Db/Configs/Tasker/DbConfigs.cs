using Tasker.Configs;

namespace Tasker.Storage.Db.Configs.Tasker;

/// <summary>
/// База данных. Задано одно из двух — используется оно; не задано ничего — SQLite в памяти
/// (данные пропадают при перезапуске: удобно для разработки и тестов).
/// </summary>
public class DbConfigs : AConfigs
{
    /// <summary>
    /// Строка подключения Npgsql. <c>TASKER_DB_CONFIGS_POSTGRES_CONNECTION_STRING</c> или <c>--postgres=...</c>
    /// (<c>--db=...</c> — синоним).
    /// </summary>
    [ConfigAlias("postgres")]
    [ConfigAlias("db")]
    public string? PostgresConnectionString { get; set; }

    /// <summary>Путь к файлу SQLite. <c>TASKER_DB_CONFIGS_SQLITE_FILE</c> или <c>--sqlite=tasker.db</c>.</summary>
    [ConfigAlias("sqlite")]
    public string? SqliteFile { get; set; }

    public bool IsSet => !string.IsNullOrWhiteSpace(PostgresConnectionString) || !string.IsNullOrWhiteSpace(SqliteFile);
}
