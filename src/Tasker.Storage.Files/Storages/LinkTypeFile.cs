using Tasker.Core.Links;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Формат файла типа связи <c>link-types/&lt;id&gt;.yaml</c>:
/// <code>
/// id: 9c8b7a6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d
/// name: Blocks
/// outwardName: blocks
/// inwardName: is blocked by
/// allowCycles: false
/// hierarchical: false
/// </code>
/// <c>allowCycles</c> — допускает ли тип циклы; в файлах, записанных до его появления, признака нет (см. <see cref="DefaultLinkTypes.AllowCyclesWhenUnset"/>).
/// <c>hierarchical</c> — иерархический ли тип («includes» / «is part of», <see cref="LinkType.Hierarchical"/>); в файлах формата до 9 признака нет, и тип обычный.
/// </summary>
internal static class LinkTypeFile
{
    public static async Task<LinkType?> Read(Guid projectId, string path, CancellationToken ct) =>
        await YamlFile.Read<LinkTypeFileModel>(path, ct) is { } file ? Map(projectId, file) : null;

    public static Task<string> Write(string path, LinkType type, CancellationToken ct) =>
        YamlFile.Write(path, ToFile(type), ct);

    /// <summary>Перезапись; <paramref name="newPath"/> другой — файл переименовывается (изменилось название или имя старого формата).</summary>
    public static Task<string?> Update(string path, string newPath, LinkType type, string expectedVersion, CancellationToken ct) =>
        YamlFile.WriteIfMatch(path, newPath, ToFile(type), expectedVersion, ct);

    private static LinkTypeFileModel ToFile(LinkType type) => new()
    {
        Id = type.Id,
        Name = type.Name,
        OutwardName = type.OutwardName,
        InwardName = type.InwardName,
        AllowCycles = type.AllowCycles,
        Hierarchical = type.Hierarchical
    };

    private static LinkType Map(Guid projectId, Versioned<LinkTypeFileModel> file) => new()
    {
        Id = file.Model.Id,
        ProjectId = projectId,
        Name = file.Model.Name ?? "",
        OutwardName = file.Model.OutwardName ?? "",
        InwardName = file.Model.InwardName ?? file.Model.OutwardName ?? "",
        AllowCycles = file.Model.AllowCycles ?? DefaultLinkTypes.AllowCyclesWhenUnset(projectId, file.Model.Id),
        Hierarchical = file.Model.Hierarchical ?? false,
        Version = file.Version
    };

    private class LinkTypeFileModel : FileModel
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? OutwardName { get; set; }
        public string? InwardName { get; set; }
        public bool? AllowCycles { get; set; }
        public bool? Hierarchical { get; set; }
    }
}
