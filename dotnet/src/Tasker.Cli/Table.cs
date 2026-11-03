using System.Globalization;
using System.Text;

namespace Tasker.Cli;

/// <summary>
/// Таблица-строки для консоли: колонки выравниваются по самой широкой ячейке (ширина — в знаках терминала, а не в символах UTF-16),
/// между колонками два пробела, последняя колонка не дополняется. Колонка, пустая во всех строках, не занимает места;
/// пустая ячейка в остальных занимает место своей колонки. Если задан предел ширины, строка, не помещающаяся в него, обрезается справа
/// с «…»: так как последняя колонка (заголовок) идёт в конце строки, укорачивается именно она, а остальные остаются целыми и выровненными.
/// </summary>
internal static class Table
{
    private const string Separator = "  ";

    /// <summary>Многоточие, которым заканчивается обрезанный текст.</summary>
    public const string Ellipsis = "…";

    /// <summary>
    /// Строки таблицы; <paramref name="indent"/> добавляется перед каждой строкой. Ширина колонок считается по переданным строкам (по странице).
    /// <paramref name="maxWidth"/> — предел ширины строки в знаках вместе с отступом; null — не обрезать.
    /// </summary>
    public static string[] Format(IReadOnlyList<string[]> rows, string indent = "", int? maxWidth = null)
    {
        var count = rows.Count == 0 ? 0 : rows.Max(x => x.Length);
        var widths = new int[count];
        foreach (var row in rows)
            for (var i = 0; i < row.Length - 1; i++)
                widths[i] = Math.Max(widths[i], Width(row[i]));

        var lines = new string[rows.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var line = new StringBuilder(indent);
            var first = true;
            for (var i = 0; i < row.Length; i++)
            {
                var last = i == row.Length - 1;
                if (!last && widths[i] == 0)
                    continue; // колонка пуста во всех строках

                if (!first)
                    line.Append(Separator);
                first = false;
                line.Append(row[i]);
                if (!last)
                    line.Append(' ', widths[i] - Width(row[i]));
            }

            lines[r] = maxWidth is { } limit ? Truncate(line.ToString(), limit) : line.ToString();
        }

        return lines;
    }

    /// <summary>
    /// Текст не шире <paramref name="limit"/> знаков: если не помещается, обрезается справа и заканчивается «…» (он входит в ширину).
    /// Режет по графемам, поэтому суррогатные пары, эмодзи с модификаторами и буквы с комбинируемыми знаками не разрываются.
    /// </summary>
    public static string Truncate(string text, int limit)
    {
        if (Width(text) <= limit)
            return text;

        var keep = Math.Max(limit - Width(Ellipsis), 0);
        var result = new StringBuilder();
        var width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var next = width + ElementWidth(element);
            if (next > keep)
                break;
            result.Append(element);
            width = next;
        }

        return result + Ellipsis;
    }

    /// <summary>Ширина текста в знаках терминала: кириллица и латиница — 1, CJK и эмодзи — 2, комбинируемые знаки — 0.</summary>
    public static int Width(string text)
    {
        var width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
            width += ElementWidth((string)elements.Current);
        return width;
    }

    private static int ElementWidth(string element)
    {
        var first = Rune.GetRuneAt(element, 0);
        if (element.Contains('️')) // вариант «эмодзи»
            return 2;
        if (IsWide(first.Value))
            return 2;

        var category = Rune.GetUnicodeCategory(first);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.Control ? 0 : 1;
    }

    private static bool IsWide(int c) =>
        c is >= 0x1100 and <= 0x115F
            or >= 0x2E80 and <= 0x303E
            or >= 0x3041 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F1E6 and <= 0x1F1FF // региональные индикаторы (флаги)
            or >= 0x1F300 and <= 0x1F64F
            or >= 0x1F680 and <= 0x1F6FF
            or >= 0x1F900 and <= 0x1F9FF
            or >= 0x1FA70 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD
            or 0x231A or 0x231B or (>= 0x23E9 and <= 0x23EC) or 0x23F0 or 0x23F3 or 0x25FD or 0x25FE
            or 0x2614 or 0x2615 or (>= 0x2648 and <= 0x2653) or 0x267F or 0x2693 or 0x26A1 or 0x26AA or 0x26AB
            or 0x26BD or 0x26BE or 0x26C4 or 0x26C5 or 0x26CE or 0x26D4 or 0x26EA or 0x26F2 or 0x26F3 or 0x26F5
            or 0x26FA or 0x26FD or 0x2705 or 0x270A or 0x270B or 0x2728 or 0x274C or 0x274E
            or (>= 0x2753 and <= 0x2755) or 0x2757 or (>= 0x2795 and <= 0x2797) or 0x27B0 or 0x27BF
            or 0x2B1B or 0x2B1C or 0x2B50 or 0x2B55;
}
