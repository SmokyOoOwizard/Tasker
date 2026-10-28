using System.Reflection;
using Tasker.Configs.Sources;

namespace Tasker.Configs;

/// <summary>
/// Находит все группы настроек (<see cref="AConfigs"/>) и заполняет их: сначала переменные окружения,
/// затем аргументы командной строки (они важнее).
/// </summary>
public static class ConfigsParser
{
    private const string Namespace = ".Configs.Tasker";

    public static AConfigs[] GetConfigs(string[] args, IEnumerable<IConfigSource>? sources = null)
    {
        var configs = FindConfigTypes()
            .Select(x => (AConfigs)Activator.CreateInstance(x)!)
            .ToArray();

        foreach (var source in sources ?? [new EnvConfigSource(), new ArgsConfigSource(args)])
            source.Apply(configs);

        return configs;
    }

    public static T Get<T>(this IEnumerable<AConfigs> configs) where T : AConfigs =>
        configs.OfType<T>().SingleOrDefault()
        ?? throw new InvalidOperationException($"Config {typeof(T).Name} not found: is its assembly referenced by the host?");

    internal static IEnumerable<PropertyInfo> Settable(Type configType) =>
        configType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.CanWrite);

    internal static void Set(AConfigs config, PropertyInfo property, string key, string value)
    {
        try
        {
            property.SetValue(config, ConfigKeys.Parse(value, property.PropertyType));
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException($"Config {key}: cannot parse '{value}' as {property.PropertyType.Name}", e);
        }
    }

    // Сборки Tasker.* подгружаются явно по ссылкам от точки входа:
    // иначе группа настроек из ещё не загруженной сборки молча не нашлась бы.
    private static IEnumerable<Type> FindConfigTypes()
    {
        var baseType = typeof(AConfigs);
        return LoadTaskerAssemblies()
            .SelectMany(x => x.GetTypes())
            .Where(x => baseType.IsAssignableFrom(x) && x is { IsAbstract: false, IsInterface: false })
            .Where(x => x.Namespace?.EndsWith(Namespace, StringComparison.Ordinal) == true);
    }

    private static IEnumerable<Assembly> LoadTaskerAssemblies()
    {
        var seen = new Dictionary<string, Assembly>();
        var queue = new Queue<Assembly>(AppDomain.CurrentDomain.GetAssemblies().Where(IsTasker));
        if (Assembly.GetEntryAssembly() is { } entry)
            queue.Enqueue(entry);

        while (queue.TryDequeue(out var assembly))
        {
            if (!seen.TryAdd(assembly.FullName!, assembly))
                continue;

            foreach (var reference in assembly.GetReferencedAssemblies().Where(x => x.Name?.StartsWith("Tasker", StringComparison.Ordinal) == true))
                queue.Enqueue(Assembly.Load(reference));
        }

        return seen.Values;
    }

    private static bool IsTasker(Assembly assembly) =>
        assembly.GetName().Name?.StartsWith("Tasker", StringComparison.Ordinal) == true;
}
