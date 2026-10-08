using System.Text;
using System.Text.RegularExpressions;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Имя файла сущности проекта (задачи, серии, типа задачи, статуса, набора статусов, доски, типа связи, поля, перечисления):
/// <c>&lt;название&gt;-&lt;первые 8 символов id&gt;.yaml</c>, например <c>исправить-вход-3f2a9c1e.yaml</c>.
/// Название (у задачи — заголовок) делает файл узнаваемым в папке и в диффах, а суффикс из id — уникальным: две ветки, где создали
/// сущности с одинаковым названием, получают разные файлы и сливаются без конфликта «оба добавили один файл»; одинаковые названия
/// в одной папке — обычное дело. Настоящий id — поле <c>id</c> внутри файла; имя только подсказывает, где искать
/// (<see cref="IdPrefix"/>), и проверяется при чтении.
/// <para>
/// Файлы старого формата (задачи — до версии формата 2, остальные сущности — до версии 5) называются по полному Guid:
/// <c>&lt;guid&gt;.yaml</c>. Их читают наравне с новыми, а при записи они получают новое имя (и <c>tasker migrate</c> переименовывает остальные).
/// </para>
/// </summary>
internal static partial class EntityFileNames
{
    /// <summary>Сколько знаков id идёт в имя.</summary>
    public const int IdLength = 8;

    /// <summary>Не больше стольких знаков названия: имена файлов не должны упираться в ограничения файловых систем.</summary>
    public const int MaxSlugLength = 60;

    [GeneratedRegex(@"^(?<slug>.+)-(?<id>[0-9a-f]{8})$")]
    private static partial Regex Stem();

    /// <summary>Первые знаки id в нижнем регистре, без дефисов — как в имени файла.</summary>
    public static string IdPrefix(Guid id) => id.ToString("N")[..IdLength];

    /// <summary>
    /// Название для имени файла: буквы и цифры (любого алфавита, кириллица остаётся) в нижнем регистре, всё остальное — один дефис между словами.
    /// Знаки, недопустимые в именах файлов (<c>/ \ : * ? " &lt; &gt; |</c>), в слова не входят, поэтому результат безопасен на любой системе.
    /// </summary>
    /// <param name="emptySlug">Что подставляется, если в названии нет ни одной буквы или цифры (у каждого вида сущности своё: <see cref="EntityFolder.EmptySlug"/>).</param>
    public static string Slug(string? name, string emptySlug)
    {
        var builder = new StringBuilder();
        var pendingDash = false;
        var length = 0;

        foreach (var rune in (name ?? "").Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (!Rune.IsLetterOrDigit(rune))
            {
                pendingDash = builder.Length > 0;
                continue;
            }

            var text = rune.ToString().ToLowerInvariant();
            if (length + text.Length + (pendingDash ? 1 : 0) > MaxSlugLength)
                break;

            if (pendingDash)
                builder.Append('-');
            builder.Append(text);
            length += text.Length + (pendingDash ? 1 : 0);
            pendingDash = false;
        }

        // Поэлементное приведение к нижнему регистру не должно оставить текст не в форме NFC (имена сравнивают как NFC).
        // Имя «<slug>-<id8>.yaml» безопасно и для Windows: в нём только буквы, цифры и дефисы, оно не кончается точкой или пробелом,
        // а зарезервированные имена (CON, NUL…) с ним не совпадают из-за суффикса «-<id8>» (WindowsNames).
        return builder.Length == 0 ? emptySlug : builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Имя файла с расширением: <c>исправить-вход-3f2a9c1e.yaml</c>.</summary>
    public static string FileName(string? name, Guid id, string emptySlug) => $"{Slug(name, emptySlug)}-{IdPrefix(id)}{YamlFile.Extension}";

    /// <summary>Что говорит имя файла об id: полный Guid (старое имя) или только первые знаки (новое).</summary>
    /// <returns>false — имя не похоже на имя файла сущности.</returns>
    public static bool TryParse(string fileName, out Guid? fullId, out string? idPrefix)
    {
        fullId = null;
        idPrefix = null;
        if (!fileName.EndsWith(YamlFile.Extension, StringComparison.Ordinal))
            return false;

        var stem = fileName[..^YamlFile.Extension.Length];
        if (Guid.TryParse(stem, out var guid))
        {
            fullId = guid;
            return true;
        }

        if (Stem().Match(stem) is not { Success: true } match)
            return false;

        idPrefix = match.Groups["id"].Value;
        return true;
    }

    /// <summary>Совпадает ли имя файла с тем, каким оно должно быть у этой сущности (без учёта формы Unicode: файловые системы её меняют).</summary>
    public static bool IsCurrent(string fileName, string? name, Guid id, string emptySlug) =>
        string.Equals(fileName.Normalize(NormalizationForm.FormC), FileName(name, id, emptySlug), StringComparison.Ordinal);
}
