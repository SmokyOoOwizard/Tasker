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
    /// <param name="relativePath">Путь относительно .tasker, через «/».</param>
    public static LayoutEntry? Classify(string relativePath)
    {
        var parts = relativePath.Split('/');
        return parts switch
        {
            ["users", var file] when IdOf(file) is { } id =>
                new LayoutEntry(IndexKind.User, null, id, Stem(file)),

            ["projects", var project, ProjectDirectory.ProjectFileName] when Guid.TryParse(project, out var projectId) =>
                new LayoutEntry(IndexKind.Project, null, projectId, Stem(ProjectDirectory.ProjectFileName)),

            // Сущности проекта: имя — «название-первые знаки id» (см. EntityFileNames) или, в старом формате, полный Guid.
            ["projects", var project, var folder, var file]
                when Guid.TryParse(project, out var projectId)
                     && EntityFolders.Named(folder) is { } entityFolder
                     && EntityFileNames.TryParse(file, out var fullId, out var prefix) =>
                new LayoutEntry(entityFolder.Kind, projectId, fullId, Stem(file), prefix),

            _ => null
        };
    }

    private static string Stem(string fileName) =>
        fileName.EndsWith(YamlFile.Extension, StringComparison.Ordinal) ? fileName[..^YamlFile.Extension.Length] : fileName;

    private static Guid? IdOf(string fileName) =>
        fileName.EndsWith(YamlFile.Extension, StringComparison.Ordinal)
        && Guid.TryParse(fileName[..^YamlFile.Extension.Length], out var id)
            ? id
            : null;
}
