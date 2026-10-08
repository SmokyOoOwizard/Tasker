using Tasker.Core.Dto;

namespace Tasker.Core.Workspace;

/// <summary>Файл в .tasker, который не удалось прочитать; пока его не исправят, сущности нет в списках.</summary>
/// <param name="Path">Путь относительно .tasker, через «/».</param>
/// <param name="Error">Что не так: конфликт слияния git, ошибка YAML, id не совпадает с именем файла.</param>
public record WorkspaceProblem(string Path, string Error);

/// <summary>Файл, который изменился в индексе.</summary>
/// <param name="Path">Путь относительно .tasker, через «/»; пустой — вся папка.</param>
/// <param name="Id">
/// Id сущности — из содержимого файла, а не из его имени (файл называется по названию сущности). У удалённого файла — тот, что был
/// в индексе; null — файл не читается или это не файл сущности (папка, весь .tasker).
/// </param>
public record WorkspaceFileChange(string Path, Guid? Id);

/// <summary>
/// Индекс рабочей папки — только в файловом режиме (в БД индекс не нужен, сервис не зарегистрирован).
/// Файлы — источник правды, индекс — пересобираемый кэш для списков, как Library в Unity.
/// </summary>
public interface IWorkspaceIndex
{
    Task<ListDto<WorkspaceProblem>> GetProblems(Page page, CancellationToken ct = default);

    /// <summary>Сверяет индекс со всеми файлами — страховка на случай пропущенных событий файловой системы.</summary>
    Task Rescan(CancellationToken ct = default);

    /// <summary>
    /// Файлы, которые изменились в индексе (добавлены, изменены, удалены) — после того, как изменение
    /// записано: списки уже его видят. Пути относительно .tasker, через «/», и id сущностей (<see cref="WorkspaceFileChange"/>).
    /// Вызывается не в UI-потоке.
    /// </summary>
    event Action<IReadOnlyList<WorkspaceFileChange>>? Changed;
}
