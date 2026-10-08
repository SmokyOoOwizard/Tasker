using Tasker.Core.Fields;
using Tasker.Core.TaskSeries;
using Tasker.Core.Tasks;
using Tasker.Core.Workspace;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Формат файла задачи <c>tasks/&lt;заголовок&gt;-&lt;id8&gt;.yaml</c> (имя — <see cref="EntityFileNames"/>):
/// <code>
/// id: 0b6f5f9e-5c1a-4d7e-9a4b-2f1d3c4e5a6b
/// title: Настроить CI
/// description: |
///   Первая строка описания.
///   Вторая строка.
/// typeId: 9c8b7a6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d
/// statusId: 5d2c1a3b-7e4f-4a6b-8c9d-0e1f2a3b4c5d
/// createdAt: 2026-09-25T10:00:00.0000000+00:00
/// updatedAt: 2026-09-25T10:00:00.0000000+00:00
/// series:
/// - seriesId: 5d2c1a3b-7e4f-4a6b-8c9d-0e1f2a3b4c5d
///   number: 5
/// links:
/// - typeId: 9c8b7a6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d
///   taskId: 3a2b1c0d-9e8f-4a7b-8c6d-5e4f3a2b1c0d
/// fields:
/// - id: 4b1c2d3e-5f6a-4b7c-8d9e-0f1a2b3c4d5e
///   values:
///   - '5'
/// - id: 6d2e3f4a-7b8c-4d9e-af0b-1c2d3e4f5a6b
///   name: Оценка
///   type: int
///   required: true
///   multiple: true
///   values:
///   - '3'
///   - '8'
/// </code>
/// Списки есть, только если они не пусты: <c>series</c> — номера в сериях (порядок как в <see cref="TaskItem.SeriesNumbers"/>),
/// <c>links</c> — исходящие связи с другими задачами проекта (<see cref="TaskItem.Links"/>): тип связи и задача-цель,
/// <c>fields</c> — значения полей и дополнительные поля (<see cref="TaskItem.Fields"/>): <c>id</c> — поле каталога, а если есть
/// <c>name</c>, то это собственное поле задачи с полным определением (<c>name</c>, <c>type</c>, <c>enum</c> — id перечисления, только у enum;
/// <c>required</c> и <c>multiple</c> пишутся, только когда истинны) и <c>id</c> — его id внутри задачи.
/// Значения — всегда строки в каноническом виде (<see cref="Tasker.Core.Fields.FieldValues"/>): число, <c>true</c>/<c>false</c>,
/// дата <c>yyyy-MM-dd</c>, у enum — id значения перечисления.
/// </summary>
internal static class TaskFile
{
    public static async Task<TaskItem?> Read(Guid projectId, string path, CancellationToken ct) =>
        await YamlFile.Read<TaskFileModel>(path, ct) is { } file ? Map(projectId, file, path) : null;

    public static Task<string> Write(string path, TaskItem task, CancellationToken ct) =>
        YamlFile.Write(path, ToFile(task), ct);

    /// <summary>Перезапись; <paramref name="newPath"/> другой — файл переименовывается (заголовок изменился или имя старого формата).</summary>
    public static Task<string?> Update(string path, string newPath, TaskItem task, string expectedVersion, CancellationToken ct) =>
        YamlFile.WriteIfMatch(path, newPath, ToFile(task), expectedVersion, ct);

    private static TaskFileModel ToFile(TaskItem task) => new()
    {
        Id = task.Id,
        Title = task.Title,
        Description = task.Description,
        TypeId = task.TypeId,
        StatusId = task.StatusId,
        CreatedAt = task.CreatedAt,
        UpdatedAt = task.UpdatedAt,
        Series = task.SeriesNumbers.Count == 0
            ? null
            : task.SeriesNumbers.Select(x => new TaskSeriesFileModel { SeriesId = x.SeriesId, Number = x.Number }).ToList(),
        Links = task.Links.Count == 0
            ? null
            : task.Links.Distinct().Select(x => new TaskLinkFileModel { TypeId = x.TypeId, TaskId = x.TargetId }).ToList(),
        Fields = task.Fields.Count == 0
            ? null
            : task.Fields.Select(x => new TaskFieldFileModel
            {
                Id = x.FieldId,
                Name = x.Own?.Name,
                Type = x.Own?.Type.ToString().ToLowerInvariant(),
                Enum = x.Own?.EnumId,
                Required = x.Own is { Required: true } ? true : null,
                Multiple = x.Own is { Multiple: true } ? true : null,
                Values = x.Values.Count == 0 ? null : x.Values.Select(v => (string?)v).ToList()
            }).ToList()
    };

    private static TaskItem Map(Guid projectId, Versioned<TaskFileModel> file, string path) => new()
    {
        Id = file.Model.Id,
        ProjectId = projectId,
        Title = file.Model.Title ?? "",
        Description = file.Model.Description,
        TypeId = file.Model.TypeId,
        StatusId = file.Model.StatusId,
        CreatedAt = file.Model.CreatedAt,
        UpdatedAt = file.Model.UpdatedAt,
        SeriesNumbers = file.Model.Series?.Select(x => new TaskSeriesNumber(x.SeriesId, x.Number)).ToArray() ?? [],
        // Одна и та же связь, записанная дважды (правка руками, слияние), — одна связь.
        Links = file.Model.Links?.Select(x => new Core.Links.TaskLink(x.TypeId, x.TaskId)).Distinct().ToArray() ?? [],
        Fields = file.Model.Fields?.Select(x => MapField(x, path)).ToArray() ?? [],
        Version = file.Version
    };

    private static TaskField MapField(TaskFieldFileModel field, string path) => new(
        field.Id,
        field.Values?.Where(x => x != null).Select(x => x!).ToArray() ?? [],
        field.Name == null
            ? null
            : new OwnField(
                field.Name,
                Enum.TryParse<FieldType>(field.Type, ignoreCase: true, out var type) && Enum.IsDefined(type)
                    ? type
                    : throw new UnsupportedFormatException($"{Path.GetFileName(path)}: unknown field type '{field.Type}': update Tasker"),
                field.Required ?? false,
                field.Multiple ?? false,
                field.Enum));

    // Отдельная модель под формат файла, чтобы TaskItem мог меняться, не ломая файлы на диске.
    private class TaskFileModel : FileModel
    {
        public Guid Id { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public Guid TypeId { get; set; }
        public Guid StatusId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<TaskSeriesFileModel>? Series { get; set; }
        public List<TaskLinkFileModel>? Links { get; set; }
        public List<TaskFieldFileModel>? Fields { get; set; }
    }

    private class TaskFieldFileModel
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
        public Guid? Enum { get; set; }
        public bool? Required { get; set; }
        public bool? Multiple { get; set; }
        public List<string?>? Values { get; set; }
    }

    private class TaskLinkFileModel
    {
        public Guid TypeId { get; set; }
        public Guid TaskId { get; set; }
    }

    private class TaskSeriesFileModel
    {
        public Guid SeriesId { get; set; }
        public int Number { get; set; }
    }
}
