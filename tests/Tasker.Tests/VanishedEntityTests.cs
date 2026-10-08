using Tasker.Cli;
using Tasker.Core;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Global;
using Xunit;

namespace Tasker.Tests;

/// <summary>
/// TSK-99: тип задач или набор статусов исчезли посреди операции (проект удаляют параллельно с созданием задач): вместо голого
/// <see cref="InvalidOperationException"/> — понятная ошибка «не найдено» (консоль: <c>Not found: …</c>).
/// </summary>
public class VanishedEntityTests
{
    private static async Task<CliResult> Ok(Task<CliResult> run)
    {
        var result = await run;
        Assert.True(result.Code == 0, $"exit {result.Code}: {result.Err}");
        return result;
    }

    private static async Task<(Guid Type, Guid Set)> Seed(TestWorkspace ws)
    {
        await Ok(ws.Run("project", "create", "Demo"));
        await Ok(ws.InProject("Demo", "status", "create", "Todo"));
        var set = (await Ok(ws.InProject("Demo", "status-set", "create", "Flow", "--status", "Todo"))).Id;
        var type = (await Ok(ws.InProject("Demo", "task-type", "create", "Bug", "--status-set", "Flow"))).Id;
        await Ok(ws.InProject("Demo", "task", "create", "One", "--type", "Bug"));
        return (type, set);
    }

    private static async Task<Guid> ProjectId(TestWorkspace ws) =>
        Guid.Parse((await Ok(ws.Run("project", "list", "--json"))).Json["data"]![0]!["id"]!.GetValue<string>());

    private static WorkspaceSettings Settings(TestWorkspace ws) =>
        ws.Location[0] == "--sqlite" ? new WorkspaceSettings(null, ws.Location[1]) : new WorkspaceSettings(ws.Root, null);

    /// <summary>Только папка: в SQLite внешний ключ не даёт убрать набор из-под типа — там проект исчезает целиком (см. следующий тест).</summary>
    [Fact]
    public async Task Create_after_the_status_set_vanished_is_not_found()
    {
        using var ws = TestWorkspace.Create("files");
        var (type, set) = await Seed(ws);
        var project = await ProjectId(ws);

        await using (var session = await Session.Open(Settings(ws), CancellationToken.None))
        {
            var sets = session.Get<IStatusSetStorage>();
            Assert.True(await sets.Delete(project, set, (await sets.GetById(project, set))!.Version));

            await Assert.ThrowsAsync<TaskerNotFoundException>(() =>
                session.Get<TaskService>().Create(project, new CreateTask("late", null, type, null)));
        }

        var result = await ws.InProject("Demo", "task", "create", "Late", "--type", "Bug");
        Assert.Equal(1, result.Code);
        Assert.StartsWith("Not found:", result.Err);
    }

    [Theory, MemberData(nameof(TestWorkspace.Storages), MemberType = typeof(TestWorkspace))]
    public async Task Describe_after_the_project_vanished_is_not_found(string storage)
    {
        using var ws = TestWorkspace.Create(storage);
        await Seed(ws);
        var project = await ProjectId(ws);

        await using var session = await Session.Open(Settings(ws), CancellationToken.None);
        var task = (await session.Get<ITaskStorage>().GetRange(project, null, new Tasker.Core.Dto.Page(0, 10))).Data[0];

        // Задачу прочитали, а проект удалили (вместе с типом и набором) — до того, как дошли до её полей.
        await Ok(ws.Run("project", "delete", "Demo", "--yes"));

        await Assert.ThrowsAsync<TaskerNotFoundException>(() => session.Get<TaskService>().Describe(project, task));
        await Assert.ThrowsAsync<TaskerNotFoundException>(() => session.Get<TaskService>().GetFields(project, task));
    }
}
