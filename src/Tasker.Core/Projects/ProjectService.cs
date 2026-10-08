using Tasker.Core.Boards;
using Tasker.Core.Dto;
using Tasker.Core.Links;
using Tasker.Core.Statuses;
using Tasker.Core.TaskSeries;
using Tasker.Core.Tasks;

namespace Tasker.Core.Projects;

public record CreateProject(string Name);

/// <summary>Поля null — не меняются. Version — версия, которую видел клиент (см. <see cref="Versioning"/>).</summary>
public record UpdateProject(string? Name, string? Version);

/// <summary>Сколько чего в проекте: показывается перед удалением, чтобы было видно, что пропадёт.</summary>
public record ProjectStats(int Tasks, int Boards, int Statuses, int StatusSets, int TaskTypes, int Series, int LinkTypes);

/// <summary>
/// Проекты с учётом доступа (<see cref="IProjectAccess"/>): недоступный проект ведёт себя как несуществующий.
/// </summary>
public class ProjectService(
    IProjectStorage projects, IProjectAccess access, TimeProvider time, Locks.EditLockService locks,
    ITaskStorage tasks, IBoardStorage boards, IStatusStorage statuses, IStatusSetStorage statusSets,
    ITaskTypeStorage taskTypes, ISeriesStorage series, ILinkTypeStorage linkTypes)
{
    /// <summary>Страница проектов, видимых текущему пользователю, по имени.</summary>
    public async Task<ListDto<Project>> GetRange(Page page, CancellationToken ct = default) =>
        await projects.GetRange(await access.VisibleProjectIds(ct), page, ct);

    public async Task<Project?> GetById(Guid id, CancellationToken ct = default) =>
        await access.CanAccess(id, ct) ? await projects.GetById(id, ct) : null;

    /// <summary>
    /// Количество сущностей проекта (без загрузки самих сущностей: только счётчики хранилищ).
    /// </summary>
    /// <returns>null — проекта нет (или он недоступен).</returns>
    public async Task<ProjectStats?> GetStats(Guid id, CancellationToken ct = default)
    {
        if (await GetById(id, ct) == null)
            return null;

        // Страница из одной записи: нужен только TotalCount.
        var one = new Page(0, 1);
        return new ProjectStats(
            await tasks.Count(id, new TaskFilter(), ct),
            (await boards.GetRange(id, one, ct)).TotalCount,
            (await statuses.GetRange(id, one, ct)).TotalCount,
            (await statusSets.GetRange(id, one, ct)).TotalCount,
            (await taskTypes.GetRange(id, one, ct)).TotalCount,
            (await series.GetRange(id, one, ct)).TotalCount,
            (await linkTypes.GetRange(id, one, ct)).TotalCount);
    }

    /// <summary>На сервере создатель становится участником проекта.</summary>
    public async Task<Project> Create(CreateProject command, CancellationToken ct = default)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = Validate.Name(command.Name, "Project name"),
            CreatedAt = time.GetUtcNow(),
            Version = Versioning.New
        };

        project = project with { Version = await projects.Add(project, ct) };
        await access.OnCreated(project, ct);
        return project;
    }

    /// <returns>null — проекта нет (или он недоступен).</returns>
    public async Task<Project?> Update(Guid id, UpdateProject command, CancellationToken ct = default)
    {
        var project = await GetById(id, ct);
        if (project == null)
            return null;
        await locks.EnsureWritable(Locks.LockedEntity.Project, project.Id, $"Project '{project.Name}'", ct);
        var expected = Versioning.Check(project.Version, command.Version, $"Project '{project.Name}'");

        var updated = project with
        {
            Name = command.Name == null ? project.Name : Validate.Name(command.Name, "Project name")
        };

        var version = await projects.Update(updated, expected, ct) ?? throw Versioning.Modified($"Project '{project.Name}'");
        return updated with { Version = version };
    }

    /// <summary>
    /// Удаляет проект вместе со всем содержимым. Проект — контейнер, а не ссылка,
    /// поэтому проверок «используется» нет.
    /// </summary>
    /// <returns>false — проекта нет (или он недоступен).</returns>
    public async Task<bool> Delete(Guid id, string? version, CancellationToken ct = default)
    {
        var project = await GetById(id, ct);
        if (project == null)
            return false;
        await locks.EnsureWritable(Locks.LockedEntity.Project, project.Id, $"Project '{project.Name}'", ct);
        var expected = Versioning.Check(project.Version, version, $"Project '{project.Name}'");

        if (!await projects.Delete(id, expected, ct))
            throw Versioning.Modified($"Project '{project.Name}'");
        await locks.Forget(Locks.LockedEntity.Project, project.Id, ct);
        return true;
    }
}
