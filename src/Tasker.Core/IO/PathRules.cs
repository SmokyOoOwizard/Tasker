namespace Tasker.Core.IO;

/// <summary>
/// Как сравнивать пути на этой системе: на Windows файловая система не различает регистр, на Linux различает.
/// (На macOS по умолчанию тоже не различает, но том может быть и чувствительным к регистру, поэтому там сравнение точное:
/// настоящий регистр каждого имени приводит к одному виду <c>CanonicalPath</c>.) Ключи блокировок и словарей по путям берут отсюда.
/// </summary>
public static class PathRules
{
    public static StringComparison Comparison => ComparisonFor(OperatingSystem.IsWindows());

    public static StringComparer Comparer => Comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison ComparisonFor(bool windows) => windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Один и тот же ли это путь (оба приводятся к полному).</summary>
    public static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), Comparison);

    /// <summary>Лежит ли <paramref name="path"/> внутри <paramref name="folder"/> (или это она сама); границу по разделителю учитывает.</summary>
    public static bool IsInside(string folder, string path) => IsInside(folder, path, Comparison);

    public static bool IsInside(string folder, string path, StringComparison comparison)
    {
        var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!path.StartsWith(trimmed, comparison))
            return false;

        return path.Length == trimmed.Length || path[trimmed.Length] is '/' or '\\';
    }
}
