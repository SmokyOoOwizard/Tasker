namespace Tasker.Core.Projects;

/// <summary>
/// Кому виден проект. Реализацию выбирает хост:
/// сервер — <see cref="MemberProjectAccess"/> (участники проекта и админы),
/// десктоп — <see cref="OpenProjectAccess"/> (все проекты, входа нет).
/// </summary>
public interface IProjectAccess
{
    Task<bool> CanAccess(Guid projectId, CancellationToken ct = default);

    /// <summary>Id видимых проектов для выборки страницы в хранилище; null — видны все.</summary>
    Task<Guid[]?> VisibleProjectIds(CancellationToken ct = default);

    /// <summary>Вызывается после создания проекта — на сервере создатель становится его участником.</summary>
    Task OnCreated(Project project, CancellationToken ct = default);
}

public class OpenProjectAccess : IProjectAccess
{
    public Task<bool> CanAccess(Guid projectId, CancellationToken ct = default) => Task.FromResult(true);

    public Task<Guid[]?> VisibleProjectIds(CancellationToken ct = default) => Task.FromResult<Guid[]?>(null);

    public Task OnCreated(Project project, CancellationToken ct = default) => Task.CompletedTask;
}
