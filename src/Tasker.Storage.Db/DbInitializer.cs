using Autofac;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Tasker.Core;

namespace Tasker.Storage.Db;

/// <summary>
/// Создаёт схему БД при старте, если её ещё нет.
/// TODO: временно, до миграций — EnsureCreated не умеет обновлять уже созданную схему,
/// поэтому после изменения моделей старую БД нужно удалить.
/// </summary>
internal class DbInitializer(ILifetimeScope scope) : IHostedService, IStorageLifecycle
{
    public Task StartAsync(CancellationToken ct) => Start(ct);

    public async Task Start(CancellationToken ct)
    {
        // Файл SQLite открывают несколько процессов: если два создают схему одновременно, проигравший
        // получает «table already exists» или «database is locked» — повторяем, схема уже будет на месте.
        for (var attempt = 1;; attempt++)
        {
            try
            {
                await using var inner = scope.BeginLifetimeScope();
                var context = inner.Resolve<AppDbContext>();
                await context.Database.EnsureCreatedAsync(ct);
                await AddMissingColumns(context, ct);
                return;
            }
            catch (SqliteException) when (attempt < 5)
            {
                await Task.Delay(100 * attempt, ct);
            }
        }
    }

    /// <summary>
    /// EnsureCreated не дополняет уже созданную схему, а без этого БД, созданная до признаков типов связей («допускает циклы», «иерархический»), перестала бы
    /// читаться. Столбцы добавляются со значениями «как было»: циклы допускаются, тип обычный; и только у «Blocks» по умолчанию циклы запрещаются.
    /// «Parent/Child» в такой БД появляется без миграции, как тип по умолчанию (<see cref="Core.Links.LinkTypeService"/>).
    /// </summary>
    private static async Task AddMissingColumns(AppDbContext context, CancellationToken ct)
    {
        if (await AddColumn(context, "AllowCycles", "1", "TRUE", ct))
        {
            foreach (var type in await context.LinkTypes.ToArrayAsync(ct))
            {
                if (type.Id == Core.Links.DefaultLinkTypes.IdOf(type.ProjectId, "blocks"))
                    type.AllowCycles = false;
            }
            await context.SaveChangesAsync(ct);
        }

        await AddColumn(context, "Hierarchical", "0", "FALSE", ct);
    }

    /// <returns>true — столбец был добавлен сейчас.</returns>
    private static async Task<bool> AddColumn(AppDbContext context, string column, string sqliteDefault, string postgresDefault, CancellationToken ct)
    {
        try
        {
            await context.Database.ExecuteSqlRawAsync($"SELECT \"{column}\" FROM link_types WHERE 1 = 0", ct);
            return false;
        }
        catch (DbException)
        {
            // Столбца нет — добавляем ниже.
        }

        await context.Database.ExecuteSqlRawAsync(context.Database.IsSqlite()
            ? $"ALTER TABLE link_types ADD COLUMN \"{column}\" INTEGER NOT NULL DEFAULT {sqliteDefault}"
            : $"ALTER TABLE link_types ADD COLUMN IF NOT EXISTS \"{column}\" boolean NOT NULL DEFAULT {postgresDefault}", ct);
        return true;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
