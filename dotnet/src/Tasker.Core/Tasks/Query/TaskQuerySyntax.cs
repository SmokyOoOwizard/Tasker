namespace Tasker.Core.Tasks.Query;

public sealed record TaskQueryDiagnostic(TaskerValidationException Failure, int Start, int End)
{
    public TaskerError Error => Failure.Error;
}

internal enum QueryComparison { Equal, NotEqual, Greater, GreaterOrEqual, Less, LessOrEqual }

internal readonly record struct QuerySegment(string Text, int Start, int End);

internal abstract record QueryNode;

internal sealed record QueryAll(QueryNode[] Parts) : QueryNode;

internal sealed record QueryAny(QueryNode[] Parts) : QueryNode;

internal sealed record QueryNegation(QueryNode Inner) : QueryNode;

internal sealed record QueryCompare(QuerySegment[] Path, QueryComparison Operator, string OperatorText, int OperatorStart, QueryValue Value) : QueryNode;

internal sealed record QueryCall(QuerySegment[] Path, QuerySegment Name, QueryValue? Argument) : QueryNode;

internal abstract record QueryValue(int Start, int End);

internal sealed record QueryScalar(string Text, int Start, int End, bool IsNumber = false, bool IsBool = false) : QueryValue(Start, End);

internal sealed record QueryArray(QueryScalar[] Items, int Start, int End) : QueryValue(Start, End);

internal sealed record QuerySortKey(QuerySegment[] Path, bool Descending, int Start, int End);

internal sealed record QuerySyntax(QueryNode? Filter, QuerySortKey[] Sort);
