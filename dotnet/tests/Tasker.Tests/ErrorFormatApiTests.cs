using System.Net;
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

public class ErrorFormatApiTests
{
    private sealed class EmptyModule : Module;

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();
    private static string Version(JsonNode node) => node["version"]!.GetValue<string>();

    private static void AssertRefusal(HttpStatusCode expected, (HttpStatusCode Status, JsonNode? Body) response, string code)
    {
        Assert.Equal(expected, response.Status);
        Assert.Equal(code, response.Body!["code"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(response.Body["error"]!.GetValue<string>()));
        Assert.IsType<JsonObject>(response.Body["params"]);
        Assert.IsType<JsonArray>(response.Body["usages"]);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task A_status_in_use_is_refused_with_its_usages(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var status = (await api.Post(api.P("/statuses"), new { name = "Busy", color = "#445566" })).Body!;
        var set = (await api.Post(api.P("/status-sets"), new { name = "Busy set", statusIds = new[] { Id(status) } })).Body!;
        var type = (await api.Post(api.P("/task-types"), new { name = "Busy type", statusSetId = Id(set) })).Body!;
        var task = await api.Post(api.P("/tasks"), new { title = "Fix login", typeId = Id(type), statusId = Id(status) });
        Assert.Equal(HttpStatusCode.Created, task.Status);
        var board = await api.Post(api.P("/boards"), new
        {
            name = "Main",
            statusSetIds = new[] { Id(set) },
            columns = new[] { new { name = "Work", statusIds = new[] { Id(status) }, dropStatuses = new Dictionary<string, string> { [Id(set)] = Id(status) } } }
        });
        Assert.Equal(HttpStatusCode.Created, board.Status);

        var refused = await api.Delete(api.P($"/statuses/{Id(status)}?version={Uri.EscapeDataString(Version(status))}"));

        AssertRefusal(HttpStatusCode.Conflict, refused, "status.inUse");
        Assert.Equal("Busy", refused.Body!["params"]!["name"]!.GetValue<string>());
        Assert.StartsWith("Status 'Busy' is used by", refused.Body["error"]!.GetValue<string>());
        var usages = refused.Body["usages"]!.AsArray();
        var tasks = Assert.Single(usages, x => x!["kind"]!.GetValue<string>() == "tasks")!;
        Assert.Equal(1, tasks["count"]!.GetValue<int>());
        var reference = Assert.Single(tasks["tasks"]!.AsArray())!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(reference));
        Assert.Equal("Busy set", Assert.Single(usages, x => x!["kind"]!.GetValue<string>() == "statusSet")!["name"]!.GetValue<string>());
        var column = Assert.Single(usages, x => x!["kind"]!.GetValue<string>() == "boardColumn")!;
        Assert.Equal("Main", column["board"]!.GetValue<string>());
        Assert.Equal("Work", column["column"]!.GetValue<string>());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task A_stale_version_is_refused_as_modified_with_the_entity(string storage)
    {
        await using var api = await ApiHost.Start(storage);
        var status = (await api.Post(api.P("/statuses"), new { name = "Fresh", color = "#445566" })).Body!;
        await api.Patch(api.P($"/statuses/{Id(status)}"), new { name = "Changed", version = Version(status) });

        var refused = await api.Patch(api.P($"/statuses/{Id(status)}"), new { name = "Again", version = Version(status) });

        AssertRefusal(HttpStatusCode.Conflict, refused, "entity.modified");
        Assert.Equal("Changed", refused.Body!["params"]!["name"]!.GetValue<string>());
        Assert.Equal("status", refused.Body["params"]!["entity"]!.GetValue<string>());
        Assert.Empty(refused.Body["usages"]!.AsArray());
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task A_lock_of_another_holder_is_refused_with_the_holder_and_expiry(string storage)
    {
        using var home = new IsolatedHome();
        await using var api = await ApiHost.Start(storage);
        var task = await api.NewTask("Locked task");
        Assert.Equal(0, (await TestWorkspace.Invoke(["whoami", "Ivan"])).Code);
        string[] location = ["--workspace", api.Root];
        var locked = await TestWorkspace.Invoke(["lock", "acquire", "task", Id(task), "--project", api.ProjectId.ToString(), .. location]);
        Assert.True(locked.Code == 0, locked.Err);

        var refused = await api.Patch(api.P($"/tasks/{Id(task)}"), new { title = "Other", version = Version(task) });

        AssertRefusal(HttpStatusCode.Conflict, refused, "entity.locked");
        var parameters = refused.Body!["params"]!;
        Assert.Equal("Ivan (console)", parameters["holder"]!.GetValue<string>());
        Assert.Equal("Locked task", parameters["name"]!.GetValue<string>());
        Assert.Equal("task", parameters["entity"]!.GetValue<string>());
        Assert.True(DateTimeOffset.TryParse(parameters["expiresAt"]!.ToString(), out var expires) && expires > DateTimeOffset.UtcNow);
        Assert.Contains("Ivan (console)", refused.Body["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_empty_status_name_is_refused_as_required_with_the_field()
    {
        await using var api = await ApiHost.Start("files");

        var refused = await api.Post(api.P("/statuses"), new { name = "", color = "#445566" });

        AssertRefusal(HttpStatusCode.BadRequest, refused, "validation.required");
        Assert.Equal("statusName", refused.Body!["params"]!["field"]!.GetValue<string>());
        Assert.Equal("Status name is required", refused.Body["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_missing_version_is_refused_as_invalid_without_params()
    {
        await using var api = await ApiHost.Start("files");
        var status = (await api.Post(api.P("/statuses"), new { name = "Plain", color = "#445566" })).Body!;

        var refused = await api.Patch(api.P($"/statuses/{Id(status)}"), new { name = "Other" });

        AssertRefusal(HttpStatusCode.BadRequest, refused, "validation.invalid");
        Assert.Empty(refused.Body!["params"]!.AsObject());
        Assert.StartsWith("Version is required", refused.Body["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unknown_api_route_is_404_not_found()
    {
        await using var api = await ApiHost.Start("files");

        var refused = await api.Send(HttpMethod.Post, "/nothing/here", new { });

        AssertRefusal(HttpStatusCode.NotFound, refused, "entity.notFound");
    }

    [Fact]
    public async Task A_graphql_syntax_error_is_invalid_with_status_400()
    {
        await using var api = await ApiHost.Start("files");
        using var http = new HttpClient();

        var (status, body) = await GraphReads.Query(http, api.BaseUrl, "{ projects { ");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var extensions = body!["errors"]![0]!["extensions"]!;
        Assert.Equal("validation.invalid", extensions["code"]!.GetValue<string>());
        Assert.Equal(400, extensions["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_graphql_unknown_field_is_invalid_with_status_400()
    {
        await using var api = await ApiHost.Start("files");
        using var http = new HttpClient();

        var (status, body) = await GraphReads.Query(http, api.BaseUrl, "{ nope }");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var extensions = body!["errors"]![0]!["extensions"]!;
        Assert.Equal("validation.invalid", extensions["code"]!.GetValue<string>());
        Assert.Equal(400, extensions["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task Server_mode_refuses_without_a_token_and_with_bad_credentials()
    {
        var builder = TaskerWebApp.CreateBuilder<EmptyModule>([], TaskerMode.Server);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapTasker();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var http = new HttpClient { BaseAddress = new Uri(address) };

            using var anonymous = await http.PostAsJsonAsync("/api/projects", new { name = "P" });
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            var body = JsonNode.Parse(await anonymous.Content.ReadAsStringAsync())!;
            Assert.Equal("auth.required", body["code"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(body["error"]!.GetValue<string>()));

            var (status, graph) = await GraphReads.Query(http, address + "/api", "{ projects { totalCount } }");
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            var extensions = graph!["errors"]![0]!["extensions"]!;
            Assert.Equal("auth.required", extensions["code"]!.GetValue<string>());
            Assert.Equal(401, extensions["status"]!.GetValue<int>());

            using var register = await http.PostAsJsonAsync("/api/auth/register", new { username = "owner", email = "owner@example.com", password = "Passw0rd!123" });
            Assert.True(register.IsSuccessStatusCode);
            using var login = await http.PostAsJsonAsync("/api/auth/login", new { login = "owner", password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
            var refusal = JsonNode.Parse(await login.Content.ReadAsStringAsync())!;
            Assert.Equal("auth.invalidCredentials", refusal["code"]!.GetValue<string>());
            Assert.IsType<JsonObject>(refusal["params"]);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task An_unhandled_exception_is_500_unexpected_without_details()
    {
        var builder = TaskerWebApp.CreateBuilder<EmptyModule>([], TaskerMode.Server);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapTasker();
        app.MapGet("/boom", () => Boom());
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var http = new HttpClient { BaseAddress = new Uri(address) };

            using var response = await http.GetAsync("/boom");

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            var body = JsonNode.Parse(text)!;
            Assert.Equal("internal.unexpected", body["code"]!.GetValue<string>());
            Assert.DoesNotContain("secret detail", text);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static string Boom() => throw new InvalidOperationException("secret detail");
}
