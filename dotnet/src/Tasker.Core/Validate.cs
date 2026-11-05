using System.Text.RegularExpressions;

namespace Tasker.Core;

internal static partial class Validate
{
    public const int MaxNameLength = 100;

    /// <summary>Непустое имя без пробелов по краям, не длиннее <paramref name="maxLength"/>.</summary>
    public static string Name(string? value, ErrorField field, int maxLength = MaxNameLength, string? english = null)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw TaskerErrors.Required(field, english == null ? null : $"{english} is required");
        if (trimmed.Length > maxLength)
            throw TaskerErrors.TooLong(field, maxLength, english == null ? null : $"{english} must be at most {maxLength} characters");
        return trimmed;
    }

    /// <summary>
    /// Имя поля: <see cref="Name"/>, и только буквы и цифры (любого алфавита; знак, который в Unicode входит в букву, — тоже). Без пробелов,
    /// «_», «-» и знаков «= ! &lt; &gt; :»: имя стоит в условии фильтра по полям (<c>Имя&gt;=3</c>, <c>Имя:set</c>) и в записи значений
    /// (<c>Имя=значение</c>), и по нему условие читается однозначно.
    /// </summary>
    public static string FieldName(string? value)
    {
        var name = Name(value, ErrorField.FieldName);
        if (!IsFieldName(name))
            throw TaskerErrors.FieldNameChars(name);
        return name;
    }

    /// <summary>Состоит ли имя только из букв и цифр (см. <see cref="FieldName"/>).</summary>
    public static bool IsFieldName(string name) =>
        name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark);

    /// <summary>
    /// Описание сущности: текст как есть (Unicode, переводы строк), без ограничения длины — как описание задачи; пустое или из одних пробелов — null.
    /// Общее правило для всех описаний: новой сущности достаточно вызвать его при создании и правке.
    /// </summary>
    public static string? Description(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    public static string StatusIcon(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (!Statuses.StatusIcons.All.Contains(value))
            throw TaskerErrors.StatusIcon(Statuses.StatusIcons.All.ToArray());
        return value;
    }

    public static string Color(string? value)
    {
        if (value == null || !ColorRegex().IsMatch(value))
            throw TaskerErrors.Color();
        return value.ToUpperInvariant();
    }

    public static Guid[] Distinct(Guid[]? ids, string field)
    {
        ids ??= [];
        if (ids.Distinct().Count() != ids.Length)
            throw TaskerErrors.Invalid($"{field} contains duplicates");
        return ids;
    }

    /// <summary>Все <paramref name="ids"/> должны быть среди <paramref name="known"/> (сущности того же проекта).</summary>
    public static void AllKnown(IEnumerable<Guid> ids, IEnumerable<Guid> known, string field)
    {
        var missing = ids.Except(known).ToArray();
        if (missing.Length > 0)
            throw TaskerErrors.Invalid($"{field}: not found in the project: {string.Join(", ", missing)}");
    }

    public const int MinPasswordLength = 8;

    /// <summary>Имя для входа: 3–50 символов, латиница, цифры и <c>. _ -</c>.</summary>
    public static string Username(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        if (!UsernameRegex().IsMatch(trimmed))
            throw TaskerErrors.Username();
        return trimmed;
    }

    /// <summary>
    /// Простая проверка формы адреса. Настоящая проверка почты — письмо с подтверждением, его пока нет.
    /// </summary>
    public static string Email(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length > 254 || !EmailRegex().IsMatch(trimmed))
            throw TaskerErrors.Email();
        return trimmed;
    }

    public static string Password(string? value)
    {
        if (value == null || value.Length < MinPasswordLength)
            throw TaskerErrors.Password(MinPasswordLength);
        return value;
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorRegex();

    [GeneratedRegex("^[A-Za-z0-9._-]{3,50}$")]
    private static partial Regex UsernameRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();
}
