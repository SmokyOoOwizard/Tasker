using System.Runtime.CompilerServices;
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
    public async Task<T?> GetById(Guid projectId, Guid id, CancellationToken ct)
    {
        await foreach (var path in Candidates(projectId, id, ct))
        {
            if (await read(projectId, path, ct) is { } entity && idOf(entity) == id)
                return entity;
        }
        return null;
    }

    /// <exception cref="Tasker.Core.TaskerValidationException">Проект удалён, пока запрос шёл (метка <see cref="TaskerDirectory.DeletedProjectMarker"/>).</exception>
    public Task<string> Add(T entity, CancellationToken ct)
    {
        var project = directory.Project(projectOf(entity));
        var path = project.EntityFile(folder, idOf(entity), nameOf(entity));

        // Проект удаляют под блокировкой записи, и запись файла сама создаёт недостающие папки: без проверки под той же блокировкой
        // «поздний» писатель (агент, чей запрос проверил проект до удаления) воскресил бы папку удалённого проекта с одним своим файлом.
        return index.Written(path, YamlFile.Locked(project.Root, ct, () => File.Exists(project.ProjectFile) || !File.Exists(directory.DeletedProjectMarker(projectOf(entity)))
            ? write(path, entity, ct)
            : throw new Tasker.Core.TaskerValidationException($"ProjectId: project not found: {projectOf(entity)}")), ct);
    }

    /// <returns>Новая версия; null — файла нет или он изменён с <paramref name="expectedVersion"/>.</returns>
    /// <exception cref="IOException">Под новым именем уже лежит другой файл.</exception>
    public async Task<string?> Update(T entity, string expectedVersion, CancellationToken ct)
    {
        var projectId = projectOf(entity);
        if (await ResolvePath(projectId, idOf(entity), ct) is not { } current)
            return null;

        var renamed = directory.Project(projectId).EntityFile(folder, idOf(entity), nameOf(entity));
        return await index.Written([current, renamed], update(current, renamed, entity, expectedVersion, ct), ct);
    }

    public async Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct)
    {
        if (await ResolvePath(projectId, id, ct) is not { } path)
            return false;

        return await index.Written(path, YamlFile.DeleteIfMatch(path, expectedVersion, ct), ct);
    }

    /// <summary>Сколько файлов этого вида не удалось прочитать.</summary>
    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct) =>
        index.CountProblems(directory.Project(projectId).Folder(folder), ct);

    private async Task<string?> ResolvePath(Guid projectId, Guid id, CancellationToken ct)
    {
        await foreach (var path in Candidates(projectId, id, ct))
        {
            if (await read(projectId, path, ct) is { } entity && idOf(entity) == id)
                return path;
        }
        return null;
    }

    /// <summary>
    /// Где может лежать файл: сначала то, что знает индекс, затем поиск по имени. Лишние кандидаты не читаются, пока подходящий не найден.
    /// </summary>
    private async IAsyncEnumerable<string> Candidates(Guid projectId, Guid id, [EnumeratorCancellation] CancellationToken ct)
    {
        var indexed = await index.EntityPath(folder.Kind, projectId, id, ct);
        if (indexed != null && File.Exists(indexed))
            yield return indexed;

        foreach (var path in directory.Project(projectId).FindFiles(folder, id))
        {
            if (indexed == null || !PathRules.SamePath(path, indexed))
                yield return path;
        }
    }
}
