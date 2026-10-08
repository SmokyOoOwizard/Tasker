using Tasker.Core;
using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Core.Links;
using Tasker.Core.Locks;
using Tasker.Core.Statuses;
using Tasker.Core.TaskSeries;
using Tasker.Core.Tasks;

namespace Tasker.Tests.SeriesCore;

/// <summary>Часы, которыми управляет тест.</summary>
public sealed class SeriesTestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// Секция записи в памяти: замок на проект, вложенный вызов из того же потока выполнения не блокируется.
/// Запоминает число входов, вложенность и одновременные секции — чтобы тесты проверяли атомарность.
/// </summary>
public sealed class FakeWriteScope : IWriteScope
{
    private readonly Dictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly AsyncLocal<int> _depth = new();
    private int _active;

    public int Calls;
    public int NestedCalls;
    public int MaxConcurrent;

    public bool IsInside => _depth.Value > 0;

    public async Task<T> Exclusive<T>(Guid projectId, Func<Task<T>> action, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        if (_depth.Value > 0)
        {
            Interlocked.Increment(ref NestedCalls);
            return await action();
        }

        SemaphoreSlim gate;
        lock (_locks)
        {
            if (!_locks.TryGetValue(projectId, out gate!))
                _locks[projectId] = gate = new SemaphoreSlim(1, 1);
        }

        await gate.WaitAsync(ct);
        try
        {
            var active = Interlocked.Increment(ref _active);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, active);
            _depth.Value = 1;
            try
            {
                return await action();
            }
            finally
            {
                _depth.Value = 0;
                Interlocked.Decrement(ref _active);
            }
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed class InMemoryTaskStorage : ITaskStorage
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, TaskItem> _items = new();

    /// <summary>Если задан — вызовы записи и <see cref="GetMaxNumber"/> вне секции записи считаются в <see cref="OutsideScope"/>.</summary>
    public FakeWriteScope? Scope { get; set; }

    public int OutsideScope;
    public int AddCount;
    public int UpdateCount;
    public int DeleteCount;

    /// <summary>Вызывается перед сравнением версии в <see cref="Update"/>: так тест имитирует чужую запись.</summary>
    public Action<TaskItem>? BeforeUpdate { get; set; }

    private Dictionary<Guid, TaskItem>? _frozenIndex;

    /// <summary>Отстающий индекс: фильтр в <see cref="GetRange"/> считается по задачам на момент включения, обновлённые задачи из выборки не выпадают.</summary>
    public bool LaggingIndex
    {
        get => _frozenIndex != null;
        set
        {
            lock (_sync)
                _frozenIndex = value ? new Dictionary<Guid, TaskItem>(_items) : null;
        }
    }

    public int WriteCount => AddCount + UpdateCount + DeleteCount;

    private void Track()
    {
        if (Scope is { IsInside: false })
            Interlocked.Increment(ref OutsideScope);
    }

    public TaskItem[] All()
    {
        lock (_sync)
            return _items.Values.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArray();
    }

    /// <summary>Записать в обход счётчиков и версий (заготовка данных теста).</summary>
    public TaskItem Seed(TaskItem task)
    {
        var stored = task with { Version = Guid.NewGuid().ToString("N") };
        lock (_sync)
            _items[stored.Id] = stored;
        return stored;
    }

    /// <summary>Изменить задачу «чужим процессом»: новая версия.</summary>
    public TaskItem Touch(Guid id, Func<TaskItem, TaskItem> change)
    {
        lock (_sync)
        {
            var updated = change(_items[id]) with { Version = Guid.NewGuid().ToString("N") };
            _items[id] = updated;
            return updated;
        }
    }

    public Task<TaskItem?> GetById(Guid projectId, Guid id, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.TryGetValue(id, out var t) && t.ProjectId == projectId ? t : null);
    }

    public Task<ListDto<TaskItem>> GetRange(Guid projectId, TaskFilter? filter, Page page, CancellationToken ct = default)
    {
        TaskItem[] matched;
        lock (_sync)
            matched = _items.Values
                .Where(x => x.ProjectId == projectId
                    && (filter == null || filter.Matches(_frozenIndex != null && _frozenIndex.TryGetValue(x.Id, out var old) ? old : x)))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArray();
        return Task.FromResult(page.Apply(matched));
    }

    public Task<TaskItem[]> GetAll(Guid projectId, TaskFilter filter, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values
                .Where(x => x.ProjectId == projectId && filter.Matches(x))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArray());
    }

    public Task<Guid[]> GetIds(Guid projectId, TaskFilter? filter, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values
                .Where(x => x.ProjectId == projectId && (filter == null || filter.Matches(x)))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => x.Id).ToArray());
    }

    public Task<int> Count(Guid projectId, TaskFilter filter, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values.Count(x => x.ProjectId == projectId && filter.Matches(x)));
    }

    public Task<OwnFieldKind[]> GetOwnFieldKinds(Guid projectId, string nameKey, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values
                .Where(x => x.ProjectId == projectId)
                .SelectMany(x => x.Fields)
                .Where(x => x.Own != null && FieldNames.Key(x.Own.Name) == nameKey)
                .Select(x => new OwnFieldKind(x.Own!.Type, x.Own.EnumId))
                .Distinct()
                .ToArray());
    }

    public async Task<int> GetMaxNumber(Guid projectId, Guid seriesId, CancellationToken ct = default)
    {
        Track();
        await Task.Yield();
        lock (_sync)
            return _items.Values.Where(x => x.ProjectId == projectId)
                .SelectMany(x => x.SeriesNumbers).Where(x => x.SeriesId == seriesId).Select(x => x.Number).DefaultIfEmpty(0).Max();
    }

    public Task<TaskItem[]> FindByNumber(Guid projectId, Guid seriesId, int number, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values
                .Where(x => x.ProjectId == projectId && x.SeriesNumbers.Any(n => n.SeriesId == seriesId && n.Number == number))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArray());
    }

    public Task<TaskItem[]> FindByIdPrefix(Guid projectId, string idKey, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values
                .Where(x => x.ProjectId == projectId && Tasker.Core.ShortId.Matches(x.Id, idKey))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArray());
    }

    public Task<NumberConflict[]> GetNumberConflicts(Guid projectId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values.Where(x => x.ProjectId == projectId)
                .SelectMany(t => t.SeriesNumbers.Select(n => (n.SeriesId, n.Number, Task: t)))
                .GroupBy(x => (x.SeriesId, x.Number))
                .Where(g => g.Count() > 1)
                .OrderBy(g => g.Key.SeriesId).ThenBy(g => g.Key.Number)
                .Select(g => new NumberConflict(g.Key.SeriesId, g.Key.Number,
                    g.Select(x => x.Task).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => x.Id).ToArray()))
                .ToArray());
    }

    /// <summary>Сколько файлов задач «нечитаемо» — для проверки, что чистка связей не стирает связи на нечитаемые задачи.</summary>
    public int UnreadableFiles { get; set; }

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => Task.FromResult(UnreadableFiles);

    public Task<TaskItem[]> GetWithInvalidLinks(Guid projectId, Guid[] knownLinkTypeIds, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values
                .Where(x => x.ProjectId == projectId && x.Links.Any(l =>
                    !knownLinkTypeIds.Contains(l.TypeId) || !(_items.TryGetValue(l.TargetId, out var target) && target.ProjectId == projectId)))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArray());
    }

    public Task<TaskItem[]> GetLinkedTo(Guid projectId, Guid targetId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values
                .Where(x => x.ProjectId == projectId && x.Links.Any(l => l.TargetId == targetId))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArray());
    }

    public Task<Dictionary<Guid, Guid[]>> GetLinkTargets(Guid projectId, Guid typeId, Guid[] sourceIds, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values
                .Where(x => x.ProjectId == projectId && sourceIds.Contains(x.Id) && x.Links.Any(l => l.TypeId == typeId))
                .ToDictionary(x => x.Id, x => x.Links.Where(l => l.TypeId == typeId).Select(l => l.TargetId).Distinct().ToArray()));
    }

    public Task<Tasker.Core.Links.LinkEdge[]> GetLinkEdges(Guid projectId, Guid typeId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values
                .Where(x => x.ProjectId == projectId)
                .SelectMany(x => x.Links.Where(l => l.TypeId == typeId).Select(l => new Tasker.Core.Links.LinkEdge(x.Id, l.TargetId)))
                .Distinct().ToArray());
    }

    public Task<Dictionary<Guid, int>> CountLinkedTo(Guid projectId, Guid[] targetIds, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values
                .Where(x => x.ProjectId == projectId)
                .SelectMany(x => x.Links)
                .Where(l => targetIds.Contains(l.TargetId))
                .GroupBy(l => l.TargetId)
                .ToDictionary(g => g.Key, g => g.Count()));
    }

    public Task<TaskItem[]> GetWithSeriesNotIn(Guid projectId, Guid[] knownSeriesIds, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values
                .Where(x => x.ProjectId == projectId && x.SeriesNumbers.Any(n => !knownSeriesIds.Contains(n.SeriesId)))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArray());
    }

    public async Task<string> Add(TaskItem task, CancellationToken ct = default)
    {
        Track();
        await Task.Yield();
        var version = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            _items[task.Id] = task with { Version = version };
            AddCount++;
        }
        return version;
    }

    public async Task<string?> Update(TaskItem task, string expectedVersion, CancellationToken ct = default)
    {
        Track();
        await Task.Yield();
        BeforeUpdate?.Invoke(task);
        lock (_sync)
        {
            if (!_items.TryGetValue(task.Id, out var current) || current.Version != expectedVersion)
                return null;
            var version = Guid.NewGuid().ToString("N");
            _items[task.Id] = task with { Version = version };
            UpdateCount++;
            return version;
        }
    }

    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default)
    {
        Track();
        lock (_sync)
        {
            if (!_items.TryGetValue(id, out var current) || current.Version != expectedVersion)
                return Task.FromResult(false);
            _items.Remove(id);
            DeleteCount++;
            return Task.FromResult(true);
        }
    }
}

public sealed class InMemorySeriesStorage : ISeriesStorage
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Series> _items = new();

    /// <summary>Сколько файлов серий «нечитаемы».</summary>
    public int Unreadable { get; set; }

    public int WriteCount;

    public Series Seed(Guid projectId, string prefix, string? name = null)
    {
        var series = new Series
        {
            Id = Guid.NewGuid(), ProjectId = projectId, Name = name ?? prefix, Prefix = prefix, Version = Guid.NewGuid().ToString("N")
        };
        lock (_sync)
            _items[series.Id] = series;
        return series;
    }

    public Task<Series?> GetById(Guid projectId, Guid id, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.TryGetValue(id, out var s) && s.ProjectId == projectId ? s : null);
    }

    public Task<Series[]> GetAll(Guid projectId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values.Where(x => x.ProjectId == projectId)
                .OrderBy(x => x.Prefix, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray());
    }

    public async Task<ListDto<Series>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        page.Apply(await GetAll(projectId, ct));

    public Task<string> Add(Series series, CancellationToken ct = default)
    {
        var version = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            _items[series.Id] = series with { Version = version };
            WriteCount++;
        }
        return Task.FromResult(version);
    }

    public Task<string?> Update(Series series, string expectedVersion, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_items.TryGetValue(series.Id, out var current) || current.Version != expectedVersion)
                return Task.FromResult<string?>(null);
            var version = Guid.NewGuid().ToString("N");
            _items[series.Id] = series with { Version = version };
            WriteCount++;
            return Task.FromResult<string?>(version);
        }
    }

    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_items.TryGetValue(id, out var current) || current.Version != expectedVersion)
                return Task.FromResult(false);
            _items.Remove(id);
            WriteCount++;
            return Task.FromResult(true);
        }
    }

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => Task.FromResult(Unreadable);
}

public sealed class InMemoryTaskTypeStorage : ITaskTypeStorage
{
    private readonly Dictionary<Guid, TaskType> _items = new();

    public void Seed(TaskType type) => _items[type.Id] = type;

    public Task<TaskType?> GetById(Guid projectId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(id, out var t) && t.ProjectId == projectId ? t : null);

    public Task<TaskType[]> GetAll(Guid projectId, CancellationToken ct = default) =>
        Task.FromResult(_items.Values.Where(x => x.ProjectId == projectId).OrderBy(x => x.Name).ToArray());

    public async Task<ListDto<TaskType>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        page.Apply(await GetAll(projectId, ct));

    public Task<string> Add(TaskType type, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string?> Update(TaskType type, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>Каталог полей и перечислений для тестов, которым поля не нужны: пуст.</summary>
public sealed class InMemoryFieldStorage : IFieldStorage
{
    public Task<FieldDefinition?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => Task.FromResult<FieldDefinition?>(null);
    public Task<FieldDefinition[]> GetAll(Guid projectId, CancellationToken ct = default) => Task.FromResult<FieldDefinition[]>([]);
    public Task<ListDto<FieldDefinition>> GetRange(Guid projectId, Page page, CancellationToken ct = default) => Task.FromResult(page.Apply(Array.Empty<FieldDefinition>()));
    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => Task.FromResult(0);
    public Task<string> Add(FieldDefinition field, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string?> Update(FieldDefinition field, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
}

public sealed class InMemoryFieldEnumStorage : IFieldEnumStorage
{
    public Task<FieldEnum?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => Task.FromResult<FieldEnum?>(null);
    public Task<FieldEnum[]> GetAll(Guid projectId, CancellationToken ct = default) => Task.FromResult<FieldEnum[]>([]);
    public Task<ListDto<FieldEnum>> GetRange(Guid projectId, Page page, CancellationToken ct = default) => Task.FromResult(page.Apply(Array.Empty<FieldEnum>()));
    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => Task.FromResult(0);
    public Task<string> Add(FieldEnum value, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string?> Update(FieldEnum value, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>Типы связей в памяти, с версиями — как настоящее хранилище.</summary>
public sealed class InMemoryLinkTypeStorage : ILinkTypeStorage
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, LinkType> _items = new();

    public int AddCount;

    /// <summary>Сколько файлов типов связей «нечитаемо».</summary>
    public int UnreadableFiles { get; set; }

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => Task.FromResult(UnreadableFiles);

    public Task<LinkType?> GetById(Guid projectId, Guid id, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.TryGetValue(id, out var t) && t.ProjectId == projectId ? t : null);
    }

    public Task<LinkType[]> GetAll(Guid projectId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values.Where(x => x.ProjectId == projectId).OrderBy(x => x.Name).ThenBy(x => x.Id).ToArray());
    }

    public async Task<ListDto<LinkType>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        page.Apply(await GetAll(projectId, ct));

    public Task<string> Add(LinkType type, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_items.ContainsKey(type.Id))
                throw new InvalidOperationException("duplicate id");
            _items[type.Id] = type with { Version = "1" };
            AddCount++;
        }
        return Task.FromResult("1");
    }

    public Task<string?> Update(LinkType type, string expectedVersion, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_items.TryGetValue(type.Id, out var current) || current.Version != expectedVersion)
                return Task.FromResult<string?>(null);
            var next = (int.Parse(current.Version) + 1).ToString();
            _items[type.Id] = type with { Version = next };
            return Task.FromResult<string?>(next);
        }
    }

    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.TryGetValue(id, out var current) && current.Version == expectedVersion && _items.Remove(id));
    }
}

public sealed class InMemoryStatusSetStorage : IStatusSetStorage
{
    private readonly Dictionary<Guid, StatusSet> _items = new();

    public void Seed(StatusSet set) => _items[set.Id] = set;

    public Task<StatusSet?> GetById(Guid projectId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(id, out var t) && t.ProjectId == projectId ? t : null);

    public Task<StatusSet[]> GetAll(Guid projectId, CancellationToken ct = default) =>
        Task.FromResult(_items.Values.Where(x => x.ProjectId == projectId).OrderBy(x => x.Name).ToArray());

    public async Task<ListDto<StatusSet>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        page.Apply(await GetAll(projectId, ct));

    public Task<string> Add(StatusSet set, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string?> Update(StatusSet set, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>Статусы в памяти: тестам серий они не нужны (только чтобы собрать TaskService).</summary>
public sealed class InMemoryStatusStorage : IStatusStorage
{
    public Task<Status?> GetById(Guid projectId, Guid id, CancellationToken ct = default) => Task.FromResult<Status?>(null);
    public Task<Status[]> GetAll(Guid projectId, CancellationToken ct = default) => Task.FromResult<Status[]>([]);
    public Task<ListDto<Status>> GetRange(Guid projectId, Page page, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string> Add(Status status, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string?> Update(Status status, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>Всё, что нужно тестам серий: сервисы Core поверх хранилищ в памяти.</summary>
/// <summary>Блокировки на время правки в памяти.</summary>
public sealed class InMemoryEditLockStorage : IEditLockStorage
{
    private readonly object _sync = new();
    private readonly Dictionary<(LockedEntity, Guid), EditLock> _items = new();

    public Task<EditLock?> Get(LockedEntity entity, Guid id, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.TryGetValue((entity, id), out var held) && held.ExpiresAt > now ? held : null);
    }

    public Task<EditLock> Acquire(LockedEntity entity, Guid id, EditHolder holder, DateTimeOffset now, DateTimeOffset expiresAt, Guid? projectId = null, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_items.TryGetValue((entity, id), out var held) && held.ExpiresAt > now)
            {
                if (held.Holder.Key != holder.Key)
                    return Task.FromResult(held);
                return Task.FromResult(_items[(entity, id)] = held with { Holder = holder, ExpiresAt = expiresAt, ProjectId = projectId });
            }

            return Task.FromResult(_items[(entity, id)] = new EditLock(entity, id, holder, now, expiresAt, projectId));
        }
    }

    public Task<EditLock[]> GetByProject(Guid projectId, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_items.Values.Where(x => x.ProjectId == projectId && x.ExpiresAt > now).OrderBy(x => x.AcquiredAt).ThenBy(x => x.Id).ToArray());
    }

    public Task<bool> Release(LockedEntity entity, Guid id, string holderKey, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_items.TryGetValue((entity, id), out var held) || held.Holder.Key != holderKey)
                return Task.FromResult(false);
            return Task.FromResult(_items.Remove((entity, id)));
        }
    }

    public Task Remove(LockedEntity entity, Guid id, CancellationToken ct = default)
    {
        lock (_sync)
            _items.Remove((entity, id));
        return Task.CompletedTask;
    }
}

/// <summary>Кто выполняет запрос — меняется в тесте, чтобы сыграть двух пользователей.</summary>
public sealed class FakeEditor : IEditorIdentity
{
    public static readonly EditHolder Ivan = new("user:ivan", "Ivan");
    public static readonly EditHolder Anna = new("user:anna", "Anna");

    public EditHolder Holder { get; set; } = Ivan;

    public Task<EditHolder> Current(CancellationToken ct = default) => Task.FromResult(Holder);
}

public sealed class SeriesEnv
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public Guid Project { get; } = Guid.NewGuid();
    public Guid TypeId { get; } = Guid.NewGuid();
    public Guid StatusId { get; } = Guid.NewGuid();

    public SeriesTestClock Clock { get; } = new(Start);
    public InMemoryLinkTypeStorage LinkTypes { get; } = new();
    public LinkTypeService LinkTypeSvc { get; }
    public LinkHealthService LinkHealthSvc { get; }
    public TaskLinkService LinkSvc { get; }
    public FakeWriteScope Scope { get; } = new();
    public InMemoryTaskStorage Tasks { get; }
    public InMemorySeriesStorage Series { get; } = new();
    public FakeEditor Editor { get; } = new();
    public EditLockService Locks { get; }

    public TaskService TaskSvc { get; }
    public SeriesService SeriesSvc { get; }
    public SeriesHealthService Health { get; }
    public CleanupService Cleanup { get; }

    private int _seeded;

    public SeriesEnv()
    {
        Tasks = new InMemoryTaskStorage { Scope = Scope };
        var types = new InMemoryTaskTypeStorage();
        var sets = new InMemoryStatusSetStorage();
        var setId = Guid.NewGuid();
        types.Seed(new TaskType { Id = TypeId, ProjectId = Project, Name = "Bug", StatusSetId = setId, Version = "1" });
        sets.Seed(new StatusSet { Id = setId, ProjectId = Project, Name = "Set", StatusIds = [StatusId], Version = "1" });

        // Ожидание массовых правок — ноль: заблокированная задача сразу даёт отказ, тесты не спят.
        Locks = new EditLockService(new InMemoryEditLockStorage(), Editor, Clock) { CascadeTimeout = TimeSpan.Zero };
        LinkTypeSvc = new LinkTypeService(LinkTypes, Tasks, Locks);
        LinkSvc = new TaskLinkService(Tasks, LinkTypeSvc, Clock, Locks, Series, Scope);
        TaskSvc = new TaskService(Tasks, types, sets, Clock, Series, Scope, Locks, new InMemoryFieldStorage(), new InMemoryFieldEnumStorage(), LinkSvc, new InMemoryStatusStorage());
        SeriesSvc = new SeriesService(Tasks, Series, Scope, Clock, Locks);
        Health = new SeriesHealthService(Tasks, Series);
        Cleanup = new CleanupService(Tasks, TaskSvc, Series, LinkTypeSvc, Scope, Locks, Clock);
        LinkHealthSvc = new LinkHealthService(Tasks, LinkTypes);
    }

    public Series AddSeries(string prefix) => Series.Seed(Project, prefix);

    /// <summary>Задача прямо в хранилище (можно с дубликатами номеров и ссылками на несуществующие серии). Порядок создания — по возрастанию, если не задан.</summary>
    public TaskItem Seed(string title, IEnumerable<(Guid Series, int Number)>? numbers = null, DateTimeOffset? createdAt = null, Guid? id = null)
    {
        var created = createdAt ?? Start.AddMinutes(++_seeded);
        return Tasks.Seed(new TaskItem
        {
            Id = id ?? Guid.NewGuid(),
            ProjectId = Project,
            Title = title,
            TypeId = TypeId,
            StatusId = StatusId,
            SeriesNumbers = (numbers ?? []).Select(x => new TaskSeriesNumber(x.Series, x.Number)).ToArray(),
            CreatedAt = created,
            UpdatedAt = created,
            Version = ""
        });
    }

    public Task<TaskItem> Create(string title = "Task", params Guid[] seriesIds) =>
        TaskSvc.Create(Project, new CreateTask(title, null, TypeId, null, seriesIds));

    public TaskItem Get(Guid id) => Tasks.All().Single(x => x.Id == id);

    public static (Guid, int)[] Nums(params (Guid, int)[] numbers) => numbers;
}
