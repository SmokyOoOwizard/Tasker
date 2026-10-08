using Tasker.Core.Statuses;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Формат файла статуса <c>statuses/&lt;id&gt;.yaml</c> (проект — по папке):
/// <code>
/// id: 5d2c1a3b-7e4f-4a6b-8c9d-0e1f2a3b4c5d
/// name: В работе
/// color: '#3B82F6'
/// description: |
///   Задачу взял исполнитель.
/// </code>
/// <c>description</c> пишется, только если описание не пусто.
/// </summary>
internal static class StatusFile
{
    public static async Task<Status?> Read(Guid projectId, string path, CancellationToken ct) =>
        await YamlFile.Read<StatusFileModel>(path, ct) is { } file ? Map(projectId, file) : null;

    public static Task<string> Write(string path, Status status, CancellationToken ct) =>
        YamlFile.Write(path, ToFile(status), ct);

    /// <summary>Перезапись; <paramref name="newPath"/> другой — файл переименовывается (изменилось название или имя старого формата).</summary>
    public static Task<string?> Update(string path, string newPath, Status status, string expectedVersion, CancellationToken ct) =>
        YamlFile.WriteIfMatch(path, newPath, ToFile(status), expectedVersion, ct);

    private static StatusFileModel ToFile(Status status) => new()
    {
        Id = status.Id,
        Name = status.Name,
        Color = status.Color,
        Description = string.IsNullOrEmpty(status.Description) ? null : status.Description
    };

    private static Status Map(Guid projectId, Versioned<StatusFileModel> file) => new()
    {
        Id = file.Model.Id,
        ProjectId = projectId,
        Name = file.Model.Name ?? "",
        Color = file.Model.Color ?? "",
        Description = file.Model.Description ?? "",
        Version = file.Version
    };

    private class StatusFileModel : FileModel
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? Color { get; set; }
        public string? Description { get; set; }
    }
}
