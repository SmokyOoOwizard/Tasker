using Tasker.Core.Dto;
using Tasker.Core.Tasks;

namespace Tasker.Core.Links;

/// <param name="InwardName">null — как <paramref name="OutwardName"/>: связь без направления («relates to»).</param>
/// <param name="AllowCycles">null — допускает (как до появления признака); false — цикл из связей типа запрещён.</param>
/// <param name="Hierarchical">null или false — обычный тип; true — иерархический (<see cref="LinkType.Hierarchical"/>): циклы у него запрещены,
/// <paramref name="AllowCycles"/> = true вместе с ним — ошибка, а названия сторон должны различаться.</param>
public record CreateLinkType(string Name, string OutwardName, string? InwardName, bool? AllowCycles = null, bool? Hierarchical = null);

/// <summary>Поля null — не меняются. Version — версия, которую видел клиент (см. <see cref="Versioning"/>).</summary>
public record UpdateLinkType(string? Name, string? OutwardName, string? InwardName, string? Version, bool? AllowCycles = null, bool? Hierarchical = null);

/// <summary>
/// Типы связей проекта. Пока в проекте нет ни одного сохранённого типа, чтение показывает типы по умолчанию
/// (<see cref="DefaultLinkTypes"/>) «виртуально» — с теми же id, но ничего не записывая: просмотр задачи не должен
/// создавать файлы в репозитории. Записываются они при первой записи — создании, правке, удалении типа или добавлении связи
/// (<see cref="EnsureDefaults"/>), и дальше это обычные типы. Так проекты, созданные до связей, получают их без миграции.
/// Удалил все типы сам — чтение снова покажет типы по умолчанию; чтобы их не было, оставьте хотя бы один свой.
/// <para>
/// Типы, добавленные в набор позже (<see cref="DefaultLinkTypes.AddedLater"/>, «Parent/Child»), в проектах с уже сохранёнными типами показываются
/// и записываются так же, без миграции данных: пока в проекте нет ни одного иерархического типа и типа с таким названием. Свой иерархический тип
/// или переименованный «Parent/Child» его заменяют; удалить «Parent/Child» насовсем можно, только оставив другой иерархический тип.
/// </para>
/// </summary>
public class LinkTypeService(ILinkTypeStorage types, ITaskStorage tasks, Locks.EditLockService locks)
{
    /// <summary>Сколько файлов типов связей проекта не удалось прочитать (см. <see cref="ILinkTypeStorage.CountUnreadable"/>).</summary>
    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => types.CountUnreadable(projectId, ct);

    /// <summary>Версия типа по умолчанию, которого ещё нет в хранилище: после записи у него настоящая, так что изменение «со старой версией» даст <c>[modified]</c> и клиент перечитает.</summary>
    public const string DefaultVersion = "default";

    /// <summary>Сохранённые типы проекта; нет ни одного — типы по умолчанию (ничего не записывая).</summary>
    public async Task<LinkType[]> GetAll(Guid projectId, CancellationToken ct = default)
    {
        var stored = await types.GetAll(projectId, ct);
        if (stored.Length == 0)
            return Defaults(projectId);
        var later = Missing(projectId, stored);
        return later.Length == 0
            ? stored
            : stored.Concat(later).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray();
    }

    public async Task<ListDto<LinkType>> GetRange(Guid projectId, Page page, CancellationToken ct = default)
    {
        var stored = await types.GetRange(projectId, page, ct);
        if (stored.TotalCount == 0)
            return page.Apply(Defaults(projectId));
        // Типов в проекте единицы: страница без «позже добавленного» типа берётся из хранилища, с ним — из полного списка.
        return Missing(projectId, await types.GetAll(projectId, ct)).Length == 0 ? stored : page.Apply(await GetAll(projectId, ct));
    }

    /// <returns>null — типа нет.</returns>
    public async Task<LinkType?> GetById(Guid projectId, Guid id, CancellationToken ct = default)
    {
        if (await types.GetById(projectId, id, ct) is { } stored)
            return stored;
        var all = await types.GetAll(projectId, ct);
        return all.Length == 0 ? Defaults(projectId).FirstOrDefault(x => x.Id == id) : Missing(projectId, all).FirstOrDefault(x => x.Id == id);
    }

    /// <summary>
    /// Типы по умолчанию, добавленные в набор позже и отсутствующие в проекте с уже сохранёнными типами (<see cref="DefaultLinkTypes.AddedLater"/>):
    /// такой тип нужен, пока в проекте нет иерархического типа вообще и типа с этим названием.
    /// </summary>
    private static LinkType[] Missing(Guid projectId, LinkType[] stored) => stored.Any(x => x.Hierarchical)
        ? []
        : Defaults(projectId, DefaultLinkTypes.AddedLater)
            .Where(x => !stored.Any(s => s.Id == x.Id || string.Equals(s.Name, x.Name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    internal static LinkType[] Defaults(Guid projectId) => Defaults(projectId, DefaultLinkTypes.All);

    private static LinkType[] Defaults(Guid projectId, IEnumerable<DefaultLinkTypes.Definition> definitions) => definitions
        .Select(x => new LinkType
        {
            Id = DefaultLinkTypes.IdOf(projectId, x.Key),
            ProjectId = projectId,
            Name = x.Name,
            OutwardName = x.OutwardName,
            InwardName = x.InwardName,
            AllowCycles = x.AllowCycles,
            Hierarchical = x.Hierarchical,
            Version = DefaultVersion
        })
        .OrderBy(x => x.Name, StringComparer.Ordinal)
        .ThenBy(x => x.Id)
        .ToArray();

    /// <summary>
    /// Записывает типы по умолчанию, если в проекте нет ни одного сохранённого. Id детерминированные, поэтому повторное или
    /// одновременное создание даёт те же записи: проигравший гонку просто видит уже созданное. Вызывается перед любой записью типов
    /// и связей; чтению это не нужно. В проекте с уже сохранёнными типами дописывает только «позже добавленные» (<see cref="DefaultLinkTypes.AddedLater"/>), если они нужны.
    /// </summary>
    public async Task EnsureDefaults(Guid projectId, CancellationToken ct = default)
    {
        var stored = await types.GetAll(projectId, ct);
        var missing = stored.Length == 0 ? Defaults(projectId) : Missing(projectId, stored);

        foreach (var definition in missing)
        {
            var type = definition with { Version = Versioning.New };
            try
            {
                await types.Add(type, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Тот же тип мог только что создать другой запрос или процесс: тогда он уже есть и всё в порядке.
                if (await types.GetById(projectId, type.Id, ct) == null)
                    throw;
            }
        }
    }

    /// <summary>Тип по id или по названию (без учёта регистра). Несколько типов с одним названием (после слияния веток) — ошибка.</summary>
    /// <returns>null — такого типа нет.</returns>
    public async Task<LinkType?> Find(Guid projectId, string reference, CancellationToken ct = default)
    {
        var text = reference?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        var all = await GetAll(projectId, ct);
        if (Guid.TryParse(text, out var id) && all.FirstOrDefault(x => x.Id == id) is { } byId)
            return byId;

        var byName = all.Where(x => string.Equals(x.Name, text, StringComparison.OrdinalIgnoreCase)).ToArray();
        return byName.Length switch
        {
            0 => null,
            1 => byName[0],
            _ => throw new TaskerValidationException(
                $"Several link types are named '{text}', use the id: {string.Join(", ", byName.Select(x => x.Id))}")
        };
    }

    /// <summary>
    /// Тип и сторона связи по тому, что написал человек. Принимает id и название типа (связь исходит от первой задачи) и любое
    /// из двух названий сторон: «blocks» — исходящая, «is blocked by» — входящая (первая задача указывается как цель,
    /// как в интерфейсе Jira, где выбирают «is blocked by»). Без учёта регистра.
    /// </summary>
    /// <exception cref="TaskerValidationException">Ничего не подходит или подходит несколько вариантов.</exception>
    public async Task<(LinkType Type, LinkDirection Direction)> Resolve(Guid projectId, string phrase, CancellationToken ct = default)
    {
        var text = phrase?.Trim();
        if (string.IsNullOrEmpty(text))
            throw new TaskerValidationException("Link type is required");

        var all = await GetAll(projectId, ct);
        if (Guid.TryParse(text, out var id) && all.FirstOrDefault(x => x.Id == id) is { } byId)
            return (byId, LinkDirection.Outward);

        var found = new List<(LinkType Type, LinkDirection Direction)>();
        foreach (var type in all)
        {
            if (Same(type.Name, text) || Same(type.OutwardName, text))
                found.Add((type, LinkDirection.Outward));
            else if (Same(type.InwardName, text))
                found.Add((type, LinkDirection.Inward));
        }

        return found.Count switch
        {
            1 => found[0],
            0 => throw new TaskerValidationException(
                $"No link type or link name '{text}'; available: {string.Join(", ", all.SelectMany(x => new[] { x.OutwardName, x.InwardName }).Distinct(StringComparer.OrdinalIgnoreCase))}"),
            _ => throw new TaskerValidationException(
                $"'{text}' fits several link types ({string.Join(", ", found.Select(x => $"{x.Type.Name} [{x.Type.Id}]"))}); use the type id")
        };

        static bool Same(string a, string b) => string.Equals(a.Trim(), b, StringComparison.OrdinalIgnoreCase);
    }

    /// <exception cref="TaskerConflictException">Тип с таким названием уже есть.</exception>
    public async Task<LinkType> Create(Guid projectId, CreateLinkType command, CancellationToken ct = default)
    {
        await EnsureDefaults(projectId, ct);

        var name = Validate.Name(command.Name, "Link type name");
        var outward = Validate.Name(command.OutwardName, "Outward name");
        var inward = command.InwardName == null ? outward : Validate.Name(command.InwardName, "Inward name");
        await EnsureNameFree(projectId, name, null, ct);
        var hierarchical = command.Hierarchical ?? false;
        EnsureHierarchyValid(hierarchical, command.AllowCycles, outward, inward);

        var type = new LinkType
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = name,
            OutwardName = outward,
            InwardName = inward,
            AllowCycles = !hierarchical && (command.AllowCycles ?? true),
            Hierarchical = hierarchical,
            Version = Versioning.New
        };

        return type with { Version = await types.Add(type, ct) };
    }

    /// <returns>null — типа нет.</returns>
    public async Task<LinkType?> Update(Guid projectId, Guid id, UpdateLinkType command, CancellationToken ct = default)
    {
        await EnsureDefaults(projectId, ct);
        var type = await GetById(projectId, id, ct);
        if (type == null)
            return null;
        await locks.EnsureWritable(Locks.LockedEntity.LinkType, type.Id, Subject(type), ct);
        var expected = Versioning.Check(type.Version, command.Version, Subject(type));

        var updated = type with
        {
            Name = command.Name == null ? type.Name : Validate.Name(command.Name, "Link type name"),
            OutwardName = command.OutwardName == null ? type.OutwardName : Validate.Name(command.OutwardName, "Outward name"),
            InwardName = command.InwardName == null ? type.InwardName : Validate.Name(command.InwardName, "Inward name"),
            AllowCycles = command.AllowCycles ?? type.AllowCycles,
            Hierarchical = command.Hierarchical ?? type.Hierarchical
        };
        EnsureHierarchyValid(updated.Hierarchical, command.AllowCycles, updated.OutwardName, updated.InwardName);
        // Иерархическому типу циклы запрещены всегда (явное «разрешить» отвергнуто выше).
        if (updated.Hierarchical)
            updated = updated with { AllowCycles = false };
        if (updated.Name != type.Name)
            await EnsureNameFree(projectId, updated.Name, id, ct);

        var version = await types.Update(updated, expected, ct) ?? throw Versioning.Modified(Subject(type));
        return updated with { Version = version };
    }

    /// <summary>Нельзя удалить тип, по которому есть связи: сначала их нужно убрать.</summary>
    /// <returns>false — типа нет.</returns>
    public async Task<bool> Delete(Guid projectId, Guid id, string? version, CancellationToken ct = default)
    {
        await EnsureDefaults(projectId, ct);
        var type = await GetById(projectId, id, ct);
        if (type == null)
            return false;
        await locks.EnsureWritable(Locks.LockedEntity.LinkType, type.Id, Subject(type), ct);
        var expected = Versioning.Check(type.Version, version, Subject(type));

        var usages = new Usages(Subject(type));
        var linked = await tasks.Count(projectId, new TaskFilter { LinkTypeIds = [id] }, ct);
        if (linked > 0)
            usages.Add($"links of {linked} task(s)");
        usages.ThrowIfAny("deleted");

        if (!await types.Delete(projectId, id, expected, ct))
            throw Versioning.Modified(Subject(type));
        await locks.Forget(Locks.LockedEntity.LinkType, type.Id, ct);
        return true;
    }

    /// <summary>Иерархический тип: циклы запрещены (граф эпиков без циклов), стороны названы по-разному (родителя не отличить от потомка иначе).</summary>
    private static void EnsureHierarchyValid(bool hierarchical, bool? allowCycles, string outward, string inward)
    {
        if (!hierarchical)
            return;
        if (allowCycles == true)
            throw new TaskerValidationException("A hierarchical link type does not allow cycles: AllowCycles cannot be true");
        if (string.Equals(outward, inward, StringComparison.OrdinalIgnoreCase))
            throw new TaskerValidationException("A hierarchical link type needs two different side names (e.g. 'includes' / 'is part of'): the parent and the child must be told apart");
    }

    private async Task EnsureNameFree(Guid projectId, string name, Guid? exceptId, CancellationToken ct)
    {
        if ((await types.GetAll(projectId, ct)).Any(x => x.Id != exceptId && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new TaskerConflictException($"Link type '{name}' already exists in the project");
    }

    private static string Subject(LinkType type) => $"Link type '{type.Name}'";
}
