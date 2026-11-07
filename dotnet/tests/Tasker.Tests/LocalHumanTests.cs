using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tasker.Core;
using Tasker.Core.Users;
using Tasker.Daemon;
using Tasker.Global;
using Tasker.Storage.Files.Workspaces;
using Tasker.Web;
using Tasker.Web.Workspaces;
using Xunit;

namespace Tasker.Tests;

public class LocalHumanDaemonTests : IDisposable
{
    private readonly DaemonFixture _daemon = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose()
    {
        _http.Dispose();
        _daemon.Dispose();
    }

    private static string Base(DaemonStatus status) => $"http://127.0.0.1:{status.Port}/w/{status.Workspaces[0].Key}/api";

    private static async Task<JsonNode[]> Humans(string folder)
    {
        var list = await TaskerProcess.Run("user", "list", "--json", "-w", folder);
        Assert.Equal(0, list.Code);
        return list.Json["data"]!.AsArray().Select(x => x!).Where(x => x["kind"]!.GetValue<string>() == "human").ToArray();
    }

    private async Task<Guid> Project(string apiBase, string name)
    {
        using var created = await _http.PostAsJsonAsync($"{apiBase}/projects", new { name });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return Guid.Parse((await created.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task First_action_creates_the_person_named_by_whoami_and_mcp_stays_the_agent()
    {
        await _daemon.Store.SetUserName("Иван Петров");
        var folder = await _daemon.Workspace("a", "Alpha");
        var status = await _daemon.Start();
        Assert.Empty(await Humans(folder));

        await Project(Base(status), "Beta");

        var human = Assert.Single(await Humans(folder));
        Assert.Equal("Иван Петров", human["username"]!.GetValue<string>());
        Assert.Equal(_daemon.Store.Load().LocalUser!.Id.ToString(), human["id"]!.GetValue<string>());

        var mcp = JsonNode.Parse(await _daemon.CallTool(status.Workspaces[0].Key, "whoami"))!;
        Assert.Equal("agent", mcp["username"]!.GetValue<string>());
        Assert.Equal("agent", mcp["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Parallel_first_actions_create_one_person()
    {
        await _daemon.Store.SetUserName("Ivan");
        var folder = await _daemon.Workspace("a", "Alpha");
        var status = await _daemon.Start();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Project(Base(status), $"P{i}")));

        Assert.Single(await Humans(folder));
        Assert.Single(Directory.GetFiles(Path.Combine(folder, ".tasker", "users")),
            x => File.ReadAllText(x).Contains("username: Ivan"));
    }

    [Fact]
    public async Task A_name_changed_while_the_daemon_was_stopped_renames_the_same_person()
    {
        await _daemon.Store.SetUserName("Ivan");
        var folder = await _daemon.Workspace("a", "Alpha");
        var status = await _daemon.Start();
        await Project(Base(status), "Beta");
        var id = Assert.Single(await Humans(folder))["id"]!.GetValue<string>();

        Assert.Equal(0, (await _daemon.Mcp("stop")).Code);
        await _daemon.Store.SetUserName("Ivan Petrov");
        status = await _daemon.Start();

        Assert.Equal("Ivan", Assert.Single(await Humans(folder))["username"]!.GetValue<string>());
        await Project(Base(status), "Gamma");

        var renamed = Assert.Single(await Humans(folder));
        Assert.Equal(id, renamed["id"]!.GetValue<string>());
        Assert.Equal("Ivan Petrov", renamed["username"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reads_and_edit_locks_leave_the_git_working_copy_clean()
    {
        await _daemon.Store.SetUserName("Ivan");
        using var repo = new GitRepo();
        var created = await TaskerProcess.Run("project", "create", "Alpha", "--json", "-w", repo.Root);
        Assert.Equal(0, created.Code);
        var project = created.Json["id"]!.GetValue<string>();
        await _daemon.Mcp("workspace", "add", repo.Root);
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "init");
        var settings = File.ReadAllText(_daemon.Store.FilePath);
        var status = await _daemon.Start();
        var api = Base(status);

        var read = await GraphReads.Get(_http, api, "/projects");
        Assert.Equal(HttpStatusCode.OK, read.Status);
        Assert.Equal(settings, File.ReadAllText(_daemon.Store.FilePath));

        using var locked = await _http.PostAsJsonAsync($"{api}/projects/{project}/lock", new { });
        var holder = (await locked.Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.Equal("Ivan", holder["holder"]!.GetValue<string>());
        Assert.True(holder["mine"]!.GetValue<bool>());
        using var released = await _http.DeleteAsync($"{api}/projects/{project}/lock");
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);

        Assert.Equal("", repo.Git("status", "--porcelain"));
    }

    [Fact]
    public async Task A_lock_taken_before_the_first_action_still_belongs_to_the_person_after_it()
    {
        await _daemon.Store.SetUserName("Ivan");
        var folder = await _daemon.Workspace("a", "Alpha");
        var listed = (await TaskerProcess.Run("project", "list", "--json", "-w", folder)).Json["data"]![0]!;
        var project = listed["id"]!.GetValue<string>();
        var status = await _daemon.Start();
        var api = Base(status);

        using var locked = await _http.PostAsJsonAsync($"{api}/projects/{project}/lock", new { });
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);

        using var renamed = await _http.PatchAsJsonAsync($"{api}/projects/{project}", new { name = "Alpha 2", version = listed["version"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        using var again = await _http.PostAsJsonAsync($"{api}/projects/{project}/lock", new { });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Single(await Humans(folder));
    }
}

public class LocalHumanIdentityTests
{
    private sealed class Fixed(LocalPerson? person) : ILocalPerson
    {
        public LocalPerson? Person { get; set; } = person;

        public Task<LocalPerson?> Current(CancellationToken ct = default) => Task.FromResult(Person);

        public Task<LocalPerson?> Remember(CancellationToken ct = default) => Task.FromResult(Person);
    }

    private static readonly LocalPerson Ivan = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"), "Ivan", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    private static User Other(string name, UserKind kind = UserKind.Human) => new()
    {
        Id = Guid.NewGuid(),
        Username = name,
        Kind = kind,
        IsAdmin = false,
        CreatedAt = DateTimeOffset.UnixEpoch,
        Version = Versioning.New
    };

    private static Task<Session> Open(string folder) =>
        Session.Open(new WorkspaceSettings(folder), CancellationToken.None);

    private static string TempFolder() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tasker-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task The_service_scope_sees_the_person_as_the_current_user()
    {
        using var home = new IsolatedHome();
        await home.Store.SetUserName("Ivan");
        var folder = TempFolder();

        var builder = TaskerWebApp.CreateBuilder<WebTestModule>([], TaskerMode.Local);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapTasker();
        app.MapPost("/api/test/current", async (ICurrentUser current, UserService users) =>
            Results.Ok(new { id = current.Id, name = (await users.GetCurrent()).Username }));
        await app.StartAsync();

        await using var lease = await app.Services.GetRequiredService<WorkspaceRegistry>().Open(WorkspaceLocation.Files(folder));
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        using var http = new HttpClient();
        using var response = await http.PostAsJsonAsync($"{address}{lease.BasePath}/api/test/current", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonNode>())!;

        Assert.Equal(home.Store.Load().LocalUser!.Id.ToString(), body["id"]!.GetValue<string>());
        Assert.Equal("Ivan", body["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_settings_keep_one_id_derived_from_the_first_name()
    {
        using var home = new IsolatedHome();
        await home.Store.SetUserName("Ivan");
        var person = new SettingsLocalPerson(home.Store, TimeProvider.System);

        var read = await person.Current();
        Assert.Null(home.Store.Load().LocalUser);
        Assert.Equal(SettingsLocalPerson.IdOf("Ivan"), read!.Id);

        var first = await person.Remember();
        await home.Store.SetUserName("Ivan Petrov");
        var second = await person.Remember();

        Assert.Equal(SettingsLocalPerson.IdOf(" ivan "), first!.Id);
        Assert.Equal(first.Id, second!.Id);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal("Ivan Petrov", second.Name);
    }

    [Fact]
    public async Task Broken_settings_leave_the_request_without_a_person()
    {
        using var home = new IsolatedHome();
        await File.WriteAllTextAsync(home.SettingsFile, "{ not json");
        var person = new SettingsLocalPerson(home.Store, TimeProvider.System);

        Assert.Null(await person.Current());
        Assert.Null(await person.Remember());
        Assert.Equal("{ not json", await File.ReadAllTextAsync(home.SettingsFile));
    }

    [Fact]
    public async Task Find_writes_nothing_and_ensure_creates_once()
    {
        var folder = TempFolder();
        await using var session = await Open(folder);
        var users = session.Get<IUserStorage>();
        var human = new LocalHuman(new Fixed(Ivan));

        Assert.Null(await human.Find(users));
        Assert.Equal(0, await users.Count());

        Assert.Equal(Ivan.Id, await human.Ensure(users));
        Assert.Equal(Ivan.Id, await human.Ensure(users));
        Assert.Equal(Ivan.Id, await human.Find(users));

        var user = (await users.GetById(Ivan.Id))!;
        Assert.Equal(UserKind.Human, user.Kind);
        Assert.Equal(Ivan.CreatedAt, user.CreatedAt);
        Assert.Equal(1, await users.Count());
    }

    [Fact]
    public async Task A_person_with_the_same_name_is_used_instead_of_a_duplicate()
    {
        await using var session = await Open(TempFolder());
        var users = session.Get<IUserStorage>();
        var existing = Other("ivan");
        await users.Add(existing, null);

        Assert.Equal(existing.Id, await new LocalHuman(new Fixed(Ivan)).Ensure(users));
        Assert.Equal(1, await users.Count());
    }

    [Fact]
    public async Task A_name_taken_by_an_agent_gets_a_suffix()
    {
        await using var session = await Open(TempFolder());
        var users = session.Get<IUserStorage>();
        await users.Add(Other("agent", UserKind.Agent), null);

        var id = await new LocalHuman(new Fixed(Ivan with { Name = "agent" })).Ensure(users);

        Assert.Equal("agent-2", (await users.GetById(id!.Value))!.Username);
    }

    [Fact]
    public async Task Renaming_to_a_taken_name_keeps_the_old_one_and_does_not_fail()
    {
        await using var session = await Open(TempFolder());
        var users = session.Get<IUserStorage>();
        var person = new Fixed(Ivan);
        var human = new LocalHuman(person);
        await human.Ensure(users);
        await users.Add(Other("Petr", UserKind.Agent), null);

        person.Person = Ivan with { Name = "Petr" };

        Assert.Equal(Ivan.Id, await human.Ensure(users));
        Assert.Equal("Ivan", (await users.GetById(Ivan.Id))!.Username);
    }

    [Fact]
    public async Task A_file_with_conflict_markers_does_not_stop_the_request()
    {
        var folder = TempFolder();
        await using var session = await Open(folder);
        var users = session.Get<IUserStorage>();
        var human = new LocalHuman(new Fixed(Ivan));
        await human.Ensure(users);

        var file = Path.Combine(folder, ".tasker", "users", $"{Ivan.Id}.yaml");
        await File.WriteAllTextAsync(file, "<<<<<<< ours\nusername: a\n=======\nusername: b\n>>>>>>> theirs\n");

        Assert.Null(await human.Find(users));
        Assert.Null(await human.Ensure(users));
        Assert.StartsWith("<<<<<<<", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Branches_that_both_created_the_person_merge_without_conflicts_and_duplicates()
    {
        using var repo = new GitRepo();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "README"), "x");
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "init");

        foreach (var branch in new[] { "a", "b" })
        {
            repo.Git("checkout", "-q", "-b", branch, "main");
            await using (var session = await Open(repo.Root))
                await new LocalHuman(new Fixed(Ivan)).Ensure(session.Get<IUserStorage>());
            repo.Git("add", "-A");
            repo.Git("commit", "-q", "-m", branch);
        }

        repo.Git("checkout", "-q", "a");
        repo.Git("merge", "-q", "--no-edit", "b");

        await using var merged = await Open(repo.Root);
        Assert.Equal(1, await merged.Get<IUserStorage>().Count());
    }
}
