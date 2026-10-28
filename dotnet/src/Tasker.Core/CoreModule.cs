using Autofac;
using Autofac.Builder;
using Serilog;
using Tasker.Core.Boards;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.Users;

namespace Tasker.Core;

/// <summary>
/// Регистрации бизнес-логики, общей для сервера и десктопа.
/// Что зависит от режима — <see cref="Users.UserOptions"/>, <see cref="Users.ICurrentUser"/>,
/// <see cref="IProjectAccess"/>, <see cref="ProjectMemberService"/> — регистрирует хост (Tasker.Web).
/// </summary>
/// <param name="lazy">Сервисы регистрируются «по требованию» (<see cref="LazyRegistration"/>): консоли, где процесс короткий и нужна пара сервисов.</param>
public class CoreModule(bool lazy) : Module
{
    public CoreModule() : this(false)
    {
    }

    protected override void Load(ContainerBuilder builder)
    {
        // Log.Logger настраивает хост (Tasker.Server / Tasker.Desktop) в Program.
        builder.Register<ILogger>(_ => Log.Logger);
        builder.RegisterInstance(TimeProvider.System);

        // Scoped: хранилища БД живут в скоупе запроса вместе с DbContext.
        Scoped<Locks.EditLockService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<Locks.EntityLockService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<Locks.CurrentUserEditor>(builder, x => x.As<Locks.IEditorIdentity>().InstancePerLifetimeScope());
        Scoped<UserService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<Agents.AgentService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<ProjectService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<StatusService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<StatusSetService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<TaskTypeService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<TaskService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<BoardService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<Links.LinkTypeService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<Links.TaskLinkService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<Links.LinkHealthService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<Fields.FieldService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<Fields.FieldEnumService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<TaskSeries.SeriesService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<TaskSeries.SeriesHealthService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
        Scoped<TaskSeries.CleanupService>(builder, x => x.AsSelf().InstancePerLifetimeScope());
    }

    private void Scoped<T>(ContainerBuilder builder, Action<IRegistrationBuilder<T, ConcreteReflectionActivatorData, SingleRegistrationStyle>> configure) where T : notnull
    {
        if (lazy)
            builder.RegisterTypeLazily(configure);
        else
            configure(builder.RegisterType<T>());
    }
}
