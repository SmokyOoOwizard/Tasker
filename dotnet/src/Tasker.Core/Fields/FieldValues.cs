using System.Globalization;

namespace Tasker.Core.Fields;

/// <summary>
/// Значения полей: проверка по типу и приведение к каноническому тексту, в котором они хранятся (и в файлах, и в БД).
/// Канонический вид один у всех клиентов: «007» и «7» — одно значение, «TRUE» и «true» — одно.
/// </summary>
internal static class FieldValues
{
    public const int MaxStringLength = 2000;
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// Значения поля из текста клиента: каждое проверено по типу и приведено к каноническому виду.
    /// Одно значение — у поля без «нескольких значений»; повторов нет; пустое значение недопустимо (убрать значения — пустым списком).
    /// </summary>
    /// <param name="enumeration">Перечисление поля — для типа enum.</param>
    /// <exception cref="TaskerValidationException">Значение не подходит типу, лишние значения, повтор.</exception>
    public static string[] Normalize(string name, FieldType type, bool multiple, FieldEnum? enumeration, IEnumerable<string?>? raw)
    {
        var result = new List<string>();
        foreach (var value in raw ?? [])
        {
            var canonical = One(name, type, enumeration, value);
            if (result.Contains(canonical, StringComparer.Ordinal))
                throw new TaskerValidationException($"Field '{name}': value '{value}' is repeated");
            result.Add(canonical);
        }

        if (result.Count > 1 && !multiple)
            throw new TaskerValidationException($"Field '{name}' holds a single value, but {result.Count} were given");
        return result.ToArray();
    }

    /// <summary>Значение, записанное текстом, в каноническом виде своего типа (не enum); null — не разбирается.</summary>
    public static string? TryParse(FieldType type, string text)
    {
        try
        {
            return One("", type, null, text);
        }
        catch (TaskerValidationException)
        {
            return null;
        }
    }

    private static string One(string name, FieldType type, FieldEnum? enumeration, string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
            throw new TaskerValidationException($"Field '{name}': a value must not be empty (pass no values to clear the field)");

        switch (type)
        {
            case FieldType.String:
                if (text.Length > MaxStringLength)
                    throw new TaskerValidationException($"Field '{name}': a value must be at most {MaxStringLength} characters");
                return text;

            case FieldType.Int:
                return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
                    ? integer.ToString(CultureInfo.InvariantCulture)
                    : throw Invalid(name, text, "an integer");

            case FieldType.Float:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                    ? number.ToString("R", CultureInfo.InvariantCulture)
                    : throw Invalid(name, text, "a number (use '.' as the decimal separator)");

            case FieldType.Bool:
                return text.ToLowerInvariant() switch
                {
                    "true" => "true",
                    "false" => "false",
                    _ => throw Invalid(name, text, "true or false")
                };

            case FieldType.Date:
                return DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? date.ToString(DateFormat, CultureInfo.InvariantCulture)
                    : throw Invalid(name, text, $"a date in the format {DateFormat}");

            case FieldType.Enum:
                return EnumValue(name, enumeration, text).ToString("D");

            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }

    /// <summary>Значение перечисления по id или по названию (без учёта регистра).</summary>
    private static Guid EnumValue(string name, FieldEnum? enumeration, string text)
    {
        if (enumeration == null)
            throw new TaskerValidationException($"Field '{name}': its enum was not found");

        if (Guid.TryParse(text, out var id) && enumeration.Values.Any(x => x.Id == id))
            return id;

        var byName = enumeration.Values.Where(x => string.Equals(x.Name, text, StringComparison.OrdinalIgnoreCase)).ToArray();
        return byName.Length == 1
            ? byName[0].Id
            : throw new TaskerValidationException(
                $"Field '{name}': '{text}' is not a value of enum '{enumeration.Name}' (values: {string.Join(", ", enumeration.Values.Select(x => x.Name))})");
    }

    /// <summary>Значения для показа: у enum — названия значений (неизвестное значение — как есть), у остальных — сами значения.</summary>
    public static string[] Texts(FieldType type, FieldEnum? enumeration, IEnumerable<string> values) =>
        type == FieldType.Enum
            ? values.Select(x => Guid.TryParse(x, out var id) && enumeration?.Values.FirstOrDefault(v => v.Id == id) is { } found ? found.Name : x).ToArray()
            : values.ToArray();

    private static TaskerValidationException Invalid(string name, string text, string expected) =>
        new($"Field '{name}': '{text}' is not {expected}");
}
