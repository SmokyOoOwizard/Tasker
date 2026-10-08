using Autofac;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Tasker.Core;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Storage.Db.Storages;

namespace Tasker.Storage.Db;

public enum DbMode
{
    /// <summary>Ничего не задано: данные живут, пока работает процесс.</summary>
    SqliteInMemory,
    SqliteFile,
    Postgres
}

/// <summary>
/// Хранение в базе данных. Провайдер — по <see cref="DbConfigs"/>: Postgres, файл SQLite
/// или, если не задано ничего, SQLite в памяти.
/// </summary>
public class DbStorageModule(DbConfigs configs) : Module
{
    public static DbMode ModeOf(DbConfigs configs)
    {
        var postgres = !string.IsNullOrWhiteSpace(configs.PostgresConnectionString);
        var sqlite = !string.IsNullOrWhiteSpace(configs.SqliteFile);

        if (postgres && sqlite)
            throw new InvalidOperationException("Both Postgres and SQLite are configured: set only one of them");

        return postgres ? DbMode.Postgres : sqlite ? DbMode.SqliteFile : DbMode.SqliteInMemory;
    }

    public static string Describe(DbConfigs configs) => ModeOf(configs) switch
    {
        DbMode.Postgres => "PostgreSQL",
        DbMode.SqliteFile => $"SQLite file {Path.GetFullPath(configs.SqliteFile!)}",
        _ => "SQLite in memory (data is lost on restart)"
    };

    protected override void Load(ContainerBuilder builder)
    {
        var options = new DbContextOptionsBuilder();
        switch (ModeOf(configs))
        {
            case DbMode.Postgres:
                options.UseNpgsql(configs.PostgresConnectionString);
                break;

            case DbMode.SqliteFile:
                options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = configs.SqliteFile }.ToString());
                break;

            default:
                // Общая in-memory база живёт, пока открыто хотя бы одно соединение с ней: держим одно
                // открытым всё время жизни контейнера. Имя случайное — у каждого процесса своя база.
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = $"tasker-{Guid.NewGuid():N}",
                    Mode = SqliteOpenMode.Memory,
                    Cache = SqliteCacheMode.Shared
                }.ToString();

                var keepAlive = new SqliteConnection(connectionString);
                keepAlive.Open();
                builder.RegisterInstance(keepAlive).As<IDisposable>();

                options.UseSqlite(connectionString);
                break;
        }

        // lower() в SQLite знает только ASCII, а заголовки задач сравниваются без учёта регистра по Unicode (порядок задач, task list --sort title).
        if (ModeOf(configs) != DbMode.Postgres)
            options.AddInterceptors(new SqliteUnicodeLower());

        builder.RegisterInstance(options.Options).As<DbContextOptions>();

        builder.RegisterType<AppDbContext>().InstancePerLifetimeScope();
        builder.RegisterType<DbInitializer>().As<IHostedService>().As<IStorageLifecycle>().SingleInstance();

        builder.RegisterType<ProjectStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<TaskStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<StatusStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<SeriesStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<WriteScope>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<StatusSetStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<TaskTypeStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<LinkTypeStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<FieldStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<FieldEnumStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<BoardStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<UserStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<EditLockStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<ProjectMemberStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<RefreshTokenStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
        builder.RegisterType<AgentTokenStorage>().AsImplementedInterfaces().InstancePerLifetimeScope();
    }
}

/// <summary>Подменяет lower() в каждом открытом соединении SQLite: Unicode, как <see cref="Tasker.Core.Tasks.TaskSortNames.Fold"/> (в Postgres lower() и так знает Unicode).</summary>
internal sealed class SqliteUnicodeLower : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
{
    public override void ConnectionOpened(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData) =>
        Register(connection);

    public override Task ConnectionOpenedAsync(
        System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        Register(connection);
        return Task.CompletedTask;
    }

    private static void Register(System.Data.Common.DbConnection connection)
    {
        if (connection is SqliteConnection sqlite)
            sqlite.CreateFunction("lower", (string? text) => text == null ? null : Tasker.Core.Tasks.TaskSortNames.Fold(text), isDeterministic: true);
    }
}
