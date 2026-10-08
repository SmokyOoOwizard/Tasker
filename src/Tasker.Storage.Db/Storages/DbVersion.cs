using System.Globalization;

namespace Tasker.Storage.Db.Storages;

/// <summary>
/// Версия записи в БД — счётчик: 1 при создании, +1 при каждом изменении.
/// Наружу отдаётся строкой (см. Tasker.Core.Versioning).
/// </summary>
internal static class DbVersion
{
    public const int Initial = 1;

    public static string ToText(int version) => version.ToString(CultureInfo.InvariantCulture);

    /// <summary>Чужая или испорченная строка версии — null: такой версии в БД точно нет.</summary>
    public static int? Parse(string version) =>
        int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}
