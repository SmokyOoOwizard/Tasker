using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tasker.Core;

namespace Tasker.Cli;

internal static class CliJson
{
    public static string Serialize(object? value) => ToNode(value)?.ToJsonString(TaskerJson.Options) ?? "null";

    public static JsonNode? ToNode(object? value) => ToNode(value, typeof(object));

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Types of the output are registered in CliJsonContext")]
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Anonymous types of Tasker.Cli are kept by the trimmer root")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Anonymous types of Tasker.Cli are kept by the trimmer root")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Types of the output are registered in CliJsonContext")]
    private static JsonNode? ToNode(object? value, Type declared)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonNode node:
                return node.Parent == null ? node : node.DeepClone();
            case string text:
                return JsonValue.Create(text);
            case bool flag:
                return JsonValue.Create(flag);
            case int number:
                return JsonValue.Create(number);
            case long number:
                return JsonValue.Create(number);
            case double number:
                return JsonValue.Create(number);
            case Guid id:
                return JsonValue.Create(id);
            case DateTimeOffset time:
                return JsonValue.Create(time);
            case Enum choice:
                return JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(choice.ToString()));
        }

        var type = declared == typeof(object) || IsAnonymous(value.GetType()) ? value.GetType() : declared;
        if (value is ICascadeResult cascade)
            return Cascade(cascade);
        if (IsAnonymous(type))
        {
            var result = new JsonObject();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = TaskerJson.Options.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name;
                result[name] = ToNode(property.GetValue(value), property.PropertyType);
            }
            return result;
        }

        if (value is IDictionary dictionary && !HasContract(type))
        {
            var valueType = DictionaryValueType(type);
            var result = new JsonObject();
            foreach (DictionaryEntry entry in dictionary)
                result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? ""] = ToNode(entry.Value, valueType);
            return result;
        }

        if (value is IEnumerable items && !HasContract(type))
        {
            var elementType = ElementType(type);
            var result = new JsonArray();
            foreach (var item in items)
                result.Add(ToNode(item, elementType));
            return result;
        }

        return JsonSerializer.SerializeToNode(value, type, TaskerJson.Options);
    }

    private static JsonNode? Cascade(ICascadeResult value)
    {
        var result = ToNode(value.Value, value.ValueType) as JsonObject ?? new JsonObject();
        var naming = TaskerJson.Options.PropertyNamingPolicy;
        result[naming?.ConvertName("AffectedTasks") ?? "AffectedTasks"] = value.AffectedTasks;
        return result;
    }

    private static bool IsAnonymous(Type type) =>
        type.IsDefined(typeof(CompilerGeneratedAttribute), false) && type.Name.Contains("AnonymousType", StringComparison.Ordinal);

    private static bool HasContract(Type type) => !type.IsArray && type.Namespace?.StartsWith("Tasker", StringComparison.Ordinal) == true && !IsAnonymous(type);

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Interfaces of collections are kept")]
    private static Type ElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType() ?? typeof(object);
        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0] ?? typeof(object);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Interfaces of collections are kept")]
    private static Type DictionaryValueType(Type type)
    {
        var dictionary = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>)
            ? type
            : type.GetInterfaces().FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        return dictionary?.GetGenericArguments()[1] ?? typeof(object);
    }
}
