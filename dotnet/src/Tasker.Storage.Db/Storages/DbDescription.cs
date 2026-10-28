namespace Tasker.Storage.Db.Storages;

/// <summary>
/// Описание сущности в БД: столбец <c>Description</c> (NULL — описания нет), в модели Core — пустая строка.
/// Общий приём для всех сущностей с описанием.
/// </summary>
internal static class DbDescription
{
    public static string? ToColumn(string description) => description.Length == 0 ? null : description;

    public static string FromColumn(string? column) => column ?? "";
}
