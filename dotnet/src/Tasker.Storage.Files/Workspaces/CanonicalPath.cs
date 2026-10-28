using System.Runtime.InteropServices;
using System.Text;

namespace Tasker.Storage.Files.Workspaces;

/// <summary>
/// Канонический путь — один на каждый файл или папку на диске: абсолютный, с раскрытыми симлинками
/// и с регистром букв, как он записан на диске (на macOS и Windows файловая система обычно не различает регистр).
/// По нему рабочие области и блокировки записи совпадают, как бы ни открыли папку.
/// <para>
/// Windows: буква диска всегда заглавная (<c>c:\x</c> и <c>C:\x</c> — одна папка), префикс <c>\\?\</c> снимается, короткие имена 8.3
/// (<c>RUNNER~1</c>) раскрываются в длинные, junction и символические ссылки раскрываются, как и на Unix. UNC-путь
/// (<c>\\server\share\папка</c>) остаётся UNC: общий ресурс не сопоставляется с буквой диска. Пробелы и кириллица в именах — обычные знаки.
/// </para>
/// </summary>
public static class CanonicalPath
{
    // Защита от циклов симлинков.
    private const int MaxLinks = 40;

    public static string Of(string path)
    {
        var full = StripExtendedPrefix(Path.GetFullPath(path));
        var root = NormalizeRoot(Path.GetPathRoot(full)!);
        var parts = full[Path.GetPathRoot(full)!.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        var links = 0;
        var queue = new Queue<string>(parts);
        while (queue.TryDequeue(out var part))
        {
            var next = Path.Combine(current, ActualName(current, part));
            var info = new FileInfo(next);

            if (info.LinkTarget != null && ++links <= MaxLinks)
            {
                // Цель симлинка (на Windows — и junction) может быть относительной и сама содержать симлинки — разбираем её заново.
                var target = StripExtendedPrefix(Path.GetFullPath(info.LinkTarget, current));
                var targetRoot = Path.GetPathRoot(target)!;
                var rest = queue.ToArray();
                queue = new Queue<string>(target[targetRoot.Length..]
                    .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                    .Concat(rest));
                current = NormalizeRoot(targetRoot);
                continue;
            }

            current = next;
        }

        return OperatingSystem.IsWindows() ? ExpandShortNames(current) : current;
    }

    /// <summary>
    /// Снимает префикс длинных путей Windows: <c>\\?\C:\x</c> → <c>C:\x</c>, <c>\\?\UNC\server\share\x</c> → <c>\\server\share\x</c>.
    /// Остальные пути (в том числе Unix) возвращает как есть. Чистая функция.
    /// </summary>
    public static string StripExtendedPrefix(string path)
    {
        const string unc = @"\\?\UNC\";
        const string extended = @"\\?\";

        if (path.StartsWith(unc, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[unc.Length..];

        // Только вид «\\?\C:\…»: другие формы (\\?\Volume{guid}\) не пути с буквой диска, их не трогаем.
        if (path.StartsWith(extended, StringComparison.Ordinal) && path.Length >= extended.Length + 2
            && char.IsAsciiLetter(path[extended.Length]) && path[extended.Length + 1] == ':')
            return path[extended.Length..];

        return path;
    }

    /// <summary>Корень пути в единой форме: буква диска заглавная (<c>c:\</c> → <c>C:\</c>). Остальные корни (<c>/</c>, UNC) — как есть. Чистая функция.</summary>
    public static string NormalizeRoot(string root) =>
        root.Length >= 2 && char.IsAsciiLetterLower(root[0]) && root[1] == ':'
            ? char.ToUpperInvariant(root[0]) + root[1..]
            : root;

    // Имя элемента каталога, как оно записано на диске: точное совпадение, иначе единственное без учёта регистра.
    // Элемента нет (файл ещё не создан) — имя как есть.
    private static string ActualName(string directory, string name)
    {
        if (name is "." or "..")
            return name;

        try
        {
            var matches = new DirectoryInfo(directory)
                .EnumerateFileSystemInfos()
                .Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Name)
                .ToArray();

            return matches.Contains(name) || matches.Length != 1 ? name : matches[0];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return name;
        }
    }

    // Короткие имена 8.3 (PROGRA~1, RUNNER~1 во временной папке CI): одна папка под двумя написаниями была бы двумя областями.
    // Имя с «~» может быть и обычным — тогда GetLongPathName вернёт его же. Нет такого пути (область ещё не создана) — как есть.
    private static string ExpandShortNames(string path)
    {
        if (!path.Contains('~'))
            return path;

        try
        {
            var buffer = new StringBuilder(1024);
            var length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
            return length > 0 && length < buffer.Capacity ? NormalizeRoot(StripExtendedPrefix(buffer.ToString())) : path;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return path;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetLongPathNameW")]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint length);
}
