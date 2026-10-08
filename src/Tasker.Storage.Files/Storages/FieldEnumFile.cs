using Tasker.Core.Fields;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Формат файла перечисления <c>enums/&lt;id&gt;.yaml</c>. Значения — списком в порядке отображения, у каждого свой id
/// (по нему значение переименовывают и переназначают):
/// <code>
/// formatVersion: 3
/// id: 3f2a9c1e-7b4d-4e6a-8c1f-5d2b9a7e4c30
/// name: Приоритет
/// values:
/// - id: 6e3d2b4c-8f5a-4b7c-9d0e-1f2a3b4c5d6e
///   name: Низкий
/// - id: 7f4e3c5d-9a6b-4c8d-ae1f-2a3b4c5d6e7f
///   name: Высокий
/// </code>
/// </summary>
internal static class FieldEnumFile
{
    public static async Task<FieldEnum?> Read(Guid projectId, string path, CancellationToken ct) =>
        await YamlFile.Read<FieldEnumFileModel>(path, ct) is { } file ? Map(projectId, file) : null;

    public static Task<string> Write(string path, FieldEnum value, CancellationToken ct) =>
        YamlFile.Write(path, ToFile(value), ct);

    /// <summary>Перезапись; <paramref name="newPath"/> другой — файл переименовывается (изменилось название или имя старого формата).</summary>
    public static Task<string?> Update(string path, string newPath, FieldEnum value, string expectedVersion, CancellationToken ct) =>
        YamlFile.WriteIfMatch(path, newPath, ToFile(value), expectedVersion, ct);

    private static FieldEnumFileModel ToFile(FieldEnum value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        Values = value.Values.Select(x => new ValueModel { Id = x.Id, Name = x.Name }).ToList()
    };

    private static FieldEnum Map(Guid projectId, Versioned<FieldEnumFileModel> file) => new()
    {
        Id = file.Model.Id,
        ProjectId = projectId,
        Name = file.Model.Name ?? "",
        Values = (file.Model.Values ?? []).Select(x => new FieldEnumValue(x.Id, x.Name ?? "")).ToArray(),
        Version = file.Version
    };

    private class FieldEnumFileModel : FileModel
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public List<ValueModel>? Values { get; set; }
    }

    private class ValueModel
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
    }
}
