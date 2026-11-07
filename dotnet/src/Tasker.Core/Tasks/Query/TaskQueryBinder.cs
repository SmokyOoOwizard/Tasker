using Tasker.Core.Fields;

namespace Tasker.Core.Tasks.Query;

internal sealed class TaskQueryBinder(TaskQueryVocabulary vocabulary)
{
    private readonly List<TaskQueryDiagnostic> _errors = [];

    public IReadOnlyList<TaskQueryDiagnostic> Errors => _errors;

    public static IEnumerable<string> OwnFieldNames(QuerySyntax syntax, TaskQueryVocabulary vocabulary)
    {
        var names = new List<string>();
        Walk(syntax.Filter);
        foreach (var key in syntax.Sort)
            Take(key.Path);
        return names.Where(name => !vocabulary.Fields.Any(x => Same(x.Name, name))).Distinct(StringComparer.OrdinalIgnoreCase);

        void Walk(QueryNode? node)
        {
            switch (node)
            {
                case QueryAll all:
                    foreach (var part in all.Parts)
                        Walk(part);
                    break;
                case QueryAny any:
                    foreach (var part in any.Parts)
                        Walk(part);
                    break;
                case QueryNegation not:
                    Walk(not.Inner);
                    break;
                case QueryCompare compare:
                    Take(compare.Path);
                    break;
                case QueryCall call:
                    Take(call.Path);
                    break;
            }
        }

        void Take(QuerySegment[] path)
        {
            if (path.Length == 2 && Same(path[0].Text, "Field"))
                names.Add(path[1].Text);
        }
    }

    public TaskPredicate? Bind(QueryNode? node) => node switch
    {
        null => null,
        QueryAll all => Combine(all.Parts, x => new TaskAllOf(x)),
        QueryAny any => Combine(any.Parts, x => new TaskAnyOf(x)),
        QueryNegation not => Bind(not.Inner) is { } inner ? new TaskNot(inner) : null,
        QueryCompare compare => BindCompare(compare),
        QueryCall call => BindCall(call),
        _ => null
    };

    public (string? Builtin, QueryField? Field, bool Descending)[] BindSort(QuerySortKey[] keys)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string?, QueryField?, bool)>();
        foreach (var key in keys)
        {
            var path = key.Path;
            var text = "." + string.Join(".", path.Select(x => x.Text));
            if (!seen.Add(text))
            {
                Error(TaskerErrors.QuerySortRepeated(text), key.Start, key.End);
                continue;
            }

            if (path.Length == 1 && TaskSortNames.Builtin.ContainsKey(path[0].Text))
            {
                result.Add((path[0].Text.ToLowerInvariant(), null, key.Descending));
                continue;
            }

            if (path.Length == 2 && Same(path[0].Text, "Field"))
            {
                if (Field(path[1]) is { } field)
                    result.Add((null, field, key.Descending));
                continue;
            }
            Error(TaskerErrors.QueryNotSortable(text), key.Start, key.End);
        }
        return [.. result];
    }

    private TaskPredicate? Combine(QueryNode[] parts, Func<TaskPredicate[], TaskPredicate> make)
    {
        var bound = parts.Select(Bind).ToArray();
        return bound.Any(x => x == null) ? null : make(bound!);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void Error(TaskerValidationException error, int start, int end) => _errors.Add(new TaskQueryDiagnostic(error, start, end));

    private TaskPredicate? BindCompare(QueryCompare node)
    {
        var path = node.Path;
        var name = path[0];
        var label = Label(path);
        if (path.Length == 1 && Same(name.Text, "Status"))
            return Choice(node, label, vocabulary.Statuses, TaskerErrors.QueryUnknownStatus, ids => new TaskStatusIn(ids));
        if (path.Length == 1 && Same(name.Text, "Type"))
            return Choice(node, label, vocabulary.Types, TaskerErrors.QueryUnknownType, ids => new TaskTypeIn(ids));
        if (path.Length == 1 && Same(name.Text, "Title"))
            return Title(node, label);
        if (path.Length == 1 && Same(name.Text, "Series"))
            return SeriesCompare(node, label);
        if (path.Length == 2 && Same(name.Text, "Field"))
            return Field(path[1]) is { } field ? FieldCompare(node, field, label) : null;
        if (path.Length == 1 && (Same(name.Text, "Created") || Same(name.Text, "Updated")))
        {
            Error(TaskerErrors.QuerySortOnly(label), name.Start, name.End);
            return null;
        }
        if (IsLinkPath(path) || (path.Length == 1 && Same(name.Text, "Parent")))
        {
            Error(TaskerErrors.QueryOperatorNotAllowed(node.OperatorText, label), node.OperatorStart, node.OperatorStart + node.OperatorText.Length);
            return null;
        }
        Error(TaskerErrors.QueryUnknownProperty(label), name.Start, path[^1].End);
        return null;
    }

    private static string Label(QuerySegment[] path) => "." + string.Join(".", path.Select(x => x.Text.Contains(' ') ? $"\"{x.Text}\"" : x.Text));

    private static bool IsLinkPath(QuerySegment[] path) => path.Length == 2 && Same(path[0].Text, "Link");

    private bool OnlyEquality(QueryCompare node, string label)
    {
        if (node.Operator is QueryComparison.Equal or QueryComparison.NotEqual)
            return true;
        Error(TaskerErrors.QueryOperatorNotAllowed(node.OperatorText, label), node.OperatorStart, node.OperatorStart + node.OperatorText.Length);
        return false;
    }

    private static TaskPredicate Signed(QueryCompare node, TaskPredicate equal) =>
        node.Operator == QueryComparison.NotEqual ? new TaskNot(equal) : equal;

    private static QueryScalar[] Scalars(QueryValue value) => value is QueryArray array ? array.Items : [(QueryScalar)value];

    private TaskPredicate? Choice(
        QueryCompare node, string label, QueryNamedItem[] items, Func<string, TaskerValidationException> unknown, Func<Guid[], TaskPredicate> make)
    {
        if (!OnlyEquality(node, label))
            return null;
        var ids = new List<Guid>();
        var failed = false;
        foreach (var scalar in Scalars(node.Value))
        {
            var found = items.Where(x => Same(x.Name, scalar.Text)).Select(x => x.Id).ToArray();
            if (found.Length == 0)
            {
                Error(unknown(scalar.Text), scalar.Start, scalar.End);
                failed = true;
            }
            ids.AddRange(found);
        }
        return failed ? null : Signed(node, make([.. ids.Distinct()]));
    }

    private TaskPredicate? Title(QueryCompare node, string label)
    {
        if (!OnlyEquality(node, label))
            return null;
        var parts = Scalars(node.Value).Select(x => (TaskPredicate)new TaskTitleEquals(TaskSortNames.Fold(x.Text))).ToArray();
        return Signed(node, parts.Length == 1 ? parts[0] : new TaskAnyOf(parts));
    }

    private Guid[]? SeriesIds(QueryScalar scalar)
    {
        var exact = vocabulary.Series.Where(x => x.Name == scalar.Text).Select(x => x.Id).ToArray();
        var found = exact.Length > 0 ? exact : vocabulary.Series.Where(x => Same(x.Name, scalar.Text)).Select(x => x.Id).ToArray();
        if (found.Length > 0)
            return found;
        Error(TaskerErrors.QueryUnknownSeries(scalar.Text), scalar.Start, scalar.End);
        return null;
    }

    private TaskPredicate? SeriesCompare(QueryCompare node, string label)
    {
        if (!OnlyEquality(node, label))
            return null;
        var resolved = Scalars(node.Value).Select(SeriesIds).ToArray();
        if (resolved.Any(x => x == null))
            return null;
        var all = resolved.SelectMany(x => x!).Distinct().ToArray();
        TaskPredicate[] parts = [.. resolved.Select(x => (TaskPredicate)new TaskInSeries(x!)), new TaskNot(new TaskInOtherSeries(all))];
        return Signed(node, new TaskAllOf(parts));
    }

    private QueryField? Field(QuerySegment name)
    {
        if (vocabulary.Fields.FirstOrDefault(x => Same(x.Name, name.Text)) is { } catalog)
            return catalog;
        if (vocabulary.OwnFields.TryGetValue(FieldNames.Key(name.Text), out var own))
        {
            if (own.Field != null)
                return own.Field;
            if (own.Ambiguous)
            {
                Error(TaskerErrors.QueryAmbiguousField(name.Text), name.Start, name.End);
                return null;
            }
        }
        Error(TaskerErrors.QueryUnknownField(name.Text), name.Start, name.End);
        return null;
    }

    private string? Canonical(QueryField field, QueryScalar scalar)
    {
        try
        {
            return FieldValues.Normalize(field.Name, field.Type, false, field.Enumeration, [scalar.Text])[0];
        }
        catch (TaskerValidationException)
        {
            Error(field.Enumeration != null ? TaskerErrors.QueryUnknownEnumValue(field.Name, scalar.Text) : TaskerErrors.QueryBadValue(field.Name, scalar.Text),
                scalar.Start, scalar.End);
            return null;
        }
    }

    private string[]? Canonicals(QueryField field, QueryValue value)
    {
        var result = Scalars(value).Select(x => Canonical(field, x)).ToArray();
        return result.Any(x => x == null) ? null : [.. result.Distinct()!];
    }

    private static FieldCondition Condition(QueryField field, FieldOperator op, string? value = null, double? number = null, Guid[]? typeIds = null) =>
        new(field.Name, field.Type, op, value, number, field.FieldId, typeIds);

    private TaskPredicate? FieldCompare(QueryCompare node, QueryField field, string label)
    {
        if (node.Operator is QueryComparison.Equal or QueryComparison.NotEqual)
        {
            if (Canonicals(field, node.Value) is not { } values)
                return null;
            return Signed(node, field.Multiple ? ExactValues(field, values) : AnyValue(field, values));
        }

        if (field.Type is not (FieldType.Int or FieldType.Float or FieldType.Date))
        {
            Error(TaskerErrors.QueryOperatorNotAllowed(node.OperatorText, label), node.OperatorStart, node.OperatorStart + node.OperatorText.Length);
            return null;
        }
        if (node.Value is not QueryScalar scalar)
        {
            Error(TaskerErrors.QueryArrayNotAllowed(node.OperatorText), node.Value.Start, node.Value.End);
            return null;
        }
        if (Canonical(field, scalar) is not { } canonical)
            return null;

        var op = node.Operator switch
        {
            QueryComparison.Greater => FieldOperator.Greater,
            QueryComparison.GreaterOrEqual => FieldOperator.GreaterOrEqual,
            QueryComparison.Less => FieldOperator.Less,
            _ => FieldOperator.LessOrEqual
        };
        double? number = field.Type is FieldType.Int or FieldType.Float ? FieldNumbers.Parse(canonical) : null;
        return new TaskFieldMatches(Condition(field, op, canonical, number));
    }

    private static TaskPredicate AnyValue(QueryField field, string[] values)
    {
        var parts = values.Select(x => (TaskPredicate)new TaskFieldMatches(Condition(field, FieldOperator.Equal, x))).ToArray();
        return parts.Length == 1 ? parts[0] : new TaskAnyOf(parts);
    }

    private static TaskPredicate ExactValues(QueryField field, string[] values) =>
        new TaskAllOf([
            .. values.Select(x => (TaskPredicate)new TaskFieldMatches(Condition(field, FieldOperator.Equal, x))),
            new TaskNot(new TaskFieldHasOther(Condition(field, FieldOperator.Set), values))
        ]);

    private TaskPredicate? BindCall(QueryCall node)
    {
        var path = node.Path;
        var function = node.Name;
        var label = Label(path);
        if (path.Length == 0)
        {
            Error(TaskerErrors.QueryUnknownProperty("." + function.Text), function.Start, function.End);
            return null;
        }

        if (path.Length == 2 && Same(path[0].Text, "Field"))
            return Field(path[1]) is { } field ? FieldCall(node, field, label) : null;
        if (path.Length == 1 && Same(path[0].Text, "Series"))
            return SeriesCall(node, label);
        if (path.Length == 1 && Same(path[0].Text, "Title"))
            return TitleCall(node, label);
        if (path.Length == 1 && Same(path[0].Text, "Parent"))
            return Presence(node, label, new TaskHasIncomingLink(vocabulary.LinkTypes.Where(x => x.Hierarchical).Select(x => x.Id).ToArray()));
        if (IsLinkPath(path))
            return LinkPredicate(path[1]) is { } link ? Presence(node, label, link) : null;
        if (path.Length == 1 && (Same(path[0].Text, "Status") || Same(path[0].Text, "Type")))
        {
            Error(TaskerErrors.QueryUnknownFunction(function.Text, label), function.Start, function.End);
            return null;
        }
        Error(TaskerErrors.QueryUnknownProperty(label), path[0].Start, path[^1].End);
        return null;
    }

    private TaskPredicate? Presence(QueryCall node, string label, TaskPredicate present)
    {
        var function = node.Name;
        if (!NoArgument(node))
            return null;
        if (Same(function.Text, "IsSet"))
            return present;
        if (Same(function.Text, "IsUnset"))
            return new TaskNot(present);
        Error(TaskerErrors.QueryUnknownFunction(function.Text, label), function.Start, function.End);
        return null;
    }

    private bool NoArgument(QueryCall node)
    {
        if (node.Argument == null || !(Same(node.Name.Text, "IsSet") || Same(node.Name.Text, "IsUnset") || Same(node.Name.Text, "IsAttached") || Same(node.Name.Text, "IsDetached")))
            return true;
        Error(TaskerErrors.QueryFunctionArgument(node.Name.Text), node.Argument.Start, node.Argument.End);
        return false;
    }

    private TaskPredicate? LinkPredicate(QuerySegment name)
    {
        var parts = new List<TaskPredicate>();
        var outward = vocabulary.LinkTypes.Where(x => Same(x.Name, name.Text) || Same(x.Outward, name.Text)).Select(x => x.Id).ToArray();
        var inward = vocabulary.LinkTypes.Where(x => Same(x.Inward, name.Text)).Select(x => x.Id).ToArray();
        if (outward.Length > 0)
            parts.Add(new TaskHasOutgoingLink(outward));
        if (inward.Length > 0)
            parts.Add(new TaskHasIncomingLink(inward));
        if (parts.Count == 0)
        {
            Error(TaskerErrors.QueryUnknownLinkType(name.Text), name.Start, name.End);
            return null;
        }
        return parts.Count == 1 ? parts[0] : new TaskAnyOf([.. parts]);
    }

    private TaskPredicate? TitleCall(QueryCall node, string label)
    {
        if (!Same(node.Name.Text, "Contains"))
        {
            Error(TaskerErrors.QueryUnknownFunction(node.Name.Text, label), node.Name.Start, node.Name.End);
            return null;
        }
        if (node.Argument is not QueryScalar scalar)
        {
            Error(TaskerErrors.QueryFunctionArgument(node.Name.Text), node.Name.Start, node.Name.End);
            return null;
        }
        return new TaskTitleContains(TaskSortNames.Fold(scalar.Text));
    }

    private TaskPredicate? ContainsFamily(QueryCall node, string label, Func<QueryScalar, TaskPredicate?> one)
    {
        var name = node.Name;
        var kind = Same(name.Text, "Contains") ? 0 : Same(name.Text, "ContainsAny") ? 1 : Same(name.Text, "ContainsAll") ? 2 : -1;
        if (kind < 0)
        {
            Error(TaskerErrors.QueryUnknownFunction(name.Text, label), name.Start, name.End);
            return null;
        }

        if (kind == 0)
        {
            if (node.Argument is QueryScalar scalar)
                return one(scalar);
            Error(TaskerErrors.QueryFunctionArgument(name.Text), name.Start, name.End);
            return null;
        }

        if (node.Argument is not QueryArray array)
        {
            Error(TaskerErrors.QueryFunctionArgument(name.Text), name.Start, name.End);
            return null;
        }
        var parts = array.Items.Select(one).ToArray();
        if (parts.Any(x => x == null))
            return null;
        return kind == 1 ? new TaskAnyOf(parts!) : new TaskAllOf(parts!);
    }

    private TaskPredicate? SeriesCall(QueryCall node, string label)
    {
        if (Same(node.Name.Text, "IsSet") || Same(node.Name.Text, "IsUnset"))
            return Presence(node, label, new TaskInOtherSeries([]));
        return ContainsFamily(node, label, scalar => SeriesIds(scalar) is { } ids ? new TaskInSeries(ids) : null);
    }

    private TaskPredicate? FieldCall(QueryCall node, QueryField field, string label)
    {
        var name = node.Name;
        if (Same(name.Text, "IsSet") || Same(name.Text, "IsUnset") || Same(name.Text, "IsAttached") || Same(name.Text, "IsDetached"))
        {
            if (!NoArgument(node))
                return null;
            var op = name.Text.ToLowerInvariant() switch
            {
                "isset" => FieldOperator.Set,
                "isunset" => FieldOperator.Unset,
                "isattached" => FieldOperator.Attached,
                _ => FieldOperator.Detached
            };
            Guid[]? typeIds = op is FieldOperator.Attached or FieldOperator.Detached && field.FieldId is { } id
                ? vocabulary.TypesOfField.GetValueOrDefault(id) ?? []
                : null;
            return new TaskFieldMatches(Condition(field, op, typeIds: typeIds));
        }

        if (!field.Multiple)
        {
            Error(TaskerErrors.QueryUnknownFunction(name.Text, label), name.Start, name.End);
            return null;
        }
        return ContainsFamily(node, label, scalar =>
            Canonical(field, scalar) is { } value ? new TaskFieldMatches(Condition(field, FieldOperator.Equal, value)) : null);
    }
}
