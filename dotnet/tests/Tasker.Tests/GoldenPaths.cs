using System.Text;

namespace Tasker.Tests;

internal static class GoldenPaths
{
    public const string Root = "<ROOT>";

    public static string Rooted(string text, string root)
    {
        if (!OperatingSystem.IsWindows())
            return text.Replace(root, Root);
        foreach (var level in new[] { 4, 2, 1 })
        {
            var separator = new string('\\', level);
            text = Slashes(text.Replace(root.Replace("\\", separator), Root), separator);
        }
        return text;
    }

    private static string Slashes(string text, string separator)
    {
        var result = new StringBuilder(text.Length);
        var at = 0;
        while (text.IndexOf(Root, at, StringComparison.Ordinal) is var found and >= 0)
        {
            var end = found + Root.Length;
            result.Append(text, at, end - at);
            at = end;
            while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] is not ('"' or '\'' or ')' or ','))
            {
                var next = at + separator.Length;
                if (string.CompareOrdinal(text, at, separator, 0, separator.Length) == 0 && next < text.Length && text[next] is not ('"' or '\\'))
                {
                    result.Append('/');
                    at = next;
                }
                else
                {
                    result.Append(text[at++]);
                }
            }
        }
        result.Append(text, at, text.Length - at);
        return result.ToString();
    }
}
