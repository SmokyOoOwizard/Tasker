using Tasker.Core.Dto;

namespace Tasker.Core.TaskSeries;

/// <summary>
/// Хранилище серий. Реализации — в Tasker.Storage.Db и Tasker.Storage.Files, рядом со статусами.
/// Все методы работают в рамках одного проекта. Уникальность префикса и проверки ссылок — в сервисах Core,
/// хранилище их не делает. Запись — с проверкой версии, как в <see cref="Statuses.IStatusStorage"/>.
/// </summary>
public interface ISeriesStorage
{
    Task<Series?> GetById(Guid projectId, Guid id, CancellationToken ct = default);

    /// <summary>Все серии проекта по префиксу, затем по id — для проверок. Для списков — <see cref="GetRange"/>.</summary>
    Task<Series[]> GetAll(Guid projectId, CancellationToken ct = default);

    /// <summary>Страница по префиксу, затем по id.</summary>
    Task<ListDto<Series>> GetRange(Guid projectId, Page page, CancellationToken ct = default);

    Task<string> Add(Series series, CancellationToken ct = default);
    Task<string?> Update(Series series, string expectedVersion, CancellationToken ct = default);
    Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default);

    /// <summary>
    /// Сколько файлов серий проекта не удалось прочитать (конфликт слияния git, битый YAML): такие серии нет в
    /// <see cref="GetAll"/>, хотя они есть. Чистка ссылок при этом ничего не удаляет. В БД — всегда 0.
    /// </summary>
    Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default);
}
