using System.Text.RegularExpressions;
using Tasker.Core.TaskSeries;

namespace Tasker.Core.Tasks.Search;

public sealed record TaskSearchNumberQuery(string Digits, Guid[]? SeriesIds);

public sealed partial record TaskSearchQuery(string Text, string[] Words, string[]? SwitchedWords, TaskSearchNumberQuery? Number)
{
    public IEnumerable<string[]> WordSets => SwitchedWords == null ? [Words] : [Words, SwitchedWords];

    public static TaskSearchQuery Parse(string text, IReadOnlyCollection<Series> series)
    {
        var trimmed = text.Trim();
        var normalized = TaskSearchText.Normalize(trimmed);
        var switched = TaskSearchText.SwitchLayout(trimmed);
        var words = TaskSearchText.QueryWords(normalized);
        var switchedWords = switched == null ? null : TaskSearchText.QueryWords(switched);
        if (switchedWords is { Length: 0 } || (switchedWords != null && switchedWords.SequenceEqual(words)))
            switchedWords = null;

        var number = ParseNumber(normalized, series) ?? (switched == null ? null : ParseNumber(switched, series));
        return new TaskSearchQuery(trimmed, words, switchedWords, number);
    }

    private static TaskSearchNumberQuery? ParseNumber(string normalized, IReadOnlyCollection<Series> series)
    {
        var match = NumberPattern().Match(normalized);
        if (!match.Success)
            return null;

        var digits = match.Groups["digits"].Value;
        if (digits.Length > 10)
            return null;
        if (match.Groups["prefix"].Length == 0)
            return new TaskSearchNumberQuery(digits, null);

        var prefix = match.Groups["prefix"].Value;
        var ids = series.Where(x => TaskSearchText.Normalize(x.Prefix) == prefix).Select(x => x.Id).ToArray();
        return ids.Length == 0 ? null : new TaskSearchNumberQuery(digits, ids);
    }

    [GeneratedRegex(@"^(?<prefix>\S*?)[-\s]*(?<digits>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
