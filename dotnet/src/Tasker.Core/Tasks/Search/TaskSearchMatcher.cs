namespace Tasker.Core.Tasks.Search;

public enum TaskSearchField
{
    Number,
    Title,
    Description
}

public enum TaskSearchMatch
{
    Exact,
    Prefix,
    Substring,
    Typo
}

public sealed record TaskSearchRange(int Start, int Length);

public sealed record TaskSearchSnippet(string Text, TaskSearchRange[] Ranges);

public sealed record TaskSearchHit(TaskItem Task, TaskSearchField Field, TaskSearchMatch Match, bool SwitchedLayout, double Score, TaskSearchSnippet? Snippet);

public static class TaskSearchMatcher
{
    private const int SnippetBefore = 40;
    private const int SnippetLength = 160;

    private readonly record struct WordHit(TaskSearchMatch Match, double Quality, TaskSearchToken Token, bool InDescription);

    public static TaskSearchHit? Match(TaskItem task, TaskSearchQuery query)
    {
        var number = MatchNumber(task, query.Number);
        var title = TaskSearchText.Normalize(task.Title);
        var titleTokens = TaskSearchText.Tokenize(title);
        var description = TaskSearchText.Normalize(task.Description ?? "");
        var descriptionTokens = new Lazy<List<TaskSearchToken>>(() => TaskSearchText.Tokenize(description));

        var direct = MatchWords(query.Words, title, titleTokens, description, descriptionTokens);
        var switched = query.SwitchedWords == null || IsTitleExact(direct)
            ? null
            : MatchWords(query.SwitchedWords, title, titleTokens, description, descriptionTokens);
        var text = Best(task, direct, switched, description);

        if (number != null)
            return new TaskSearchHit(task, TaskSearchField.Number, number.Value.Match, false, number.Value.Score, text?.Snippet);
        return text;
    }

    public static IEnumerable<TaskSearchHit> Order(IEnumerable<TaskSearchHit> hits) => hits
        .OrderByDescending(x => x.Score)
        .ThenByDescending(x => x.Task.CreatedAt)
        .ThenBy(x => x.Task.Id);

    private static (TaskSearchMatch Match, double Score)? MatchNumber(TaskItem task, TaskSearchNumberQuery? query)
    {
        if (query == null)
            return null;

        (TaskSearchMatch Match, double Score)? best = null;
        foreach (var number in task.SeriesNumbers)
        {
            if (query.SeriesIds != null && !query.SeriesIds.Contains(number.SeriesId))
                continue;

            var text = number.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (text == query.Digits)
                return (TaskSearchMatch.Exact, 1.0);
            if (text.StartsWith(query.Digits, StringComparison.Ordinal))
            {
                var score = 0.9 + 0.09 * query.Digits.Length / text.Length;
                if (best == null || score > best.Value.Score)
                    best = (TaskSearchMatch.Prefix, score);
            }
        }
        return best;
    }

    private sealed record WordsResult(WordHit[] Hits, TaskSearchField Field);

    private static bool IsTitleExact(WordsResult? result) =>
        result is { Field: TaskSearchField.Title } && result.Hits.All(x => x.Match == TaskSearchMatch.Exact);

    private static WordsResult? MatchWords(
        string[] words, string title, List<TaskSearchToken> titleTokens, string description, Lazy<List<TaskSearchToken>> descriptionTokens)
    {
        if (words.Length == 0)
            return null;

        var hits = new WordHit[words.Length];
        var inTitle = true;
        for (var i = 0; i < words.Length; i++)
        {
            var hit = BestToken(words[i], title, titleTokens, false);
            if (hit == null)
            {
                inTitle = false;
                hit = BestToken(words[i], description, descriptionTokens.Value, true);
            }
            if (hit == null)
                return null;
            hits[i] = hit.Value;
        }
        return new WordsResult(hits, inTitle ? TaskSearchField.Title : TaskSearchField.Description);
    }

    private static WordHit? BestToken(string word, string text, List<TaskSearchToken> tokens, bool inDescription)
    {
        WordHit? best = null;
        var edits = TaskSearchText.AllowedEdits(word);
        foreach (var token in tokens)
        {
            var value = text.AsSpan(token.Start, token.Length);
            WordHit? hit = null;
            if (value.SequenceEqual(word))
                return new WordHit(TaskSearchMatch.Exact, 1.0, token, inDescription);
            if (value.StartsWith(word, StringComparison.Ordinal))
                hit = new WordHit(TaskSearchMatch.Prefix, 0.8, new TaskSearchToken(token.Start, word.Length), inDescription);
            else if (value.IndexOf(word, StringComparison.Ordinal) is >= 0 and var position)
                hit = new WordHit(TaskSearchMatch.Substring, 0.6, new TaskSearchToken(token.Start + position, word.Length), inDescription);
            else if (edits > 0 && (best == null || best.Value.Match == TaskSearchMatch.Typo))
            {
                var distance = TaskSearchText.EditDistance(word, value, edits);
                if (distance <= edits)
                    hit = new WordHit(TaskSearchMatch.Typo, distance == 1 ? 0.3 : 0.2, token, inDescription);
            }

            if (hit != null && (best == null || hit.Value.Quality > best.Value.Quality))
                best = hit;
        }
        return best;
    }

    private static TaskSearchHit? Best(TaskItem task, WordsResult? direct, WordsResult? switched, string description)
    {
        var first = ToHit(task, direct, false, description);
        var second = ToHit(task, switched, true, description);
        if (first == null)
            return second;
        if (second == null)
            return first;
        return second.Score > first.Score ? second : first;
    }

    private static TaskSearchHit? ToHit(TaskItem task, WordsResult? result, bool layout, string description)
    {
        if (result == null)
            return null;

        var quality = result.Hits.Average(x => x.Quality) * (layout ? 0.9 : 1.0);
        var score = result.Field == TaskSearchField.Title ? 0.6 + 0.29 * quality : 0.3 + 0.29 * quality;
        var match = result.Hits.Max(x => x.Match);
        var snippet = result.Field == TaskSearchField.Description ? Snippet(task.Description ?? "", result.Hits) : null;
        return new TaskSearchHit(task, result.Field, match, layout, score, snippet);
    }

    private static TaskSearchSnippet? Snippet(string description, WordHit[] hits)
    {
        var inside = hits.Where(x => x.InDescription).OrderBy(x => x.Token.Start).ToArray();
        if (inside.Length == 0 || description.Length == 0)
            return null;

        var start = Math.Max(0, inside[0].Token.Start - SnippetBefore);
        while (start > 0 && !char.IsWhiteSpace(description[start - 1]) && inside[0].Token.Start - start < SnippetBefore + 16)
            start--;
        if (start > 0 && char.IsLowSurrogate(description[start]))
            start--;

        var end = Math.Min(description.Length, start + SnippetLength);
        if (end < description.Length && char.IsLowSurrogate(description[end]))
            end++;

        var prefix = start > 0 ? "…" : "";
        var suffix = end < description.Length ? "…" : "";
        var body = description[start..end].Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        var ranges = inside
            .Where(x => x.Token.Start + x.Token.Length <= end)
            .Select(x => new TaskSearchRange(x.Token.Start - start + prefix.Length, x.Token.Length))
            .ToArray();
        return new TaskSearchSnippet(prefix + body + suffix, ranges);
    }
}
