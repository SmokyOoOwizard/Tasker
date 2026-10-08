namespace Tasker.Core.TaskSeries;

/// <summary>
/// Ссылка на задачу: Guid или «ПРЕФИКС-номер» (<c>TSK-5</c>). Разбор по последнему дефису, регистр префикса важен.
/// Короткий id (8 и более шестнадцатеричных символов Guid, <see cref="ShortId"/>) разбирается только с <c>allowIdPrefix</c>: REST принимает
/// полный id, консоль и MCP — ещё и префикс. Со ссылкой серии он не пересекается: в ссылке есть дефис.
/// </summary>
/// <param name="IdKey">Префикс id в виде начала Guid (<see cref="ShortId.TryKey"/>); задан только у короткого id.</param>
public readonly record struct TaskReference(Guid? Id, string? Prefix, int? Number, string? IdKey = null)
{
    /// <returns>null — строка не похожа ни на Guid, ни на «ПРЕФИКС-номер» (ни на префикс id, если он разрешён).</returns>
    public static TaskReference? TryParse(string? text, bool allowIdPrefix = false)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        if (Guid.TryParse(trimmed, out var id))
            return new TaskReference(id, null, null);

        if (allowIdPrefix && ShortId.TryKey(trimmed) is { } key)
            return new TaskReference(null, null, null, key);

        var dash = trimmed.LastIndexOf('-');
        if (dash <= 0 || dash == trimmed.Length - 1)
            return null;

        var prefix = trimmed[..dash];
        var digits = trimmed[(dash + 1)..];
        if (!SeriesPrefix.IsValid(prefix) || !digits.All(char.IsAsciiDigit))
            return null;
        if (!int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 1)
            return null;

        return new TaskReference(null, prefix, number);
    }

    public static TaskReference Parse(string? text, bool allowIdPrefix = false) =>
        TryParse(text, allowIdPrefix) ?? throw new TaskerValidationException(allowIdPrefix
            ? $"'{text}' is neither a task id (full, or its first {ShortId.Length}+ hex characters) nor a reference like TSK-5"
            : $"'{text}' is neither a task id nor a reference like TSK-5");
}
