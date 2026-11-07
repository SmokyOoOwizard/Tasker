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

public sealed class TaskQueryApiTests : IAsyncLifetime
{
    private sealed class EmptyModule : Module;

    private const string ListQuery = """
        query List($project: UUID!, $query: String) {
          project(id: $project) { tasks(query: $query, limit: 100) { totalCount data { title } } }
        }
        """;

    private const string TreeQuery = """
        query Tree($project: UUID!, $query: String) {
          project(id: $project) { taskTree(query: $query, limit: 100) { totalCount data { title } } }
        }
        """;

    private const string ColumnQuery = """
        query Column($project: UUID!, $board: UUID!, $column: UUID!, $query: String) {
          project(id: $project) { columnTasks(boardId: $board, columnId: $column, query: $query, limit: 100) { totalCount data { title } } }
        }
        """;

    private const string AnalysisQuery = """
        query Analysis($project: UUID!, $text: String!, $cursor: Int) {
          project(id: $project) {
            taskQueryAnalysis(text: $text, cursor: $cursor) {
              valid hasSort
              errors { code message start end params { name value } }
              suggestions { kind label insert start end caret detail }
            }
          }
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

    private sealed record Stand(string Base, string? Token, string Project, string Board, string Column);

    private async Task<JsonNode> Post(string apiBase, string path, string? token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, apiBase.TrimEnd('/') + path) { Content = JsonContent.Create(body) };
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();

    private async Task<Stand> Seed(string mode)
    {
        string apiBase;
        string? token = null;
        string projectId;
        if (mode == "server")
        {
            apiBase = _serverBase;
            token = (await Post(apiBase, "/auth/register", null, new { username = "owner", email = "owner@example.com", password = "Passw0rd!123" }))["accessToken"]!.GetValue<string>();
            projectId = Id(await Post(apiBase, "/projects", token, new { name = "Demo" }));
        }
        else
        {
            await _daemon.Workspace("a", "Demo");
            var status = await _daemon.Start();
            apiBase = $"http://127.0.0.1:{status.Port}/w/{status.Workspaces[0].Key}/api";
            var (_, projects) = await GraphReads.Query(_http, apiBase, "{ projects { data { id } } }");
            projectId = projects!["data"]!["projects"]!["data"]![0]!["id"]!.GetValue<string>();
        }

        var p = $"/projects/{projectId}";
        var todo = Id(await Post(apiBase, p + "/statuses", token, new { name = "Todo", color = "#112233" }));
        var doing = Id(await Post(apiBase, p + "/statuses", token, new { name = "Doing", color = "#112233" }));
        var done = Id(await Post(apiBase, p + "/statuses", token, new { name = "Done", color = "#112233" }));
        var set = Id(await Post(apiBase, p + "/status-sets", token, new { name = "Flow", statusIds = new[] { todo, doing, done } }));
        var estimate = Id(await Post(apiBase, p + "/fields", token, new { name = "Estimate", type = "int" }));
        var labels = Id(await Post(apiBase, p + "/fields", token, new { name = "Метки", type = "string", multiple = true }));
        var levels = Id(await Post(apiBase, p + "/enums", token, new { name = "Levels", values = new[] { "Low", "High" } }));
        var level = Id(await Post(apiBase, p + "/fields", token, new { name = "Level", type = "enum", enumId = levels }));
        var extra = Id(await Post(apiBase, p + "/fields", token, new { name = "Extra", type = "string" }));
        var bug = Id(await Post(apiBase, p + "/task-types", token, new
        {
            name = "Bug", statusSetId = set,
            fields = new[] { new { fieldId = estimate, required = false }, new { fieldId = labels, required = false }, new { fieldId = level, required = false } }
        }));
        var story = Id(await Post(apiBase, p + "/task-types", token, new { name = "Story", statusSetId = set }));
        var tsk = Id(await Post(apiBase, p + "/series", token, new { name = "Tasks", prefix = "TSK" }));
        var web = Id(await Post(apiBase, p + "/series", token, new { name = "Web", prefix = "WEB" }));
        var linkTypes = await GraphReads.Query(_http, apiBase, "query($p: UUID!) { project(id: $p) { linkTypes { data { id name } } } }", new { p = projectId }, token);
        var linkData = linkTypes.Body!["data"]!["project"]!["linkTypes"]!["data"]!.AsArray();
        string Link(string name) => linkData.First(x => x!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<string>();

        async Task<string> Add(string title, string type, string status, string[] series, params (string Field, string[] Values)[] fields) =>
            Id(await Post(apiBase, p + "/tasks", token, new
            {
                title, typeId = type, statusId = status, seriesIds = series,
                fields = new { values = fields.Select(x => new { fieldId = x.Field, values = x.Values }).ToArray() }
            }));

        var one = await Add("Логин падает", bug, todo, [tsk], (estimate, ["8"]), (labels, ["bug", "ui"]), (level, ["High"]));
        var two = await Add("Кнопка", bug, doing, [tsk, web], (estimate, ["3"]), (labels, ["ui", "bug"]));
        var three = await Add("Отчёт", story, done, [web]);
        var four = await Add("Экспорт логина", bug, todo, [], (labels, ["bug"]), (level, ["Low"]));
        var five = await Add("Заметка", story, todo, [tsk], (extra, ["x"]));
        await Post(apiBase, p + $"/tasks/{one}/links", token, new { typeId = Link("Blocks"), targetId = two });
        await Post(apiBase, p + $"/tasks/{three}/links", token, new { typeId = Link("Parent/Child"), targetId = four });

        var board = await Post(apiBase, p + "/boards", token, new
        {
            name = "Main", statusSetIds = new[] { set },
            columns = new[] { new { name = "Open", statusIds = new[] { todo, doing }, fieldConditions = Array.Empty<object>() }, new { name = "Closed", statusIds = new[] { done }, fieldConditions = Array.Empty<object>() } }
        });
        return new Stand(apiBase, token, projectId, Id(board), board["columns"]![0]!["id"]!.GetValue<string>());
    }

    private async Task<string[]> Titles(Stand stand, string query)
    {
        var (_, body) = await GraphReads.Query(_http, stand.Base, ListQuery, new { project = stand.Project, query }, stand.Token);
        Assert.True(body!["errors"] == null, $"{query}: {body["errors"]}");
        return body["data"]!["project"]!["tasks"]!["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()).ToArray();
    }

    private async Task<JsonNode> Analyze(Stand stand, string text, int? cursor = null)
    {
        var (_, body) = await GraphReads.Query(_http, stand.Base, AnalysisQuery, new { project = stand.Project, text, cursor }, stand.Token);
        Assert.True(body!["errors"] == null, $"{text}: {body["errors"]}");
        return body["data"]!["project"]!["taskQueryAnalysis"]!;
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task The_string_expresses_every_list_filter_with_and_or_and_parentheses(string mode)
    {
        var s = await Seed(mode);
        string[] all = ["Логин падает", "Кнопка", "Отчёт", "Экспорт логина", "Заметка"];
        Assert.Equal(all, await Titles(s, ""));

        Assert.Equal(["Логин падает", "Экспорт логина", "Заметка", "Отчёт"], await Titles(s, ".Status == \"Todo\" || .Status == \"Done\" | sort .Status"));
        Assert.Equal(["Логин падает", "Отчёт", "Экспорт логина", "Заметка"], await Titles(s, ".Status == [\"Todo\", \"Done\"]"));
        Assert.Equal(["Кнопка"], await Titles(s, ".Status != [\"Todo\", \"Done\"]"));
        Assert.Equal(["Логин падает", "Экспорт логина"], await Titles(s, "(.Status == \"Todo\" || .Status == \"Done\") && .Type == \"Bug\""));
        Assert.Equal(["Отчёт", "Заметка"], await Titles(s, ".Type == \"Story\""));
        Assert.Equal(["Логин падает", "Кнопка", "Заметка"], await Titles(s, ".Series.Contains(\"TSK\")"));
        Assert.Equal(["Логин падает", "Заметка"], await Titles(s, ".Series == \"TSK\""));
        Assert.Equal(["Кнопка"], await Titles(s, ".Series == [\"WEB\", \"TSK\"]"));
        Assert.Equal(["Экспорт логина"], await Titles(s, ".Series.IsUnset()"));
        Assert.Equal(["Кнопка", "Отчёт"], await Titles(s, ".Series.ContainsAny([\"WEB\"])"));
        Assert.Equal(["Кнопка"], await Titles(s, ".Series.ContainsAll([\"TSK\", \"WEB\"])"));
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task Fields_support_comparisons_checks_and_multiple_values(string mode)
    {
        var s = await Seed(mode);
        Assert.Equal(["Логин падает"], await Titles(s, ".Field.Estimate > 3"));
        Assert.Equal(["Логин падает", "Кнопка"], await Titles(s, ".Field.Estimate >= 3"));
        Assert.Equal(["Кнопка", "Отчёт", "Экспорт логина", "Заметка"], await Titles(s, ".Field.Estimate != 8"));
        Assert.Equal(["Логин падает"], await Titles(s, ".Field.Level == \"High\""));
        Assert.Equal(["Логин падает", "Кнопка"], await Titles(s, ".Field.Estimate.IsSet()"));
        Assert.Equal(["Отчёт", "Экспорт логина", "Заметка"], await Titles(s, ".Field.Estimate.IsUnset()"));
        Assert.Equal(["Логин падает", "Кнопка", "Экспорт логина"], await Titles(s, ".Field.Estimate.IsAttached()"));
        Assert.Equal(["Отчёт", "Заметка"], await Titles(s, ".Field.Estimate.IsDetached()"));
        Assert.Equal(["Заметка"], await Titles(s, ".Field.Extra.IsAttached()"));

        Assert.Equal(["Экспорт логина"], await Titles(s, ".Field.Метки == \"bug\""));
        Assert.Equal(["Логин падает", "Кнопка"], await Titles(s, ".Field.Метки == [\"ui\", \"bug\"]"));
        Assert.Equal(["Логин падает", "Кнопка", "Отчёт", "Заметка"], await Titles(s, ".Field.Метки != \"bug\""));
        Assert.Equal(["Логин падает", "Кнопка", "Экспорт логина"], await Titles(s, ".Field.Метки.Contains(\"bug\")"));
        Assert.Equal(["Логин падает", "Кнопка"], await Titles(s, ".Field.Метки.ContainsAll([\"bug\", \"ui\"])"));
        Assert.Equal(["Логин падает", "Кнопка", "Экспорт логина"], await Titles(s, ".Field.Метки.ContainsAny([\"bug\", \"docs\"])"));
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task Negation_links_parent_and_title_work_in_the_storage(string mode)
    {
        var s = await Seed(mode);
        Assert.Equal(["Кнопка", "Отчёт"], await Titles(s, "!(.Status == \"Todo\")"));
        Assert.Equal(["Кнопка", "Заметка"], await Titles(s, ".Field.Estimate < 5 || (.Type == \"Story\" && !.Series.Contains(\"WEB\"))"));
        Assert.Equal(["Логин падает"], await Titles(s, ".Link.Blocks.IsSet()"));
        Assert.Equal(["Кнопка"], await Titles(s, ".Link.\"is blocked by\".IsSet()"));
        Assert.Equal(["Экспорт логина"], await Titles(s, ".Parent.IsSet()"));
        Assert.Equal(4, (await Titles(s, ".Parent.IsUnset()")).Length);
        Assert.Equal(["Логин падает", "Экспорт логина"], await Titles(s, ".Title.Contains(\"лог\")"));
        Assert.Equal(["Кнопка"], await Titles(s, ".Title == \"кнопка\""));
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task Sort_in_the_string_orders_the_list(string mode)
    {
        var s = await Seed(mode);
        Assert.Equal(["Логин падает", "Кнопка"], await Titles(s, ".Field.Estimate.IsSet() | sort -.Field.Estimate"));
        Assert.Equal(["Заметка", "Кнопка", "Логин падает", "Отчёт", "Экспорт логина"], await Titles(s, "| sort .Title"));
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task Tree_and_board_columns_take_the_query_too(string mode)
    {
        var s = await Seed(mode);
        var (_, tree) = await GraphReads.Query(_http, s.Base, TreeQuery, new { project = s.Project, query = ".Type == \"Story\" || .Parent.IsSet()" }, s.Token);
        Assert.True(tree!["errors"] == null, tree["errors"]?.ToJsonString());
        Assert.Equal(3, tree["data"]!["project"]!["taskTree"]!["totalCount"]!.GetValue<int>());

        var (_, column) = await GraphReads.Query(_http, s.Base, ColumnQuery, new { project = s.Project, board = s.Board, column = s.Column, query = ".Field.Estimate > 3 || .Type == \"Story\"" }, s.Token);
        Assert.True(column!["errors"] == null, column["errors"]?.ToJsonString());
        Assert.Equal(["Логин падает", "Заметка"], column["data"]!["project"]!["columnTasks"]!["data"]!.AsArray().Select(x => x!["title"]!.GetValue<string>()));
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task An_invalid_query_is_refused_with_its_code(string mode)
    {
        var s = await Seed(mode);
        var (_, body) = await GraphReads.Query(_http, s.Base, ListQuery, new { project = s.Project, query = ".Status == \"Nope\"" }, s.Token);
        Assert.NotNull(body!["errors"]);
        Assert.Equal("query.unknownStatus", body["errors"]![0]!["extensions"]!["code"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task Analysis_answers_errors_and_suggestions_from_the_project(string mode)
    {
        var s = await Seed(mode);

        var valid = await Analyze(s, ".Status == \"Todo\" | sort -.Updated");
        Assert.True(valid["valid"]!.GetValue<bool>());
        Assert.True(valid["hasSort"]!.GetValue<bool>());
        Assert.Empty(valid["errors"]!.AsArray());

        var unknown = await Analyze(s, ".Status == \"Nope\"");
        Assert.False(unknown["valid"]!.GetValue<bool>());
        var error = unknown["errors"]![0]!;
        Assert.Equal("query.unknownStatus", error["code"]!.GetValue<string>());
        Assert.Equal(11, error["start"]!.GetValue<int>());
        Assert.Equal(17, error["end"]!.GetValue<int>());
        Assert.Equal("Nope", error["params"]![0]!["value"]!.GetValue<string>());

        var values = await Analyze(s, ".Status == ");
        Assert.Equal(["Doing", "Done", "Todo"], values["suggestions"]!.AsArray().Select(x => x!["label"]!.GetValue<string>()));
        Assert.Equal("value", values["suggestions"]![0]!["kind"]!.GetValue<string>());

        var fields = await Analyze(s, ".Field.Мет");
        Assert.Equal("string[]", fields["suggestions"]![0]!["detail"]!.GetValue<string>());

        var middle = await Analyze(s, ".Sta == \"Todo\"", 3);
        Assert.Equal(".Status", middle["suggestions"]![0]!["label"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await GraphReads.Query(_http, s.Base, AnalysisQuery, new { project = s.Project, text = "", cursor = (int?)null }, s.Token)).Status);
    }
}
