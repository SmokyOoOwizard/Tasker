using Tasker.Core.Fields;
using Tasker.Core.Workspace;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Формат файла поля <c>fields/&lt;id&gt;.yaml</c>. Тип — слово (string, int, float, bool, date, enum); <c>multiple</c> пишется
/// только у полей с несколькими значениями; <c>enum</c> — id перечисления, только у поля типа enum:
/// <code>
/// formatVersion: 3
/// id: 9c8b7a6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d
/// name: Приоритет
/// type: enum
/// enum: 3f2a9c1e-7b4d-4e6a-8c1f-5d2b9a7e4c30
/// multiple: true
/// </code>
/// </summary>
internal static class FieldFile
{
    public static async Task<FieldDefinition?> Read(Guid projectId, string path, CancellationToken ct) =>
        await YamlFile.Read<FieldFileModel>(path, ct) is { } file ? Map(projectId, file, path) : null;

    public static Task<string> Write(string path, FieldDefinition field, CancellationToken ct) =>
        YamlFile.Write(path, ToFile(field), ct);

    /// <summary>Перезапись; <paramref name="newPath"/> другой — файл переименовывается (изменилось название или имя старого формата).</summary>
    public static Task<string?> Update(string path, string newPath, FieldDefinition field, string expectedVersion, CancellationToken ct) =>
        YamlFile.WriteIfMatch(path, newPath, ToFile(field), expectedVersion, ct);

    private static FieldFileModel ToFile(FieldDefinition field) => new()
    {
        Id = field.Id,
        Name = field.Name,
        Type = field.Type.ToString().ToLowerInvariant(),
        Enum = field.EnumId,
        Multiple = field.Multiple ? true : null
    };

    private static FieldDefinition Map(Guid projectId, Versioned<FieldFileModel> file, string path) => new()
    {
        Id = file.Model.Id,
        ProjectId = projectId,
        Name = file.Model.Name ?? "",
        Type = Enum.TryParse<FieldType>(file.Model.Type, ignoreCase: true, out var type) && Enum.IsDefined(type)
            ? type
            : throw new UnsupportedFormatException($"{Path.GetFileName(path)}: unknown field type '{file.Model.Type}': update Tasker"),
        Multiple = file.Model.Multiple ?? false,
        EnumId = file.Model.Enum,
        Version = file.Version
    };

    private class FieldFileModel : FileModel
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
        public Guid? Enum { get; set; }
        public bool? Multiple { get; set; }
    }
}
