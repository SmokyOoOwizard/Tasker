namespace Tasker.Core.Tasks.Query;

internal enum QueryToken
{
    Dot, Word, Text, Number, OpenParen, CloseParen, OpenBracket, CloseBracket, Comma, Equal, NotEqual, Greater, GreaterOrEqual, Less, LessOrEqual,
    And, Or, Pipe, Not, Minus, Unknown
}

internal readonly record struct QueryLexeme(QueryToken Kind, string Value, int Start, int End, bool Closed = true);

internal static class TaskQueryLexer
{
    public static List<QueryLexeme> Read(string text)
    {
        var result = new List<QueryLexeme>();
        var at = 0;
        while (at < text.Length)
        {
            var c = text[at];
            if (char.IsWhiteSpace(c))
            {
                at++;
                continue;
            }

            var two = at + 1 < text.Length ? text.Substring(at, 2) : "";
            var pair = two switch
            {
                "==" => QueryToken.Equal,
                "!=" => QueryToken.NotEqual,
                ">=" => QueryToken.GreaterOrEqual,
                "<=" => QueryToken.LessOrEqual,
                "&&" => QueryToken.And,
                "||" => QueryToken.Or,
                _ => (QueryToken?)null
            };
            if (pair is { } paired)
            {
                result.Add(new QueryLexeme(paired, two, at, at + 2));
                at += 2;
                continue;
            }

            if (c == '"')
            {
                var lexeme = ReadText(text, at);
                result.Add(lexeme);
                at = lexeme.End;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '-' && at + 1 < text.Length && char.IsAsciiDigit(text[at + 1])))
            {
                var end = at + 1;
                while (end < text.Length && (char.IsAsciiDigit(text[end]) || (text[end] == '.' && end + 1 < text.Length && char.IsAsciiDigit(text[end + 1]))))
                    end++;
                result.Add(new QueryLexeme(QueryToken.Number, text[at..end], at, end));
                at = end;
                continue;
            }

            if (IsWordChar(c))
            {
                var end = at + 1;
                while (end < text.Length && IsWordChar(text[end]))
                    end++;
                result.Add(new QueryLexeme(QueryToken.Word, text[at..end], at, end));
                at = end;
                continue;
            }

            var single = c switch
            {
                '.' => QueryToken.Dot,
                '(' => QueryToken.OpenParen,
                ')' => QueryToken.CloseParen,
                '[' => QueryToken.OpenBracket,
                ']' => QueryToken.CloseBracket,
                ',' => QueryToken.Comma,
                '>' => QueryToken.Greater,
                '<' => QueryToken.Less,
                '|' => QueryToken.Pipe,
                '!' => QueryToken.Not,
                '-' => QueryToken.Minus,
                _ => QueryToken.Unknown
            };
            result.Add(new QueryLexeme(single, c.ToString(), at, at + 1));
            at++;
        }
        return result;
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static QueryLexeme ReadText(string text, int start)
    {
        var value = new System.Text.StringBuilder();
        var at = start + 1;
        while (at < text.Length)
        {
            var c = text[at];
            if (c == '"')
                return new QueryLexeme(QueryToken.Text, value.ToString(), start, at + 1);
            if (c == '\\' && at + 1 < text.Length)
            {
                value.Append(text[at + 1]);
                at += 2;
                continue;
            }
            value.Append(c);
            at++;
        }
        return new QueryLexeme(QueryToken.Text, value.ToString(), start, text.Length, false);
    }
}
