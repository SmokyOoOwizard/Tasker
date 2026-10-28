using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Tasker.Core;

/// <summary>
/// Единственные настройки JSON во всём приложении: REST, SSE, MCP, <c>--json</c> консоли, обмен CLI и демона,
/// файлы настроек. camelCase, enum строками (<c>"kind": "agent"</c>), и главное — нестрогое экранирование:
/// кириллица, обратные кавычки, «ёлочки», эмодзи остаются как есть, а не <c>\uXXXX</c> (ответ короче в разы
/// и читается человеком и агентом). <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> тоже не экранируются: JSON здесь
/// всегда данные для разбора, а не фрагмент HTML; кавычки, обратная косая и управляющие символы по-прежнему
/// экранируются, JSON остаётся валидным. Своё кодирование нужно из-за эмодзи: даже <c>UnsafeRelaxedJsonEscaping</c>
/// экранирует всё вне BMP (суррогатные пары), а мы оставляем и их.
/// </summary>
public static class TaskerJson
{
    /// <summary>Общий экземпляр, неизменяемый после первого использования. Для вариантов (например, с отступами) — <c>new(Options)</c>.</summary>
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>
    /// Применяет настройки к чужому экземпляру опций — там, где его создаёт не мы (<c>ConfigureHttpJsonOptions</c> в ASP.NET Core).
    /// </summary>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Encoder = RelaxedEncoder.Instance;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        // Обязателен для SDK MCP: он делает опции неизменяемыми и требует резолвер.
        options.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();
        return options;
    }

    /// <summary>Экранирует только то, без чего JSON невалиден или двусмыслен: <c>"</c>, <c>\</c>, управляющие символы, U+2028/2029 и одиночные суррогаты.</summary>
    private sealed class RelaxedEncoder : JavaScriptEncoder
    {
        public static readonly RelaxedEncoder Instance = new();

        public override int MaxOutputCharactersPerInputCharacter => 12;

        public override bool WillEncode(int unicodeScalar) =>
            unicodeScalar < 0x20 || unicodeScalar is '"' or '\\' or >= 0x7F and < 0xA0 or 0x2028 or 0x2029 || unicodeScalar is >= 0xD800 and <= 0xDFFF;

        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            for (var i = 0; i < textLength; i++)
            {
                var c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < textLength && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }

                if (WillEncode(c))
                    return i;
            }

            return -1;
        }

        public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
        {
            if (!WillEncode(unicodeScalar))
            {
                var rune = new System.Text.Rune(unicodeScalar);
                if (bufferLength < rune.Utf16SequenceLength)
                {
                    numberOfCharactersWritten = 0;
                    return false;
                }

                numberOfCharactersWritten = rune.EncodeToUtf16(new Span<char>(buffer, bufferLength));
                return true;
            }

            var shortForm = unicodeScalar switch
            {
                '"' => '"', '\\' => '\\', '\n' => 'n', '\r' => 'r', '\t' => 't', '\b' => 'b', '\f' => 'f', _ => '\0'
            };
            if (shortForm != '\0')
            {
                if (bufferLength < 2)
                {
                    numberOfCharactersWritten = 0;
                    return false;
                }

                buffer[0] = '\\';
                buffer[1] = shortForm;
                numberOfCharactersWritten = 2;
                return true;
            }

            if (bufferLength < 6)
            {
                numberOfCharactersWritten = 0;
                return false;
            }

            var hex = unicodeScalar.ToString("X4");
            buffer[0] = '\\';
            buffer[1] = 'u';
            for (var i = 0; i < 4; i++)
                buffer[2 + i] = hex[i];
            numberOfCharactersWritten = 6;
            return true;
        }
    }
}
