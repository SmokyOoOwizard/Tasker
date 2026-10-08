using Tasker.Core.Tasks;

namespace Tasker.Core.Statuses;

/// <summary>
/// Статус в списке: всё, что в <see cref="Status"/>, но описание может быть усечено (см. <see cref="DescriptionPreview"/>).
/// Один статус (get, <c>GET /statuses/{id}</c>) отдают полным.
/// </summary>
public record StatusListItem : Status
{
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public StatusListItem(Status status, int descriptionLength) : base(status)
    {
        var (text, length) = DescriptionPreview.Cut(status.Description, descriptionLength);
        Description = text ?? "";
        DescriptionTruncated = Description.Length < status.Description.Length;
        DescriptionLength = length;
    }

    /// <summary>Описание в списке усечено: полный текст — в get или с <c>descriptionLength</c> = -1.</summary>
    public bool DescriptionTruncated { get; init; }

    /// <summary>Полная длина описания в символах Unicode; 0 — описания нет. Не зависит от усечения.</summary>
    public int DescriptionLength { get; init; }
}
