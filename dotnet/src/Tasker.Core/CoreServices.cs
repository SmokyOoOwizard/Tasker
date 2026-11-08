using Tasker.Core.Agents;
using Tasker.Core.Boards;
using Tasker.Core.Comments;
using Tasker.Core.Fields;
using Tasker.Core.Links;
using Tasker.Core.Locks;
using Tasker.Core.Projects;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;
using Tasker.Core.Users;

namespace Tasker.Core;

public sealed class CoreServices(
    StorageSet storage,
    ICurrentUser currentUser,
    IProjectAccess access,
    UserOptions options,
    ILocalEditor? localEditor = null,
    ILockNotifier? notifier = null,
    TimeProvider? time = null,
    LocalHuman? localHuman = null)
{
    private static readonly Dictionary<Type, Func<CoreServices, object?>> Services = new()
    {
        [typeof(IProjectStorage)] = x => x.Storage.Projects,
        [typeof(ITaskStorage)] = x => x.Storage.Tasks,
        [typeof(IStatusStorage)] = x => x.Storage.Statuses,
        [typeof(IStatusSetStorage)] = x => x.Storage.StatusSets,
        [typeof(ITaskTypeStorage)] = x => x.Storage.TaskTypes,
        [typeof(ISeriesStorage)] = x => x.Storage.Series,
        [typeof(IWriteScope)] = x => x.Storage.Writes,
        [typeof(ILinkTypeStorage)] = x => x.Storage.LinkTypes,
        [typeof(IFieldStorage)] = x => x.Storage.Fields,
        [typeof(IFieldEnumStorage)] = x => x.Storage.FieldEnums,
        [typeof(IBoardStorage)] = x => x.Storage.Boards,
        [typeof(IUserStorage)] = x => x.Storage.Users,
        [typeof(IEditLockStorage)] = x => x.Storage.EditLocks,
        [typeof(ITaskCommentStorage)] = x => x.Storage.Comments,
        [typeof(IAgentTokenStorage)] = x => x.Storage.AgentTokens,
        [typeof(ICurrentUser)] = x => x.CurrentUser,
        [typeof(IProjectAccess)] = x => x.Access,
        [typeof(UserOptions)] = x => x.Options,
        [typeof(TimeProvider)] = x => x.Time,
        [typeof(IEditorIdentity)] = x => x.EditorIdentity,
        [typeof(EditLockService)] = x => x.EditLocks,
        [typeof(EntityLockService)] = x => x.EntityLocks,
        [typeof(UserService)] = x => x.Users,
        [typeof(AgentService)] = x => x.Agents,
        [typeof(ProjectService)] = x => x.Projects,
        [typeof(StatusService)] = x => x.Statuses,
        [typeof(StatusSetService)] = x => x.StatusSets,
        [typeof(TaskTypeService)] = x => x.TaskTypes,
        [typeof(TaskService)] = x => x.Tasks,
        [typeof(BoardService)] = x => x.Boards,
        [typeof(LinkTypeService)] = x => x.LinkTypes,
        [typeof(TaskLinkService)] = x => x.TaskLinks,
        [typeof(TaskCommentService)] = x => x.TaskComments,
        [typeof(LinkHealthService)] = x => x.LinkHealth,
        [typeof(FieldService)] = x => x.Fields,
        [typeof(FieldEnumService)] = x => x.FieldEnums,
        [typeof(SeriesService)] = x => x.Series,
        [typeof(SeriesHealthService)] = x => x.SeriesHealth,
        [typeof(CleanupService)] = x => x.Cleanup,
        [typeof(SeriesConflictService)] = x => x.SeriesConflicts,
    };

    private readonly Dictionary<Type, object> _created = [];

    public StorageSet Storage => storage;
    public ICurrentUser CurrentUser => currentUser;
    public IProjectAccess Access => access;
    public UserOptions Options => options;
    public TimeProvider Time { get; } = time ?? TimeProvider.System;

    public IEditorIdentity EditorIdentity => One<IEditorIdentity>(() => new CurrentUserEditor(currentUser, storage.Users, localEditor));
    public EditLockService EditLocks => One(() => new EditLockService(storage.EditLocks, EditorIdentity, Time, notifier));

    public EntityLockService EntityLocks => One(() => new EntityLockService(EditLocks, EditorIdentity, storage.Projects, storage.Tasks,
        storage.TaskTypes, storage.Statuses, storage.StatusSets, storage.Boards, storage.Series, storage.Users, storage.LinkTypes,
        storage.Fields, storage.FieldEnums));

    public UserService Users => One(() => new UserService(storage.Users, currentUser, options, Time, EditLocks));
    public AgentService Agents => One(() => new AgentService(Users, storage.Users, options, Time, EditLocks, storage.AgentTokens));

    public ProjectService Projects => One(() => new ProjectService(storage.Projects, access, Time, EditLocks, storage.Tasks, storage.Boards,
        storage.Statuses, storage.StatusSets, storage.TaskTypes, storage.Series, storage.LinkTypes));

    public StatusService Statuses => One(() => new StatusService(storage.Statuses, storage.StatusSets, storage.Boards, storage.Tasks, EditLocks, storage.Series));

    public StatusSetService StatusSets => One(() => new StatusSetService(storage.StatusSets, storage.Statuses, storage.TaskTypes, storage.Boards,
        storage.Tasks, EditLocks, storage.Series));

    public TaskTypeService TaskTypes => One(() => new TaskTypeService(storage.TaskTypes, storage.StatusSets, storage.Tasks, storage.Fields,
        storage.Writes, Time, EditLocks, storage.Series));

    public TaskService Tasks => One(() => new TaskService(storage.Tasks, storage.TaskTypes, storage.StatusSets, Time, storage.Series,
        storage.Writes, EditLocks, storage.Fields, storage.FieldEnums, TaskLinks, storage.Statuses, storage.Comments));

    public BoardService Boards => One(() => new BoardService(storage.Boards, storage.StatusSets, storage.Statuses, storage.TaskTypes, storage.Tasks, storage.Fields,
        Tasks, storage.Writes, EditLocks));

    public LinkTypeService LinkTypes => One(() => new LinkTypeService(storage.LinkTypes, storage.Tasks, EditLocks));

    public TaskLinkService TaskLinks => One(() => new TaskLinkService(storage.Tasks, LinkTypes, Time, EditLocks, storage.Series, storage.Writes));

    public TaskCommentService TaskComments => One(() => new TaskCommentService(storage.Comments, storage.Tasks, storage.Users, currentUser, Time, localHuman));

    public LinkHealthService LinkHealth => One(() => new LinkHealthService(storage.Tasks, storage.LinkTypes));

    public FieldService Fields => One(() => new FieldService(storage.Fields, storage.FieldEnums, storage.TaskTypes, storage.Tasks,
        storage.Writes, Time, EditLocks, storage.Series));

    public FieldEnumService FieldEnums => One(() => new FieldEnumService(storage.FieldEnums, storage.Fields, storage.TaskTypes, storage.Tasks,
        storage.Writes, Time, EditLocks));

    public SeriesService Series => One(() => new SeriesService(storage.Tasks, storage.Series, storage.Writes, Time, EditLocks));
    public SeriesHealthService SeriesHealth => One(() => new SeriesHealthService(storage.Tasks, storage.Series));

    public SeriesConflictService SeriesConflicts => One(() => new SeriesConflictService(storage.Tasks, storage.Series, storage.Writes, EditLocks, Time));

    public CleanupService Cleanup => One(() => new CleanupService(storage.Tasks, Tasks, storage.Series, LinkTypes, storage.Writes, EditLocks, Time, storage.Comments));

    public T Get<T>() where T : class =>
        Find<T>() ?? throw new InvalidOperationException($"{typeof(T).Name} is not available in this workspace");

    public T? Find<T>() where T : class => Services.TryGetValue(typeof(T), out var get) ? (T?)get(this) : null;

    private T One<T>(Func<T> create) where T : class
    {
        if (_created.TryGetValue(typeof(T), out var existing))
            return (T)existing;

        var created = create();
        _created[typeof(T)] = created;
        return created;
    }
}
