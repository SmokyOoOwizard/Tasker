using Tasker.Core.Tasks;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Формат файла типа задачи <c>task-types/&lt;id&gt;.yaml</c>:
/// <code>
/// id: 9c8b7a6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d
/// name: Баг
/// description: Дефект в уже выпущенном поведении
/// statusSetId: 7a1b2c3d-4e5f-4a6b-8c9d-0e1f2a3b4c5d
/// fields:
/// - field: 3f2a9c1e-7b4d-4e6a-8c1f-5d2b9a7e4c30
///   required: true
/// - field: 5d2c1a3b-7e4f-4a6b-8c9d-0e1f2a3b4c5d
/// </code>
/// <c>description</c> пишется, только если описание не пусто.
/// <c>fields</c> — поля каталога, подключённые к типу, в порядке отображения; пишется, только если у типа есть поля;
/// <c>required</c> — только у обязательных.
/// </summary>
internal static class TaskTypeFile
{
    public static async Task<TaskType?> Read(Guid projectId, string path, CancellationToken ct) =>
        await YamlFile.Read<TaskTypeFileModel>(path, ct) is { } file ? Map(projectId, file) : null;

    public static Task<string> Write(string path, TaskType type, CancellationToken ct) =>
        YamlFile.Write(path, ToFile(type), ct);

    /// <summary>Перезапись; <paramref name="newPath"/> другой — файл переименовывается (изменилось название или имя старого формата).</summary>
    public static Task<string?> Update(string path, string newPath, TaskType type, string expectedVersion, CancellationToken ct) =>
        YamlFile.WriteIfMatch(path, newPath, ToFile(type), expectedVersion, ct);

    private static TaskTypeFileModel ToFile(TaskType type) => new()
    {
        Id = type.Id,
        Name = type.Name,
        Description = string.IsNullOrEmpty(type.Description) ? null : type.Description,
        StatusSetId = type.StatusSetId,
        Fields = type.Fields.Count == 0
            ? null
            : type.Fields.Select(x => new TypeFieldFileModel { Field = x.FieldId, Required = x.Required ? true : null }).ToList()
    };

    private static TaskType Map(Guid projectId, Versioned<TaskTypeFileModel> file) => new()
    {
        Id = file.Model.Id,
        ProjectId = projectId,
        Name = file.Model.Name ?? "",
        Description = file.Model.Description ?? "",
        StatusSetId = file.Model.StatusSetId,
        Fields = file.Model.Fields?.Select(x => new TaskTypeField(x.Field, x.Required ?? false)).ToArray() ?? [],
        Version = file.Version
    };

    private class TaskTypeFileModel : FileModel
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public Guid StatusSetId { get; set; }
        public List<TypeFieldFileModel>? Fields { get; set; }
    }

    private class TypeFieldFileModel
    {
        public Guid Field { get; set; }
        public bool? Required { get; set; }
    }
}
