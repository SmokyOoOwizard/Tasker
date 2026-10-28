namespace Tasker.Core.Tasks;

/// <summary>
/// Тип задачи в списке: всё, что в <see cref="TaskType"/>, но описание может быть усечено (см. <see cref="DescriptionPreview"/>).
/// Один тип (get, <c>GET /task-types/{id}</c>) отдают полным.
/// </summary>
public record TaskTypeListItem : TaskType
{
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public TaskTypeListItem(TaskType type, int descriptionLength) : base(type)
    {
        var (text, length) = DescriptionPreview.Cut(type.Description, descriptionLength);
        Description = text ?? "";
        DescriptionTruncated = Description.Length < type.Description.Length;
        DescriptionLength = length;
    }

    /// <summary>Описание в списке усечено: полный текст — в get или с <c>descriptionLength</c> = -1.</summary>
    public bool DescriptionTruncated { get; init; }

    /// <summary>Полная длина описания в символах Unicode; 0 — описания нет. Не зависит от усечения.</summary>
    public int DescriptionLength { get; init; }
}
