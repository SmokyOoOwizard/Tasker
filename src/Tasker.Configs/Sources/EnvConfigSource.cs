namespace Tasker.Configs.Sources;

/// <summary>
/// Переменные окружения — только по полному ключу (<c>TASKER_DB_CONFIGS_SQLITE_FILE</c>), без алиасов.
/// Пустые значения пропускаются.
/// </summary>
public class EnvConfigSource(Func<string, string?>? getVariable = null) : IConfigSource
{
    private readonly Func<string, string?> _getVariable = getVariable ?? Environment.GetEnvironmentVariable;

    public void Apply(IReadOnlyList<AConfigs> configs)
    {
        foreach (var config in configs)
        {
            var configType = config.GetType();
            foreach (var property in ConfigsParser.Settable(configType))
            {
                var key = ConfigKeys.For(configType, property);
                var value = _getVariable(key);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                ConfigsParser.Set(config, property, key, value);
            }
        }
    }
}
