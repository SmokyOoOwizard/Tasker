using Tasker.Core.Dto;

namespace Tasker.Core.Projects;

/// <summary>
/// Запись — с проверкой версии (см. <see cref="Versioning"/>): Add возвращает версию новой записи,
/// Update — новую версию или null, Delete — false, если запись изменилась или её уже нет.
/// </summary>
public interface IProjectStorage
{
    Task<Project?> GetById(Guid id, CancellationToken ct = default);

    /// <summary>Страница проектов по имени.</summary>
    /// <param name="ids">Только эти проекты (например, где пользователь участник); null — все.</param>
    Task<ListDto<Project>> GetRange(Guid[]? ids, Page page, CancellationToken ct = default);

    Task<string> Add(Project project, CancellationToken ct = default);
    Task<string?> Update(Project project, string expectedVersion, CancellationToken ct = default);

    /// <summary>Удаляет проект вместе со всем его содержимым.</summary>
    Task<bool> Delete(Guid id, string expectedVersion, CancellationToken ct = default);
}
