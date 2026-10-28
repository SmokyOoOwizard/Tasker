using Tasker.Core.Statuses;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Формат файла набора статусов <c>status-sets/&lt;id&gt;.yaml</c>.
/// Статусы — списком по одному на строку, в порядке отображения:
/// <code>
/// id: 7a1b2c3d-4e5f-4a6b-8c9d-0e1f2a3b4c5d
/// name: Разработка
/// statuses:
/// - 5d2c1a3b-7e4f-4a6b-8c9d-0e1f2a3b4c5d
/// - 6e3d2b4c-8f5a-4b7c-9d0e-1f2a3b4c5d6e
/// </code>
/// </summary>
internal static class StatusSetFile
{
    public static async Task<StatusSet?> Read(Guid projectId, string path, CancellationToken ct) =>
        await YamlFile.Read<StatusSetFileModel>(path, ct) is { } file ? Map(projectId, file) : null;

    public static Task<string> Write(string path, StatusSet set, CancellationToken ct) =>
        YamlFile.Write(path, ToFile(set), ct);

    /// <summary>Перезапись; <paramref name="newPath"/> другой — файл переименовывается (изменилось название или имя старого формата).</summary>
    public static Task<string?> Update(string path, string newPath, StatusSet set, string expectedVersion, CancellationToken ct) =>
        YamlFile.WriteIfMatch(path, newPath, ToFile(set), expectedVersion, ct);

    private static StatusSetFileModel ToFile(StatusSet set) => new()
    {
        Id = set.Id,
        Name = set.Name,
        Statuses = set.StatusIds.ToList()
    };

    private static StatusSet Map(Guid projectId, Versioned<StatusSetFileModel> file) => new()
    {
        Id = file.Model.Id,
        ProjectId = projectId,
        Name = file.Model.Name ?? "",
        StatusIds = file.Model.Statuses?.ToArray() ?? [],
        Version = file.Version
    };

    private class StatusSetFileModel : FileModel
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public List<Guid>? Statuses { get; set; }
    }
}
