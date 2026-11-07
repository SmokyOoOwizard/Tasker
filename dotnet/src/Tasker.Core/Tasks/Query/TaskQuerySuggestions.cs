using Tasker.Core.Fields;

namespace Tasker.Core.Tasks.Query;

public enum TaskQuerySuggestionKind { Property, Field, Link, Function, Operator, Value, Keyword }

public sealed record TaskQuerySuggestion(TaskQuerySuggestionKind Kind, string Label, string Insert, int Start, int End, int Caret, string? Detail = null);

internal static class TaskQuerySuggester
{
    private const int Limit = 40;

    private static readonly string[] FilterRoots = ["Status", "Type", "Title", "Series", "Field", "Link", "Parent"];
    private static readonly string[] SortRoots = ["Status", "Type", "Title", "Created", "Updated", "Series", "Field"];

    private sealed record Cursor(int Start, int End, string Partial, bool IsText);

    private enum Mode { Predicate, InPath, AfterPath, Value, AfterValue, AfterPredicate, SortWord, SortKey, AfterSortKey }

    private sealed class State
    {
        public Mode Mode = Mode.Predicate;
        public List<string> Path = [];
        public string? Function;
        public bool InArray;
        public bool InSort;
        public int ParenDepth;
        public bool SortDescending;
        public readonly List<string> Used = [];
    }

    public static TaskQuerySuggestion[] Suggest(string text, int cursor, TaskQueryVocabulary vocabulary)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        var tokens = TaskQueryLexer.Read(text[..cursor]);
        var place = Place(text, cursor, tokens);
        var state = Walk(tokens);
        var result = state.Mode switch
        {
            Mode.Predicate => Predicate(state, place),
            Mode.InPath => InPath(state, place, vocabulary),
            Mode.AfterPath => AfterPath(state, place, vocabulary),
            Mode.Value => Values(state, place, vocabulary),
            Mode.AfterValue => AfterValue(state, place),
            Mode.AfterPredicate => AfterPredicate(state, place),
            Mode.SortWord => [Item(TaskQuerySuggestionKind.Keyword, "sort", place, "sort ")],
            Mode.SortKey => SortKey(state, place),
            Mode.AfterSortKey => place.Partial.Length == 0 ? [Item(TaskQuerySuggestionKind.Operator, ",", place, ", ")] : [],
            _ => []
        };
        return Rank(result, place)
            .Where(x => x.Start + x.Insert.Length != x.End || string.CompareOrdinal(text, x.Start, x.Insert, 0, x.Insert.Length) != 0)
            .Take(Limit)
            .ToArray();
    }

    private static Cursor Place(string text, int cursor, List<QueryLexeme> tokens)
    {
        var end = cursor;
        if (tokens.Count > 0 && tokens[^1] is { End: var last } token && last == cursor)
        {
            if (token.Kind == QueryToken.Word || token.Kind == QueryToken.Number || (token.Kind == QueryToken.Text && !token.Closed))
            {
                tokens.RemoveAt(tokens.Count - 1);
                while (end < text.Length && TaskQueryLexer.IsWordChar(text[end]))
                    end++;
                if (token.Kind == QueryToken.Text && end < text.Length && text[end] == '"')
                    end++;
                return new Cursor(token.Start, end, token.Value, token.Kind == QueryToken.Text);
            }
        }
        while (end < text.Length && TaskQueryLexer.IsWordChar(text[end]))
            end++;
        return new Cursor(cursor, end, "", false);
    }

    private static State Walk(List<QueryLexeme> tokens)
    {
        var state = new State();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            switch (state.Mode)
            {
                case Mode.Predicate:
                    if (token.Kind == QueryToken.Dot)
                        (state.Mode, state.Path) = (Mode.InPath, []);
                    else if (token.Kind == QueryToken.OpenParen)
                        state.ParenDepth++;
                    else if (token.Kind == QueryToken.Pipe)
                        state.Mode = Mode.SortWord;
                    break;
                case Mode.InPath:
                    if (token.Kind is QueryToken.Word or QueryToken.Text)
                    {
                        state.Path.Add(token.Value);
                        state.Mode = state.InSort ? Mode.AfterSortKey : Mode.AfterPath;
                    }
                    break;
                case Mode.AfterPath:
                    if (token.Kind == QueryToken.Dot)
                    {
                        state.Mode = Mode.InPath;
                    }
                    else if (token.Kind == QueryToken.OpenParen)
                    {
                        state.Function = state.Path[^1];
                        state.Path.RemoveAt(state.Path.Count - 1);
                        (state.Mode, state.InArray) = (Mode.Value, false);
                        state.Used.Clear();
                    }
                    else if (token.Kind is QueryToken.Equal or QueryToken.NotEqual or QueryToken.Greater or QueryToken.GreaterOrEqual or QueryToken.Less or QueryToken.LessOrEqual)
                    {
                        (state.Mode, state.InArray, state.Function) = (Mode.Value, false, null);
                        state.Used.Clear();
                    }
                    else
                    {
                        state.Mode = Mode.AfterPredicate;
                    }
                    break;
                case Mode.Value:
                    if (token.Kind == QueryToken.OpenBracket)
                    {
                        state.InArray = true;
                    }
                    else if (token.Kind == QueryToken.CloseParen)
                    {
                        (state.Mode, state.Function) = (Mode.AfterPredicate, null);
                    }
                    else
                    {
                        state.Used.Add(token.Value);
                        state.Mode = Mode.AfterValue;
                    }
                    break;
                case Mode.AfterValue:
                    if (state.InArray && token.Kind == QueryToken.Comma)
                        state.Mode = Mode.Value;
                    else if (state.InArray && token.Kind == QueryToken.CloseBracket)
                        state.InArray = false;
                    else if (state.Function != null && token.Kind == QueryToken.CloseParen)
                        (state.Mode, state.Function) = (Mode.AfterPredicate, null);
                    else
                    {
                        state.Function = null;
                        state.Mode = Mode.AfterPredicate;
                        i--;
                    }
                    break;
                case Mode.AfterPredicate:
                    if (token.Kind is QueryToken.And or QueryToken.Or)
                        state.Mode = Mode.Predicate;
                    else if (token.Kind == QueryToken.CloseParen)
                        state.ParenDepth = Math.Max(0, state.ParenDepth - 1);
                    else if (token.Kind == QueryToken.Pipe)
                        state.Mode = Mode.SortWord;
                    break;
                case Mode.SortWord:
                    if (token.Kind == QueryToken.Word && token.Value.Equals("sort", StringComparison.OrdinalIgnoreCase))
                        (state.Mode, state.InSort) = (Mode.SortKey, true);
                    break;
                case Mode.SortKey:
                    if (token.Kind == QueryToken.Minus)
                        state.SortDescending = true;
                    else if (token.Kind == QueryToken.Dot)
                        (state.Mode, state.Path) = (Mode.InPath, []);
                    break;
                case Mode.AfterSortKey:
                    if (token.Kind == QueryToken.Dot)
                    {
                        state.Mode = Mode.InPath;
                    }
                    else if (token.Kind == QueryToken.Comma)
                    {
                        state.Mode = Mode.SortKey;
                        state.SortDescending = false;
                    }
                    break;
            }
        }
        return state;
    }

    private static TaskQuerySuggestion Item(TaskQuerySuggestionKind kind, string label, Cursor place, string insert, int? caret = null, string? detail = null) =>
        new(kind, label, insert, place.Start, place.End, caret ?? insert.Length, detail);

    private static string Segment(string name) => name.All(TaskQueryLexer.IsWordChar) ? name : $"\"{name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static string Quoted(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static List<TaskQuerySuggestion> Predicate(State state, Cursor place)
    {
        var result = FilterRoots.Select(x => Root(x, place, x is "Field" or "Link" ? "." : " ", true)).ToList();
        if (place.Partial.Length > 0)
            result.Insert(0, Item(TaskQuerySuggestionKind.Function, $".Title.Contains({Quoted(place.Partial)})", place, $".Title.Contains({Quoted(place.Partial)}) "));
        return result;
    }

    private static TaskQuerySuggestion Root(string name, Cursor place, string suffix, bool dot) =>
        Item(TaskQuerySuggestionKind.Property, "." + name, place, (dot ? "." : "") + name + suffix);

    private static List<TaskQuerySuggestion> SortKey(State state, Cursor place) =>
        SortRoots.Select(x => Root(x, place, x == "Field" ? "." : "", true)).ToList();

    private static List<TaskQuerySuggestion> InPath(State state, Cursor place, TaskQueryVocabulary vocabulary)
    {
        if (state.Path.Count == 0)
        {
            var roots = state.InSort ? SortRoots : FilterRoots;
            return roots.Select(x => Root(x, place, x is "Field" or "Link" ? "." : state.InSort ? "" : " ", false)).ToList();
        }

        if (state.Path.Count == 1 && Same(state.Path[0], "Field"))
            return vocabulary.Fields.Select(x => Item(TaskQuerySuggestionKind.Field, x.Name, place, Segment(x.Name) + (state.InSort ? "" : " "), detail: Detail(x))).ToList();
        if (state.Path.Count == 1 && Same(state.Path[0], "Link"))
            return vocabulary.LinkTypes.Select(x => Item(TaskQuerySuggestionKind.Link, x.Name, place, Segment(x.Name) + ".")).ToList();

        return state.InSort ? [] : Functions(state.Path, vocabulary).Select(x => FunctionItem(x, place, "")).ToList();
    }

    private static string Detail(QueryField field) => field.Type.ToString().ToLowerInvariant() + (field.Multiple ? "[]" : "");

    private static IEnumerable<string> Functions(List<string> path, TaskQueryVocabulary vocabulary)
    {
        if (path.Count == 1 && Same(path[0], "Title"))
            return ["Contains"];
        if (path.Count == 1 && Same(path[0], "Series"))
            return ["Contains", "ContainsAny", "ContainsAll", "IsSet", "IsUnset"];
        if (path.Count == 1 && Same(path[0], "Parent"))
            return ["IsSet", "IsUnset"];
        if (path.Count == 2 && Same(path[0], "Link"))
            return ["IsSet", "IsUnset"];
        if (path.Count == 2 && Same(path[0], "Field"))
        {
            var multiple = FieldOf(path[1], vocabulary)?.Multiple ?? true;
            return multiple
                ? ["Contains", "ContainsAny", "ContainsAll", "IsSet", "IsUnset", "IsAttached", "IsDetached"]
                : ["IsSet", "IsUnset", "IsAttached", "IsDetached"];
        }
        return [];
    }

    private static TaskQuerySuggestion FunctionItem(string name, Cursor place, string prefix)
    {
        var (arguments, caret) = name switch
        {
            "Contains" => ("(\"\")", 2),
            "ContainsAny" or "ContainsAll" => ("([\"\"])", 3),
            _ => ("()", 2)
        };
        var insert = prefix + name + arguments + " ";
        var label = prefix + name + (arguments == "()" ? "()" : "(…)");
        return Item(TaskQuerySuggestionKind.Function, label, place, insert, caret == 2 && arguments == "()" ? insert.Length : prefix.Length + name.Length + caret);
    }

    private static QueryField? FieldOf(string name, TaskQueryVocabulary vocabulary) =>
        vocabulary.Fields.FirstOrDefault(x => Same(x.Name, name)) ?? vocabulary.OwnFields.GetValueOrDefault(FieldNames.Key(name))?.Field;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string[] Operators(List<string> path, TaskQueryVocabulary vocabulary)
    {
        if (path.Count == 2 && Same(path[0], "Field") && FieldOf(path[1], vocabulary)?.Type is FieldType.Int or FieldType.Float or FieldType.Date)
            return ["==", "!=", ">", ">=", "<", "<="];
        if (path.Count == 1 && (Same(path[0], "Parent") || Same(path[0], "Created") || Same(path[0], "Updated")))
            return [];
        if (path.Count == 2 && Same(path[0], "Link"))
            return [];
        return ["==", "!="];
    }

    private static List<TaskQuerySuggestion> AfterPath(State state, Cursor place, TaskQueryVocabulary vocabulary)
    {
        var result = Operators(state.Path, vocabulary).Select(x => Item(TaskQuerySuggestionKind.Operator, x, place, x + " ")).ToList();
        result.AddRange(Functions(state.Path, vocabulary).Select(x => FunctionItem(x, place, ".")));
        return result;
    }

    private static List<TaskQuerySuggestion> Values(State state, Cursor place, TaskQueryVocabulary vocabulary)
    {
        var path = state.Path;
        IEnumerable<string> values = [];
        if (path.Count == 1 && Same(path[0], "Status"))
            values = vocabulary.Statuses.Select(x => x.Name);
        else if (path.Count == 1 && Same(path[0], "Type"))
            values = vocabulary.Types.Select(x => x.Name);
        else if (path.Count == 1 && Same(path[0], "Series"))
            values = vocabulary.Series.Select(x => x.Name);
        else if (path.Count == 2 && Same(path[0], "Field") && FieldOf(path[1], vocabulary) is { } field)
        {
            if (field.Type == FieldType.Bool)
                return [Item(TaskQuerySuggestionKind.Value, "true", place, "true "), Item(TaskQuerySuggestionKind.Value, "false", place, "false ")];
            values = field.Enumeration?.Values.Select(x => x.Name) ?? [];
        }

        var open = state.InArray || state.Function != null;
        return values
            .Where(x => !state.Used.Contains(x, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => Item(TaskQuerySuggestionKind.Value, x, place, Quoted(x) + (open ? "" : " ")))
            .ToList();
    }

    private static List<TaskQuerySuggestion> AfterValue(State state, Cursor place)
    {
        if (place.Partial.Length > 0)
            return [];
        if (state.InArray)
            return [Item(TaskQuerySuggestionKind.Operator, ",", place, ", "), Item(TaskQuerySuggestionKind.Operator, "]", place, state.Function != null ? "]" : "] ")];
        if (state.Function != null)
            return [Item(TaskQuerySuggestionKind.Operator, ")", place, ") ")];
        return AfterPredicate(state, place);
    }

    private static List<TaskQuerySuggestion> AfterPredicate(State state, Cursor place)
    {
        if (place.Partial.Length > 0)
            return [];
        var result = new List<TaskQuerySuggestion>
        {
            Item(TaskQuerySuggestionKind.Operator, "&&", place, "&& "),
            Item(TaskQuerySuggestionKind.Operator, "||", place, "|| ")
        };
        if (state.ParenDepth > 0)
            result.Add(Item(TaskQuerySuggestionKind.Operator, ")", place, ") "));
        else
            result.Add(Item(TaskQuerySuggestionKind.Keyword, "| sort", place, "| sort "));
        return result;
    }

    private static IEnumerable<TaskQuerySuggestion> Rank(List<TaskQuerySuggestion> items, Cursor place)
    {
        if (place.Partial.Length == 0)
            return items;
        var needle = place.Partial;
        return items
            .Select((item, index) => (item, index, rank: Score(item, needle)))
            .Where(x => x.rank >= 0)
            .OrderBy(x => x.rank)
            .ThenBy(x => x.index)
            .Select(x => x.item);
    }

    private static int Score(TaskQuerySuggestion item, string needle)
    {
        if (item.Label.StartsWith(".Title.Contains(", StringComparison.Ordinal))
            return 2;
        var name = item.Label.TrimStart('.');
        if (name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            return 0;
        return name.Contains(needle, StringComparison.OrdinalIgnoreCase) ? 1 : -1;
    }
}
