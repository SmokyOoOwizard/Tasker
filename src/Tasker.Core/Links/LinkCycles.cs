using Tasker.Core.Tasks;
using Tasker.Core.TaskSeries;

namespace Tasker.Core.Links;

/// <summary>Связь «источник —тип→ цель» без остального содержимого задачи.</summary>
public record LinkEdge(Guid SourceId, Guid TargetId);

/// <summary>Задача в цикле — достаточно, чтобы показать её человеку (<c>TSK-7</c>, название).</summary>
public record LinkCycleTask(Guid Id, string Title, IReadOnlyList<TaskSeriesNumber> SeriesNumbers);

/// <summary>
/// Цикл из связей одного типа: <see cref="Path"/> замкнут — первая и последняя задача одна и та же («T-1 → T-2 → T-1»).
/// Такие циклы создаёт слияние веток git (каждая ветка добавила свою половину) или тип, у которого циклы потом запретили.
/// </summary>
public record LinkCycle(Guid TypeId, string TypeName, LinkCycleTask[] Path)
{
    /// <summary>Путь как его читает человек: <c>TSK-1 → TSK-2 → TSK-1</c>; у задачи без номера — короткий id.</summary>
    public string Format(IReadOnlyDictionary<Guid, string> prefixes) =>
        string.Join(" → ", Path.Select(x => Reference(x.Id, x.SeriesNumbers, prefixes)));

    /// <summary>Задача для показа: первая действительная ссылка (<c>TSK-5</c>, по префиксу), иначе короткий id.</summary>
    public static string Reference(Guid id, IEnumerable<TaskSeriesNumber> numbers, IReadOnlyDictionary<Guid, string> prefixes)
    {
        var valid = numbers
            .Where(x => prefixes.ContainsKey(x.SeriesId))
            .OrderBy(x => prefixes[x.SeriesId], StringComparer.Ordinal)
            .ThenBy(x => x.SeriesId.ToString("D"), StringComparer.Ordinal)
            .FirstOrDefault();
        return valid == null ? ShortId.Of(id) : $"{prefixes[valid.SeriesId]}-{valid.Number}";
    }
}

/// <summary>Поиск циклов в связях: перед добавлением связи (обход вперёд от цели) и по всему проекту (после слияния git).</summary>
public static class LinkCycles
{
    /// <summary>Сколько задач обход готов посетить: защита от огромных графов, проверка не должна «зависать» на запись.</summary>
    public const int MaxVisited = 20000;

    /// <summary>Сколько циклов проект показывает (по одному на связный кусок графа); остальное — после исправления этих.</summary>
    public const int MaxReported = 50;

    /// <summary>
    /// Путь по связям типа от <paramref name="from"/> до <paramref name="to"/> (оба конца включены), кратчайший; null — пути нет.
    /// Обход по уровням: один запрос хранилища на уровень (индекс связей), а не на задачу.
    /// </summary>
    /// <exception cref="TaskerValidationException">Достижимых задач больше <see cref="MaxVisited"/>: цикл проверить не удалось.</exception>
    public static Task<Guid[]?> FindPath(ITaskStorage tasks, Guid projectId, Guid typeId, Guid from, Guid to, CancellationToken ct) =>
        FindPath(tasks, projectId, [typeId], from, to, ct);

    /// <summary>
    /// Как <see cref="FindPath(ITaskStorage, Guid, Guid, Guid, Guid, CancellationToken)"/>, но путь может идти по связям любого из типов: иерархические типы
    /// образуют один граф эпиков, и цикл «A включает B (Parent/Child), B включает A (свой иерархический тип)» — тоже цикл.
    /// </summary>
    public static async Task<Guid[]?> FindPath(ITaskStorage tasks, Guid projectId, IReadOnlyCollection<Guid> typeIds, Guid from, Guid to, CancellationToken ct)
    {
        var parent = new Dictionary<Guid, Guid> { [from] = from };
        var level = new[] { from };

        while (level.Length > 0)
        {
            var next = new List<Guid>();
            var targets = new Dictionary<Guid, List<Guid>>();
            foreach (var typeId in typeIds)
            {
                foreach (var (source, list) in await tasks.GetLinkTargets(projectId, typeId, level, ct))
                {
                    if (!targets.TryGetValue(source, out var all))
                        targets[source] = all = [];
                    all.AddRange(list);
                }
            }

            foreach (var source in level)
            {
                if (!targets.TryGetValue(source, out var list))
                    continue;
                foreach (var target in list)
                {
                    if (!parent.TryAdd(target, source))
                        continue;
                    if (target == to)
                        return Unwind(parent, from, to);
                    next.Add(target);
                }
            }

            if (parent.Count > MaxVisited)
                throw new TaskerValidationException(
                    $"Cycle check: more than {MaxVisited} tasks are reachable through links of this type, cannot verify that the link does not close a cycle");
            level = next.ToArray();
        }

        return null;
    }

    private static Guid[] Unwind(Dictionary<Guid, Guid> parent, Guid from, Guid to)
    {
        var path = new List<Guid> { to };
        for (var current = to; current != from;)
        {
            current = parent[current];
            path.Add(current);
        }
        path.Reverse();
        return path.ToArray();
    }

    /// <summary>
    /// Циклы проекта по типам, которые циклов не допускают: по одному — кратчайшему — на каждую компоненту сильной связности,
    /// не больше <see cref="MaxReported"/>. Порядок стабильный (по типу, затем по id задач). Только чтение.
    /// </summary>
    public static async Task<LinkCycle[]> Find(ITaskStorage tasks, Guid projectId, IEnumerable<LinkType> types, CancellationToken ct)
    {
        var found = new List<LinkCycle>();
        foreach (var type in types.Where(x => !x.AllowCycles && !x.IsSymmetric).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id))
        {
            var edges = await tasks.GetLinkEdges(projectId, type.Id, ct);
            foreach (var path in CyclesOf(edges))
            {
                if (found.Count >= MaxReported)
                    return found.ToArray();

                var loaded = new List<TaskItem?>();
                foreach (var id in path)
                    loaded.Add(await tasks.GetById(projectId, id, ct));

                // Цикл не имеет начала; показываем с самой ранней задачи (по созданию, затем по наименьшему номеру в серии), чтобы вывод не зависел от id.
                var nodes = path.Length - 1;
                var first = Enumerable.Range(0, nodes)
                    .OrderBy(i => loaded[i]?.CreatedAt ?? DateTimeOffset.MaxValue)
                    .ThenBy(i => loaded[i]?.SeriesNumbers.Select(x => x.Number).DefaultIfEmpty(int.MaxValue).Min() ?? int.MaxValue)
                    .ThenBy(i => path[i].ToString("D"), StringComparer.Ordinal)
                    .First();
                var shown = new List<LinkCycleTask>();
                for (var i = 0; i <= nodes; i++)
                {
                    var index = (first + i) % nodes;
                    var task = loaded[index];
                    shown.Add(task == null ? new LinkCycleTask(path[index], "", []) : new LinkCycleTask(task.Id, task.Title, task.SeriesNumbers));
                }
                found.Add(new LinkCycle(type.Id, type.Name, shown.ToArray()));
            }
        }

        return found.ToArray();
    }

    /// <summary>Циклы графа (замкнутые пути): по одному на компоненту сильной связности (и на петлю); детерминированно.</summary>
    internal static IEnumerable<Guid[]> CyclesOf(IEnumerable<LinkEdge> edges)
    {
        var comparer = Comparer<Guid>.Create((a, b) => string.CompareOrdinal(a.ToString("D"), b.ToString("D")));
        var graph = new SortedDictionary<Guid, SortedSet<Guid>>(comparer);
        foreach (var edge in edges)
        {
            if (!graph.TryGetValue(edge.SourceId, out var set))
                graph[edge.SourceId] = set = new SortedSet<Guid>(comparer);
            set.Add(edge.TargetId);
        }

        // Цикл целиком состоит из задач-источников: цели без исходящих связей из рассмотрения выпадают.
        foreach (var set in graph.Values)
            set.RemoveWhere(x => !graph.ContainsKey(x));

        var result = new List<Guid[]>();
        foreach (var component in StronglyConnected(graph))
        {
            if (component.Count == 1 && !graph[component[0]].Contains(component[0]))
                continue;

            var inside = component.ToHashSet();
            // Кратчайший путь обратно в начало: стартуем с наименьшего id компоненты.
            var start = component.OrderBy(x => x.ToString("D"), StringComparer.Ordinal).First();
            var parent = new Dictionary<Guid, Guid>();
            var queue = new Queue<Guid>();
            queue.Enqueue(start);
            Guid[]? cycle = null;
            while (queue.Count > 0 && cycle == null)
            {
                var current = queue.Dequeue();
                foreach (var target in graph[current].Where(inside.Contains))
                {
                    if (target == start)
                    {
                        var chain = new List<Guid> { current };
                        for (var x = current; x != start; x = parent[x])
                            chain.Add(parent[x]);
                        chain.Reverse();
                        chain.Add(start);
                        cycle = chain.ToArray();
                        break;
                    }
                    if (parent.TryAdd(target, current))
                        queue.Enqueue(target);
                }
            }

            if (cycle != null)
                result.Add(cycle);
        }

        return result.OrderBy(x => x[0].ToString("D"), StringComparer.Ordinal);
    }

    /// <summary>Компоненты сильной связности (Tarjan, без рекурсии: граф может быть глубоким).</summary>
    private static List<List<Guid>> StronglyConnected(SortedDictionary<Guid, SortedSet<Guid>> graph)
    {
        var index = new Dictionary<Guid, int>();
        var low = new Dictionary<Guid, int>();
        var onStack = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        var result = new List<List<Guid>>();
        var counter = 0;

        foreach (var root in graph.Keys)
        {
            if (index.ContainsKey(root))
                continue;

            var work = new Stack<(Guid Node, IEnumerator<Guid> Targets)>();
            index[root] = low[root] = counter++;
            stack.Push(root);
            onStack.Add(root);
            work.Push((root, graph[root].GetEnumerator()));

            while (work.Count > 0)
            {
                var (node, targets) = work.Peek();
                if (targets.MoveNext())
                {
                    var target = targets.Current;
                    if (!index.ContainsKey(target))
                    {
                        index[target] = low[target] = counter++;
                        stack.Push(target);
                        onStack.Add(target);
                        work.Push((target, graph[target].GetEnumerator()));
                    }
                    else if (onStack.Contains(target))
                    {
                        low[node] = Math.Min(low[node], index[target]);
                    }
                    continue;
                }

                work.Pop();
                if (work.Count > 0)
                {
                    var parentNode = work.Peek().Node;
                    low[parentNode] = Math.Min(low[parentNode], low[node]);
                }

                if (low[node] == index[node])
                {
                    var component = new List<Guid>();
                    Guid member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component.Add(member);
                    } while (member != node);
                    result.Add(component);
                }
            }
        }

        return result;
    }
}
