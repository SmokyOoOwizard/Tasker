namespace Tasker.Storage.Db.Storages;

/// <summary>Время в БД хранится как DateTime в UTC (см. TaskDbModel).</summary>
internal static class DbTime
{
    public static DateTime ToDb(DateTimeOffset value) => value.UtcDateTime;

    public static DateTimeOffset FromDb(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
