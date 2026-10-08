using Tasker.Core.Dto;

namespace Tasker.Core.Tasks;

/// <summary>Запись — с проверкой версии, как в <see cref="Projects.IProjectStorage"/>.</summary>
public interface ITaskTypeStorage
{
    Task<TaskType?> GetById(Guid projectId, Guid id, CancellationToken ct = default);

    /// <summary>Все типы проекта по имени — для проверок ссылок. Для списков — <see cref="GetRange"/>.</summary>
    Task<TaskType[]> GetAll(Guid projectId, CancellationToken ct = default);

    /// <summary>Страница по имени.</summary>
    Task<ListDto<TaskType>> GetRange(Guid projectId, Page page, CancellationToken ct = default);

    Task<string> Add(TaskType type, CancellationToken ct = default);
    Task<string?> Update(TaskType type, string expectedVersion, CancellationToken ct = default);
    Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default);
}
