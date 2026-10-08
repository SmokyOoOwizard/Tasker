using Tasker.Core.Dto;

namespace Tasker.Core.Boards;

/// <summary>Запись — с проверкой версии, как в <see cref="Projects.IProjectStorage"/>.</summary>
public interface IBoardStorage
{
    Task<Board?> GetById(Guid projectId, Guid id, CancellationToken ct = default);

    /// <summary>Все доски проекта по имени — для проверок ссылок. Для списков — <see cref="GetRange"/>.</summary>
    Task<Board[]> GetAll(Guid projectId, CancellationToken ct = default);

    /// <summary>Страница по имени.</summary>
    Task<ListDto<Board>> GetRange(Guid projectId, Page page, CancellationToken ct = default);

    Task<string> Add(Board board, CancellationToken ct = default);

    /// <summary>Заменяет доску целиком: имя, наборы и колонки со всеми их настройками.</summary>
    Task<string?> Update(Board board, string expectedVersion, CancellationToken ct = default);

    Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default);
}
