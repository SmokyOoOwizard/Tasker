using Tasker.Core.Dto;

namespace Tasker.Core.Statuses;

/// <summary>
/// Хранилище статусов. Реализации — в тех же Tasker.Storage.Db и Tasker.Storage.Files, что и задачи.
/// Все методы работают в рамках одного проекта: чужой статус по id не найдётся.
/// Проверки ссылок (можно ли удалить) — в сервисах Core, хранилище их не делает.
/// Запись — с проверкой версии, как в <see cref="Projects.IProjectStorage"/>.
/// </summary>
public interface IStatusStorage
{
    Task<Status?> GetById(Guid projectId, Guid id, CancellationToken ct = default);

    /// <summary>Все статусы проекта по имени — для проверок ссылок. Для списков — <see cref="GetRange"/>.</summary>
    Task<Status[]> GetAll(Guid projectId, CancellationToken ct = default);

    /// <summary>Страница по имени.</summary>
    Task<ListDto<Status>> GetRange(Guid projectId, Page page, CancellationToken ct = default);

    Task<string> Add(Status status, CancellationToken ct = default);
    Task<string?> Update(Status status, string expectedVersion, CancellationToken ct = default);
    Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default);
}
