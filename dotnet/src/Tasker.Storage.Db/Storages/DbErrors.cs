using Microsoft.Data.Sqlite;
using Npgsql;

namespace Tasker.Storage.Db.Storages;

internal static class DbErrors
{
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;
    private const string PostgresUniqueViolation = "23505";

    /// <summary>
    /// Нарушение уникального индекса или первичного ключа — у SQLite и Postgres оно выглядит по-разному
    /// (SQLite для ключа даёт другой расширенный код, хотя текст тот же: «UNIQUE constraint failed»).
    /// </summary>
    public static bool IsUniqueViolation(Exception e)
    {
        for (var current = e; current != null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteExtendedErrorCode: SqliteConstraintUnique or SqliteConstraintPrimaryKey })
                return true;
            if (current is PostgresException { SqlState: PostgresUniqueViolation })
                return true;
        }
        return false;
    }
}
