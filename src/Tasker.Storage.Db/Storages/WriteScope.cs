using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tasker.Core.TaskSeries;

namespace Tasker.Storage.Db.Storages;

/// <summary>
/// Транзакция на контексте запроса; хранилища запроса делят этот контекст и работают внутри неё.
/// Если сработал уникальный индекс номеров (другой запрос успел взять тот же номер) или сбой сериализации,
/// откатывает всё, очищает трекер и повторяет действие целиком, ограниченное число раз.
/// </summary>
internal class WriteScope(AppDbContext context) : IWriteScope
{
    private const int MaxAttempts = 5;
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const string PostgresSerializationFailure = "40001";
    private const string PostgresDeadlock = "40P01";

    public async Task<T> Exclusive<T>(Guid projectId, Func<Task<T>> action, CancellationToken ct = default)
    {
        // Вложенный вызов: транзакция уже открыта внешним, повторять будет он.
        if (context.Database.CurrentTransaction != null)
            return await action();

        for (var attempt = 1;; attempt++)
        {
            try
            {
                return await RunInTransaction(action, ct);
            }
            catch (Exception e) when (attempt < MaxAttempts && IsRetryable(e))
            {
                // Транзакция уже откачена при выходе из using; изменения, принятые трекером, больше не верны.
                context.ChangeTracker.Clear();
                await Task.Delay(Random.Shared.Next(10, 30) * attempt, ct);
            }
        }
    }

    private async Task<T> RunInTransaction<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (context.Database.IsSqlite())
        {
            // BEGIN IMMEDIATE: блокировку записи берём сразу. С отложенной транзакцией «прочитать максимум, затем записать»
            // у второго писателя не получается повысить блокировку, и SQLite сразу отвечает «database is locked», не дожидаясь.
            await context.Database.OpenConnectionAsync(ct);
            try
            {
                var connection = (SqliteConnection)context.Database.GetDbConnection();
                await using var sqlite = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                await context.Database.UseTransactionAsync(sqlite, ct);
                try
                {
                    var result = await action();
                    await sqlite.CommitAsync(ct);
                    return result;
                }
                finally
                {
                    await context.Database.UseTransactionAsync(null, ct);
                }
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
        }

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var value = await action();
        await transaction.CommitAsync(ct);
        return value;
    }

    private static bool IsRetryable(Exception e)
    {
        if (DbErrors.IsUniqueViolation(e))
            return true;
        for (var current = e; current != null; current = current.InnerException)
        {
            // База SQLite занята другим писателем (5) или таблица заблокирована в общем кэше (6).
            if (current is SqliteException { SqliteErrorCode: SqliteBusy or SqliteLocked })
                return true;
            if (current is Npgsql.PostgresException { SqlState: PostgresSerializationFailure or PostgresDeadlock })
                return true;
        }
        return false;
    }
}
