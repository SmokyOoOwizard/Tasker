using Tasker.Core.TaskSeries;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Формат файла серии <c>series/&lt;id&gt;.yaml</c> (проект — по папке):
/// <code>
/// id: 5d2c1a3b-7e4f-4a6b-8c9d-0e1f2a3b4c5d
/// name: Задачи разработки
/// prefix: TSK
/// </code>
/// </summary>
internal static class SeriesFile
{
    public static async Task<Series?> Read(Guid projectId, string path, CancellationToken ct) =>
        await YamlFile.Read<SeriesFileModel>(path, ct) is { } file ? Map(projectId, file) : null;

    public static Task<string> Write(string path, Series series, CancellationToken ct) =>
        YamlFile.Write(path, ToFile(series), ct);

    /// <summary>Перезапись; <paramref name="newPath"/> другой — файл переименовывается (изменилось название или имя старого формата).</summary>
    public static Task<string?> Update(string path, string newPath, Series series, string expectedVersion, CancellationToken ct) =>
        YamlFile.WriteIfMatch(path, newPath, ToFile(series), expectedVersion, ct);

    private static SeriesFileModel ToFile(Series series) => new()
    {
        Id = series.Id,
        Name = series.Name,
        Prefix = series.Prefix
    };

    private static Series Map(Guid projectId, Versioned<SeriesFileModel> file) => new()
    {
        Id = file.Model.Id,
        ProjectId = projectId,
        Name = file.Model.Name ?? "",
        Prefix = file.Model.Prefix ?? "",
        Version = file.Version
    };

    private class SeriesFileModel : FileModel
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? Prefix { get; set; }
    }
}
