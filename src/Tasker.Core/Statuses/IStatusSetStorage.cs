using Tasker.Core.Dto;

namespace Tasker.Core.Statuses;

/// <summary>Запись — с проверкой версии, как в <see cref="Projects.IProjectStorage"/>.</summary>
public interface IStatusSetStorage
{
    Task<StatusSet?> GetById(Guid projectId, Guid id, CancellationToken ct = default);

    /// <summary>Все наборы проекта по имени — для проверок ссылок. Для списков — <see cref="GetRange"/>.</summary>
    Task<StatusSet[]> GetAll(Guid projectId, CancellationToken ct = default);

    /// <summary>Страница по имени.</summary>
    Task<ListDto<StatusSet>> GetRange(Guid projectId, Page page, CancellationToken ct = default);

    Task<string> Add(StatusSet set, CancellationToken ct = default);

    /// <summary>Заменяет имя и список статусов набора целиком.</summary>
    Task<string?> Update(StatusSet set, string expectedVersion, CancellationToken ct = default);

    Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default);
}
