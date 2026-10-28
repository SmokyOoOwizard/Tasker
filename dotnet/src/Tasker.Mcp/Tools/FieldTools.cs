using System.ComponentModel;
using ModelContextProtocol.Server;
using Tasker.Core;
using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Core.Projects;

namespace Tasker.Mcp.Tools;

/// <summary>
/// Каталог полей задач проекта и перечисления. Поля подключаются к типам задач (create_task_type / update_task_type, параметр `fields`),
/// значения и собственные поля задаются у задач (create_task / update_task, параметр `fields`).
/// </summary>
[McpServerToolType]
public static class FieldTools
{
    private const string VersionHint =
        "`version` is the entity's current version (from the list/get call); if someone changed it since, the call fails with [modified] — re-read and retry.";

    private const string FieldHint = "`field` is a field id (Guid) or its name (case-insensitive).";
    private const string EnumHint = "`enumeration` is an enum id (Guid) or its name (case-insensitive).";

    /// <summary>Что сделать у задач со значениями перечисления, которые убирают, пока они где-то выбраны.</summary>
    public enum RemovedValuesChoice
    {
        /// <summary>Убрать значение у всех задач, которые его используют.</summary>
        Clear,

        /// <summary>Подставить вместо него другое значение (<c>reassignTo</c>).</summary>
        Reassign
    }

    [McpServerTool(Name = "list_fields", ReadOnly = true)]
    [Description(
        "Lists the field catalog of a project by name. A field has a name, a value `type` (string, int, float, bool, date, enum), " +
        "`multiple` (several values) and, for enum, `enumId`. Paged: offset/limit (limit 1-200, default 50); the result has totalCount.")]
    public static Task<ListDto<FieldDefinition>> ListFields(
        ProjectService projects, FieldService fields, Guid projectId, int offset = 0, int limit = Page.DefaultLimit, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await fields.GetRange(projectId, Page.Of(offset, limit), ct);
        });

    [McpServerTool(Name = "get_field", ReadOnly = true)]
    [Description("Gets a field of the catalog by id or name, including its current `version`. " + FieldHint)]
    public static Task<FieldDefinition> GetField(ProjectService projects, FieldService fields, Guid projectId, string field, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return McpCall.Found(await fields.Find(projectId, field, ct), $"Field '{field}'");
        });

    [McpServerTool(Name = "create_field")]
    [Description(
        "Creates a field in the catalog. type: string, int, float, bool, date or enum. multiple: the field may hold several values (default: one). " +
        "A field of type enum needs `enumId` (an enum of this project, see list_enums); other types must not have one. " +
        "The name is unique in the project (fails with [in_use] if taken). Type, `multiple` and enum can be changed later with update_field. " +
        "A new field is not used anywhere yet: connect it to a task type with the `fields` parameter of create_task_type/update_task_type, " +
        "or give a task its value with the `fields` parameter of create_task/update_task.")]
    public static Task<FieldDefinition> CreateField(
        ProjectService projects, FieldService fields, Guid projectId, string name, FieldType type,
        bool? multiple = null, Guid? enumId = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await fields.Create(projectId, new CreateField(name, type, multiple, enumId), ct);
        });

    [McpServerTool(Name = "update_field", Idempotent = true)]
    [Description(
        "Changes a field: its name, `type`, `multiple` and enum (`enumId`); null keeps. The values already chosen in tasks are converted atomically " +
        "(all values and the field, or nothing). Allowed type changes: any type to string (the value becomes text; for enum its name), int to float, " +
        "string to int/float/bool/date (every value must parse) or to enum (matched by name); enum to another enum (`enumId`; matched by name). " +
        "Other changes (e.g. float to int) are refused: go through string. A field that becomes enum needs `enumId`; one that stops being enum must not pass it. " +
        "`multiple`: one to several makes the value a list; several to one needs a choice for tasks having several values. " +
        "If tasks are affected and their values cannot be kept as they are, the call fails with [in_use] giving the number of tasks (and example values); " +
        "repeat it with a choice: clearUnconvertible=true clears the values that do not parse / have no counterpart in the new enum (the others are converted); " +
        "mapping=[{from, to}] matches old values to values of the new enum explicitly (from: the old value or, for enum, its id or name; to: a value id or name); " +
        "several=keepFirst keeps the first value of tasks that have several, several=clear clears them. A required field left empty by a choice stays empty on " +
        "the task until its next content edit. A field used in the conditions of board columns keeps its type, enum and `multiple` (fails with [in_use] naming the columns; " +
        "remove the conditions first); renaming it is fine. " + VersionHint)]
    public static Task<FieldDefinition> UpdateField(
        ProjectService projects, FieldService fields, Guid projectId, Guid fieldId, string version, string? name = null,
        FieldType? type = null, bool? multiple = null, Guid? enumId = null,
        bool? clearUnconvertible = null, SeveralValues? several = null, FieldValueMapping[]? mapping = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var choice = clearUnconvertible == true || several != null || mapping != null
                ? new FieldChangeChoice(clearUnconvertible == true, several, mapping)
                : null;
            return McpCall.Found(
                await fields.Update(projectId, fieldId, new UpdateField(name, version, type, multiple, enumId, choice), ct), $"Field {fieldId}");
        });

    [McpServerTool(Name = "delete_field", Destructive = true)]
    [Description(
        "Deletes a field from the catalog. Fails with [in_use] naming the task types the field is connected to, the number of tasks that have values " +
        "or the field added, and the board columns with a condition on it: disconnect/clear it or remove the condition first. " + VersionHint)]
    public static Task<string> DeleteField(
        ProjectService projects, FieldService fields, Guid projectId, Guid fieldId, string version, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            McpCall.Found(await fields.Delete(projectId, fieldId, version, ct), $"Field {fieldId}");
            return "deleted";
        });

    [McpServerTool(Name = "list_enums", ReadOnly = true)]
    [Description(
        "Lists the enums of a project by name. An enum is a named list of values (each with its own `id` and `name`) that fields of type enum choose from; " +
        "one enum can be used by several fields. Paged: offset/limit (limit 1-200, default 50); the result has totalCount.")]
    public static Task<ListDto<FieldEnum>> ListEnums(
        ProjectService projects, FieldEnumService enums, Guid projectId, int offset = 0, int limit = Page.DefaultLimit, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await enums.GetRange(projectId, Page.Of(offset, limit), ct);
        });

    [McpServerTool(Name = "get_enum", ReadOnly = true)]
    [Description("Gets an enum by id or name with its values and current `version`. " + EnumHint)]
    public static Task<FieldEnum> GetEnum(ProjectService projects, FieldEnumService enums, Guid projectId, string enumeration, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return McpCall.Found(await enums.Find(projectId, enumeration, ct), $"Enum '{enumeration}'");
        });

    [McpServerTool(Name = "create_enum")]
    [Description(
        "Creates an enum from value names in display order: at least one, no repeats (case-insensitive). The name is unique in the project " +
        "(fails with [in_use] if taken). Use it in a field: create_field with type enum and this enum's id.")]
    public static Task<FieldEnum> CreateEnum(
        ProjectService projects, FieldEnumService enums, Guid projectId, string name, string[] values, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await enums.Create(projectId, new CreateFieldEnum(name, values), ct);
        });

    [McpServerTool(Name = "update_enum", Idempotent = true)]
    [Description(
        "Renames an enum and/or replaces its values (full ordered list; null keeps). Each value is {id?, name}: a value with an `id` stays the same value " +
        "(this is how to rename or reorder it), one without `id` is new, one missing from the list is removed. " +
        "Removing a value that tasks have selected needs a choice, one for all removed values: removedValues=clear removes it from those tasks, " +
        "removedValues=reassign with reassignTo=<id of a remaining value> selects that value instead. Without a choice the call fails with [in_use] " +
        "giving the number of tasks. A required field left empty by `clear` stays empty on the task until its next content edit. " +
        "A removed value that is used in the field conditions of board columns (see create_board fieldFilters) needs the same choice: clear drops the condition from the column, " +
        "reassign replaces the value in it; if that would make columns sharing a status show the same tasks, the call fails with [in_use] and the board must be changed first. " +
        "The result is the enum plus `affectedTasks`: how many tasks were rewritten by the choice (0 when no task used a removed value), and `affectedColumns` " +
        "(only when not 0): how many board column conditions were dropped or replaced. " + VersionHint)]
    public static Task<CascadeResult<FieldEnum>> UpdateEnum(
        ProjectService projects, FieldEnumService enums, Guid projectId, Guid enumId, string version,
        string? name = null, FieldEnumValueInput[]? values = null,
        RemovedValuesChoice? removedValues = null, Guid? reassignTo = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var removed = removedValues switch
            {
                null when reassignTo != null => throw new TaskerValidationException("ReassignTo: only together with removedValues=reassign"),
                null => null,
                RemovedValuesChoice.Clear when reassignTo != null => throw new TaskerValidationException("ReassignTo: not with removedValues=clear"),
                RemovedValuesChoice.Clear => new RemovedEnumValues(Clear: true),
                RemovedValuesChoice.Reassign when reassignTo == null => throw new TaskerValidationException("ReassignTo: required with removedValues=reassign"),
                _ => new RemovedEnumValues(ReassignTo: reassignTo)
            };
            return McpCall.Found(await enums.Update(projectId, enumId, new UpdateFieldEnum(name, values, version, removed), ct), $"Enum {enumId}");
        });

    [McpServerTool(Name = "delete_enum", Destructive = true)]
    [Description("Deletes an enum. Fails with [in_use] if catalog fields or own fields of tasks refer to it. " + VersionHint)]
    public static Task<string> DeleteEnum(
        ProjectService projects, FieldEnumService enums, Guid projectId, Guid enumId, string version, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            McpCall.Found(await enums.Delete(projectId, enumId, version, ct), $"Enum {enumId}");
            return "deleted";
        });
}
