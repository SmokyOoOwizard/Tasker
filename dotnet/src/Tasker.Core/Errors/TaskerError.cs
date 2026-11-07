using System.Text.Json.Serialization;

namespace Tasker.Core;

public sealed record TaskerError(string Code, string Message, IReadOnlyDictionary<string, object?> Params, IReadOnlyList<ErrorUsage> Usages)
{
    public TaskerError Reworded(string message, params (string Name, object? Value)[] context)
    {
        if (context.Length == 0)
            return this with { Message = message };
        var merged = new Dictionary<string, object?>(Params);
        foreach (var (name, value) in context)
            merged[name] = value;
        return this with { Message = message, Params = merged };
    }
}

public sealed record ErrorUsage(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Tasks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Board = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Column = null)
{
    public const string TasksKind = "tasks";
    public const string LinksKind = "links";
    public const string OwnFieldsKind = "ownFields";
    public const string BoardKind = "board";
    public const string BoardColumnKind = "boardColumn";
    public const string TaskTypeKind = "taskType";
    public const string StatusSetKind = "statusSet";
    public const string FieldKind = "field";

    public static readonly IReadOnlyList<string> Kinds =
        [TasksKind, LinksKind, OwnFieldsKind, BoardKind, BoardColumnKind, TaskTypeKind, StatusSetKind, FieldKind];
}

public sealed record ErrorSubject(string Entity, string English, string? Name)
{
    public override string ToString() => Entity == SeveralEntity ? English : $"{English} '{Name}'";

    private const string SeveralEntity = "several";

    public static ErrorSubject Task(string title) => new("task", "Task", title);
    public static ErrorSubject TaskType(string name) => new("taskType", "Task type", name);
    public static ErrorSubject Status(string name) => new("status", "Status", name);
    public static ErrorSubject StatusSet(string name) => new("statusSet", "Status set", name);
    public static ErrorSubject Board(string name) => new("board", "Board", name);
    public static ErrorSubject Series(string name) => new("series", "Series", name);
    public static ErrorSubject Project(string name) => new("project", "Project", name);
    public static ErrorSubject User(string name) => new("user", "User", name);
    public static ErrorSubject Agent(string name) => new("agent", "Agent", name);
    public static ErrorSubject LinkType(string name) => new("linkType", "Link type", name);
    public static ErrorSubject Field(string name) => new("field", "Field", name);
    public static ErrorSubject Enum(string name) => new("enum", "Enum", name);
    public static ErrorSubject Comment(string name) => new("comment", "Comment", name);

    public static ErrorSubject Statuses(IReadOnlyList<string> names) =>
        new(SeveralEntity, $"Statuses {string.Join(", ", names.Select(x => $"'{x}'"))}", string.Join(", ", names));

    public static ErrorSubject Several(IReadOnlyList<ErrorSubject> subjects) =>
        new(SeveralEntity, string.Join(", ", subjects), string.Join(", ", subjects.Select(x => x.Name)));
}

public enum ErrorField
{
    StatusName,
    StatusSetName,
    TaskTypeName,
    LinkTypeName,
    InwardName,
    OutwardName,
    EnumName,
    EnumValue,
    ProjectName,
    SeriesName,
    TokenName,
    Title,
    BoardName,
    ColumnName,
    FieldName,
    CommentBody
}

public static class ErrorFields
{
    public static string Wire(this ErrorField field)
    {
        var name = field.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static string English(this ErrorField field) => field switch
    {
        ErrorField.StatusName => "Status name",
        ErrorField.StatusSetName => "Status set name",
        ErrorField.TaskTypeName => "Task type name",
        ErrorField.LinkTypeName => "Link type name",
        ErrorField.InwardName => "Inward name",
        ErrorField.OutwardName => "Outward name",
        ErrorField.EnumName => "Enum name",
        ErrorField.EnumValue => "Enum value",
        ErrorField.ProjectName => "Project name",
        ErrorField.SeriesName => "Series name",
        ErrorField.TokenName => "Token name",
        ErrorField.Title => "Title",
        ErrorField.BoardName => "Board name",
        ErrorField.ColumnName => "Column name",
        ErrorField.FieldName => "Field name",
        ErrorField.CommentBody => "Comment text",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
    };
}
