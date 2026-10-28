using Tasker.Core.IO;
using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Чтение и запись файлов сущностей одного вида в папке проекта — общая часть всех файловых хранилищ. Файл называется по названию
/// сущности (<see cref="EntityFileNames"/>), а настоящий id — поле внутри него, поэтому файл ищут не по имени, а по id:
/// сначала там, где его знает индекс (быстро), затем по имени — старому (Guid) и новому (суффикс из id), чтобы найти и файл, принесённый
/// <c>git pull</c> или другим процессом, о котором индекс ещё не знает. Совпадение id проверяется по содержимому.
/// <para>
/// Смена названия переименовывает файл (в git — удаление и добавление похожего файла), сущность со старым именем (Guid) получает новое
/// при первой же записи. Переименование и запись — один шаг под блокировкой записи: файл с новым именем уже есть — запись отклоняется
/// (<see cref="IOException"/>), чужой файл не затирается.
/// </para>
/// </summary>
/// <param name="read">Читает сущность из файла проекта <c>Guid</c> по пути; null — файл пуст.</param>
/// <param name="write">Создаёт файл.</param>
/// <param name="update">Перезаписывает файл (<c>path</c>) в <c>newPath</c>, если он не изменился с ожидаемой версии.</param>
internal sealed class EntityFiles<T>(
    TaskerDirectory directory,
    WorkspaceIndex index,
    EntityFolder folder,
    Func<T, Guid> idOf,
    Func<T, Guid> projectOf,
    Func<T, string?> nameOf,
    Func<Guid, string, CancellationToken, Task<T?>> read,
    Func<string, T, CancellationToken, Task<string>> write,
    Func<string, string, T, string, CancellationToken, Task<string?>> update) where T : class
{
    /// <returns>null — файла этой сущности нет.</returns>
    public async Task<T?> GetById(Guid projectId, Guid id, CancellationToken ct) => (await Resolve(projectId, id, ct))?.Entity;

    /// <exception cref="Tasker.Core.TaskerValidationException">Проект удалён, пока запрос шёл (метка <see cref="TaskerDirectory.DeletedProjectMarker"/>).</exception>
    public async Task<string> Add(T entity, CancellationToken ct)
    {
        // Проект удаляют под блокировкой записи, и запись файла сама создаёт недостающие папки: без проверки под той же блокировкой
        // «поздний» писатель (агент, чей запрос проверил проект до удаления) воскресил бы папку удалённого проекта с одним своим файлом.
        // Папку проекта ищут под той же блокировкой: её переносит переименование проекта.
        string? written = null;
        try
        {
            return await YamlFile.Locked(directory.Projects, ct, () =>
            {
                var project = directory.Project(projectOf(entity));
                var path = written = project.EntityFile(folder, idOf(entity), nameOf(entity));
                return File.Exists(project.ProjectFile) || !File.Exists(directory.DeletedProjectMarker(projectOf(entity)))
                    ? write(path, entity, ct)
                    : throw new Tasker.Core.TaskerValidationException($"ProjectId: project not found: {projectOf(entity)}");
            });
        }
        finally
        {
            if (written != null)
                await index.Refresh(written, ct);
        }
    }

    /// <returns>Новая версия; null — файла нет или он изменён с <paramref name="expectedVersion"/>.</returns>
    /// <exception cref="IOException">Под новым именем уже лежит другой файл.</exception>
    /// <remarks>
    /// Файл пишется в основную папку проекта (из второй — переносится). Файл пропал до записи, потому что проект переименовали и папка
    /// переехала, — поиск повторяется.
    /// </remarks>
    public async Task<string?> Update(T entity, string expectedVersion, CancellationToken ct)
    {
        var projectId = projectOf(entity);
        for (var attempt = 0; attempt < ResolveAttempts; attempt++)
        {
            if (await ResolvePath(projectId, idOf(entity), ct) is not { } current)
                return null;

            var renamed = directory.Project(projectId).EntityFile(folder, idOf(entity), nameOf(entity));
            var version = await index.Written([current, renamed], update(current, renamed, entity, expectedVersion, ct), ct);
            if (version != null || File.Exists(current))
                return version;
        }
        return null;
    }

    public async Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct)
    {
        for (var attempt = 0; attempt < ResolveAttempts; attempt++)
        {
            if (await ResolvePath(projectId, id, ct) is not { } path)
                return false;

            var deleted = await index.Written(path, YamlFile.DeleteIfMatch(path, expectedVersion, ct), ct);
            if (deleted || File.Exists(path))
                return deleted;
        }
        return false;
    }

    /// <summary>Сколько файлов этого вида не удалось прочитать (во всех папках проекта).</summary>
    public async Task<int> CountUnreadable(Guid projectId, CancellationToken ct)
    {
        var count = 0;
        foreach (var project in directory.ProjectDirs(projectId))
            count += await index.CountProblems(project.Folder(folder), ct);
        return count;
    }

    // Сколько раз искать файл заново, если его папку перенёс другой процесс (переименование проекта) между поиском и записью.
    private const int ResolveAttempts = 5;

    private async Task<string?> ResolvePath(Guid projectId, Guid id, CancellationToken ct) => (await Resolve(projectId, id, ct))?.Path;

    /// <summary>
    /// Файл сущности и сама сущность: сначала там, где файл знает индекс, затем поиск по имени во всех папках проекта (вторая папка после
    /// слияния git — тоже его). Файл пропал до чтения (его переименовал или удалил другой процесс, папку проекта перенесло переименование
    /// проекта) — поиск повторяется: «нет сущности» — только если её действительно нет.
    /// </summary>
    private async Task<(string Path, T Entity)?> Resolve(Guid projectId, Guid id, CancellationToken ct)
    {
        for (var attempt = 0; attempt < ResolveAttempts; attempt++)
        {
            var vanished = false;
            var indexed = await index.EntityPath(folder.Kind, projectId, id, ct);
            if (indexed != null)
            {
                if (await read(projectId, indexed, ct) is { } entity && idOf(entity) == id)
                    return (indexed, entity);
                vanished |= !File.Exists(indexed);
            }

            var projects = directory.ProjectDirs(projectId);
            foreach (var path in projects.SelectMany(x => x.FindFiles(folder, id)))
            {
                if (indexed != null && PathRules.SamePath(path, indexed))
                    continue;
                if (await read(projectId, path, ct) is { } entity && idOf(entity) == id)
                    return (path, entity);
                vanished |= !File.Exists(path);
            }

            // Во время переноса папки проекта список папок может её не увидеть вовсе.
            vanished |= projects.Length == 0 || projects.Any(x => !Directory.Exists(x.Root));
            if (!vanished)
                break;
        }
        return null;
    }
}
