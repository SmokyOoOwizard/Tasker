using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HotChocolate.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Web;
using Tasker.Web.Graph;
using Xunit;

namespace Tasker.Tests;

public class GraphTests
{
    private sealed class EmptyModule : Autofac.Module;

    private static readonly HashSet<string> Extensions = ["WorkspaceMcp.agentHeader"];

    [Fact]
    public async Task The_schema_file_matches_the_code()
    {
        var path = RepoFile("graphql", "schema.graphql");
        Assert.True(
            (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n") == await GraphSetup.Print(),
            "graphql/schema.graphql differs from the schema in code: run dotnet run --project dotnet/tools/Tasker.GraphSchema and commit the file");
    }

    [Fact]
    public async Task Every_dto_property_is_a_field_and_every_type_is_declared_explicitly()
    {
        var schema = await GraphSetup.BuildSchema();
        var types = schema.Types.OfType<ObjectType>()
            .Where(x => x.Name is not ("Query" or "Subscription") && !x.Name.StartsWith("__", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(types, x => x.Name == "TaskDetails");

        var inferred = types.Where(x => x.GetType().Assembly != typeof(GraphSetup).Assembly).Select(x => x.Name).ToArray();
        Assert.True(inferred.Length == 0, $"Types without their own class: {string.Join(", ", inferred)}");

        var missing = types
            .SelectMany(type => type.RuntimeType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(x => x.DeclaringType != typeof(object) && x.Name != "EqualityContract")
                .Select(x => JsonNamingPolicy.CamelCase.ConvertName(x.Name))
                .Where(x => !type.Fields.ContainsField(x))
                .Select(x => $"{type.Name}.{x}"))
            .ToArray();
        var extra = types
            .Where(x => x.Name != "Project")
            .SelectMany(type => type.Fields.Where(x => !x.IsIntrospectionField)
                .Where(x => type.RuntimeType.GetProperty(x.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) == null)
                .Select(x => $"{type.Name}.{x.Name}"))
            .Where(x => !Extensions.Contains(x))
            .ToArray();

        Assert.True(missing.Length == 0, $"DTO properties without a GraphQL field: {string.Join(", ", missing)}");
        Assert.True(extra.Length == 0, $"GraphQL fields without a DTO property: {string.Join(", ", extra)}");
    }

    [Fact]
    public async Task Reads_are_not_served_by_get_any_more()
    {
        await using var api = await ApiHost.Start("files");
        using var http = new HttpClient();

        foreach (var path in new[] { "/projects", api.P(), api.P("/tasks"), api.P("/statuses"), "/workspace/mcp", "/events", "/graphql" })
        {
            using var response = await http.GetAsync(api.BaseUrl + path);
            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"GET {path}: {(int)response.StatusCode}");
        }

        var (status, body) = await GraphReads.Query(http, api.BaseUrl, "{ projects { totalCount data { id name } } }");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body!["data"]!["projects"]!["totalCount"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.Created, (await api.Post(api.P("/statuses"), new { name = "Done", color = "#445566" })).Status);
    }

    [Fact]
    public async Task A_read_in_a_sqlite_workspace_publishes_no_change_and_a_write_does()
    {
        await using var api = await ApiHost.Start("sqlite");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await GraphReads.Subscribe(http, api.BaseUrl);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.StartsWith(": connected", await reader.ReadLineAsync());

        var changes = System.Threading.Channels.Channel.CreateUnbounded<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                    if (GraphReads.Change(line) is { } change)
                        changes.Writer.TryWrite(change["entity"]!.GetValue<string>());
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
            }
        });

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        while (changes.Reader.TryRead(out _))
        {
        }

        Assert.Equal(HttpStatusCode.OK, (await GraphReads.Get(http, api.BaseUrl, api.P("/statuses"))).Status);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(changes.Reader.TryRead(out var early), $"a read published {early}");

        await api.Post(api.P("/statuses"), new { name = "Done", color = "#445566" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.Equal("unknown", await changes.Reader.ReadAsync(timeout.Token));
    }

    [Fact]
    public async Task Errors_carry_the_code_and_status_of_the_rest_api()
    {
        await using var api = await ApiHost.Start("files");
        using var http = new HttpClient();

        var (status, body) = await GraphReads.Query(http, api.BaseUrl,
            "query($id: UUID!) { project(id: $id) { statuses(descriptionLength: -5) { totalCount } } }", new { id = api.ProjectId });
        Assert.Equal(HttpStatusCode.OK, status);
        var error = body!["errors"]![0]!;
        Assert.Equal("BAD_REQUEST", error["extensions"]!["code"]!.GetValue<string>());
        Assert.Equal(400, error["extensions"]!["status"]!.GetValue<int>());
        Assert.Contains("descriptionLength", error["message"]!.GetValue<string>());

        (status, body) = await GraphReads.Query(http, api.BaseUrl, "{ projects { nope } }");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Null(body!["data"]);

        (status, body) = await GraphReads.Query(http, api.BaseUrl, "{ registration { open } }");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("NOT_FOUND", body!["errors"]![0]!["extensions"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task On_the_server_only_registration_is_read_without_a_login()
    {
        var builder = TaskerWebApp.CreateBuilder<EmptyModule>([], TaskerMode.Server);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapTasker();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var api = address + "/api";
            using var http = new HttpClient();

            var (status, body) = await GraphReads.Query(http, api, "{ registration { open } }");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(body!["data"]!["registration"]!["open"]!.GetValue<bool>());

            (status, body) = await GraphReads.Query(http, api, "{ projects { totalCount } }");
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal("UNAUTHENTICATED", body!["errors"]![0]!["extensions"]!["code"]!.GetValue<string>());

            using var register = await http.PostAsJsonAsync(api + "/auth/register", new { username = "owner", email = "owner@example.com", password = "Passw0rd!123" });
            var token = JsonNode.Parse(await register.Content.ReadAsStringAsync())!["accessToken"]!.GetValue<string>();

            (status, body) = await GraphReads.Query(http, api, "{ registration { open } me { username isAdmin } projects { totalCount } }", token: token);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(body!["data"]!["registration"]!["open"]!.GetValue<bool>());
            Assert.Equal("owner", body["data"]!["me"]!["username"]!.GetValue<string>());
            Assert.Equal(0, body["data"]!["projects"]!["totalCount"]!.GetValue<int>());

            (status, body) = await GraphReads.Query(http, api, "{ workspaceMcp { daemon } }", token: token);
            Assert.Equal("NOT_FOUND", body!["errors"]![0]!["extensions"]!["code"]!.GetValue<string>());

            using var subscribe = new HttpRequestMessage(HttpMethod.Post, api + GraphSetup.Path)
            {
                Content = JsonContent.Create(new { query = "subscription { workspaceChanges { entity } }" })
            };
            subscribe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var stream = await http.SendAsync(subscribe);
            Assert.Contains("only in a desktop workspace", await stream.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static string RepoFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException(Path.Combine(parts));
    }
}
