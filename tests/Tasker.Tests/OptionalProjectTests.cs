using Tasker.Global;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// <c>--project</c> можно не указывать, если в рабочей области ровно один проект: команда работает с ним.
/// Проектов нет или несколько — понятная ошибка; явный параметр и <c>TASKER_PROJECT</c> по-прежнему главнее.
/// </summary>
public class OptionalProjectTests
{
    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    /// <summary>Единственный проект Demo со статусами, набором, типом и серией — всё создано без <c>--project</c>.</summary>
    private static async Task Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.Run("status", "create", "Todo"));
        await Ok(ws.Run("status", "create", "Done"));
        await Ok(ws.Run("status-set", "create", "Flow", "--status", "Todo", "Done"));
        await Ok(ws.Run("task-type", "create", "Bug", "--status-set", "Flow"));
        await Ok(ws.Run("series", "create", "Tasks", "--prefix", "TSK"));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task With_a_single_project_every_command_works_without_project(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        var task = await Ok(ws.Run("task", "create", "Fix login", "--type", "Bug", "--series", "TSK", "--json"));
        var id = task.Json["id"]!.GetValue<string>();

        Assert.Contains("Fix login", (await Ok(ws.Run("task", "list"))).Out);
        Assert.Contains("TSK-1", (await Ok(ws.Run("task", "get", id))).Out);
        Assert.Equal("Todo", (await Ok(ws.Run("status", "get", "Todo", "--json"))).Json["name"]!.GetValue<string>());
        Assert.Contains("Flow", (await Ok(ws.Run("status-set", "list"))).Out);
        Assert.Contains("Bug", (await Ok(ws.Run("task-type", "list"))).Out);
        Assert.Contains("TSK", (await Ok(ws.Run("series", "list"))).Out);
        await Ok(ws.Run("board", "create", "Main", "--status-set", "Flow", "--column", "All=Todo,Done"));
        Assert.Contains("Main", (await Ok(ws.Run("board", "list"))).Out);
        await Ok(ws.Run("task", "update", id, "--status", "Done"));
        Assert.Contains("Done", (await Ok(ws.Run("task", "get", id))).Out);
        Assert.StartsWith("Locked Task", (await Ok(ws.Run("lock", "acquire", "task", id))).Out);
        await Ok(ws.Run("task", "delete", id));
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Explicit_project_and_the_environment_variable_still_work_with_a_single_project(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);

        Assert.Contains("Todo", (await Ok(ws.InProject("Demo", "status", "list"))).Out);
        Assert.Equal(1, (await ws.InProject("Nope", "status", "list")).Code);

        // Указанный, но несуществующий проект — ошибка, а не «возьмём единственный».
        using var env = AppEnvironment.Override("TASKER_PROJECT", "Missing");
        var result = await ws.Run("status", "list");
        Assert.Equal(1, result.Code);
        Assert.Contains("No project 'Missing'", result.Err);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Several_projects_need_the_option_and_the_error_says_how_many(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        await Ok(ws.Run("project", "create", "Other"));

        var result = await ws.Run("status", "list");

        Assert.Equal(1, result.Code);
        Assert.Contains("Project is required", result.Err);
        Assert.Contains("2 projects", result.Err);
        Assert.Empty(result.Out);
        Assert.Contains("Todo", (await Ok(ws.InProject("Demo", "status", "list"))).Out);

        // Остался один проект — параметр снова необязателен.
        await Ok(ws.Run("project", "delete", "Other", "--yes"));
        Assert.Contains("Todo", (await Ok(ws.Run("status", "list"))).Out);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Without_projects_the_error_says_to_create_one(string storage)
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create(storage);

        var result = await ws.Run("status", "list");

        Assert.Equal(1, result.Code);
        Assert.Contains("no projects", result.Err);
        Assert.Contains("tasker project create", result.Err);
    }

    [Fact]
    public async Task Commands_over_all_projects_are_not_narrowed_to_the_single_project()
    {
        using var home = new IsolatedHome();
        using var ws = TestWorkspace.Create("files");
        await Seed(ws);

        // cleanup без --project по-прежнему работает со всеми проектами, а не требует и не выбирает проект.
        var result = await ws.Run("cleanup", "--check");
        Assert.True(result.Code is 0 or 2, result.Err);
    }
}
