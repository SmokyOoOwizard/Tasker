namespace Tasker.Core.Tasks;

/// <summary>Задача. Общая модель для всех хранилищ.</summary>
public record TaskItem
{
    // Guid, а не счётчик: задачи в файлах создаются параллельно в разных ветках git,
    // и последовательные номера конфликтовали бы при слиянии.
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }

    /// <summary>Ссылка на <see cref="TaskType"/>.</summary>
    public required Guid TypeId { get; init; }

    /// <summary>Ссылка на <see cref="Statuses.Status"/> из набора статусов типа задачи.</summary>
    public required Guid StatusId { get; init; }

    /// <summary>
    /// Номера в сериях (<see cref="TaskSeries.Series"/>); пусто — задача не в сериях. Одна серия — не больше одного раза.
    /// Серии, которой уже нет, — недействительная ссылка: <see cref="TaskSeries.CleanupService"/> её уберёт.
    /// </summary>
    public IReadOnlyList<TaskSeries.TaskSeriesNumber> SeriesNumbers { get; init; } = [];

    /// <summary>
    /// Исходящие связи с другими задачами проекта (<see cref="Links.TaskLink"/>): «эта —блокирует→ та». Пусто — связей нет.
    /// Входящие (кто на эту задачу ссылается) здесь не хранятся: обе стороны даёт <see cref="TaskDetails.LinkViews"/>.
    /// </summary>
    public IReadOnlyList<Links.TaskLink> Links { get; init; } = [];

    /// <summary>
    /// Значения полей и дополнительные поля задачи — из каталога и собственные (<see cref="TaskField"/>). Поля типа без значения
    /// здесь не записаны. Полный вид полей (с определениями) даёт <see cref="TaskService.GetFields(Guid, Guid, CancellationToken)"/>.
    /// </summary>
    public IReadOnlyList<TaskField> Fields { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>См. <see cref="Versioning"/>.</summary>
    public required string Version { get; init; }
}
