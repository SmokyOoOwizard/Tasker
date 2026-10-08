using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Tasker.Configs;
using Tasker.Core;
using Tasker.Storage.Db.Configs.Tasker;
using Tasker.Storage.Files.Configs.Tasker;
using Tasker.Web.Auth;
using Tasker.Web.Configs.Tasker;

namespace Tasker.Web;

public static class TaskerWebApp
{
    /// <summary>
    /// Настройки — группы <see cref="AConfigs"/> из переменных окружения и аргументов командной строки
    /// Все группы регистрируются в Autofac как есть.
    /// Autofac: общие регистрации в модулях Core/Web, хост (Server/Desktop) добавляет свой модуль.
    /// </summary>
    /// <param name="configure">Хост может дописать значения по умолчанию после разбора.</param>
    /// <param name="log">Куда писать предупреждения о незнакомых настройках; по умолчанию — <see cref="Log.Logger"/>.</param>
    public static WebApplicationBuilder CreateBuilder<THostModule>(
        string[] args,
        TaskerMode mode,
        Action<AConfigs[]>? configure = null,
        ILogger? log = null
    )
        where THostModule : Module, new()
    {
        var configs = ConfigsParser.GetConfigs(args);
        configure?.Invoke(configs);

        // Опечатки в настройках (--sqllite, TASKER_DB_CONFIGS_SQLITE_FIL) иначе молча не действуют.
        foreach (var warning in ConfigDiagnostics.Check(args, configs))
            (log ?? Log.Logger).Warning("{Warning:l}", warning);

        var files = configs.Get<FilesConfigs>();
        var db = configs.Get<DbConfigs>();
        StorageRegistration.Validate(mode, files, db);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
        builder.Host.ConfigureContainer<ContainerBuilder>((_, x) =>
        {
            foreach (var config in configs)
                x.RegisterInstance(config).As(config.GetType());

            x.RegisterModule<CoreModule>();
            x.RegisterModule<WebModule>();
            // Десктоп: хранилище не общее, а своё у каждой открытой рабочей области (см. WorkspaceRegistry).
            if (mode == TaskerMode.Server)
                x.RegisterStorage(files, db);
            else
                x.RegisterWorkspaceStoragePlaceholders();
            x.RegisterTaskerMode(mode, configs.Get<AuthConfigs>());
            x.RegisterModule<THostModule>();
        });

        builder.Services.AddTaskerMode(mode);

        // Логирование ASP.NET Core идёт в Serilog (Log.Logger, настроенный хостом).
        builder.Services.AddSerilog();

        return builder;
    }
}
