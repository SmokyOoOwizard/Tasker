using System.Globalization;

namespace Tasker.Core.Tasks;

/// <summary>
/// Задача в списке: всё, что в <see cref="TaskItem"/>, но описание может быть усечено (см. <see cref="DescriptionPreview"/>).
/// Усечение в одном месте — здесь, а не в клиентах: MCP, REST и консоль получают его из <see cref="TaskService.Preview"/>.
/// Одну задачу (get_task, <c>GET /tasks/{id}</c>) всегда отдают полной, как <see cref="TaskItem"/>.
/// </summary>
public record TaskListItem : TaskItem
{
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public TaskListItem(TaskItem task, int descriptionLength, int linksCount, Guid[]? parentIds = null, int childCount = 0) : base(task)
    {
        ParentIds = parentIds ?? [];
        ChildCount = childCount;
        var (text, length) = DescriptionPreview.Cut(task.Description, descriptionLength);
        Description = text;
        DescriptionTruncated = text != null && text.Length < task.Description!.Length;
        DescriptionLength = length;
        LinksCount = linksCount;
    }

    /// <summary>Описание в списке усечено: полный текст — в get_task или с <c>descriptionLength</c> = -1.</summary>
    public bool DescriptionTruncated { get; init; }

    /// <summary>Полная длина описания в символах Unicode (кодовых точках); 0 — описания нет. Не зависит от усечения.</summary>
    public int DescriptionLength { get; init; }

    /// <summary>Число связей задачи: исходящих и входящих (см. <see cref="TaskItem.Links"/> и <see cref="Links.TaskLinkService.GetLinks"/>).</summary>
    public int LinksCount { get; init; }

    /// <summary>
    /// Родители задачи по иерархическим связям (<see cref="Links.LinkType.Hierarchical"/>): id задач, у которых есть исходящая связь такого типа на эту.
    /// Пусто — задача не входит ни в один эпик. Список плоский и в <c>--json</c>, и в REST/MCP: дерево клиент при желании строит сам.
    /// </summary>
    public Guid[] ParentIds { get; init; }

    /// <summary>Сколько у задачи дочерних задач (исходящих иерархических связей, разные цели); больше 0 — задача «эпик».</summary>
    public int ChildCount { get; init; }
}

/// <summary>Усечение описания для списков: параметр <c>descriptionLength</c> MCP, REST и консоли.</summary>
public static class DescriptionPreview
{
    /// <summary>Значение <c>descriptionLength</c>: полный текст.</summary>
    public const int Full = -1;

    /// <summary>Предпросмотр по умолчанию в MCP-списках.</summary>
    public const int McpDefault = 200;

    /// <summary>Проверка параметра клиента: 0 (без описания), N &gt; 0 или -1 (полный текст); не задан — <paramref name="default"/>.</summary>
    /// <exception cref="TaskerValidationException">Меньше -1.</exception>
    public static int Check(int? descriptionLength, int @default = Full) =>
        descriptionLength switch
        {
            null => @default,
            < Full => throw new TaskerValidationException(
                $"descriptionLength: {descriptionLength} - use 0 (no description), N (first N characters) or -1 (full text)"),
            _ => descriptionLength.Value
        };

    /// <summary>
    /// Первые <paramref name="length"/> символов Unicode (кодовых точек) описания. Резать посреди графемы нельзя — суррогатную пару,
    /// эмодзи с модификатором или знак с комбинируемыми знаками: тогда она отбрасывается целиком, так что в тексте не больше
    /// <paramref name="length"/> символов. «…» в текст не добавляется. -1 — без усечения, 0 — пустой текст.
    /// </summary>
    /// <returns>Текст (null — описания нет) и полная длина описания в символах.</returns>
    public static (string? Text, int Length) Cut(string? description, int length)
    {
        if (string.IsNullOrEmpty(description))
            return (description, 0);

        var total = Count(description);
        if (length < 0 || length >= total)
            return (description, total);
        if (length == 0)
            return ("", total);

        var runes = 0;
        var end = 0;
        while (end < description.Length)
        {
            var cluster = StringInfo.GetNextTextElementLength(description.AsSpan(end));
            var count = Count(description.AsSpan(end, cluster));
            if (runes + count > length)
                break;
            runes += count;
            end += cluster;
        }

        return (description[..end], total);
    }

    private static int Count(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
            count++;
        return count;
    }
}
