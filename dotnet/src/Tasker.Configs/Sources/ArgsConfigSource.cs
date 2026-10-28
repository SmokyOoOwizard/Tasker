namespace Tasker.Configs.Sources;

/// <summary>
/// Аргументы командной строки: <c>--key=value</c> или <c>--key value</c>, с любым числом дефисов,
/// без учёта регистра. Ключ — алиас (<c>--sqlite</c>) или полный ключ (<c>--TASKER_DB_CONFIGS_SQLITE_FILE</c>).
/// Одно имя может задавать настройки в нескольких группах. Пустые значения пропускаются.
/// </summary>
public class ArgsConfigSource(string[] args) : IConfigSource
{
    public void Apply(IReadOnlyList<AConfigs> configs)
    {
        var values = ParseArgs(args);
        if (values.Count == 0)
            return;

        foreach (var config in configs)
        {
            var configType = config.GetType();
            foreach (var property in ConfigsParser.Settable(configType))
            {
                var fullKey = ConfigKeys.For(configType, property);
                var names = ConfigKeys.Aliases(property).Append(fullKey);

                foreach (var name in names)
                {
                    if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                        continue;

                    ConfigsParser.Set(config, property, name, value);
                    break;
                }
            }
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (!raw.StartsWith('-'))
                continue;

            var token = raw.TrimStart('-');
            string key, value;

            var eq = token.IndexOf('=');
            if (eq >= 0)
            {
                key = token[..eq];
                value = token[(eq + 1)..];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                key = token;
                value = args[++i];
            }
            else
            {
                continue;
            }

            result[key] = value;
        }
        return result;
    }
}
