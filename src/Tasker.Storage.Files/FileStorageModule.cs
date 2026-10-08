using Autofac;
using Autofac.Builder;
using Tasker.Core;
using Tasker.Core.Workspace;
using Tasker.Storage.Files.Index;
using Tasker.Storage.Files.Storages;

namespace Tasker.Storage.Files;

/// <summary>Хранение файлами в &lt;workspace&gt;/.tasker (для версионирования в git).</summary>
/// <param name="watchFiles">
/// Следить за папкой (<see cref="FileSystemWatcher"/>): нужно долгой работе — демону, десктопу. Короткой команде (консоль) слежение
/// не нужно, а запускать и останавливать его на macOS — десятки миллисекунд на каждый вызов: индекс сверяется с файлами при открытии
/// и после своих записей.
/// </param>
/// <param name="lazy">Сервисы регистрируются «по требованию» (<see cref="LazyRegistration"/>): консоли нужна пара сервисов.</param>
public class FileStorageModule(string workspacePath, bool watchFiles = true, bool lazy = false) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.Register(_ =>
        {
            var directory = new TaskerDirectory(workspacePath);
            directory.EnsureCreated();
            return directory;
        }).AsSelf().SingleInstance();

        // Один индекс и один синхронизатор на папку: все вкладки и окна с ней работают через них
        // (модуль регистрируется в контейнере рабочей области, см. Tasker.Web.Workspaces.WorkspaceRegistry).
        Single<WorkspaceIndex>(builder, x => x.AsSelf().As<IWorkspaceIndex>().SingleInstance());
        if (watchFiles)
            builder.RegisterType<WorkspaceWatcher>().As<IStorageLifecycle>().SingleInstance();
        else
            Single<IndexOnlyLifecycle>(builder, x => x.As<IStorageLifecycle>().SingleInstance());
        // Миграция файлов к текущему формату — только для папок: у БД файлов нет.
        Single<FileMigration>(builder, x => x.As<IFileMigration>().SingleInstance());

        Single<ProjectStorage>(builder, x => x.AsImplementedInterfaces().SingleInstance());
        Single<TaskStorage>(builder, x => x.AsImplementedInterfaces().SingleInstance());
        Single<StatusStorage>(builder, x => x.AsImplementedInterfaces().SingleInstance());
        Single<SeriesStorage>(builder, x => x.AsImplementedInterfaces().SingleInstance());
        Single<WriteScope>(builder, x => x.AsImplementedInterfaces().SingleInstance());
        Single<StatusSetStorage>(builder, x => x.AsImplementedInterfaces().SingleInstance());
        Single<TaskTypeStorage>(builder, x => x.AsImplementedInterfaces().SingleInstance());
        Single<LinkTypeStorage>(builder, x => x.AsImplementedInterfaces().SingleInstance());
        Single<FieldStorage>(builder, x => x.AsImplementedInterfaces().SingleInstance());
        Single<FieldEnumStorage>(builder, x => x.AsImplementedInterfaces().SingleInstance());
        Single<BoardStorage>(builder, x => x.AsImplementedInterfaces().SingleInstance());
        Single<UserStorage>(builder, x => x.AsImplementedInterfaces().SingleInstance());
        Single<EditLockStorage>(builder, x => x.AsImplementedInterfaces().SingleInstance());
        // Участников проектов в файлах нет: доступ по участию есть только на сервере, а он работает с БД.
    }

    private void Single<T>(ContainerBuilder builder, Action<IRegistrationBuilder<T, ConcreteReflectionActivatorData, SingleRegistrationStyle>> configure) where T : notnull
    {
        if (lazy)
            builder.RegisterTypeLazily(configure);
        else
            configure(builder.RegisterType<T>());
    }
}
