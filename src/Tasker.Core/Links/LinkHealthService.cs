using Tasker.Core.Tasks;

namespace Tasker.Core.Links;

/// <summary>Состояние связей проекта — что нужно чистить.</summary>
/// <param name="TasksWithInvalidLinks">
/// Сколько задач имеют связь на задачу или тип, которых нет (всегда 0, если есть нечитаемые файлы: тогда не понять —
/// нечитаемая задача или тип выглядят несуществующими).
/// </param>
/// <param name="UnreadableFiles">Сколько файлов задач и типов связей не удалось прочитать (например, конфликт слияния git).</param>
/// <param name="Cycles">
/// Циклы из связей типов, которые циклов не допускают (после слияния веток git; сервис такую связь не примет, а git файлы не сводит).
/// Чистка циклы не убирает — это решает человек (<c>task unlink</c>). Считаются и при нечитаемых файлах: цикл из прочитанных связей настоящий.
/// </param>
public record LinkHealth(int TasksWithInvalidLinks, int UnreadableFiles, LinkCycle[]? Cycles = null)
{
    public LinkCycle[] Cycles { get; init; } = Cycles ?? [];

    public virtual bool Equals(LinkHealth? other) =>
        other != null && TasksWithInvalidLinks == other.TasksWithInvalidLinks && UnreadableFiles == other.UnreadableFiles && Cycles.SequenceEqual(other.Cycles);

    public override int GetHashCode() => HashCode.Combine(TasksWithInvalidLinks, UnreadableFiles, Cycles.Length);

    public bool NeedsAttention => TasksWithInvalidLinks > 0 || UnreadableFiles > 0 || Cycles.Length > 0;
}

/// <summary>
/// Только чтение: ничего не меняет. Им пользуются сверка (<c>tasker sync</c>, демон) и <c>cleanup --check</c>.
/// Зависит только от хранилищ (как <see cref="TaskSeries.SeriesHealthService"/>), чтобы работать там, где нет текущего пользователя.
/// </summary>
public class LinkHealthService(ITaskStorage tasks, ILinkTypeStorage types)
{
    public async Task<LinkHealth> Check(Guid projectId, CancellationToken ct = default)
    {
        var unreadable = await tasks.CountUnreadable(projectId, ct) + await types.CountUnreadable(projectId, ct);

        // Пока типов нет в хранилище, показываются типы по умолчанию (LinkTypeService): связи на них действительны.
        var stored = await types.GetAll(projectId, ct);
        var all = stored.Length > 0 ? stored : LinkTypeService.Defaults(projectId);
        var cycles = await LinkCycles.Find(tasks, projectId, all, ct);
        if (unreadable > 0)
            return new LinkHealth(0, unreadable, cycles);

        var known = all.Select(x => x.Id).ToArray();
        return new LinkHealth((await tasks.GetWithInvalidLinks(projectId, known, ct)).Length, 0, cycles);
    }
}
