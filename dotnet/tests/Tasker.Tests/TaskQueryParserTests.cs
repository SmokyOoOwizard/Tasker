using Tasker.Core.Fields;
using Tasker.Core.Tasks;
using Tasker.Core.Tasks.Query;
using Xunit;

namespace Tasker.Tests;

public class TaskQueryParserTests
{
    private static readonly Guid Todo = Guid.NewGuid();
    private static readonly Guid Done = Guid.NewGuid();
    private static readonly Guid Bug = Guid.NewGuid();
    private static readonly Guid Tsk = Guid.NewGuid();
    private static readonly Guid Web = Guid.NewGuid();
    private static readonly Guid Blocks = Guid.NewGuid();
    private static readonly Guid Parent = Guid.NewGuid();
    private static readonly Guid Estimate = Guid.NewGuid();
    private static readonly Guid Labels = Guid.NewGuid();
    private static readonly Guid Spaced = Guid.NewGuid();
    private static readonly Guid Flag = Guid.NewGuid();
    private static readonly Guid Level = Guid.NewGuid();
    private static readonly Guid Low = Guid.NewGuid();

    private static readonly TaskQueryVocabulary Vocabulary = new(
        [new QueryNamedItem(Todo, "Todo"), new QueryNamedItem(Done, "Done")],
        [new QueryNamedItem(Bug, "Bug")],
        [new QueryNamedItem(Tsk, "TSK"), new QueryNamedItem(Web, "WEB")],
        [
            new QueryField("Estimate", FieldType.Int, false, Estimate, null),
            new QueryField("Метки", FieldType.String, true, Labels, null),
            new QueryField("Тестовое поле", FieldType.Int, false, Spaced, null),
            new QueryField("Flag", FieldType.Bool, false, Flag, null),
            new QueryField("Level", FieldType.Enum, false, Level, new FieldEnum
            {
                Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Name = "L", Values = [new FieldEnumValue(Low, "Low")], Version = "1"
            })
        ],
        [new QueryLinkType(Blocks, "Blocks", "blocks", "is blocked by", false), new QueryLinkType(Parent, "Parent/Child", "includes", "is part of", true)],
        new Dictionary<Guid, Guid[]> { [Estimate] = [Bug] });

    private static (TaskPredicate? Predicate, (string? Builtin, QueryField? Field, bool Descending)[] Sort, IReadOnlyList<TaskQueryDiagnostic> Errors) Bind(string text)
    {
        var (syntax, error) = TaskQueryParser.Parse(text);
        if (error != null)
            return (null, [], [error]);
        var binder = new TaskQueryBinder(Vocabulary);
        var predicate = binder.Bind(syntax!.Filter);
        var sort = binder.BindSort(syntax.Sort);
        return (predicate, sort, binder.Errors);
    }

    private static TaskPredicate Ok(string text)
    {
        var (predicate, _, errors) = Bind(text);
        Assert.True(errors.Count == 0, errors.Count > 0 ? errors[0].Error.Message : "");
        return predicate!;
    }

    private static TaskQueryDiagnostic Fails(string text)
    {
        var (_, _, errors) = Bind(text);
        Assert.NotEmpty(errors);
        return errors[0];
    }

    private static TaskItem Task(Guid? status = null, (Guid Id, string[] Values)[]? fields = null, Guid[]? series = null) => new()
    {
        Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Title = "Логин падает", Description = "", TypeId = Bug, StatusId = status ?? Todo,
        Fields = (fields ?? []).Select(x => new TaskField(x.Id, x.Values)).ToArray(),
        SeriesNumbers = (series ?? []).Select((x, i) => new Tasker.Core.TaskSeries.TaskSeriesNumber(x, i + 1)).ToArray(),
        Links = [], CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch, Version = "1"
    };

    [Fact]
    public void Or_and_and_keep_their_precedence_and_parentheses_change_it()
    {
        var plain = Ok(".Status == \"Todo\" || .Status == \"Done\" && .Type == \"Bug\"");
        Assert.IsType<TaskAnyOf>(plain);
        var grouped = Ok("(.Status == \"Todo\" || .Status == \"Done\") && .Type == \"Bug\"");
        Assert.IsType<TaskAllOf>(grouped);
    }

    [Fact]
    public void An_array_on_a_single_value_means_one_of_and_not_equal_means_none_of()
    {
        var any = Ok(".Status == [\"Todo\", \"Done\"]");
        Assert.True(any.Matches(Task(Done)));
        var none = Ok(".Status != [\"Todo\", \"Done\"]");
        Assert.False(none.Matches(Task(Done)));
        Assert.True(none.Matches(Task(Guid.NewGuid())));
    }

    [Fact]
    public void Equality_on_a_multiple_field_means_only_these_values_in_any_order()
    {
        var one = Ok(".Field.Метки == \"bug\"");
        Assert.True(one.Matches(Task(fields: [(Labels, ["bug"])])));
        Assert.False(one.Matches(Task(fields: [(Labels, ["bug", "ui"])])));

        var both = Ok(".Field.Метки == [\"bug\", \"ui\"]");
        Assert.True(both.Matches(Task(fields: [(Labels, ["ui", "bug"])])));
        Assert.False(both.Matches(Task(fields: [(Labels, ["bug"])])));
        Assert.False(both.Matches(Task(fields: [(Labels, ["bug", "ui", "docs"])])));

        var not = Ok(".Field.Метки != \"bug\"");
        Assert.True(not.Matches(Task(fields: [(Labels, ["bug", "ui"])])));
        Assert.False(not.Matches(Task(fields: [(Labels, ["bug"])])));
    }

    [Fact]
    public void Contains_family_looks_for_values_among_the_others()
    {
        var task = Task(fields: [(Labels, ["bug", "ui"])]);
        Assert.True(Ok(".Field.Метки.Contains(\"bug\")").Matches(task));
        Assert.False(Ok(".Field.Метки.Contains(\"docs\")").Matches(task));
        Assert.True(Ok(".Field.Метки.ContainsAny([\"docs\", \"ui\"])").Matches(task));
        Assert.False(Ok(".Field.Метки.ContainsAll([\"docs\", \"ui\"])").Matches(task));
        Assert.True(Ok(".Field.Метки.ContainsAll([\"bug\", \"ui\"])").Matches(task));
    }

    [Fact]
    public void Series_contains_finds_a_number_and_equal_means_only_this_series()
    {
        var both = Task(series: [Tsk, Web]);
        var one = Task(series: [Tsk]);
        Assert.True(Ok(".Series.Contains(\"TSK\")").Matches(both));
        Assert.False(Ok(".Series == \"TSK\"").Matches(both));
        Assert.True(Ok(".Series == \"TSK\"").Matches(one));
        Assert.True(Ok(".Series == [\"WEB\", \"TSK\"]").Matches(both));
        Assert.True(Ok(".Series != \"WEB\"").Matches(both));
        Assert.True(Ok(".Series.IsSet()").Matches(one));
        Assert.True(Ok(".Series.IsUnset()").Matches(Task()));
    }

    [Fact]
    public void Checks_negation_and_quoted_names_work()
    {
        var task = Task(fields: [(Estimate, ["3"])]);
        Assert.True(Ok(".Field.Estimate.IsSet()").Matches(task));
        Assert.False(Ok(".Field.Estimate.IsUnset()").Matches(task));
        Assert.True(Ok(".Field.Estimate.IsAttached()").Matches(Task()));
        Assert.False(Ok(".Field.Estimate.IsDetached()").Matches(Task()));
        Assert.False(Ok("!.Field.Estimate.IsSet()").Matches(task));
        Assert.True(Ok("!(.Status == \"Done\" || .Field.Estimate > 5)").Matches(task));
        Assert.True(Ok(".Field.\"Тестовое поле\" == 2").Matches(Task(fields: [(Spaced, ["2"])])));
        Assert.True(Ok(".Field.Flag == true").Matches(Task(fields: [(Flag, ["true"])])));
        Assert.True(Ok(".Field.Level == \"low\"").Matches(Task(fields: [(Level, [Low.ToString("D")])])));
        Assert.True(Ok(".Field.Estimate >= 3").Matches(task));
    }

    [Fact]
    public void Title_contains_ignores_case()
    {
        Assert.True(Ok(".Title.Contains(\"ЛОГИН\")").Matches(Task()));
        Assert.True(Ok(".Title == \"логин падает\"").Matches(Task()));
    }

    [Fact]
    public void Link_and_parent_checks_use_the_right_side_of_the_link()
    {
        var outgoing = Assert.IsType<TaskHasOutgoingLink>(Ok(".Link.Blocks.IsSet()"));
        Assert.Equal([Blocks], outgoing.TypeIds);
        var inward = Assert.IsType<TaskHasIncomingLink>(Ok(".Link.\"is blocked by\".IsSet()"));
        Assert.Equal([Blocks], inward.TypeIds);
        var parent = Assert.IsType<TaskHasIncomingLink>(Ok(".Parent.IsSet()"));
        Assert.Equal([Parent], parent.TypeIds);
        Assert.IsType<TaskNot>(Ok(".Parent.IsUnset()"));
    }

    [Fact]
    public void Sort_reads_keys_with_a_minus_for_descending()
    {
        var (_, sort, errors) = Bind(".Status == \"Todo\" | sort .Status, -.Updated, -.Field.Estimate");
        Assert.Empty(errors);
        Assert.Equal(3, sort.Length);
        Assert.Equal(("status", false), (sort[0].Builtin, sort[0].Descending));
        Assert.Equal(("updated", true), (sort[1].Builtin, sort[1].Descending));
        Assert.Equal("Estimate", sort[2].Field!.Name);

        var (predicate, only, none) = Bind("| sort .Title");
        Assert.Null(predicate);
        Assert.Single(only);
        Assert.Empty(none);
    }

    [Theory]
    [InlineData("логин", "query.freeText", 0, 5)]
    [InlineData(".Status == \"Todo\" логин", "query.freeText", 18, 23)]
    [InlineData(".Status", "query.expectedOperator", 0, 7)]
    [InlineData(".Status ==", "query.expectedValue", 10, 10)]
    [InlineData(".Status == Todo", "query.valueNeedsQuotes", 11, 15)]
    [InlineData(".Status == \"Todo", "query.unclosedString", 11, 16)]
    [InlineData("(.Status == \"Todo\"", "query.unclosedParen", 0, 1)]
    [InlineData(".Status == [\"Todo\"", "query.unclosedBracket", 11, 12)]
    [InlineData(".Status == []", "query.emptyArray", 11, 13)]
    [InlineData(".Status == \"Todo\" &&", "query.expectedCondition", 20, 20)]
    [InlineData(".Status == \"Todo\" | .Type", "query.expectedSort", 18, 19)]
    [InlineData("| sort", "query.expectedSortKey", 2, 6)]
    [InlineData(".", "query.expectedProperty", 0, 1)]
    [InlineData(".Nope == 1", "query.unknownProperty", 1, 5)]
    [InlineData(".Status == \"Nope\"", "query.unknownStatus", 11, 17)]
    [InlineData(".Type == \"Nope\"", "query.unknownType", 9, 15)]
    [InlineData(".Series == \"NOPE\"", "query.unknownSeries", 11, 17)]
    [InlineData(".Field.Nope == 1", "query.unknownField", 7, 11)]
    [InlineData(".Link.Nope.IsSet()", "query.unknownLinkType", 6, 10)]
    [InlineData(".Field.Level == \"Nope\"", "query.unknownEnumValue", 16, 22)]
    [InlineData(".Field.Estimate == \"abc\"", "query.badValue", 19, 24)]
    [InlineData(".Status > \"Todo\"", "query.operatorNotAllowed", 8, 9)]
    [InlineData(".Field.Метки > \"a\"", "query.operatorNotAllowed", 13, 14)]
    [InlineData(".Field.Estimate > [1, 2]", "query.arrayNotAllowed", 18, 24)]
    [InlineData(".Status.Contains(\"a\")", "query.unknownFunction", 8, 16)]
    [InlineData(".Field.Estimate.Contains(\"1\")", "query.unknownFunction", 16, 24)]
    [InlineData(".Field.Estimate.IsSet(1)", "query.functionArgument", 22, 23)]
    [InlineData(".Field.Метки.ContainsAny(\"a\")", "query.functionArgument", 13, 24)]
    [InlineData(".Created > 1", "query.badDate", 11, 12)]
    [InlineData("| sort .Title, .Title", "query.sortRepeated", 15, 21)]
    [InlineData("| sort .Link.Blocks", "query.notSortable", 7, 19)]
    [InlineData(".Parent == 1", "query.operatorNotAllowed", 8, 10)]
    public void Errors_have_a_code_and_a_range(string text, string code, int start, int end)
    {
        var error = Fails(text);
        Assert.Equal(code, error.Error.Code);
        Assert.Equal((start, end), (error.Start, error.End));
    }

    [Fact]
    public void Too_deep_and_too_long_queries_are_refused()
    {
        Assert.Equal("query.tooComplex", Fails(new string('(', 100) + ".Status == \"Todo\"").Error.Code);
        Assert.Equal("query.tooComplex", Fails(new string(' ', TaskQueryParser.MaxLength + 1)).Error.Code);
    }

    [Fact]
    public void Several_meaning_errors_are_reported_at_once()
    {
        var (_, _, errors) = Bind(".Status == \"A\" && .Type == \"B\"");
        Assert.Equal(["query.unknownStatus", "query.unknownType"], errors.Select(x => x.Error.Code));
    }

    private static string[] Labels_(string text, int? cursor = null) =>
        TaskQuerySuggester.Suggest(text, cursor ?? text.Length, Vocabulary).Select(x => x.Label).ToArray();

    private static TaskQuerySuggestion One(string text, string label) =>
        TaskQuerySuggester.Suggest(text, text.Length, Vocabulary).First(x => x.Label == label);

    private static string Apply(string text, TaskQuerySuggestion item) => text[..item.Start] + item.Insert + text[item.End..];

    [Fact]
    public void Empty_text_suggests_the_properties()
    {
        Assert.Equal([".Status", ".Type", ".Title", ".Series", ".Created", ".Updated", ".Field", ".Link", ".Parent"], Labels_(""));
        Assert.Equal([".Status"], Labels_(".Sta"));
    }

    [Fact]
    public void Text_without_a_dot_suggests_title_contains()
    {
        Assert.Contains(".Title.Contains(\"логин\")", Labels_("логин"));
        var item = One("логин", ".Title.Contains(\"логин\")");
        Assert.Equal(".Title.Contains(\"логин\") ", Apply("логин", item));
    }

    [Fact]
    public void Fields_and_link_types_come_after_their_prefix()
    {
        Assert.Contains("Estimate", Labels_(".Field."));
        Assert.Contains("Тестовое поле", Labels_(".Field.Тест"));
        Assert.Equal(".Field.\"Тестовое поле\" ", Apply(".Field.Тест", One(".Field.Тест", "Тестовое поле")));
        Assert.Equal(["Blocks", "Parent/Child"], Labels_(".Link."));
        Assert.Equal(".Link.\"Parent/Child\".", Apply(".Link.", One(".Link.", "Parent/Child")));
    }

    [Fact]
    public void Functions_depend_on_the_target()
    {
        Assert.Equal(["IsSet()", "IsUnset()", "IsAttached()", "IsDetached()"], Labels_(".Field.Estimate."));
        Assert.Contains("ContainsAny(…)", Labels_(".Field.Метки."));
        Assert.Equal(["Contains(…)"], Labels_(".Title."));
        Assert.Equal(["IsSet()", "IsUnset()"], Labels_(".Parent."));
        Assert.Equal(["IsSet()", "IsUnset()"], Labels_(".Link.Blocks."));

        var contains = One(".Title.", "Contains(…)");
        Assert.Equal(".Title.Contains(\"\") ", Apply(".Title.", contains));
        Assert.Equal(".Title.Contains(\"".Length, contains.Start + contains.Caret);
    }

    [Fact]
    public void Operators_depend_on_the_value_type()
    {
        Assert.Equal(["==", "!="], Labels_(".Status "));
        Assert.Equal(["==", "!=", ">", ">=", "<", "<=", ".IsSet()", ".IsUnset()", ".IsAttached()", ".IsDetached()"], Labels_(".Field.Estimate "));
    }

    [Fact]
    public void Values_come_from_the_project_and_replace_an_open_quote()
    {
        Assert.Equal(["Todo", "Done"], Labels_(".Status == "));
        Assert.Equal(["Todo"], Labels_(".Status == \"to"));
        Assert.Equal(".Status == \"Todo\" ", Apply(".Status == \"to", One(".Status == \"to", "Todo")));
        Assert.Equal(["Bug"], Labels_(".Type != "));
        Assert.Equal(["TSK", "WEB"], Labels_(".Series.Contains(\""));
        Assert.Equal(["Low"], Labels_(".Field.Level == "));
        Assert.Equal(["true", "false"], Labels_(".Field.Flag == "));
    }

    [Fact]
    public void Used_array_values_are_not_suggested_twice()
    {
        Assert.Equal(["Done"], Labels_(".Status == [\"Todo\", "));
        Assert.Equal([",", "]"], Labels_(".Status == [\"Todo\" "));
    }

    [Fact]
    public void After_a_condition_come_the_logic_and_sort()
    {
        Assert.Equal(["&&", "||", "| sort"], Labels_(".Status == \"Todo\" "));
        Assert.Contains(")", Labels_("(.Status == \"Todo\" "));
        Assert.Equal(["&&", "||", "| sort"], Labels_(".Field.Метки.IsSet() "));
    }

    [Fact]
    public void Sort_suggests_its_word_and_its_keys()
    {
        Assert.Equal(["sort"], Labels_(".Status == \"Todo\" | "));
        var keys = Labels_(".Status == \"Todo\" | sort ");
        Assert.Contains(".Updated", keys);
        Assert.Contains(".Created", keys);
        Assert.DoesNotContain(".Link", keys);
        Assert.Contains(".Updated", Labels_("| sort -."));
        Assert.Equal([","], Labels_("| sort .Title "));
        Assert.Contains(".Status", Labels_("| sort .Title, "));
    }

    [Fact]
    public void The_cursor_in_the_middle_uses_only_the_text_before_it()
    {
        var text = ".Sta == \"Todo\"";
        var item = TaskQuerySuggester.Suggest(text, 3, Vocabulary).Single();
        Assert.Equal(".Status ", item.Label + " ");
        Assert.Equal(1, item.Start);
        Assert.Equal(4, item.End);
    }

    [Fact]
    public void A_hint_that_changes_nothing_is_not_offered()
    {
        Assert.Empty(Labels_("| sort -.Updated"));
        Assert.Equal([".Status"], Labels_(".Status"));
    }

    [Fact]
    public void Accepting_suggestions_builds_a_valid_query()
    {
        var text = "";
        foreach (var label in new[] { ".Status", "==", "Todo", "&&", ".Field" })
            text = Apply(text, One(text, label));
        foreach (var label in new[] { "Метки" })
            text = Apply(text, One(text, label));
        text = Apply(text, One(text, ".ContainsAny(…)"));
        Assert.Equal(".Status == \"Todo\" && .Field.Метки .ContainsAny([\"\"]) ", text);
    }
}
