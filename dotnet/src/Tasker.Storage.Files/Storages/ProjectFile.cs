using Tasker.Core.Projects;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Формат файла проекта <c>.tasker/projects/&lt;название&gt;-&lt;id8&gt;/project.yaml</c> (папка — см. <see cref="ProjectFolders"/>):
/// <code>
/// id: 2b4d6f80-1a3c-4e5f-8a9b-0c1d2e3f4a5b
/// name: Tasker
/// createdAt: 2026-09-25T10:00:00.0000000+00:00
/// </code>
/// </summary>
internal static class ProjectFile
{
    public static async Task<Project?> Read(string path, CancellationToken ct) =>
        await YamlFile.Read<ProjectFileModel>(path, ct) is { } file ? Map(file) : null;

    /// <summary>Проект из уже прочитанных байтов файла; null — файл пуст.</summary>
    public static Project? ReadSync(byte[] bytes, string path) =>
        YamlFile.Parse<ProjectFileModel>(bytes, path) is { } file ? Map(file) : null;

    public static Task<string> Write(string path, Project project, CancellationToken ct) =>
        YamlFile.Write(path, ToFile(project), ct);

    public static Task<string?> Update(string path, Project project, string expectedVersion, CancellationToken ct) =>
        YamlFile.WriteIfMatch(path, ToFile(project), expectedVersion, ct);

    /// <summary>Перезапись по версии с переносом папки проекта в <paramref name="newFolder"/> (<see cref="YamlFile.WriteIfMatchMoving{T}"/>).</summary>
    public static Task<string?> Update(string path, string folder, string newFolder, Project project, string expectedVersion, CancellationToken ct) =>
        YamlFile.WriteIfMatchMoving(path, folder, newFolder, ToFile(project), expectedVersion, ct);

    private static ProjectFileModel ToFile(Project project) => new()
    {
        Id = project.Id,
        Name = project.Name,
        CreatedAt = project.CreatedAt
    };

    private static Project Map(Versioned<ProjectFileModel> file) => new()
    {
        Id = file.Model.Id,
        Name = file.Model.Name ?? "",
        CreatedAt = file.Model.CreatedAt,
        Version = file.Version
    };

    private class ProjectFileModel : FileModel
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
