using Tasker.Core.Users;

namespace Tasker.Core.Locks;

/// <summary>От чьего имени выполняется запрос — для блокировок.</summary>
public interface IEditorIdentity
{
    Task<EditHolder> Current(CancellationToken ct = default);
}

/// <summary>
/// Человек за десктопом или консолью, где нет входа: у каждого клиента свой ключ (интерфейс — <c>local</c>,
/// консоль — <c>cli</c>), поэтому блокировка интерфейса останавливает и команды консоли. Имя — из настроек.
/// Регистрирует хост; без регистрации — <see cref="CurrentUserEditor.LocalKey"/> и «Local user».
/// </summary>
public interface ILocalEditor
{
    string Key { get; }

    /// <summary>Как показывать другим; может меняться, пока приложение работает (настройки правят отдельно).</summary>
    string Name { get; }
}

/// <summary>
/// Пользователь запроса; без пользователя (REST десктопа, консоль) — <see cref="ILocalEditor"/>:
/// на десктопе правит один человек, имя которому задают в настройках.
/// </summary>
public class CurrentUserEditor(ICurrentUser currentUser, IUserStorage users, ILocalEditor? local = null) : IEditorIdentity
{
    public const string LocalKey = "local";
    public const string LocalName = "Local user";

    public async Task<EditHolder> Current(CancellationToken ct = default)
    {
        if (currentUser.Id is not { } id)
            return new EditHolder(local?.Key ?? LocalKey, local?.Name ?? LocalName);

        var user = await users.GetById(id, ct);
        return new EditHolder($"user:{id}", user?.Username ?? id.ToString());
    }
}

/// <summary>
/// Сообщает клиентам (вкладкам интерфейса), что блокировка сущности появилась или снята, — чтобы они показали
/// «правит Иван» без опроса. Регистрирует хост, у которого есть кому слушать; без регистрации сообщать некому.
/// </summary>
public interface ILockNotifier
{
    void Changed(LockedEntity entity, Guid id);
}

/// <summary>
/// Блокировка на время правки: «запись занята, её редактирует Иван».
/// <para>
/// Дополняет оптимистическую проверку версии (<see cref="Versioning"/>), а не заменяет её: версия остаётся
/// последней защитой при записи, блокировка заранее говорит другим, что запись правят. Редактор (UI) берёт блокировку
/// при открытии формы, продлевает пока форма открыта и снимает при закрытии. Забытая блокировка истекает сама
/// через <see cref="Duration"/>.
/// </para>
/// <para>
/// Запись жёсткая в одном смысле: пока блокировку держит другой, изменить и удалить сущность нельзя ни через UI,
/// ни через MCP, ни через CLI. Тем, кто правит одним вызовом (агент, команда), блокировка не нужна: свободная
/// сущность пишется как есть (<see cref="EnsureWritable"/>), а гонку между проверкой и записью закрывает версия.
/// </para>
/// </summary>
public class EditLockService(IEditLockStorage storage, IEditorIdentity identity, TimeProvider time, ILockNotifier? notifier = null)
{
    /// <summary>На сколько выдаётся и продлевается блокировка.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Сколько массовая правка задач (каскады: убрать значения у всех задач типа, переназначить значение перечисления, убрать серию,
    /// чистка) ждёт, пока другой отпустит блокировку затрагиваемой задачи. Блокировку продлевает открытая форма, так что ждать
    /// «до снятия» можно бесконечно, — ожидание ограничено; по истечении — обычная ошибка <see cref="TaskerLockedException"/>.
    /// </summary>
    public static readonly TimeSpan CascadeWait = TimeSpan.FromSeconds(30);

    /// <summary>Как часто при ожидании проверяется, снята ли блокировка.</summary>
    public static readonly TimeSpan CascadePoll = TimeSpan.FromMilliseconds(200);

    /// <summary>Ожидание массовой правки (<see cref="CascadeWait"/> по умолчанию); тесты задают своё.</summary>
    public TimeSpan CascadeTimeout { get; init; } = CascadeWait;

    /// <summary>Берёт блокировку или продлевает свою (повторный вызов — «пульс» редактора).</summary>
    /// <param name="subject">Как назвать сущность в ошибке: <c>Task 'Fix login'</c>.</param>
    /// <exception cref="TaskerLockedException">Блокировку держит другой.</exception>
    /// <param name="projectId">Проект сущности: блокировка запоминает его для <see cref="GetByProject"/>.</param>
    public async Task<EditLock> Acquire(LockedEntity entity, Guid id, string subject, Guid? projectId = null, CancellationToken ct = default)
    {
        var holder = await identity.Current(ct);
        var now = time.GetUtcNow();
        var held = await storage.Acquire(entity, id, holder, now, now + Duration, projectId, ct);
        if (held.Holder.Key != holder.Key)
            throw new TaskerLockedException(subject, held);

        // Продление («пульс» редактора) не событие: блокировка и так видна. Событие — когда она появилась.
        if (held.AcquiredAt == now)
            notifier?.Changed(entity, id);
        return held;
    }

    /// <summary>Снимает свою блокировку. Нет блокировки или она чужая — ничего не делает.</summary>
    /// <returns>true — снята.</returns>
    public async Task<bool> Release(LockedEntity entity, Guid id, CancellationToken ct = default)
    {
        var released = await storage.Release(entity, id, (await identity.Current(ct)).Key, ct);
        if (released)
            notifier?.Changed(entity, id);
        return released;
    }

    /// <returns>null — не заблокирована.</returns>
    public Task<EditLock?> Get(LockedEntity entity, Guid id, CancellationToken ct = default) =>
        storage.Get(entity, id, time.GetUtcNow(), ct);

    /// <summary>Действующие блокировки проекта: у кого что занято.</summary>
    public Task<EditLock[]> GetByProject(Guid projectId, CancellationToken ct = default) =>
        storage.GetByProject(projectId, time.GetUtcNow(), ct);

    /// <summary>
    /// Вызывается перед изменением и удалением. Свободна или своя — можно писать; чужая — отказ.
    /// </summary>
    /// <exception cref="TaskerLockedException">Сущность правит другой держатель.</exception>
    public async Task EnsureWritable(LockedEntity entity, Guid id, string subject, CancellationToken ct = default)
    {
        if (await Get(entity, id, ct) is not { } held)
            return;
        if (held.Holder.Key != (await identity.Current(ct)).Key)
            throw new TaskerLockedException(subject, held);
    }

    /// <summary>
    /// Для массовых правок: ждёт, пока другие отпустят блокировки всех перечисленных сущностей (своя блокировка не мешает).
    /// Ничего не пишет: вызывается до первой записи, поэтому отказ не оставляет правку наполовину. Опрос — через <see cref="TimeProvider"/>.
    /// <para>
    /// Ждать нужно <b>снаружи</b> атомарной секции записи (<c>IWriteScope</c>): в БД она — транзакция, и пока она открыта, держатель блокировки
    /// не смог бы её снять или продлить (SQLite пускает одного писателя). Внутри секции проверяют без ожидания (<paramref name="timeout"/> — ноль).
    /// </para>
    /// </summary>
    /// <exception cref="TaskerLockedException">Через <see cref="CascadeTimeout"/> блокировка всё ещё держится; в сообщении — все занятые сущности.</exception>
    public async Task WaitUntilWritable(
        IReadOnlyCollection<(LockedEntity Entity, Guid Id, string Subject)> items, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (items.Count == 0)
            return;
        var deadline = time.GetUtcNow() + (timeout ?? CascadeTimeout);
        while (true)
        {
            var me = (await identity.Current(ct)).Key;
            EditLock? first = null;
            var busy = new List<string>();
            foreach (var (entity, id, subject) in items)
            {
                if (await Get(entity, id, ct) is { } held && held.Holder.Key != me)
                {
                    first ??= held;
                    busy.Add(subject);
                }
            }
            if (first == null)
                return;

            var left = deadline - time.GetUtcNow();
            if (left <= TimeSpan.Zero)
                throw new TaskerLockedException(string.Join(", ", busy), first);
            await Task.Delay(left < CascadePoll ? left : CascadePoll, time, ct);
        }
    }

    /// <summary>Сущность удалена — её блокировка больше не нужна.</summary>
    public Task Forget(LockedEntity entity, Guid id, CancellationToken ct = default) =>
        storage.Remove(entity, id, ct);
}
