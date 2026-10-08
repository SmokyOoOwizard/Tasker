using Autofac;
using Autofac.Builder;
using Tasker.Core;
using Serilog;
using Tasker.Storage.Db;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Storage.Files;
using Tasker.Storage.Files.Configs.Tasker;

namespace Tasker.Web;

internal enum StorageKind
{
    Files,
    Database
}

internal static class StorageRegistration
{
    /// <summary>
    /// Задана папка — файлы; иначе БД (Postgres, файл SQLite или SQLite в памяти — см. <see cref="DbStorageModule"/>).
    /// Задано и то и другое — ошибка при старте, а не молчаливый выбор одного из них.
    /// </summary>
    public static StorageKind Resolve(FilesConfigs files, DbConfigs db)
    {
        if (files.IsSet && db.IsSet)
            throw new InvalidOperationException("Both file storage and a database are configured: set only one of them");

        return files.IsSet ? StorageKind.Files : StorageKind.Database;
    }

    /// <summary>
    /// Сервер: только БД — пароли, участники проектов и сессии хранятся только там; хэши паролей в git-репозитории не нужны.
    /// Десктоп: папки и файлы SQLite открываются вкладками (<c>--files</c> и <c>--sqlite</c> — вкладки при запуске,
    /// можно оба сразу); Postgres — только на сервере.
    /// </summary>
    public static void Validate(TaskerMode mode, FilesConfigs files, DbConfigs db)
    {
        if (mode == TaskerMode.Server)
        {
            if (Resolve(files, db) == StorageKind.Files)
                throw new InvalidOperationException("Tasker.Server requires a database: file storage is available only in Tasker.Desktop");
            return;
        }

        if (!string.IsNullOrWhiteSpace(db.PostgresConnectionString))
            throw new InvalidOperationException("PostgreSQL is available only in Tasker.Server: Tasker.Desktop works with folders (--files) and SQLite files (--sqlite)");
    }

    // Остальной код видит только интерфейсы хранилищ — какая реализация за ними, решается здесь.
    public static void RegisterStorage(this ContainerBuilder builder, FilesConfigs files, DbConfigs db)
    {
        if (Resolve(files, db) == StorageKind.Files)
        {
            Log.Information("Storage: files in {Path}", Path.GetFullPath(files.Path!));
            builder.RegisterModule(new FileStorageModule(files.Path!));
            return;
        }

        var description = DbStorageModule.Describe(db);
        if (DbStorageModule.ModeOf(db) == DbMode.SqliteInMemory)
            Log.Warning("Storage: {Storage}", description);
        else
            Log.Information("Storage: {Storage}", description);

        builder.RegisterModule(new DbStorageModule(db));
    }

    /// <summary>
    /// Десктоп: хранилище есть только в контейнере рабочей области, но эндпоинты и инструменты MCP
    /// получают интерфейсы хранилищ параметрами, и при старте ASP.NET и SDK MCP спрашивают у корневого
    /// контейнера, сервис ли это. Регистрируем в корне заглушки: сервис есть, но разрешить его вне области
    /// нельзя; в контейнере области их перекрывают настоящие регистрации.
    /// </summary>
    public static void RegisterWorkspaceStoragePlaceholders(this ContainerBuilder builder)
    {
        var coreAssembly = typeof(CoreModule).Assembly;
        var interfaces = typeof(FileStorageModule).Assembly.GetTypes()
            .Where(x => x is { IsClass: true, IsAbstract: false })
            .SelectMany(x => x.GetInterfaces())
            // Только хранилища: необязательные сервисы (индекс папки) берут через GetService — заглушка их бы сломала.
            .Where(x => x.Assembly == coreAssembly && x.Name.EndsWith("Storage", StringComparison.Ordinal))
            .Distinct();

        foreach (var type in interfaces)
        {
            builder.RegisterComponent(RegistrationBuilder
                .ForDelegate(type, (_, _) => throw new InvalidOperationException(
                    $"{type.Name} is available only inside a workspace (/w/{{folder}}/…)"))
                .As(type)
                .CreateRegistration());
        }
    }
}
