using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Autofac;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Web;
using Xunit;

namespace Tasker.Tests;

public sealed class BoardColumnQueryApiTests : IAsyncLifetime
{
    private sealed class EmptyModule : Module;

    private const string ColumnQuery = """
        query Column($project: UUID!, $board: UUID!, $column: UUID!, $query: String) {
          project(id: $project) { columnTasks(boardId: $board, columnId: $column, query: $query, limit: 100) { totalCount data { title } } }
        }
        """;

    private const string BoardQuery = """
        query Board($project: UUID!, $board: UUID!) {
          project(id: $project) { board(id: $board) { id name version columns { id name query } } }
        }
        """;

    private readonly DaemonFixture _daemon = new();
    private WebApplication _server = null!;
    private HttpClient _http = null!;
    private string _serverBase = null!;

    public async Task InitializeAsync()
    {
        var builder = TaskerWebApp.CreateBuilder<EmptyModule>([], TaskerMode.Server);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _server = builder.Build();
        _server.MapTasker();
        await _server.StartAsync();
        var address = _server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _serverBase = new Uri(new Uri(address), "/api").ToString();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.StopAsync();
        await _server.DisposeAsync();
        _daemon.Dispose();
    }

    public static IEnumerable<object[]> Modes() => [["server"], ["workspace"]];

    private sealed class Stand
    {
        public required BoardColumnQueryApiTests Test { get; init; }
        public required string Base { get; init; }
        public string? Token { get; init; }
        public required string Project { get; init; }
        public required string Set { get; init; }
        public required string Todo { get; init; }
        public required string Doing { get; init; }
        public required string Done { get; init; }
        public required string Bug { get; init; }
        public required Dictionary<string, string> Fields { get; init; }

        public string P(string path) => $"/projects/{Project}{path}";

        public Task<(HttpStatusCode Status, JsonNode? Body)> Send(HttpMethod method, string path, object? body = null) =>
            Test.Send(method, Base, P(path), Token, body);

        public async Task<JsonNode> Ok(HttpMethod method, string path, object? body = null)
        {
            var (status, node) = await Send(method, path, body);
            Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Created, $"{path}: {status} {node}");
            return node!;
        }

        public async Task<JsonNode> Fails(HttpMethod method, string path, object? body, string? code)
        {
            var (status, node) = await Send(method, path, body);
            Assert.True(status == HttpStatusCode.BadRequest, $"{path}: {status} {node}");
            if (code != null)
                Assert.Equal(code, node!["code"]!.GetValue<string>());
            return node!;
        }

        public async Task<JsonNode> Task(string title, string? status = null, params (string Field, string[] Values)[] fields) =>
            await Ok(HttpMethod.Post, "/tasks", new
            {
                title, typeId = Bug, statusId = status ?? Todo,
                fields = new { values = fields.Select(x => new { fieldId = Fields[x.Field], values = x.Values }).ToArray() }
            });

        public object Column(string name, string status, string? query = null, string? id = null) => new
        {
            id, name, statusIds = new[] { status }, dropStatuses = new Dictionary<string, string> { [Set] = status }, query
        };

        public Task<JsonNode> Board(string name, params object[] columns) =>
            Ok(HttpMethod.Post, "/boards", new { name, statusSetIds = new[] { Set }, columns });

        public async Task<(string[] Titles, JsonNode? Error)> Column(JsonNode board, int column, string? query = null)
        {
            var (_, body) = await GraphReads.Query(Test._http, Base, ColumnQuery,
                new { project = Project, board = Id(board), column = Id(board["columns"]![column]!), query }, Token);
            if (body!["errors"] is JsonArray { Count: > 0 } errors)
                return ([], errors[0]);
            return (body["data"]!["project"]!["columnTasks"]!["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray(), null);
        }

        public async Task<string[]> Titles(JsonNode board, int column, string? query = null)
        {
            var (titles, error) = await Column(board, column, query);
            Assert.True(error == null, $"column {column}: {error}");
            return titles;
        }

        public async Task<JsonNode> Read(JsonNode board)
        {
            var (_, body) = await GraphReads.Query(Test._http, Base, BoardQuery, new { project = Project, board = Id(board) }, Token);
            Assert.True(body!["errors"] == null, $"{body["errors"]}");
            return body["data"]!["project"]!["board"]!;
        }

        public async Task<JsonNode> Graph(string entity, string id)
        {
            var (_, body) = await GraphReads.Query(Test._http, Base, $"query($p: UUID!, $id: UUID!) {{ project(id: $p) {{ {entity}(id: $id) {{ id version {(entity == "task" ? "statusId" : "")} }} }} }}",
                new { p = Project, id }, Token);
            Assert.True(body!["errors"] == null, $"{body["errors"]}");
            return body["data"]!["project"]![entity]!;
        }

        public Task<(HttpStatusCode Status, JsonNode? Body)> Move(JsonNode board, int column, JsonNode task) =>
            Send(HttpMethod.Post, $"/boards/{Id(board)}/columns/{Id(board["columns"]![column]!)}/move", new { taskId = Id(task), version = Version(task) });
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Send(HttpMethod method, string apiBase, string path, string? token, object? body)
    {
        using var request = new HttpRequestMessage(method, apiBase.TrimEnd('/') + path);
        if (body != null)
            request.Content = JsonContent.Create(body);
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();
    private static string Version(JsonNode node) => node["version"]!.GetValue<string>();

    private async Task<Stand> Seed(string mode)
    {
        string apiBase;
        string? token = null;
        string projectId;
        if (mode == "server")
        {
            apiBase = _serverBase;
            var (_, auth) = await Send(HttpMethod.Post, apiBase, "/auth/register", null,
                new { username = "owner", email = "owner@example.com", password = "Passw0rd!123" });
            token = auth!["accessToken"]!.GetValue<string>();
            var (_, project) = await Send(HttpMethod.Post, apiBase, "/projects", token, new { name = "Demo" });
            projectId = Id(project!);
        }
        else
        {
            await _daemon.Workspace("a", "Demo");
            var status = await _daemon.Start();
            apiBase = $"http://127.0.0.1:{status.Port}/w/{status.Workspaces[0].Key}/api";
            var (_, projects) = await GraphReads.Query(_http, apiBase, "{ projects { data { id } } }");
            projectId = projects!["data"]!["projects"]!["data"]![0]!["id"]!.GetValue<string>();
        }

        async Task<string> Post(string path, object body)
        {
            var (status, node) = await Send(HttpMethod.Post, apiBase, $"/projects/{projectId}{path}", token, body);
            Assert.True(status == HttpStatusCode.Created, $"{path}: {status} {node}");
            return Id(node!);
        }

        var todo = await Post("/statuses", new { name = "Todo", color = "#112233" });
        var doing = await Post("/statuses", new { name = "Doing", color = "#112233" });
        var done = await Post("/statuses", new { name = "Done", color = "#112233" });
        var set = await Post("/status-sets", new { name = "Flow", statusIds = new[] { todo, doing, done } });
        var levels = await Post("/enums", new { name = "Levels", values = new[] { "Low", "High" } });
        var fields = new Dictionary<string, string>
        {
            ["Estimate"] = await Post("/fields", new { name = "Estimate", type = "int" }),
            ["Level"] = await Post("/fields", new { name = "Level", type = "enum", enumId = levels }),
            ["Labels"] = await Post("/fields", new { name = "Labels", type = "string", multiple = true }),
            ["Note"] = await Post("/fields", new { name = "Note", type = "string" })
        };
        var bug = await Post("/task-types", new
        {
            name = "Bug", statusSetId = set,
            fields = new[] { "Estimate", "Level", "Labels" }.Select(x => new { fieldId = fields[x], required = false }).ToArray()
        });
        return new Stand
        {
            Test = this, Base = apiBase, Token = token, Project = projectId, Set = set, Todo = todo, Doing = doing, Done = done, Bug = bug, Fields = fields
        };
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task The_column_query_selects_and_orders_its_tasks_and_the_row_query_narrows_and_reorders_them(string mode)
    {
        var s = await Seed(mode);
        await s.Task("a", null, ("Level", ["High"]), ("Estimate", ["5"]));
        await s.Task("b", null, ("Level", ["Low"]), ("Estimate", ["2"]));
        await s.Task("c");
        await s.Task("d", null, ("Level", ["High"]), ("Estimate", ["8"]));
        await s.Task("e", s.Done, ("Level", ["High"]), ("Estimate", ["1"]));

        var board = await s.Board("Main",
            s.Column("Critical", s.Todo, ".Field.Level == \"High\" | sort -.Field.Estimate"),
            s.Column("Rest", s.Todo, ".Field.Level != \"High\""),
            s.Column("Done", s.Done));
        Assert.Equal(".Field.Level == \"High\" | sort -.Field.Estimate", board["columns"]![0]!["query"]!.GetValue<string>());
        Assert.Null(board["columns"]![2]!["query"]);

        Assert.Equal(["d", "a"], await s.Titles(board, 0));
        Assert.Equal(["b", "c"], await s.Titles(board, 1));
        Assert.Equal(["e"], await s.Titles(board, 2));

        Assert.Equal(["d"], await s.Titles(board, 0, ".Field.Estimate > 6"));
        Assert.Equal(["d", "a"], await s.Titles(board, 0, ".Field.Estimate > 1"));
        Assert.Equal(["a", "d"], await s.Titles(board, 0, ".Field.Estimate > 1 | sort .Field.Estimate"));
        Assert.Empty(await s.Titles(board, 1, ".Field.Level == \"High\""));

        var read = await s.Read(board);
        Assert.Equal(".Field.Level != \"High\"", read["columns"]![1]!["query"]!.GetValue<string>());
        var renamed = await s.Ok(HttpMethod.Patch, $"/boards/{Id(board)}", new { version = Version(read), name = "Main 2" });
        Assert.Equal(".Field.Level != \"High\"", renamed["columns"]![1]!["query"]!.GetValue<string>());

        var keep = renamed["columns"]!.AsArray().Select(c => new
        {
            id = Id(c!), name = c!["name"]!.GetValue<string>(), statusIds = c["statusIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray(),
            dropStatuses = new Dictionary<string, string> { [s.Set] = c["statusIds"]![0]!.GetValue<string>() }
        }).ToArray();
        var kept = await s.Ok(HttpMethod.Patch, $"/boards/{Id(board)}", new { version = Version(renamed), columns = keep });
        Assert.Equal(".Field.Level == \"High\" | sort -.Field.Estimate", kept["columns"]![0]!["query"]!.GetValue<string>());

        var cleared = keep.Select(c => new { c.id, c.name, c.statusIds, c.dropStatuses, query = "" }).ToArray();
        await s.Fails(HttpMethod.Patch, $"/boards/{Id(board)}", new { version = Version(kept), columns = cleared }, "board.columnOverlap");

        var replaced = keep.Select(c => new
        {
            c.id, c.name, c.statusIds, c.dropStatuses,
            query = c.name == "Critical" ? ".Field.Estimate >= 5" : c.name == "Rest" ? ".Field.Estimate < 5" : ""
        }).ToArray();
        var changed = await s.Ok(HttpMethod.Patch, $"/boards/{Id(board)}", new { version = Version(kept), columns = replaced });
        Assert.Equal(".Field.Estimate >= 5", changed["columns"]![0]!["query"]!.GetValue<string>());
        Assert.Null(changed["columns"]![2]!["query"]);
        Assert.Equal(["a", "d"], await s.Titles(changed, 0));
        Assert.Equal(["b"], await s.Titles(changed, 1));
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task A_query_is_checked_on_save_and_a_shared_status_needs_queries_that_exclude_each_other(string mode)
    {
        var s = await Seed(mode);

        var bad = await s.Fails(HttpMethod.Post, "/boards",
            new { name = "X", statusSetIds = new[] { s.Set }, columns = new[] { s.Column("A", s.Todo, ".Field.Nope == 1") } }, "query.unknownField");
        Assert.Contains("Columns[0].Query", bad["error"]!.GetValue<string>());
        await s.Fails(HttpMethod.Post, "/boards",
            new { name = "X", statusSetIds = new[] { s.Set }, columns = new[] { s.Column("A", s.Todo, ".Field.Level ==") } }, null);

        async Task Shared(string? first, string? second) => await s.Fails(HttpMethod.Post, "/boards", new
        {
            name = "Shared", statusSetIds = new[] { s.Set }, columns = new[] { s.Column("A", s.Todo, first), s.Column("B", s.Todo, second) }
        }, "board.columnOverlap");
        await Shared(null, null);
        await Shared(".Field.Level == \"High\"", null);
        await Shared(".Field.Level == \"High\"", ".Field.Estimate > 3");
        await Shared(".Field.Estimate >= 3", ".Field.Estimate >= 2");
        await Shared(".Field.Level == \"High\" || .Field.Estimate > 3", ".Field.Level == \"Low\"");

        async Task Exclusive(string first, string second) => await s.Ok(HttpMethod.Post, "/boards", new
        {
            name = "E" + Guid.NewGuid().ToString("N")[..6], statusSetIds = new[] { s.Set },
            columns = new[] { s.Column("A", s.Todo, first), s.Column("B", s.Todo, second) }
        });
        await Exclusive(".Field.Level == \"High\"", ".Field.Level == \"Low\"");
        await Exclusive(".Field.Level == \"High\"", ".Field.Level != \"High\"");
        await Exclusive(".Field.Level.IsSet()", ".Field.Level.IsUnset()");
        await Exclusive(".Field.Estimate >= 3", ".Field.Estimate < 3 && .Field.Level == \"High\"");
        await Exclusive("(.Field.Level == \"High\" || .Field.Level == \"Low\") && .Field.Estimate > 1", ".Field.Level.IsUnset()");
        await Exclusive(".Type == \"Bug\"", ".Type != \"Bug\"");
        await Exclusive(".Field.Level == \"High\" | sort .Field.Estimate", "!(.Field.Level == \"High\")");
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task A_stored_query_that_no_longer_parses_shows_an_error_in_its_column_only(string mode)
    {
        var s = await Seed(mode);
        await s.Task("a", null, ("Level", ["High"]));
        var b = await s.Task("b", s.Done);
        var board = await s.Board("Main",
            s.Column("Critical", s.Todo, ".Field.Level == \"High\""),
            s.Column("Noted", s.Doing, ".Field.Note.IsUnset()"),
            s.Column("Done", s.Done));
        Assert.Equal(["a"], await s.Titles(board, 0));

        var level = await s.Graph("field", s.Fields["Level"]);
        await s.Ok(HttpMethod.Patch, $"/fields/{s.Fields["Level"]}", new { version = Version(level), name = "Severity" });
        var note = await s.Graph("field", s.Fields["Note"]);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Send(HttpMethod.Delete, $"/fields/{s.Fields["Note"]}?version={Uri.EscapeDataString(Version(note))}")).Status);

        var (_, critical) = await s.Column(board, 0);
        Assert.Equal("board.columnQueryInvalid", critical!["extensions"]!["code"]!.GetValue<string>());
        Assert.Contains("Critical", critical["message"]!.GetValue<string>());
        var (_, noted) = await s.Column(board, 1);
        Assert.Equal("board.columnQueryInvalid", noted!["extensions"]!["code"]!.GetValue<string>());
        Assert.Equal(["b"], await s.Titles(board, 2));

        var read = await s.Read(board);
        Assert.Equal(".Field.Level == \"High\"", read["columns"]![0]!["query"]!.GetValue<string>());
        var renamed = await s.Ok(HttpMethod.Patch, $"/boards/{Id(board)}", new { version = Version(read), name = "Main 2" });

        var moved = await s.Move(renamed, 0, b);
        Assert.Equal(HttpStatusCode.BadRequest, moved.Status);
        Assert.Equal("board.columnQueryInvalid", moved.Body!["code"]!.GetValue<string>());

        var columns = renamed["columns"]!.AsArray().Select((c, i) => s.Column(c!["name"]!.GetValue<string>(), c["statusIds"]![0]!.GetValue<string>(),
            i == 0 ? ".Field.Severity == \"High\"" : null, Id(c))).ToArray();
        var fixedBoard = await s.Ok(HttpMethod.Patch, $"/boards/{Id(board)}", new { version = Version(renamed), columns });
        Assert.Equal(["a"], await s.Titles(fixedBoard, 0));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Send(HttpMethod.Patch, $"/boards/{Id(board)}", new
        {
            version = Version(fixedBoard),
            columns = new[] { s.Column("Critical", s.Todo, ".Field.Level == \"High\"", Id(fixedBoard["columns"]![0]!)) }
        })).Status);
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task A_task_that_would_not_match_the_column_query_after_the_move_is_not_moved(string mode)
    {
        var s = await Seed(mode);
        var board = await s.Board("Main",
            s.Column("Review", s.Doing, "(.Field.Level == \"High\" || .Field.Estimate >= 5) && !.Field.Labels.Contains(\"blocked\") && .Status == \"Doing\""),
            s.Column("Waiting", s.Done, ".Link.\"is blocked by\".IsSet()"),
            s.Column("Todo", s.Todo));

        var high = await s.Task("high", null, ("Level", ["High"]), ("Estimate", ["1"]));
        var big = await s.Task("big", null, ("Level", ["Low"]), ("Estimate", ["7"]));
        var small = await s.Task("small", null, ("Level", ["Low"]), ("Estimate", ["1"]));
        var blocked = await s.Task("blocked", null, ("Level", ["High"]), ("Labels", ["blocked", "ui"]));

        var moved = await s.Move(board, 0, high);
        Assert.Equal(HttpStatusCode.OK, moved.Status);
        Assert.Equal(s.Doing, moved.Body!["statusId"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await s.Move(board, 0, big)).Status);
        Assert.Equal(["high", "big"], await s.Titles(board, 0));

        var refused = await s.Move(board, 0, small);
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Equal("board.taskNotMatchColumn", refused.Body!["code"]!.GetValue<string>());
        Assert.Contains(".Field.Estimate >= 5", refused.Body["error"]!.GetValue<string>());
        Assert.Equal(s.Todo, (await s.Graph("task", Id(small)))["statusId"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Move(board, 0, blocked)).Status);

        var links = await GraphReads.Query(_http, s.Base, "query($p: UUID!) { project(id: $p) { linkTypes { data { id name } } } }", new { p = s.Project }, s.Token);
        var blocks = links.Body!["data"]!["project"]!["linkTypes"]!["data"]!.AsArray().First(x => x!["name"]!.GetValue<string>() == "Blocks")!;
        await s.Ok(HttpMethod.Post, $"/tasks/{Id(blocked)}/links", new { typeId = Id(blocks), targetId = Id(small) });
        var freshSmall = await s.Graph("task", Id(small));
        var freshBlocked = await s.Graph("task", Id(blocked));
        Assert.Equal(HttpStatusCode.OK, (await s.Move(board, 1, freshSmall)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Move(board, 1, freshBlocked)).Status);
        Assert.Equal(["small"], await s.Titles(board, 1));
    }
}
