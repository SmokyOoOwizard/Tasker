using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// DateTimeOffset одной строкой ISO 8601 (<c>2026-09-25T10:00:00.0000000+00:00</c>).
/// Без него YamlDotNet пишет все свойства структуры (ticks, dayOfWeek и т.д.) вложенным объектом.
/// </summary>
internal class DateTimeOffsetYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(DateTimeOffset);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        return DateTimeOffset.Parse(scalar.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var text = ((DateTimeOffset)value!).ToString("O", CultureInfo.InvariantCulture);
        emitter.Emit(new Scalar(text));
    }
}
