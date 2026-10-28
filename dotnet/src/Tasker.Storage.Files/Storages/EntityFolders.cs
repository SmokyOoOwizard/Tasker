using Tasker.Storage.Files.Index;

namespace Tasker.Storage.Files.Storages;

/// <summary>Что файл говорит о сущности: настоящий id и название (то, по чему называется файл).</summary>
internal record EntityHead(Guid Id, string? Name);

/// <summary>
/// Папка проекта, в которой лежат сущности одного вида, по файлу на сущность, с именем по названию (<see cref="EntityFileNames"/>).
/// </summary>
/// <param name="Name">Имя папки в проекте: <c>series</c>, <c>task-types</c>…</param>
/// <param name="EmptySlug">Имя файла, если в названии нет ни одной буквы или цифры.</param>
/// <param name="Peek">Читает файл (старый формат — в памяти) и возвращает id и название; null — файл пуст.</param>
internal sealed record EntityFolder(
    IndexKind Kind, string Name, string EmptySlug, Func<Guid, string, CancellationToken, Task<EntityHead?>> Peek)
{
    /// <summary>Имя файла сущности с этим названием и id: <c>исправить-вход-3f2a9c1e.yaml</c>.</summary>
    public string FileName(string? name, Guid id) => EntityFileNames.FileName(name, id, EmptySlug);

    /// <summary>Совпадает ли имя файла с тем, каким оно должно быть у сущности с этим названием.</summary>
    public bool IsCurrent(string fileName, string? name, Guid id) => EntityFileNames.IsCurrent(fileName, name, id, EmptySlug);
}

/// <summary>
/// Все папки сущностей проекта в одном месте: раскладка (<see cref="WorkspaceLayout"/>), пути (<see cref="ProjectDirectory"/>),
/// хранилища и миграция берут имена файлов и порядок чтения отсюда, а не повторяют их каждый у себя.
/// Пользователи (<c>users/&lt;id&gt;.yaml</c>) и проекты (<c>projects/&lt;id&gt;/project.yaml</c>) сюда не входят: у проекта
/// имя папки — Guid, а файл называется всегда одинаково, у пользователя имя уникально и меняется редко, поэтому файл остаётся по id.
/// </summary>
internal static class EntityFolders
{
    public static readonly EntityFolder Tasks = new(IndexKind.Task, "tasks", "task",
        async (project, path, ct) => await TaskFile.Read(project, path, ct) is { } x ? new EntityHead(x.Id, x.Title) : null);

    public static readonly EntityFolder TaskTypes = new(IndexKind.TaskType, "task-types", "type",
        async (project, path, ct) => await TaskTypeFile.Read(project, path, ct) is { } x ? new EntityHead(x.Id, x.Name) : null);

    public static readonly EntityFolder Statuses = new(IndexKind.Status, "statuses", "status",
        async (project, path, ct) => await StatusFile.Read(project, path, ct) is { } x ? new EntityHead(x.Id, x.Name) : null);

    public static readonly EntityFolder StatusSets = new(IndexKind.StatusSet, "status-sets", "set",
        async (project, path, ct) => await StatusSetFile.Read(project, path, ct) is { } x ? new EntityHead(x.Id, x.Name) : null);

    public static readonly EntityFolder Boards = new(IndexKind.Board, "boards", "board",
        async (project, path, ct) => await BoardFile.Read(project, path, ct) is { } x ? new EntityHead(x.Id, x.Name) : null);

    public static readonly EntityFolder Series = new(IndexKind.Series, "series", "series",
        async (project, path, ct) => await SeriesFile.Read(project, path, ct) is { } x ? new EntityHead(x.Id, x.Name) : null);

    public static readonly EntityFolder LinkTypes = new(IndexKind.LinkType, "link-types", "link",
        async (project, path, ct) => await LinkTypeFile.Read(project, path, ct) is { } x ? new EntityHead(x.Id, x.Name) : null);

    public static readonly EntityFolder Fields = new(IndexKind.Field, "fields", "field",
        async (project, path, ct) => await FieldFile.Read(project, path, ct) is { } x ? new EntityHead(x.Id, x.Name) : null);

    public static readonly EntityFolder Enums = new(IndexKind.FieldEnum, "enums", "enum",
        async (project, path, ct) => await FieldEnumFile.Read(project, path, ct) is { } x ? new EntityHead(x.Id, x.Name) : null);

    public static readonly IReadOnlyList<EntityFolder> All =
        [Tasks, TaskTypes, Statuses, StatusSets, Boards, Series, LinkTypes, Fields, Enums];

    /// <returns>null — у этого вида нет папки сущностей в проекте (пользователь, проект).</returns>
    public static EntityFolder? Find(IndexKind kind) => All.FirstOrDefault(x => x.Kind == kind);

    /// <exception cref="ArgumentOutOfRangeException">Для этого вида сущности нет папки в проекте (пользователь, проект).</exception>
    public static EntityFolder Of(IndexKind kind) =>
        Find(kind) ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "The kind has no folder of its own in a project");

    /// <returns>null — это не папка сущностей.</returns>
    public static EntityFolder? Named(string folder) => All.FirstOrDefault(x => x.Name == folder);
}
