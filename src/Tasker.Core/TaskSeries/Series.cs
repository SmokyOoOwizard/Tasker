using System.Text.RegularExpressions;

namespace Tasker.Core.TaskSeries;

/// <summary>
/// Серия задач проекта: задачам в ней выдаются номера 1, 2, 3… (ссылка «ПРЕФИКС-номер», например <c>TSK-5</c>).
/// Не привязана к типу задачи; задача может быть в нескольких сериях (у неё по номеру в каждой) или ни в одной.
/// </summary>
public record Series
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required string Name { get; init; }

    /// <summary>Только латинские буквы и цифры, регистр важен (<c>TSK</c> и <c>tsk</c> — разные серии), уникален в проекте.</summary>
    public required string Prefix { get; init; }

    /// <summary>См. <see cref="Versioning"/>.</summary>
    public required string Version { get; init; }
}

/// <summary>Правила префикса серии.</summary>
public static partial class SeriesPrefix
{
    public const int MaxLength = 20;

    public static bool IsValid(string? prefix) =>
        prefix is { Length: > 0 and <= MaxLength } && PrefixRegex().IsMatch(prefix);

    /// <exception cref="TaskerValidationException">Префикс не подходит.</exception>
    public static string Validate(string? prefix) =>
        IsValid(prefix)
            ? prefix!
            : throw new TaskerValidationException($"Series prefix must be 1-{MaxLength} Latin letters or digits");

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex PrefixRegex();
}
