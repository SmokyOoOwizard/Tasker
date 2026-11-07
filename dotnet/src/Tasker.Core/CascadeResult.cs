using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tasker.Core;

/// <summary>
/// Итог правки сущности, которая могла затронуть задачи (убрали поле из типа, удалили значение перечисления): сама сущность
/// и число задач, которым выбор вызвавшего что-то изменил. 0 — выбор не понадобился (ни у одной задачи значений не было).
/// В JSON — плоско: поля сущности как раньше плюс <c>affectedTasks</c>, поэтому существующие клиенты ничего не замечают.
/// </summary>
/// <param name="AffectedTasks">
/// Сколько задач затронуто: у выбора «убрать» и «переназначить» — переписанные задачи; у «оставить» поле типа — задачи, у которых
/// значения остались и поле стало дополнительным (сами задачи не переписываются).
/// </param>
[JsonConverter(typeof(CascadeResultConverterFactory))]
public record CascadeResult<T>(T Value, int AffectedTasks) : ICascadeResult
{
    object? ICascadeResult.Value => Value;
    Type ICascadeResult.ValueType => typeof(T);
}

public interface ICascadeResult
{
    object? Value { get; }
    Type ValueType { get; }
    int AffectedTasks { get; }
}

/// <summary>Пишет сущность плоско, с добавленным полем <c>affectedTasks</c> (имя — по политике имён настроек сериализации).</summary>
internal sealed class CascadeResultConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(CascadeResult<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class Converter<T> : JsonConverter<CascadeResult<T>>
    {
        public override CascadeResult<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var count = 0;
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, Name(options, "AffectedTasks"), StringComparison.OrdinalIgnoreCase))
                    count = property.Value.GetInt32();
            }
            return new CascadeResult<T>(root.Deserialize<T>(options)!, count);
        }

        public override void Write(Utf8JsonWriter writer, CascadeResult<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            using (var document = JsonSerializer.SerializeToDocument(value.Value, options))
            {
                foreach (var property in document.RootElement.EnumerateObject())
                    property.WriteTo(writer);
            }
            writer.WriteNumber(Name(options, "AffectedTasks"), value.AffectedTasks);
            writer.WriteEndObject();
        }

        private static string Name(JsonSerializerOptions options, string name) =>
            options.PropertyNamingPolicy?.ConvertName(name) ?? name;
    }
}
