using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;

namespace Tasker.Core.Links;

/// <summary>Общее для связей: правка списка связей задач и каскад при удалении задачи.</summary>
internal static class TaskLinks
{
    private const int Attempts = 3;

    /// <summary>
    /// Убирает у других задач связи, которые указывали на удалённую задачу. Недействительные связи в любом случае
    /// пропускаются при показе, так что сбой здесь не опасен: оставшиеся уберёт <c>cleanup</c>.
    /// </summary>
    public static async Task RemoveInbound(ITaskStorage tasks, TimeProvider time, Guid projectId, Guid targetId, CancellationToken ct)
    {
        foreach (var source in await tasks.GetLinkedTo(projectId, targetId, ct))
        {
            await TaskRewrites.Modify(tasks, time, source,
                t => t.Links.Any(x => x.TargetId == targetId)
                    ? t with { Links = t.Links.Where(x => x.TargetId != targetId).ToArray() }
                    : null,
                Attempts, ct);
        }
    }
}

/// <summary>
/// Связи между задачами одного проекта: «A блокирует B», «A дублирует B», «A связана с B» (типы — <see cref="LinkType"/>).
/// Связь хранится в задаче-источнике, поэтому создание и удаление связи — изменение этой задачи (её версия и время меняются,
/// на неё распространяется блокировка на время правки); у задачи-цели ничего не меняется.
/// <para>
/// Добавление и удаление коммутативны (два человека добавили разные связи — обе нужны), поэтому версия задачи необязательна:
/// без неё операция берёт актуальную задачу и при гонке повторяется. С версией — как везде: при расхождении <c>[modified]</c>.
/// </para>
/// </summary>
public class TaskLinkService(
    ITaskStorage tasks, LinkTypeService types, TimeProvider time, Locks.EditLockService locks, ISeriesStorage series, IWriteScope writes)
{
    // Связи коммутативны, и версию клиент не передаёт: гонку надо пережить, поэтому попыток много (с паузой между ними, см. TaskRewrites.Modify).
    private const int Attempts = 25;

    // Очередь изменений связей по задаче-источнику внутри процесса (полосы по хэшу id: задач много, очередей достаточно).
    private static readonly SemaphoreSlim[] Stripes = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>Сколько связей показывает представление задачи (<c>get_task</c>, REST, <c>task get</c>); остальные — через <c>get_task_links</c>.</summary>
    public const int MaxViewed = 100;

    /// <summary>
    /// Добавляет связь «задача <paramref name="taskId"/> —тип→ <paramref name="targetId"/>». Такая связь уже есть — возвращает задачу как есть;
    /// у симметричного типа («relates to») обратная связь с той же парой тоже считается имеющейся.
    /// </summary>
    /// <param name="version">Версия задачи-источника, которую видел клиент; null или пусто — без проверки.</param>
    /// <returns>Задача-источник; null — её нет.</returns>
    /// <exception cref="TaskerValidationException">
    /// Нет такого типа или целевой задачи в проекте; задача связывается сама с собой; связь замкнула бы цикл у типа, который циклов не допускает
    /// (<see cref="LinkType.AllowCycles"/>): сообщение называет путь цикла.
    /// </exception>
    public async Task<TaskItem?> Add(Guid projectId, Guid taskId, Guid typeId, Guid targetId, string? version, CancellationToken ct = default)
    {
        var task = await tasks.GetById(projectId, taskId, ct);
        if (task == null)
            return null;

        // Связь — запись: типы по умолчанию должны быть сохранены (в БД на них внешний ключ), чтение их только показывает.
        await types.EnsureDefaults(projectId, ct);
        var type = await types.GetById(projectId, typeId, ct)
            ?? throw new TaskerValidationException($"TypeId: link type not found in the project: {typeId}");
        if (targetId == taskId)
            throw new TaskerValidationException("TargetId: a task cannot be linked to itself");
        var target = await tasks.GetById(projectId, targetId, ct)
            ?? throw new TaskerValidationException($"TargetId: task not found in the project: {targetId}");

        await locks.EnsureWritable(Locks.LockedEntity.Task, task.Id, Subject(task), ct);

        var link = new TaskLink(typeId, targetId);
        // Симметричная связь уже есть с другой стороны («B relates to A») — второй раз не нужна.
        if (type.IsSymmetric && target.Links.Contains(new TaskLink(typeId, taskId)))
            return task;

        Func<TaskItem, TaskItem?> add = t => t.Links.Contains(link) ? null : t with { Links = [.. t.Links, link] };
        if (type.AllowCycles || type.IsSymmetric || task.Links.Contains(link))
            return await Change(task, version, add, ct);

        // Тип без циклов: проверка и запись — одна секция записи проекта, иначе две встречные связи («A blocks B» и «B blocks A»), каждая из
        // которых по отдельности безвредна, обе прошли бы проверку до записи другой. Внутри секции файлы сверены с диском (git pull), БД — транзакция.
        return await writes.Exclusive(projectId, async () =>
        {
            await EnsureNoCycle(projectId, type, taskId, target, ct);
            return await Change(task, version, add, ct);
        }, ct);
    }

    /// <summary>Id иерархических типов проекта (<see cref="LinkType.Hierarchical"/>); пусто — иерархии нет. Типы по умолчанию читаются без записи.</summary>
    public async Task<Guid[]> HierarchicalTypeIds(Guid projectId, CancellationToken ct = default) =>
        (await types.GetAll(projectId, ct)).Where(x => x.Hierarchical && !x.IsSymmetric).Select(x => x.Id).ToArray();

    /// <summary>Связь «источник → цель» замкнёт цикл, если от цели по связям этого типа можно дойти до источника.</summary>
    private async Task EnsureNoCycle(Guid projectId, LinkType type, Guid sourceId, TaskItem target, CancellationToken ct)
    {
        // Иерархические типы — один граф эпиков: цикл может пройти по связям разных иерархических типов.
        var typeIds = type.Hierarchical ? await HierarchicalTypeIds(projectId, ct) : [type.Id];
        var path = await LinkCycles.FindPath(tasks, projectId, typeIds, target.Id, sourceId, ct);
        if (path == null)
            return;

        // Источник — первая задача пути цикла: «A → B → … → A».
        var prefixes = (await series.GetAll(projectId, ct)).ToDictionary(x => x.Id, x => x.Prefix);
        var cycle = new List<Guid> { sourceId };
        cycle.AddRange(path);
        var names = new List<string>();
        foreach (var id in cycle)
        {
            var task = id == target.Id ? target : await tasks.GetById(projectId, id, ct);
            names.Add(task == null ? ShortId.Of(id) : LinkCycle.Reference(id, task.SeriesNumbers, prefixes));
        }

        throw new TaskerValidationException(
            $"Cycle: {string.Join(" → ", names)} (link type '{type.Name}' does not allow cycles; remove the opposite link or allow cycles for the type)");
    }

    /// <summary>
    /// Убирает связь «задача —тип→ цель». Нет такой связи — возвращает задачу как есть; у симметричного типа убирается и обратная.
    /// </summary>
    /// <returns>Задача-источник; null — её нет.</returns>
    public async Task<TaskItem?> Remove(Guid projectId, Guid taskId, Guid typeId, Guid targetId, string? version, CancellationToken ct = default)
    {
        var task = await tasks.GetById(projectId, taskId, ct);
        if (task == null)
            return null;
        await locks.EnsureWritable(Locks.LockedEntity.Task, task.Id, Subject(task), ct);

        var link = new TaskLink(typeId, targetId);
        var result = await Change(task, version, t => t.Links.Contains(link) ? t with { Links = t.Links.Where(x => x != link).ToArray() } : null, ct);

        // Симметричную связь могли создать с другой стороны: «обе задачи связаны» — убираем её тоже.
        if (result != null && await types.GetById(projectId, typeId, ct) is { IsSymmetric: true } && await tasks.GetById(projectId, targetId, ct) is { } other)
        {
            var reverse = new TaskLink(typeId, taskId);
            if (other.Links.Contains(reverse))
            {
                await locks.EnsureWritable(Locks.LockedEntity.Task, other.Id, Subject(other), ct);
                await TaskRewrites.Modify(tasks, time, other, t => t.Links.Contains(reverse) ? t with { Links = t.Links.Where(x => x != reverse).ToArray() } : null, Attempts, ct);
            }
        }

        return result;
    }

    /// <summary>
    /// Все связи задачи — исходящие и входящие — с названиями именно с её стороны («blocks» / «is blocked by»).
    /// Связи, у которых нет типа или другой задачи (удалены в другой ветке), пропускаются. Порядок: тип, исходящие раньше входящих, заголовок.
    /// </summary>
    /// <returns>null — задачи нет.</returns>
    public async Task<TaskLinkView[]?> GetLinks(Guid projectId, Guid taskId, CancellationToken ct = default)
    {
        return await tasks.GetById(projectId, taskId, ct) is { } task ? await GetLinks(projectId, task, ct) : null;
    }

    /// <summary>Как <see cref="GetLinks(Guid, Guid, CancellationToken)"/>, для уже прочитанной задачи (читает только типы и другие задачи).</summary>
    public async Task<TaskLinkView[]> GetLinks(Guid projectId, TaskItem task, CancellationToken ct = default)
    {
        var taskId = task.Id;
        var known = (await types.GetAll(projectId, ct)).ToDictionary(x => x.Id);
        var views = new List<TaskLinkView>();

        foreach (var link in task.Links)
        {
            if (known.TryGetValue(link.TypeId, out var type) && await tasks.GetById(projectId, link.TargetId, ct) is { } other)
                views.Add(new TaskLinkView(type.Id, type.Name, LinkDirection.Outward, type.OutwardName, Linked(other)));
        }

        foreach (var source in await tasks.GetLinkedTo(projectId, taskId, ct))
        {
            foreach (var link in source.Links.Where(x => x.TargetId == taskId))
            {
                if (!known.TryGetValue(link.TypeId, out var type))
                    continue;
                // У симметричной связи, созданной с обеих сторон, обе записи выглядят одинаково — показываем одну.
                if (type.IsSymmetric && views.Any(x => x.TypeId == type.Id && x.Task.Id == source.Id))
                    continue;
                views.Add(new TaskLinkView(type.Id, type.Name, LinkDirection.Inward, type.InwardName, Linked(source)));
            }
        }

        return views
            .OrderBy(x => x.TypeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Direction)
            .ThenBy(x => x.Task.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Task.Id)
            .ToArray();
    }

    private async Task<TaskItem?> Change(TaskItem task, string? version, Func<TaskItem, TaskItem?> change, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            // Параллельные связи от одной задачи в одном процессе (демон: десять агентов) ставим в очередь по задаче-источнику:
            // иначе каждый круг записи выигрывает один из десяти, остальные перечитывают задачу и снова проигрывают — кому-то не везёт
            // много кругов подряд. Очередь берётся вне секции записи и не вложена в другие: взаимной блокировки с каскадами нет.
            // Процессы (консоль) между собой разводит версия с повтором и паузой.
            var gate = Stripes[(task.Id.GetHashCode() & int.MaxValue) % Stripes.Length];
            await gate.WaitAsync(ct);
            try
            {
                var current = await tasks.GetById(task.ProjectId, task.Id, ct);
                return current == null ? null : await TaskRewrites.Modify(tasks, time, current, change, Attempts, ct);
            }
            finally
            {
                gate.Release();
            }
        }

        var expected = Versioning.Check(task.Version, version, Subject(task));
        if (change(task) is not { } changed)
            return task;

        var updated = changed with { UpdatedAt = time.GetUtcNow() };
        var newVersion = await tasks.Update(updated, expected, ct) ?? throw Versioning.Modified(Subject(task));
        return updated with { Version = newVersion };
    }

    private static LinkedTask Linked(TaskItem task) => new(task.Id, task.Title, task.StatusId, task.SeriesNumbers);

    private static string Subject(TaskItem task) => $"Task '{task.Title}'";
}
