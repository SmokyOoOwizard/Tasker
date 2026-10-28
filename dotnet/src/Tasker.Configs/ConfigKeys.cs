using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Tasker.Configs;

/// <summary>
/// Имена ключей настроек. Ключ = префикс класса + имя свойства в SCREAMING_SNAKE_CASE:
/// <c>Tasker.Storage.Db.Configs.Tasker.DbConfigs.SqliteFile</c> → <c>TASKER_DB_CONFIGS_SQLITE_FILE</c>.
/// </summary>
public static partial class ConfigKeys
{
    private const string Root = "Tasker.";

    public static string For(Type configType, PropertyInfo property)
    {
        var propertyName = property.GetCustomAttribute<CustomEnvNameAttribute>()?.Name
                           ?? ToEnvSegment(property.Name);

        return $"{EnvPrefix(configType)}_{propertyName}";
    }

    public static string[] Aliases(PropertyInfo property) =>
        property.GetCustomAttributes<ConfigAliasAttribute>().Select(x => x.Name).ToArray();

    /// <summary>Часть полного имени после <c>...Configs.Tasker.</c>, с префиксом TASKER: <c>TASKER_DB_CONFIGS</c>.</summary>
    public static string EnvPrefix(Type configType)
    {
        var workName = configType.FullName![Root.Length..];
        var relativeName = workName[workName.IndexOf(Root, StringComparison.Ordinal)..];

        relativeName = string.Join('.', relativeName.Split('.').Select(ToEnvSegment));

        var baseName = relativeName.ToUpperInvariant().Replace('.', '_');
        return MultipleUnderscores().Replace(baseName, "_");
    }

    /// <summary>Строка → тип свойства. Поддержаны string, int, double, bool, TimeSpan, enum и их nullable-варианты.</summary>
    public static object Parse(string value, Type propertyType)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (type == typeof(string)) return value;
        if (type == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
        if (type == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
        if (type == typeof(bool)) return bool.Parse(value);
        if (type == typeof(TimeSpan)) return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
        if (type.IsEnum) return Enum.Parse(type, value, ignoreCase: true);

        throw new NotSupportedException($"Unsupported config type: {propertyType}");
    }

    /// <summary><c>ConnectionString</c> → <c>CONNECTION_STRING</c>, <c>DbConfigs</c> → <c>DB_CONFIGS</c>.</summary>
    public static string ToEnvSegment(string name)
    {
        var parts = WordBoundary().Split(name);
        if (string.IsNullOrWhiteSpace(parts[0]))
            parts = parts[1..];

        return string.Join('_', parts.Select(x => x.ToUpperInvariant()));
    }

    [GeneratedRegex("_+")]
    private static partial Regex MultipleUnderscores();

    [GeneratedRegex(@"(?=\p{Lu}\p{Ll})|(?<=\p{Ll})(?=\p{Lu})")]
    private static partial Regex WordBoundary();
}
