namespace Tasker.Core.IO;

/// <summary>
/// Правила имён файлов Windows — чистые функции без обращения к системе, поэтому проверяются на любой платформе. Файлы .tasker
/// попадают в git и открываются на всех системах, так что имя, допустимое на Linux и macOS, но недопустимое на Windows,
/// сломало бы <c>git clone</c> на Windows.
/// </summary>
public static class WindowsNames
{
    /// <summary>Знаки, которых не может быть в имени файла Windows (управляющие знаки 0–31 проверяются отдельно).</summary>
    public static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    // Устройства DOS: имя «CON», «con.txt», «CON .txt», «COM1.yaml» зарезервировано, даже с расширением.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
    };

    /// <summary>Зарезервированное ли имя: часть до первой точки (без хвостовых пробелов) — имя устройства.</summary>
    public static bool IsReserved(string fileName)
    {
        var dot = fileName.IndexOf('.');
        var stem = (dot < 0 ? fileName : fileName[..dot]).TrimEnd(' ');
        return Reserved.Contains(stem);
    }

    /// <summary>Чем имя файла не годится для Windows; null — годится на любой системе.</summary>
    public static string? Problem(string fileName)
    {
        if (fileName.Length == 0)
            return "the name is empty";

        foreach (var c in fileName)
        {
            if (c < ' ')
                return $"the name has a control character (U+{(int)c:X4})";
            if (Array.IndexOf(InvalidChars, c) >= 0)
                return $"the name has the character '{c}', which Windows does not allow";
        }

        if (fileName[^1] is '.' or ' ')
            return "Windows does not allow a name that ends with a dot or a space";
        if (IsReserved(fileName))
            return "the name is reserved by Windows (CON, PRN, AUX, NUL, COM1-9, LPT1-9)";
        return null;
    }

    public static bool IsValid(string fileName) => Problem(fileName) == null;
}
