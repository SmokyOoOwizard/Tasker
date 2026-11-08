using System.Security.Cryptography;
using System.Text;
using Tasker.Core.Tasks;

namespace Tasker.Core.TaskSeries;

/// <param name="KeeperId">Задача, которая сохраняет номер (самая ранняя).</param>
public record ConflictPlanStep(
    Guid TaskId, string TaskTitle, int OldNumber, int NewNumber, Guid KeeperId, string KeeperTitle);

/// <param name="Token">Отпечаток плана: состояние задач, на котором он построен. Применить можно только план с токеном, совпавшим с текущим.</param>
public record ConflictPlan(Guid SeriesId, string Token, ConflictPlanStep[] Steps);

/// <summary>
/// Разрешение конфликтов номеров одной серии в два шага: <see cref="Plan"/> показывает, что изменится, <see cref="Apply"/> делает это.
/// Правило то же, что у <see cref="CleanupService"/> с <c>ResolveConflicts</c>: самая ранняя задача (<c>CreatedAt</c>, затем Guid) сохраняет номер,
/// остальные получают максимум + 1 по возрастанию номеров конфликтов и в том же порядке задач.
/// Защита от гонки: <see cref="Apply"/> внутри секции записи заново строит план и сравнивает его токен с тем, что подтвердил клиент
/// (токен включает версии всех затронутых задач). Два пользователя подтверждают один и тот же план — секции записи идут по очереди,
/// первый применяет, у второго токен уже не совпадает (409 Modified), и повторно ничего не перенумеровывается.
/// </summary>
public class SeriesConflictService(ITaskStorage tasks, ISeriesStorage series, IWriteScope writes, Locks.EditLockService locks, TimeProvider time, Agents.AgentGuard guard)
{
    private const int Attempts = 2;

    public SeriesConflictService(ITaskStorage tasks, ISeriesStorage series, IWriteScope writes, Locks.EditLockService locks, Agents.AgentGuard guard)
        : this(tasks, series, writes, locks, TimeProvider.System, guard)
    {
    }

    /// <returns>План (пустой, если конфликтов нет); null — серии нет.</returns>
    public async Task<ConflictPlan?> Plan(Guid projectId, Guid seriesId, CancellationToken ct = default) =>
        (await Build(projectId, seriesId, ct))?.Plan;

    /// <exception cref="TaskerNotFoundException">Серии нет.</exception>
    /// <exception cref="TaskerConflictException">Токен не совпал: после построения плана задачи изменились или план уже применён (<see cref="ConflictCode.Modified"/>).</exception>
    /// <returns>Применённый план.</returns>
    public async Task<ConflictPlan> Apply(Guid projectId, Guid seriesId, string? token, CancellationToken ct = default)
    {
        await guard.Require(projectId, Agents.AgentRight.ManageSeries, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw TaskerErrors.Invalid("Token is required: pass the token of the plan you are confirming");

        // Блокировки ждём до секции записи (внутри неё держатель не смог бы её снять), как в CleanupService.
        var preview = await Build(projectId, seriesId, ct) ?? throw TaskerErrors.EntityNotFound($"Series {seriesId} was not found");
        await TaskRewrites.WaitForLocks(locks, preview.Moves.Select(x => x.Task), ct);

        return await writes.Exclusive(projectId, async () =>
        {
            var current = await Build(projectId, seriesId, ct) ?? throw TaskerErrors.EntityNotFound($"Series {seriesId} was not found");
            if (current.Plan.Token != token)
                throw TaskerErrors.SeriesPlanChanged();

            await TaskRewrites.ModifyAll(tasks, time, locks, current.Moves.Select(x => x.Task),
                t =>
                {
                    var move = current.Moves.First(x => x.Task.Id == t.Id);
                    return t.SeriesNumbers.Any(n => n.SeriesId == seriesId && n.Number == move.Step.OldNumber)
                        ? t with { SeriesNumbers = t.SeriesNumbers.Select(n => n.SeriesId == seriesId && n.Number == move.Step.OldNumber ? n with { Number = move.Step.NewNumber } : n).ToArray() }
                        : null;
                }, Attempts, ct);
            return current.Plan;
        }, ct);
    }

    private sealed record Move(TaskItem Task, ConflictPlanStep Step);

    private sealed record Built(ConflictPlan Plan, Move[] Moves);

    private async Task<Built?> Build(Guid projectId, Guid seriesId, CancellationToken ct)
    {
        if (await series.GetById(projectId, seriesId, ct) == null)
            return null;
        if (await series.CountUnreadable(projectId, ct) > 0)
            throw TaskerErrors.Invalid(
                "Some series files cannot be read (merge conflict or broken YAML): fix them first, then resolve the conflicts");

        var next = await tasks.GetMaxNumber(projectId, seriesId, ct);
        var moves = new List<Move>();
        var fingerprint = new StringBuilder().Append(seriesId.ToString("D")).Append('|').Append(next);

        foreach (var conflict in (await tasks.GetNumberConflicts(projectId, ct)).Where(x => x.SeriesId == seriesId).OrderBy(x => x.Number))
        {
            var members = new List<TaskItem>();
            foreach (var id in conflict.TaskIds)
            {
                if (await tasks.GetById(projectId, id, ct) is { } member)
                    members.Add(member);
            }

            var ordered = members.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id.ToString("D"), StringComparer.Ordinal).ToArray();
            if (ordered.Length < 2)
                continue;

            var keeper = ordered[0];
            fingerprint.Append('|').Append(conflict.Number).Append(':').Append(keeper.Id.ToString("D")).Append('@').Append(keeper.Version);
            foreach (var task in ordered.Skip(1))
            {
                next++;
                moves.Add(new Move(task, new ConflictPlanStep(task.Id, task.Title, conflict.Number, next, keeper.Id, keeper.Title)));
                fingerprint.Append(',').Append(task.Id.ToString("D")).Append('@').Append(task.Version).Append("->").Append(next);
            }
        }

        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString()))).ToLowerInvariant();
        return new Built(new ConflictPlan(seriesId, token, moves.Select(x => x.Step).ToArray()), moves.ToArray());
    }
}
