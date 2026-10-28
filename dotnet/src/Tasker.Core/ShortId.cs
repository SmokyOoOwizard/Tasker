using System.Text;

namespace Tasker.Core;

/// <summary>
/// Короткий id — первые 8 символов Guid (как суффикс в имени файла <c>&lt;название&gt;-&lt;id8&gt;.yaml</c>): им показываются сущности в списках консоли,
/// а команды принимают вместо полного id префикс Guid из 8 и более шестнадцатеричных символов (без дефисов, регистр не важен).
/// Короче 8 символов префикс не принимается: слишком много совпадений.
/// </summary>
public static class ShortId
{
    public const int Length = 8;

    /// <summary>Короткий id: первые 8 символов Guid строчными.</summary>
    public static string Of(Guid id) => id.ToString("D")[..Length];

    /// <summary>
    /// Ключ префикса — начало Guid в виде <c>D</c> (строчные, с дефисами на своих местах: <c>70053344-29</c>), которым сравнивают и ищут в хранилищах.
    /// </summary>
    /// <returns>null — текст не префикс: короче 8 символов, не только шестнадцатеричные цифры или длиннее Guid.</returns>
    public static string? TryKey(string? text)
    {
        var hex = text?.Trim();
        if (hex is null || hex.Length is < Length or > 32 || !hex.All(char.IsAsciiHexDigit))
            return null;

        hex = hex.ToLowerInvariant();
        // Дефисы стоят после 8, 12, 16 и 20 цифр.
        var key = new StringBuilder(hex.Length + 4);
        for (var i = 0; i < hex.Length; i++)
        {
            if (i is 8 or 12 or 16 or 20)
                key.Append('-');
            key.Append(hex[i]);
        }

        return key.ToString();
    }

    public static bool Matches(Guid id, string key) => id.ToString("D").StartsWith(key, StringComparison.Ordinal);
}
