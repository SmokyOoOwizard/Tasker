using Tasker.Core.Tasks.Query;

namespace Tasker.Core.Tasks;

public sealed record TaskQueryParam(string Name, string Value);

public sealed record TaskQueryError(string Code, string Message, TaskQueryParam[] Params, int Start, int End);

public sealed record TaskQueryAnalysis(bool Valid, bool HasSort, TaskQueryError[] Errors, TaskQuerySuggestion[] Suggestions);

public sealed record ParsedTaskQuery(TaskPredicate? Predicate, TaskSortKey[]? Sort);

public partial class TaskService
{
    public async Task<ParsedTaskQuery?> ParseQuery(Guid projectId, string? text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var (syntax, error) = TaskQueryParser.Parse(text);
        if (error != null)
            throw error.Failure;

        var vocabulary = await QueryVocabulary(projectId, syntax!, ct);
        var binder = new TaskQueryBinder(vocabulary);
        var predicate = binder.Bind(syntax!.Filter);
        var sort = binder.BindSort(syntax.Sort);
        if (binder.Errors.Count > 0)
            throw binder.Errors[0].Failure;
        return new ParsedTaskQuery(predicate, sort.Length == 0 ? null : await QuerySortKeys(projectId, sort, ct));
    }

    public async Task<TaskQueryAnalysis> AnalyzeQuery(Guid projectId, string text, int? cursor, CancellationToken ct = default)
    {
        var (syntax, error) = TaskQueryParser.Parse(text);
        var vocabulary = await QueryVocabulary(projectId, syntax, ct);
        var suggestions = TaskQuerySuggester.Suggest(text, cursor ?? text.Length, vocabulary);
        if (error != null)
            return new TaskQueryAnalysis(false, false, [Wire(error)], suggestions);

        var binder = new TaskQueryBinder(vocabulary);
        binder.Bind(syntax!.Filter);
        binder.BindSort(syntax.Sort);
        return new TaskQueryAnalysis(binder.Errors.Count == 0, syntax.Sort.Length > 0, [.. binder.Errors.Select(Wire)], suggestions);
    }

    private static TaskQueryError Wire(TaskQueryDiagnostic diagnostic) =>
        new(diagnostic.Error.Code, diagnostic.Error.Message,
            diagnostic.Error.Params.Select(x => new TaskQueryParam(x.Key, x.Value?.ToString() ?? "")).ToArray(), diagnostic.Start, diagnostic.End);

    private async Task<TaskQueryVocabulary> QueryVocabulary(Guid projectId, QuerySyntax? syntax, CancellationToken ct)
    {
        var enumerations = (await enums.GetAll(projectId, ct)).ToDictionary(x => x.Id);
        var catalog = await fields.GetAll(projectId, ct);
        var allTypes = await types.GetAll(projectId, ct);
        var vocabulary = new TaskQueryVocabulary(
            (await statuses.GetAll(projectId, ct)).Select(x => new QueryNamedItem(x.Id, x.Name)).ToArray(),
            allTypes.Select(x => new QueryNamedItem(x.Id, x.Name)).ToArray(),
            (await seriesStorage.GetAll(projectId, ct)).Select(x => new QueryNamedItem(x.Id, x.Prefix)).ToArray(),
            catalog.Select(x => new QueryField(x.Name, x.Type, x.Multiple, x.Id, x.EnumId is { } id ? enumerations.GetValueOrDefault(id) : null)).ToArray(),
            (await links.LinkTypes(projectId, ct)).Select(x => new QueryLinkType(x.Id, x.Name, x.OutwardName, x.InwardName, x.Hierarchical && !x.IsSymmetric)).ToArray(),
            catalog.ToDictionary(x => x.Id, x => allTypes.Where(t => t.Fields.Any(f => f.FieldId == x.Id)).Select(t => t.Id).ToArray()));
        if (syntax == null)
            return vocabulary;

        var own = new Dictionary<string, OwnFieldChoice>();
        foreach (var name in TaskQueryBinder.OwnFieldNames(syntax, vocabulary))
        {
            var kinds = await tasks.GetOwnFieldKinds(projectId, FieldNames.Key(name), ct);
            if (kinds.Length == 1)
                own[FieldNames.Key(name)] = new OwnFieldChoice(
                    new QueryField(name, kinds[0].Type, true, null, kinds[0].EnumId is { } id ? enumerations.GetValueOrDefault(id) : null), false);
            else if (kinds.Length > 1)
                own[FieldNames.Key(name)] = new OwnFieldChoice(null, true);
        }
        return vocabulary with { OwnFields = own };
    }

    private async Task<TaskSortKey[]> QuerySortKeys(Guid projectId, (string? Builtin, QueryField? Field, bool Descending)[] items, CancellationToken ct)
    {
        var catalog = await fields.GetAll(projectId, ct);
        var result = new List<TaskSortKey>();
        foreach (var (builtin, field, descending) in items)
        {
            if (builtin != null)
                result.Add(await BuiltinKey(projectId, TaskSortNames.Builtin[builtin], descending, builtin, ct));
            else
                result.Add(await FieldKey(projectId, field!.Name, field.Name, descending, catalog, ct));
        }
        return [.. result];
    }

    private static TaskPredicate? Combine(TaskPredicate? first, TaskPredicate? second) =>
        first == null ? second : second == null ? first : new TaskAllOf([first, second]);
}
