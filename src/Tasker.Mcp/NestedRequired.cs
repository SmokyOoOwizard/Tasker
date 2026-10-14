using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Tasker.Mcp;

/// <summary>
/// <c>required</c> во вложенных объектах <c>inputSchema</c> (<c>columns[]</c>, <c>fields[]</c>, <c>values[]</c>…): схема записи C# перечисляет
/// в нём все параметры конструктора без значения по умолчанию, хотя <c>JsonSerializer</c> собирает запись и без части свойств — пропущенное
/// становится null. Здесь из <c>required</c> вложенного объекта убираются параметры, допускающие null (<c>Guid?</c>, <c>string[]?</c>…);
/// параметры со значением по умолчанию туда и так не попадают. Аргументы самого инструмента (верхний уровень схемы) не трогаются.
/// </summary>
internal static class NestedRequired
{
    public static readonly AIJsonSchemaCreateOptions SchemaOptions = AIJsonSchemaCreateOptions.Default with { TransformSchemaNode = Transform };

    private static JsonNode Transform(AIJsonSchemaCreateContext context, JsonNode node)
    {
        if (context.TypeInfo.Kind != JsonTypeInfoKind.Object || node is not JsonObject schema || schema["required"] is not JsonArray required)
            return node;

        var optional = context.TypeInfo.Properties
            .Where(p => p.AssociatedParameter is { IsNullable: true })
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in required.Where(x => x is not null && optional.Contains(x.GetValue<string>())).ToList())
            required.Remove(item);
        if (required.Count == 0)
            schema.Remove("required");
        return node;
    }
}
