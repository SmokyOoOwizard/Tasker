using Tasker.Core.Dto;

namespace Tasker.Core.Tasks;

/// <summary>
/// Запись списка в режиме дерева (<see cref="TaskService.ListTree"/>): задача и её положение в дереве. Строки идут в порядке показа —
/// эпик, под ним его дочерние задачи и так далее вглубь.
/// </summary>
public record TaskTreeItem : TaskListItem
{
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public TaskTreeItem(TaskListItem task, int depth, bool repeated) : base(task)
    {
        Depth = depth;
        Repeated = repeated;
    }

    /// <summary>Уровень вложенности: 0 — верхний уровень (задача без родителя в результате), 1 — её дочерняя задача и так далее.</summary>
    public int Depth { get; init; }

    /// <summary>
    /// Повтор: эту задачу (с родителями на разных эпиках или в разных ветках) список уже показывал выше. Строка и её поддерево показаны снова;
    /// консоль помечает такую строку суффиксом <c>(+)</c>. В «найдено» она не входит: считаются уникальные задачи.
    /// </summary>
    public bool Repeated { get; init; }
}

/// <summary>
/// Страница списка в виде дерева. <see cref="ListDto{T}.TotalCount"/> — сколько уникальных задач нашлось (повторы не считаются),
/// <see cref="TopLevelCount"/> — сколько из них на верхнем уровне. <see cref="ListDto{T}.Offset"/> и <see cref="ListDto{T}.Limit"/> относятся
/// к верхнему уровню: страница — это Limit задач верхнего уровня вместе с их поддеревьями целиком, а <c>Data</c> — строки этих деревьев.
/// </summary>
public class TaskTreeList : ListDto<TaskTreeItem>
{
    public required int TopLevelCount { get; init; }
}

/// <summary>
/// Иерархические связи проекта в памяти: родитель → дети и обратно (граф без циклов, у задачи может быть несколько родителей). Строится из
/// связей иерархических типов (<see cref="Links.LinkType.Hierarchical"/>), которых в проекте единицы по сравнению с задачами, поэтому хранится целиком
/// (только id), а задачи для показа загружаются отдельно, по страницам.
/// </summary>
public sealed class TaskHierarchy
{
    /// <summary>Сколько уровней дерева показывает список (верхний — первый). Глубже задачи не показываются; полный список — <c>--flat</c>.</summary>
    public const int MaxDepth = 10;

    /// <summary>
    /// Сколько строк страницы списка допускает показ повторов: задача, входящая в несколько эпиков, показывается под каждым вместе с поддеревом,
    /// и в тяжёлых графах (общие поддеревья под многими эпиками) повторы растут быстро. После этого предела повторы остаются строкой (с пометкой), но
    /// без своего поддерева.
    /// </summary>
    public const int MaxRows = 5000;

    public static readonly TaskHierarchy Empty = new([]);

    private readonly Dictionary<Guid, Guid[]> children;
    private readonly Dictionary<Guid, Guid[]> parents;

    public TaskHierarchy(IEnumerable<Links.LinkEdge> edges)
    {
        var distinct = edges.Where(x => x.SourceId != x.TargetId).Distinct().ToArray();
        children = distinct.GroupBy(x => x.SourceId).ToDictionary(g => g.Key, g => g.Select(x => x.TargetId).Distinct().ToArray());
        parents = distinct.GroupBy(x => x.TargetId).ToDictionary(g => g.Key, g => g.Select(x => x.SourceId).Distinct().ToArray());
    }

    public bool IsEmpty => children.Count == 0;

    public Guid[] ParentsOf(Guid id) => parents.GetValueOrDefault(id) ?? [];

    public int ChildCount(Guid id) => children.TryGetValue(id, out var list) ? list.Length : 0;

    /// <summary>
    /// Верхний уровень результата <paramref name="ordered"/> (id найденных задач в порядке списка): задачи без родителя среди найденных. Родитель, не прошедший
    /// фильтр, не считается: ребёнок, чей эпик не подошёл, тоже на верхнем уровне. Задачи, замкнутые в цикл родителей (слияние git может его
    /// создать), на верхний уровень не попали бы и пропали бы из списка, поэтому ещё не достигнутые от верхнего уровня берутся на него по порядку списка.
    /// </summary>
    public List<Guid> TopLevel(IReadOnlyList<Guid> ordered)
    {
        var found = ordered.ToHashSet();
        var top = ordered.Where(id => !ParentsOf(id).Any(found.Contains)).ToHashSet();

        var reached = new HashSet<Guid>();
        foreach (var id in ordered.Where(top.Contains))
            Reach(id, found, reached);
        foreach (var id in ordered)
        {
            if (reached.Contains(id))
                continue;
            top.Add(id);
            Reach(id, found, reached);
        }

        return ordered.Where(top.Contains).ToList();
    }

    private void Reach(Guid start, HashSet<Guid> found, HashSet<Guid> reached)
    {
        var stack = new Stack<Guid>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!reached.Add(id))
                continue;
            foreach (var child in children.GetValueOrDefault(id) ?? [])
                if (found.Contains(child) && !reached.Contains(child))
                    stack.Push(child);
        }
    }

    /// <summary>
    /// Строки деревьев верхнего уровня <paramref name="roots"/>: задача, под ней её дочерние задачи среди найденных (<paramref name="position"/> — место в порядке
    /// списка, им же упорядочены дети), и так до <see cref="MaxDepth"/> уровней. Задача с несколькими родителями показывается под каждым вместе с поддеревом
    /// (повторы помечены), задача не повторяется внутри своей ветки (защита от цикла), повторы после <see cref="MaxRows"/> строк идут без поддерева.
    /// </summary>
    public List<(Guid Id, int Depth, bool Repeated)> Rows(IEnumerable<Guid> roots, IReadOnlyDictionary<Guid, int> position)
    {
        var rows = new List<(Guid, int, bool)>();
        var seen = new HashSet<Guid>();
        var path = new HashSet<Guid>();
        foreach (var root in roots)
            Walk(root, 0);
        return rows;

        void Walk(Guid id, int depth)
        {
            var repeated = !seen.Add(id);
            rows.Add((id, depth, repeated));
            if (depth + 1 >= MaxDepth || (repeated && rows.Count >= MaxRows))
                return;

            var kids = (children.GetValueOrDefault(id) ?? []).Where(x => position.ContainsKey(x) && !path.Contains(x) && x != id).OrderBy(x => position[x]).ToArray();
            if (kids.Length == 0)
                return;
            path.Add(id);
            foreach (var kid in kids)
                Walk(kid, depth + 1);
            path.Remove(id);
        }
    }
}
