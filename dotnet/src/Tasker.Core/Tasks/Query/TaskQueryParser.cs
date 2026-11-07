namespace Tasker.Core.Tasks.Query;

internal sealed class TaskQueryParser
{
    public const int MaxLength = 4000;
    private const int MaxDepth = 64;

    private readonly string _text;
    private readonly List<QueryLexeme> _tokens;
    private int _at;
    private int _depth;

    private sealed class Failure(TaskQueryDiagnostic diagnostic) : Exception
    {
        public TaskQueryDiagnostic Diagnostic { get; } = diagnostic;
    }

    private TaskQueryParser(string text)
    {
        _text = text;
        _tokens = TaskQueryLexer.Read(text);
    }

    public static (QuerySyntax? Syntax, TaskQueryDiagnostic? Error) Parse(string text)
    {
        if (text.Length > MaxLength)
            return (null, new TaskQueryDiagnostic(TaskerErrors.QueryTooComplex(), 0, text.Length));
        var parser = new TaskQueryParser(text);
        try
        {
            return (parser.Query(), null);
        }
        catch (Failure failure)
        {
            return (null, failure.Diagnostic);
        }
    }

    private QueryLexeme? Current => _at < _tokens.Count ? _tokens[_at] : null;

    private bool Is(QueryToken kind) => Current?.Kind == kind;

    private bool IsSortWord(int at) =>
        at < _tokens.Count && _tokens[at].Kind == QueryToken.Word && _tokens[at].Value.Equals("sort", StringComparison.OrdinalIgnoreCase);

    private static Failure Fail(TaskerValidationException error, int start, int end) => new(new TaskQueryDiagnostic(error, start, end));

    private Failure FailAtEnd(TaskerValidationException error) => Fail(error, _text.Length, _text.Length);

    private Failure Unexpected(QueryLexeme token) => Fail(TaskerErrors.QueryUnexpected(token.Value), token.Start, token.End);

    private QuerySyntax Query()
    {
        CheckStrings();
        QueryNode? filter = null;
        if (Current != null && !Is(QueryToken.Pipe))
            filter = Or();

        var sort = Array.Empty<QuerySortKey>();
        if (Is(QueryToken.Pipe))
            sort = Sort();
        if (Current is { } extra)
            throw extra.Kind == QueryToken.CloseParen ? Unexpected(extra) : FreeTextOr(extra);
        return new QuerySyntax(filter, sort);
    }

    private void CheckStrings()
    {
        foreach (var token in _tokens.Where(x => x is { Kind: QueryToken.Text, Closed: false }))
            throw Fail(TaskerErrors.QueryUnclosedString(), token.Start, token.End);
    }

    private Failure FreeTextOr(QueryLexeme token)
    {
        if (token.Kind is QueryToken.Word or QueryToken.Text or QueryToken.Number)
            return FreeText();
        return Unexpected(token);
    }

    private Failure FreeText()
    {
        var first = Current!.Value;
        var last = first;
        var words = new List<string>();
        while (Current is { Kind: QueryToken.Word or QueryToken.Text or QueryToken.Number } token)
        {
            words.Add(token.Value);
            last = token;
            _at++;
        }
        return Fail(TaskerErrors.QueryFreeText(string.Join(' ', words)), first.Start, last.End);
    }

    private QueryNode Or()
    {
        var parts = new List<QueryNode> { And() };
        while (Is(QueryToken.Or))
        {
            _at++;
            parts.Add(And());
        }
        return parts.Count == 1 ? parts[0] : new QueryAny([.. parts]);
    }

    private QueryNode And()
    {
        var parts = new List<QueryNode> { Unary() };
        while (Is(QueryToken.And))
        {
            _at++;
            parts.Add(Unary());
        }
        return parts.Count == 1 ? parts[0] : new QueryAll([.. parts]);
    }

    private QueryNode Unary()
    {
        if (++_depth > MaxDepth)
            throw Fail(TaskerErrors.QueryTooComplex(), 0, _text.Length);
        try
        {
            if (Current is not { } token)
                throw FailAtEnd(TaskerErrors.QueryExpectedCondition());

            switch (token.Kind)
            {
                case QueryToken.Not:
                    _at++;
                    return new QueryNegation(Unary());
                case QueryToken.OpenParen:
                    _at++;
                    var inner = Or();
                    if (!Is(QueryToken.CloseParen))
                        throw Fail(TaskerErrors.QueryUnclosedParen(), token.Start, token.End);
                    _at++;
                    return inner;
                case QueryToken.Dot:
                    return Condition();
                case QueryToken.Word or QueryToken.Text or QueryToken.Number:
                    throw FreeText();
                case QueryToken.Pipe:
                    throw Fail(TaskerErrors.QueryExpectedCondition(), token.Start, token.End);
                default:
                    throw Fail(TaskerErrors.QueryExpectedCondition(), token.Start, token.End);
            }
        }
        finally
        {
            _depth--;
        }
    }

    private QuerySegment[] Path()
    {
        var segments = new List<QuerySegment>();
        while (Is(QueryToken.Dot))
        {
            var dot = Current!.Value;
            _at++;
            if (Current is not { Kind: QueryToken.Word or QueryToken.Text } name)
                throw Fail(TaskerErrors.QueryExpectedProperty(), dot.Start, dot.End);
            segments.Add(new QuerySegment(name.Value, name.Start, name.End));
            _at++;
        }
        return [.. segments];
    }

    private QueryNode Condition()
    {
        var start = Current!.Value.Start;
        var path = Path();

        if (Is(QueryToken.OpenParen))
        {
            var open = Current!.Value;
            _at++;
            QueryValue? argument = null;
            if (!Is(QueryToken.CloseParen))
                argument = Value();
            if (!Is(QueryToken.CloseParen))
                throw Fail(TaskerErrors.QueryUnclosedParen(), open.Start, open.End);
            _at++;
            return new QueryCall(path[..^1], path[^1], argument);
        }

        if (Current is not { } op)
            throw Fail(TaskerErrors.QueryExpectedOperator(_text[start..path[^1].End]), start, path[^1].End);

        QueryComparison? comparison = op.Kind switch
        {
            QueryToken.Equal => QueryComparison.Equal,
            QueryToken.NotEqual => QueryComparison.NotEqual,
            QueryToken.Greater => QueryComparison.Greater,
            QueryToken.GreaterOrEqual => QueryComparison.GreaterOrEqual,
            QueryToken.Less => QueryComparison.Less,
            QueryToken.LessOrEqual => QueryComparison.LessOrEqual,
            _ => null
        };
        if (comparison is not { } known)
            throw Fail(TaskerErrors.QueryExpectedOperator(_text[start..path[^1].End]), op.Start, op.End);
        _at++;
        return new QueryCompare(path, known, op.Value, op.Start, Value());
    }

    private QueryValue Value()
    {
        if (Current is not { } token)
            throw FailAtEnd(TaskerErrors.QueryExpectedValue());

        if (token.Kind != QueryToken.OpenBracket)
            return Scalar();

        _at++;
        var items = new List<QueryScalar>();
        while (!Is(QueryToken.CloseBracket))
        {
            if (Current == null)
                throw Fail(TaskerErrors.QueryUnclosedBracket(), token.Start, token.End);
            items.Add(Scalar());
            if (Is(QueryToken.Comma))
            {
                _at++;
                continue;
            }
            if (!Is(QueryToken.CloseBracket))
                throw Current is { } other ? Unexpected(other) : Fail(TaskerErrors.QueryUnclosedBracket(), token.Start, token.End);
        }
        var close = Current!.Value;
        _at++;
        if (items.Count == 0)
            throw Fail(TaskerErrors.QueryEmptyArray(), token.Start, close.End);
        return new QueryArray([.. items], token.Start, close.End);
    }

    private QueryScalar Scalar()
    {
        if (Current is not { } token)
            throw FailAtEnd(TaskerErrors.QueryExpectedValue());
        _at++;
        switch (token.Kind)
        {
            case QueryToken.Text:
                return new QueryScalar(token.Value, token.Start, token.End);
            case QueryToken.Number:
                return new QueryScalar(token.Value, token.Start, token.End, IsNumber: true);
            case QueryToken.Word when token.Value.Equals("true", StringComparison.OrdinalIgnoreCase) || token.Value.Equals("false", StringComparison.OrdinalIgnoreCase):
                return new QueryScalar(token.Value.ToLowerInvariant(), token.Start, token.End, IsBool: true);
            case QueryToken.Word:
                throw Fail(TaskerErrors.QueryValueNeedsQuotes(token.Value), token.Start, token.End);
            default:
                throw Fail(TaskerErrors.QueryExpectedValue(), token.Start, token.End);
        }
    }

    private QuerySortKey[] Sort()
    {
        var pipe = Current!.Value;
        _at++;
        if (!IsSortWord(_at))
            throw Fail(TaskerErrors.QueryExpectedSort(), pipe.Start, pipe.End);
        var word = Current!.Value;
        _at++;

        var keys = new List<QuerySortKey>();
        while (true)
        {
            if (Current is not { } token)
                throw Fail(TaskerErrors.QueryExpectedSortKey(), word.Start, word.End);
            var descending = token.Kind == QueryToken.Minus;
            if (descending)
                _at++;
            if (!Is(QueryToken.Dot))
                throw Fail(TaskerErrors.QueryExpectedSortKey(), Current?.Start ?? token.Start, Current?.End ?? token.End);
            var path = Path();
            keys.Add(new QuerySortKey(path, descending, token.Start, path[^1].End));
            if (!Is(QueryToken.Comma))
                break;
            word = Current!.Value;
            _at++;
        }
        return [.. keys];
    }
}
