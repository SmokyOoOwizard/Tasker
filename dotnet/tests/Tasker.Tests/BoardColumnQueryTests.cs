using Autofac;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tasker.Core;
using Tasker.Core.Boards;
using Tasker.Core.Fields;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.Tasks.Query;
using Tasker.Core.Workspace;
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Storage.Files;
using Tasker.Storage.Files.Storages;
using Xunit;

namespace Tasker.Tests;

public class BoardColumnQueryTests
{
    public static IEnumerable<object[]> Storages() => TestWorkspace.Storages();

    private static async Task<CliResult> CliOk(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    private static string Plain(CliResult result) => System.Text.RegularExpressions.Regex.Replace(result.Out, @" \([0-9a-f-]{36}\)", "");

    private static async Task<CliResult> CliFail(Task<CliResult> run, string message)
    {
        var result = await run;
        Assert.Equal(1, result.Code);
        Assert.Contains(message, result.Err);
        return result;
    }

    private static string[] Column(CliResult r) =>
        r.Data.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(' ')[^1]).ToArray();

    [Theory, MemberData(nameof(Storages))]
    public async Task The_console_creates_and_changes_column_queries_and_shows_them(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await CliOk(ws.Run("project", "create", "Demo"));
        foreach (var status in new[] { "Open", "Closed" })
            await CliOk(ws.Run("status", "create", status));
        await CliOk(ws.Run("status-set", "create", "Flow", "--status", "Open", "Closed"));
        await CliOk(ws.Run("enum", "create", "Priority", "--value", "Low", "High"));
        await CliOk(ws.Run("field", "create", "Severity", "--type", "enum", "--enum", "Priority"));
        await CliOk(ws.Run("field", "create", "Estimate", "--type", "int"));
        await CliOk(ws.Run("task-type", "create", "Bug", "--status-set", "Flow", "--field", "Severity", "--field", "Estimate"));
        await CliOk(ws.Run("series", "create", "Tasks", "--prefix", "TSK"));

        const string critical = ".Field.Severity == \"High\" | sort -.Field.Estimate";
        await CliOk(ws.Run("board", "create", "Main", "--status-set", "Flow",
            "--column", "Critical=Open", "--column", "Rest=Open", "--column", "Closed=Closed",
            "--column-query", "Critical:" + critical, "--column-query", "Rest:.Field.Severity != \"High\""));
        foreach (var (title, severity, estimate) in new[] { ("One", "High", "5"), ("Two", "Low", "9"), ("Three", "High", "7") })
            await CliOk(ws.Run("task", "create", title, "--type", "Bug", "--series", "TSK", "--field", $"Severity={severity}", "--field", $"Estimate={estimate}"));

        var get = await CliOk(ws.Run("board", "get", "Main"));
        Assert.Contains($"  Critical: Open; query {critical}\n", Plain(get));
        Assert.Contains("  Rest: Open; query .Field.Severity != \"High\"\n", Plain(get));
        Assert.Contains("  Closed: Closed\n", Plain(get));

        Assert.Equal(["Three", "One"], Column(await CliOk(ws.Run("board", "tasks", "Main", "Critical"))));
        Assert.Equal(["One", "Three"], Column(await CliOk(ws.Run("board", "tasks", "Main", "Critical", "--sort", "Estimate"))));
        Assert.Equal(["Two"], Column(await CliOk(ws.Run("board", "tasks", "Main", "Rest"))));

        var show = await CliOk(ws.Run("board", "show", "Main"));
        Assert.Contains($"Critical [{critical}] (2)", show.Out);
        Assert.Contains("Closed (0)", show.Out);
        var json = (await CliOk(ws.Run("board", "show", "Main", "--json"))).Json;
        Assert.Equal(critical, json["columns"]![0]!["query"]!.GetValue<string>());

        await CliOk(ws.Run("board", "update", "Main", "--column-query", "Critical:.Field.Severity == \"High\""));
        var changed = Plain(await CliOk(ws.Run("board", "get", "Main")));
        Assert.Contains("  Critical: Open; query .Field.Severity == \"High\"\n", changed);
        Assert.Contains("  Rest: Open; query .Field.Severity != \"High\"\n", changed);
        Assert.Equal(["One", "Three"], Column(await CliOk(ws.Run("board", "tasks", "Main", "Critical"))));

        await CliOk(ws.Run("board", "update", "Main", "--column", "Critical=Open", "--column", "Rest=Open", "--column", "Closed=Closed"));
        Assert.Contains("  Rest: Open; query .Field.Severity != \"High\"\n", Plain(await CliOk(ws.Run("board", "get", "Main"))));
        await CliFail(ws.Run("board", "update", "Main", "--column-query", "Rest:"), "already used by column");
        await CliOk(ws.Run("board", "update", "Main", "--column-query", "Closed:.Field.Estimate >= 1"));
        Assert.Contains("  Closed: Closed; query .Field.Estimate >= 1\n", Plain(await CliOk(ws.Run("board", "get", "Main"))));
        await CliOk(ws.Run("board", "update", "Main", "--column-query", "Closed:"));
        Assert.Contains("  Closed: Closed\n", Plain(await CliOk(ws.Run("board", "get", "Main"))));

        await CliFail(ws.Run("board", "update", "Main", "--column-query", "Nope:.Field.Estimate >= 1"), "name of one of the columns");
        await CliFail(ws.Run("board", "update", "Main", "--column-query", "Closed:.Field.Nope == 1"), "Columns[2].Query");
        await CliFail(ws.Run("board", "update", "Main", "--column-query", "Closed:.Field.Estimate >= 1", "--column-query", "Closed:.Field.Estimate >= 2"), "given twice");

        await CliOk(ws.Run("field", "update", "Severity", "--name", "Level"));
        var broken = await CliOk(ws.Run("board", "show", "Main"));
        Assert.Contains("  error: The query of column 'Critical'", broken.Out);
        Assert.Contains("Closed (0)", broken.Out);
        await CliFail(ws.Run("board", "tasks", "Main", "Critical"), "The query of column 'Critical'");
    }

    [Fact]
    public async Task A_board_file_keeps_the_query_in_format_13_and_old_field_conditions_are_skipped_without_failing()
    {
        var root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var container = OpenFiles(root);
            var project = Guid.NewGuid();
            var (setId, open, board) = NewBoard(project);
            await container.Resolve<IBoardStorage>().Add(board);

            var folder = Path.Combine(ProjectPaths.Folder(root, project), "boards");
            var path = Directory.GetFiles(folder).Single();
            var text = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n");
            Assert.StartsWith("formatVersion: 13\n", text);
            Assert.Contains("  query: .Field.Estimate >= 3 | sort -.Updated\n", text);
            Assert.DoesNotContain("query", text[text.IndexOf("name: Rest", StringComparison.Ordinal)..]);

            await using var fresh = OpenFiles(root);
            var read = (await fresh.Resolve<IBoardStorage>().GetById(project, board.Id))!;
            Assert.Equal(board.Columns[0].Query, read.Columns[0].Query);
            Assert.Null(read.Columns[1].Query);

            var old = $"formatVersion: 12\nid: {board.Id}\nname: Old\nstatusSets:\n- {setId}\ncolumns:\n- id: {Guid.NewGuid()}\n  name: A\n  statuses:\n  - {open}\n" +
                $"  fieldFilters:\n  - field: {Guid.NewGuid()}\n    op: between\n    value: x\n";
            await File.WriteAllTextAsync(path, old);
            var oldRead = (await fresh.Resolve<IBoardStorage>().GetById(project, board.Id))!;
            Assert.Null(oldRead.Columns.Single().Query);
            Assert.Equal(old, await File.ReadAllTextAsync(path));

            var report = await fresh.Resolve<IFileMigration>().Run(new MigrationOptions());
            Assert.Equal(13, report.CurrentFormat);
            Assert.Single(report.Migrated);

            var updated = oldRead with { Name = "Renamed" };
            Assert.NotNull(await fresh.Resolve<IBoardStorage>().Update(updated, (await fresh.Resolve<IBoardStorage>().GetById(project, board.Id))!.Version));
            var rewritten = (await File.ReadAllTextAsync(Directory.GetFiles(folder).Single())).ReplaceLineEndings("\n");
            Assert.DoesNotContain("fieldFilters", rewritten);
            Assert.StartsWith("formatVersion: 13\n", rewritten);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IContainer OpenFiles(string root)
    {
        var builder = new ContainerBuilder();
        builder.RegisterGeneric(typeof(NullLogger<>)).As(typeof(ILogger<>)).SingleInstance();
        builder.RegisterModule(new FileStorageModule(root));
        return builder.Build();
    }

    private static (Guid SetId, Guid Open, Board Board) NewBoard(Guid project, Guid? setId = null, Guid? open = null, Guid? closed = null)
    {
        var (set, openId, closedId) = (setId ?? Guid.NewGuid(), open ?? Guid.NewGuid(), closed ?? Guid.NewGuid());
        var board = new Board
        {
            Id = Guid.NewGuid(), ProjectId = project, Name = "Main", StatusSetIds = [set], Version = "",
            Columns =
            [
                new BoardColumn
                {
                    Id = Guid.NewGuid(), Name = "Big", StatusIds = [openId], DropStatuses = new Dictionary<Guid, Guid> { [set] = openId },
                    Query = ".Field.Estimate >= 3 | sort -.Updated"
                },
                new BoardColumn { Id = Guid.NewGuid(), Name = "Rest", StatusIds = [closedId], DropStatuses = new Dictionary<Guid, Guid>() }
            ]
        };
        return (set, openId, board);
    }

    [Fact]
    public async Task The_database_keeps_the_query_and_an_old_database_with_field_conditions_is_upgraded_on_start()
    {
        var root = Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "tasker.db");
        try
        {
            var project = Guid.NewGuid();
            var (open, closed, setId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var field = new FieldDefinition { Id = Guid.NewGuid(), ProjectId = project, Name = "Estimate", Type = FieldType.Int, Version = "" };
            var (_, _, board) = NewBoard(project, setId, open, closed);

            await using (var container = OpenDb(file))
            {
                await container.Resolve<IStorageLifecycle>().Start(default);
                await using var scope = container.BeginLifetimeScope();
                await scope.Resolve<IProjectStorage>().Add(new Project { Id = project, Name = "P", CreatedAt = DateTimeOffset.UtcNow, Version = "" });
                var statuses = scope.Resolve<IStatusStorage>();
                await statuses.Add(new Status { Id = open, ProjectId = project, Name = "Open", Color = "#111111", Version = "" });
                await statuses.Add(new Status { Id = closed, ProjectId = project, Name = "Closed", Color = "#222222", Version = "" });
                await scope.Resolve<IStatusSetStorage>().Add(new StatusSet { Id = setId, ProjectId = project, Name = "Flow", StatusIds = [open, closed], Version = "" });
                await scope.Resolve<IFieldStorage>().Add(field);
                var version = await scope.Resolve<IBoardStorage>().Add(board);

                var read = (await scope.Resolve<IBoardStorage>().GetById(project, board.Id))!;
                Assert.Equal(board.Columns[0].Query, read.Columns[0].Query);
                Assert.Null(read.Columns[1].Query);

                await using var next = container.BeginLifetimeScope();
                var cleared = board with
                {
                    Columns = board.Columns.Select(c => new BoardColumn { Id = c.Id, Name = c.Name, StatusIds = c.StatusIds, DropStatuses = c.DropStatuses }).ToArray()
                };
                Assert.NotNull(await next.Resolve<IBoardStorage>().Update(cleared, version));
                Assert.All((await next.Resolve<IBoardStorage>().GetById(project, board.Id))!.Columns, c => Assert.Null(c.Query));
            }

            await using (var connection = new SqliteConnection($"Data Source={file}"))
            {
                await connection.OpenAsync();
                foreach (var sql in new[]
                {
                    "ALTER TABLE board_columns DROP COLUMN \"Query\"",
                    "CREATE TABLE board_column_field_filters (\"ColumnId\" TEXT NOT NULL, \"Position\" INTEGER NOT NULL, \"FieldId\" TEXT NOT NULL, " +
                    "\"Operator\" TEXT NOT NULL, \"Value\" TEXT NULL, PRIMARY KEY (\"ColumnId\", \"Position\"), " +
                    "FOREIGN KEY (\"ColumnId\") REFERENCES board_columns (\"Id\") ON DELETE CASCADE, FOREIGN KEY (\"FieldId\") REFERENCES fields (\"Id\"))",
                    $"INSERT INTO board_column_field_filters VALUES ('{board.Columns[0].Id.ToString().ToUpperInvariant()}', 0, '{field.Id.ToString().ToUpperInvariant()}', 'GreaterOrEqual', '3')"
                })
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync();
                }
            }
            SqliteConnection.ClearAllPools();

            await using (var container = OpenDb(file))
            {
                await container.Resolve<IStorageLifecycle>().Start(default);
                await using var scope = container.BeginLifetimeScope();
                var boards = scope.Resolve<IBoardStorage>();
                var read = (await boards.GetById(project, board.Id))!;
                Assert.All(read.Columns, c => Assert.Null(c.Query));
                Assert.NotNull(await boards.Update(read with { Columns = [new BoardColumn { Id = read.Columns[0].Id, Name = "Big", StatusIds = [open], DropStatuses = read.Columns[0].DropStatuses, Query = ".Field.Estimate > 1" }] }, read.Version));
                Assert.Equal(".Field.Estimate > 1", (await boards.GetById(project, board.Id))!.Columns.Single().Query);
                var fields = scope.Resolve<IFieldStorage>();
                Assert.True(await fields.Delete(project, field.Id, (await fields.GetById(project, field.Id))!.Version));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static IContainer OpenDb(string file)
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule(new DbStorageModule(new DbConfigs { SqliteFile = file }));
        return builder.Build();
    }

    private static readonly Guid FieldId = Guid.NewGuid();

    private static FieldDefinition Field(FieldType type, bool multiple = false) =>
        new() { Id = FieldId, ProjectId = Guid.Empty, Name = "F", Type = type, Multiple = multiple, Version = "" };

    private static TaskPredicate F(FieldType type, FieldOperator op, string? value = null)
    {
        if (op == FieldOperator.NotEqual)
            return new TaskNot(new TaskFieldMatches(new FieldCondition("F", type, FieldOperator.Equal, value, FieldId: FieldId)));
        double? number = value != null && type is FieldType.Int or FieldType.Float ? FieldNumbers.Parse(value) : null;
        return new TaskFieldMatches(new FieldCondition("F", type, op, value, number, FieldId));
    }

    [Theory]
    [InlineData(FieldType.Int, false, FieldOperator.Equal, "1", FieldOperator.Equal, "2", true)]
    [InlineData(FieldType.Int, false, FieldOperator.Equal, "1", FieldOperator.Equal, "1", false)]
    [InlineData(FieldType.Int, true, FieldOperator.Equal, "1", FieldOperator.Equal, "2", false)]
    [InlineData(FieldType.Int, true, FieldOperator.Equal, "1", FieldOperator.NotEqual, "1", true)]
    [InlineData(FieldType.Int, false, FieldOperator.NotEqual, "1", FieldOperator.NotEqual, "2", false)]
    [InlineData(FieldType.Int, false, FieldOperator.GreaterOrEqual, "3", FieldOperator.Less, "3", true)]
    [InlineData(FieldType.Int, false, FieldOperator.Greater, "3", FieldOperator.LessOrEqual, "3", true)]
    [InlineData(FieldType.Int, false, FieldOperator.GreaterOrEqual, "3", FieldOperator.LessOrEqual, "3", false)]
    [InlineData(FieldType.Int, false, FieldOperator.Greater, "3", FieldOperator.Less, "4", false)]
    [InlineData(FieldType.Int, false, FieldOperator.Greater, "5", FieldOperator.Less, "3", true)]
    [InlineData(FieldType.Int, false, FieldOperator.Equal, "5", FieldOperator.Less, "5", true)]
    [InlineData(FieldType.Int, true, FieldOperator.Greater, "5", FieldOperator.Less, "3", false)]
    [InlineData(FieldType.Float, false, FieldOperator.Greater, "1.5", FieldOperator.LessOrEqual, "1.5", true)]
    [InlineData(FieldType.Date, false, FieldOperator.Less, "2026-01-01", FieldOperator.GreaterOrEqual, "2026-01-01", true)]
    [InlineData(FieldType.Date, false, FieldOperator.Less, "2026-03-01", FieldOperator.Greater, "2026-01-01", false)]
    [InlineData(FieldType.String, false, FieldOperator.Equal, "a", FieldOperator.Equal, "b", true)]
    [InlineData(FieldType.String, false, FieldOperator.Equal, "a", FieldOperator.NotEqual, "b", false)]
    [InlineData(FieldType.Int, false, FieldOperator.Set, null, FieldOperator.Unset, null, true)]
    [InlineData(FieldType.Int, true, FieldOperator.Set, null, FieldOperator.Unset, null, true)]
    [InlineData(FieldType.Int, false, FieldOperator.Equal, "1", FieldOperator.Unset, null, true)]
    [InlineData(FieldType.Int, false, FieldOperator.Greater, "1", FieldOperator.Detached, null, true)]
    [InlineData(FieldType.Int, false, FieldOperator.NotEqual, "1", FieldOperator.Unset, null, false)]
    [InlineData(FieldType.Int, false, FieldOperator.Attached, null, FieldOperator.Detached, null, true)]
    [InlineData(FieldType.Int, false, FieldOperator.Attached, null, FieldOperator.Unset, null, false)]
    [InlineData(FieldType.Int, false, FieldOperator.Set, null, FieldOperator.Equal, "1", false)]
    public void Field_conditions_of_two_queries_exclude_each_other_only_when_they_cannot_hold_together(
        FieldType type, bool multiple, FieldOperator first, string? firstValue, FieldOperator second, string? secondValue, bool exclusive)
    {
        var fields = new Dictionary<Guid, FieldDefinition> { [FieldId] = Field(type, multiple) };
        var a = F(type, first, firstValue);
        var b = F(type, second, secondValue);

        Assert.Equal(exclusive, ColumnQueries.AreExclusive(a, b, fields));
        Assert.Equal(exclusive, ColumnQueries.AreExclusive(b, a, fields));
        Assert.Equal(exclusive, ColumnQueries.AreExclusive(new TaskAllOf([a, new TaskTitleContains("x")]), b, fields));
    }

    [Fact]
    public void And_or_not_types_and_statuses_are_taken_into_account_cautiously()
    {
        var fields = new Dictionary<Guid, FieldDefinition> { [FieldId] = Field(FieldType.Int) };
        var (bug, story) = (Guid.NewGuid(), Guid.NewGuid());
        var one = F(FieldType.Int, FieldOperator.Equal, "1");
        var two = F(FieldType.Int, FieldOperator.Equal, "2");
        var unset = F(FieldType.Int, FieldOperator.Unset);

        Assert.False(ColumnQueries.AreExclusive(null, one, fields));
        Assert.False(ColumnQueries.AreExclusive(one, new TaskTitleContains("x"), fields));
        Assert.True(ColumnQueries.AreExclusive(new TaskAnyOf([one, two]), unset, fields));
        Assert.False(ColumnQueries.AreExclusive(new TaskAnyOf([one, new TaskTitleContains("x")]), unset, fields));
        Assert.True(ColumnQueries.AreExclusive(new TaskAllOf([new TaskTitleContains("x"), one]), new TaskAnyOf([two, unset]), fields));
        Assert.True(ColumnQueries.AreExclusive(new TaskTypeIn([bug]), new TaskTypeIn([story]), fields));
        Assert.False(ColumnQueries.AreExclusive(new TaskTypeIn([bug, story]), new TaskTypeIn([story]), fields));
        Assert.True(ColumnQueries.AreExclusive(new TaskTypeIn([bug]), new TaskNot(new TaskTypeIn([bug, story])), fields));
        Assert.False(ColumnQueries.AreExclusive(new TaskTypeIn([bug, story]), new TaskNot(new TaskTypeIn([bug])), fields));
        Assert.True(ColumnQueries.AreExclusive(new TaskStatusIn([bug]), new TaskNot(new TaskStatusIn([bug])), fields));
        Assert.False(ColumnQueries.AreExclusive(new TaskTypeIn([bug]), new TaskStatusIn([story]), fields));
        Assert.False(ColumnQueries.AreExclusive(new TaskNot(new TaskTypeIn([bug])), new TaskNot(new TaskTypeIn([story])), fields));
    }
}
