using Autofac;
using Tasker.Core;
using Microsoft.Data.Sqlite;
using Tasker.Core.Fields;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.Tasks.Query;
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
using Xunit;

namespace Tasker.Tests;

public sealed class TaskQueryDateTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 10, 30, 15, TimeSpan.Zero);
    private static readonly Guid Due = Guid.NewGuid();

    private static readonly TaskQueryVocabulary Vocabulary = new(
        [], [], [], [new QueryField("Due", FieldType.Date, false, Due, null)], [], new Dictionary<Guid, Guid[]>());

    private static DateTimeOffset At(string text) => DateTimeOffset.Parse(text + "Z", null, System.Globalization.DateTimeStyles.AssumeUniversal);

    private static readonly (string Title, DateTimeOffset Created, DateTimeOffset Updated)[] Seed =
    [
        ("A", At("2026-11-04T23:59:59"), At("2026-11-10T08:00:00")),
        ("B", At("2026-11-05T00:00:00"), At("2026-11-05T00:00:00")),
        ("C", At("2026-11-05T12:00:30"), At("2026-11-06T09:15:00")),
        ("D", At("2026-11-06T00:00:00"), At("2026-11-06T00:00:00")),
        ("E", At("2026-11-07T10:00:00"), At("2026-11-07T10:20:00"))
    ];

    public static IEnumerable<object[]> Cases() =>
    [
        [".Created == \"2026-11-05\"", "BC"],
        [".Created != \"2026-11-05\"", "ADE"],
        [".Created > \"2026-11-05\"", "DE"],
        [".Created >= \"2026-11-05\"", "BCDE"],
        [".Created < \"2026-11-05\"", "A"],
        [".Created <= \"2026-11-05\"", "ABC"],
        [".Created >= \"2026-11-05\" && .Updated < \"2026-11-07\"", "BCD"],
        [".Updated > \"2026-11-06\"", "AE"],
        [".Updated == \"2026-11-06T09:15\"", "C"],
        [".Created == \"2026-11-05T12:00:30\"", "C"],
        [".Created == \"2026-11-05T12:00\"", "C"],
        [".Created > \"2026-11-05T12:00\"", "DE"],
        [".Created >= \"2026-11-05T12:00:30Z\"", "CDE"],
        [".Created >= \"today\"", "E"],
        [".Created < \"today\"", "ABCD"],
        [".Created >= \"today-2d\"", "BCDE"],
        [".Created == \"today-2d\"", "BC"],
        [".Updated >= \"-1d\"", "AE"],
        [".Created >= \"now-1h\"", "E"],
        [".Created >= \"now-30m\"", ""],
        [".Created >= \"-1d\"", "E"],
        [".Created >= \"-2d\"", "CDE"],
        [".Created >= \"-3d\"", "ABCDE"],
        [".Created >= \"-2d\" || .Updated == \"2026-11-05\"", "BCDE"],
        ["!(.Created >= \"2026-11-05\")", "A"],
        [".Updated >= \"2026-11-05\" && .Updated <= \"2026-11-06\"", "BCD"],
        [".Created == \"now\"", ""]
    ];

    private static TaskPredicate Parse(string text, DateTimeOffset? now = null)
    {
        var (syntax, error) = TaskQueryParser.Parse(text);
        Assert.Null(error);
        var binder = new TaskQueryBinder(Vocabulary, now ?? Now);
        var predicate = binder.Bind(syntax!.Filter);
        Assert.True(binder.Errors.Count == 0, binder.Errors.Count > 0 ? binder.Errors[0].Error.Message : "");
        return predicate!;
    }

    private static TaskQueryDiagnostic Fails(string text)
    {
        var (syntax, error) = TaskQueryParser.Parse(text);
        if (error != null)
            return error;
        var binder = new TaskQueryBinder(Vocabulary, Now);
        binder.Bind(syntax!.Filter);
        Assert.NotEmpty(binder.Errors);
        return binder.Errors[0];
    }

    private static string Titles(IEnumerable<TaskItem> tasks) => string.Concat(tasks.Select(x => x.Title).Order());

    private static TaskItem Make(string title, DateTimeOffset created, DateTimeOffset updated, Guid project, Guid type, Guid status, string? due = null) => new()
    {
        Id = Guid.NewGuid(), ProjectId = project, Title = title, TypeId = type, StatusId = status, CreatedAt = created, UpdatedAt = updated, Version = "",
        Fields = due == null ? [] : [new TaskField(Due, [due])]
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_in_memory(string query, string expected)
    {
        var predicate = Parse(query);
        var tasks = Seed.Select(x => Make(x.Title, x.Created, x.Updated, Guid.Empty, Guid.Empty, Guid.Empty));
        Assert.Equal(expected, Titles(tasks.Where(predicate.Matches)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Folder_index_selects_the_same(string query, string expected)
    {
        using var harness = new FilesHarness();
        foreach (var x in Seed)
            await harness.Tasks.Add(Make(x.Title, x.Created, x.Updated, harness.ProjectId, Guid.NewGuid(), Guid.NewGuid()));

        var filter = new TaskFilter { Predicate = Parse(query) };
        Assert.Equal(expected, Titles(await harness.Tasks.GetAll(harness.ProjectId, filter)));
        Assert.Equal(expected.Length, await harness.Tasks.Count(harness.ProjectId, filter));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Server_database_selects_the_same(string query, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var builder = new ContainerBuilder();
        builder.RegisterModule(new DbStorageModule(new DbConfigs { SqliteFile = Path.Combine(root, "tasker.db") }));
        await using var container = builder.Build();
        try
        {
            await container.Resolve<IStorageLifecycle>().Start(default);
            await using var scope = container.BeginLifetimeScope();
            var (project, type, status, set) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await scope.Resolve<IProjectStorage>().Add(new Project { Id = project, Name = "P", CreatedAt = DateTimeOffset.UtcNow, Version = "" });
            await scope.Resolve<IStatusStorage>().Add(new Status { Id = status, ProjectId = project, Name = "Open", Color = "#000000", Version = "" });
            await scope.Resolve<IStatusSetStorage>().Add(new StatusSet { Id = set, ProjectId = project, Name = "S", StatusIds = [status], Version = "" });
            await scope.Resolve<ITaskTypeStorage>().Add(new TaskType { Id = type, ProjectId = project, Name = "T", StatusSetId = set, Version = "" });
            var tasks = scope.Resolve<ITaskStorage>();
            foreach (var x in Seed)
                await tasks.Add(Make(x.Title, x.Created, x.Updated, project, type, status));

            var filter = new TaskFilter { Predicate = Parse(query) };
            Assert.Equal(expected, Titles(await tasks.GetAll(project, filter)));
            Assert.Equal(expected.Length, await tasks.Count(project, filter));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(".Created > \"2026-13-01\"", 11, 23)]
    [InlineData(".Created > \"2026-11-5\"", 11, 22)]
    [InlineData(".Updated == \"yesterday\"", 12, 23)]
    [InlineData(".Created > \"2026-11-05 10:00\"", 11, 29)]
    [InlineData(".Created > 5", 11, 12)]
    [InlineData(".Created > \"today+1h\"", 11, 21)]
    [InlineData(".Created > \"\"", 11, 13)]
    public void Wrong_dates_get_a_clear_error_with_a_range(string text, int start, int end)
    {
        var error = Fails(text);
        Assert.Equal("query.badDate", error.Error.Code);
        Assert.Equal((start, end), (error.Start, error.End));
        Assert.Contains("2026-11-05", error.Error.Message);
    }

    [Fact]
    public void An_array_is_not_a_date()
    {
        var error = Fails(".Created == [\"2026-11-05\"]");
        Assert.Equal("query.arrayNotAllowed", error.Error.Code);
    }

    [Theory]
    [InlineData(".Field.Due >= \"today\"", "2026-11-07", true)]
    [InlineData(".Field.Due >= \"today\"", "2026-11-06", false)]
    [InlineData(".Field.Due == \"today-7d\"", "2026-10-31", true)]
    [InlineData(".Field.Due < \"-1d\"", "2026-11-05", true)]
    [InlineData(".Field.Due < \"-1d\"", "2026-11-06", false)]
    [InlineData(".Field.Due == \"2026-11-07\"", "2026-11-07", true)]
    public void Date_fields_take_relative_values(string query, string stored, bool expected)
    {
        var task = Make("T", Now, Now, Guid.Empty, Guid.Empty, Guid.Empty, stored);
        Assert.Equal(expected, Parse(query).Matches(task));
    }

    [Theory]
    [InlineData(".Field.Due >= \"2026-11-05T10:00\"")]
    [InlineData(".Field.Due >= \"today+2h\"")]
    [InlineData(".Field.Due >= \"nonsense\"")]
    public void Date_fields_reject_values_that_are_not_days(string query)
    {
        var error = Fails(query);
        Assert.Contains(error.Error.Code, new[] { "query.badValue", "query.badDate" });
    }

    [Fact]
    public void Relative_values_follow_the_clock_given_to_the_binder()
    {
        var later = Now.AddDays(10);
        var task = Make("T", Now, Now, Guid.Empty, Guid.Empty, Guid.Empty);
        Assert.True(Parse(".Created >= \"today\"").Matches(task));
        Assert.False(Parse(".Created >= \"today\"", later).Matches(task));
    }

    [Theory]
    [InlineData(".Created ", "==,!=,>,>=,<,<=")]
    [InlineData(".Updated ", "==,!=,>,>=,<,<=")]
    public void Operators_are_suggested_after_created_and_updated(string text, string expected)
    {
        var items = TaskQuerySuggester.Suggest(text, text.Length, Vocabulary).Where(x => x.Kind == TaskQuerySuggestionKind.Operator).Select(x => x.Label);
        Assert.Equal(expected.Split(','), items);
    }

    [Fact]
    public void Values_with_the_format_are_suggested_and_created_is_a_root()
    {
        var values = TaskQuerySuggester.Suggest(".Created >= ", 12, Vocabulary).Where(x => x.Kind == TaskQuerySuggestionKind.Value).ToArray();
        Assert.Contains(values, x => x.Label == "today" && x.Insert == "\"today\" " && x.Detail!.Contains("yyyy-MM-dd"));
        Assert.Contains(TaskQuerySuggester.Suggest(".", 1, Vocabulary), x => x.Label == ".Created");
        Assert.Contains(TaskQuerySuggester.Suggest(".", 1, Vocabulary), x => x.Label == ".Updated");
        var dates = TaskQuerySuggester.Suggest(".Field.Due >= ", 14, Vocabulary).Where(x => x.Kind == TaskQuerySuggestionKind.Value);
        Assert.Contains(dates, x => x.Label == "today");
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;
}
