using Tasker.Storage.Files.Storages;

namespace Tasker.Storage.Files.Index;

internal enum IndexKind
{
    Project,
    User,
    Status,
    StatusSet,
    TaskType,
    Board,
    Task,
    Series,
    LinkType,
    Field,
    FieldEnum
}

/// <summary>
/// Чем является файл по своему пути: вид сущности, её проект и то, что имя говорит об id. У пользователей имя — полный Guid
/// (<see cref="Id"/>); у сущностей проекта — название и первые знаки id (<see cref="IdPrefix"/>, настоящий id — внутри файла) или,
/// в старом формате, тоже полный Guid; <see cref="Stem"/> — имя без расширения (когда по имени id не узнать, а файл не прочитан).
/// </summary>
internal record LayoutEntry(IndexKind Kind, Guid? ProjectId, Guid? Id, string Stem, string? IdPrefix = null);

/// <summary>
/// Индексируются только файлы раскладки <see cref="TaskerDirectory"/>. Всё остальное в .tasker —
/// .cache, временные файлы атомарной записи, .gitignore, чужие файлы — пропускается,
/// поэтому разбирать сам .gitignore не нужно.
/// </summary>
internal static class WorkspaceLayout
{
    /// <summary>
    /// Чем является файл. Проект файла — по его папке (<see cref="ProjectFolders.ProjectId"/>); файлы папки, проект которой не узнать, — null.
    /// <c>project.yaml</c> второй папки проекта — тоже null: проект — <c>project.yaml</c> основной папки, о второй сообщают проблемы области
    /// (<see cref="ProjectFolders.SecondFolderProblems"/>). У папки с новым именем id проекта — в файле, а в имени только начало
    /// (<see cref="LayoutEntry.IdPrefix"/>, сверяется при чтении).
    /// </summary>
    /// <param name="relativePath">Путь относительно .tasker, через «/».</param>
    public static LayoutEntry? Classify(string relativePath, ProjectFolders folders)
    {
        var parts = relativePath.Split('/');
        switch (parts)
        {
            case ["users", var file] when IdOf(file) is { } id:
                return new LayoutEntry(IndexKind.User, null, id, Stem(file));

            case ["projects", var project, ProjectDirectory.ProjectFileName] when ProjectFolders.TryParse(project, out var id8, out var guid):
                if (folders.ProjectId(project) is { } projectId && folders.Primary(projectId) is { } primary && primary != project)
                    return null;
                return new LayoutEntry(IndexKind.Project, null, guid, Stem(ProjectDirectory.ProjectFileName), guid == null ? id8 : null);

            // Сущности проекта: имя — «название-первые знаки id» (см. EntityFileNames) или, в старом формате, полный Guid.
            case ["projects", var project, var folder, var file]
                when ProjectFolders.TryParse(project, out _, out _)
                     && EntityFolders.Named(folder) is { } entityFolder
                     && EntityFileNames.TryParse(file, out var fullId, out var prefix):
                return folders.ProjectId(project) is { } owner ? new LayoutEntry(entityFolder.Kind, owner, fullId, Stem(file), prefix) : null;

            default:
                return null;
        }
    }

    private static string Stem(string fileName) =>
        fileName.EndsWith(YamlFile.Extension, StringComparison.Ordinal) ? fileName[..^YamlFile.Extension.Length] : fileName;

    private static Guid? IdOf(string fileName) =>
        fileName.EndsWith(YamlFile.Extension, StringComparison.Ordinal)
        && Guid.TryParse(fileName[..^YamlFile.Extension.Length], out var id)
            ? id
            : null;
}
