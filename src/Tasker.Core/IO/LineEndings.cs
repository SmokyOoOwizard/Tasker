namespace Tasker.Core.IO;

/// <summary>
/// Окончания строк и BOM в файлах .tasker. Файлы пишутся с LF, но git на Windows при <c>core.autocrlf=true</c> отдаёт их
/// с CRLF (а редакторы иногда добавляют BOM). Содержимое от этого не меняется, поэтому версия файла (хэш) считается по тексту,
/// приведённому к LF и без BOM, а читатели терпят и то и другое.
/// </summary>
public static class LineEndings
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>Байты, по которым считается версия: без BOM, пары CRLF заменены на LF. Нет ни BOM, ни CR — возвращает тот же массив.</summary>
    public static byte[] ForHash(byte[] bytes)
    {
        var start = bytes.AsSpan().StartsWith(Bom) ? Bom.Length : 0;
        if (start == 0 && !bytes.AsSpan().Contains((byte)'\r'))
            return bytes;

        var result = new byte[bytes.Length - start];
        var length = 0;
        for (var i = start; i < bytes.Length; i++)
        {
            if (bytes[i] == '\r' && i + 1 < bytes.Length && bytes[i + 1] == '\n')
                continue;
            result[length++] = bytes[i];
        }

        return length == result.Length ? result : result[..length];
    }

    /// <summary>Текст без BOM (U+FEFF в начале), если он есть.</summary>
    public static string StripBom(string text) => text.Length > 0 && text[0] == '﻿' ? text[1..] : text;

    /// <summary>CRLF → LF (одиночный CR остаётся).</summary>
    public static string ToLf(string text) => text.Contains('\r') ? text.Replace("\r\n", "\n") : text;

    /// <summary>Окончание строки, которым написан текст: CRLF, если оно в тексте встречается, иначе LF.</summary>
    public static string Of(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
