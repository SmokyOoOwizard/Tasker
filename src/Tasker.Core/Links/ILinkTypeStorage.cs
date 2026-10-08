using Tasker.Core.Dto;

namespace Tasker.Core.Links;

/// <summary>Хранилище типов связей. Запись — с проверкой версии, как в <see cref="Projects.IProjectStorage"/>.</summary>
public interface ILinkTypeStorage
{
    Task<LinkType?> GetById(Guid projectId, Guid id, CancellationToken ct = default);

    /// <summary>Все типы проекта по имени — для поиска и проверок. Для списков — <see cref="GetRange"/>.</summary>
    Task<LinkType[]> GetAll(Guid projectId, CancellationToken ct = default);

    /// <summary>Страница по имени.</summary>
    Task<ListDto<LinkType>> GetRange(Guid projectId, Page page, CancellationToken ct = default);

    /// <summary>Сколько файлов типов связей проекта не удалось прочитать; нечитаемый тип выглядел бы несуществующим. В БД — 0.</summary>
    Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default);

    Task<string> Add(LinkType type, CancellationToken ct = default);
    Task<string?> Update(LinkType type, string expectedVersion, CancellationToken ct = default);
    Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default);
}
