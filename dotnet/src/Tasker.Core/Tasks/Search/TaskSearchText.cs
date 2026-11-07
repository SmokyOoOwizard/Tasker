using System.Text;

namespace Tasker.Core.Tasks.Search;

public readonly record struct TaskSearchToken(int Start, int Length);

public static class TaskSearchText
{
    private const string Latin = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
    private const string Cyrillic = "йцукенгшщзхъфывапролджэячсмитьбюё";
    private const int MaxWords = 16;

    public static string Normalize(string text)
    {
        var folded = TaskSortNames.Fold(text);
        return folded.Contains('ё') ? folded.Replace('ё', 'е') : folded;
    }

    public static List<TaskSearchToken> Tokenize(string normalized)
    {
        var tokens = new List<TaskSearchToken>();
        var start = -1;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (IsWordChar(normalized, i))
            {
                if (start < 0)
                    start = i;
                if (char.IsHighSurrogate(normalized[i]))
                    i++;
                continue;
            }

            if (start >= 0)
            {
                tokens.Add(new TaskSearchToken(start, i - start));
                start = -1;
            }
        }
        if (start >= 0)
            tokens.Add(new TaskSearchToken(start, normalized.Length - start));
        return tokens;
    }

    public static string[] Words(string normalized)
    {
        var tokens = Tokenize(normalized);
        var words = new List<string>(tokens.Count);
        foreach (var token in tokens)
            words.Add(normalized.Substring(token.Start, token.Length));
        return words.ToArray();
    }

    public static string IndexText(string text) => string.Join(' ', Words(Normalize(text)));

    public static int AllowedEdits(string word) => word.Length switch
    {
        >= 8 => 2,
        >= 4 => 1,
        _ => 0
    };

    public static string[] QueryWords(string normalized) => Words(normalized).Distinct(StringComparer.Ordinal).Take(MaxWords).ToArray();

    public static string? SwitchLayout(string text)
    {
        var folded = TaskSortNames.Fold(text);
        var switched = new StringBuilder(folded.Length);
        var changed = false;
        foreach (var c in folded)
        {
            var latin = Latin.IndexOf(c);
            var cyrillic = Cyrillic.IndexOf(c);
            var mapped = latin >= 0 ? Cyrillic[latin] : cyrillic >= 0 ? Latin[cyrillic] : c;
            changed |= mapped != c;
            switched.Append(mapped);
        }
        return changed ? Normalize(switched.ToString()) : null;
    }

    public static int EditDistance(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit)
            return limit + 1;

        var width = b.Length + 1;
        Span<int> beforePrevious = width <= 128 ? stackalloc int[width] : new int[width];
        Span<int> previous = width <= 128 ? stackalloc int[width] : new int[width];
        Span<int> current = width <= 128 ? stackalloc int[width] : new int[width];
        for (var j = 0; j < width; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var best = i;
            for (var j = 1; j < width; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    value = Math.Min(value, beforePrevious[j - 2] + 1);
                current[j] = value;
                best = Math.Min(best, value);
            }
            if (best > limit)
                return limit + 1;

            var spare = beforePrevious;
            beforePrevious = previous;
            previous = current;
            current = spare;
        }
        return previous[b.Length];
    }

    private static bool IsWordChar(string text, int index) =>
        char.IsLetterOrDigit(text, index) || char.GetUnicodeCategory(text, index) is System.Globalization.UnicodeCategory.NonSpacingMark;
}
