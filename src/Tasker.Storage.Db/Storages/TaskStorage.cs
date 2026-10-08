using Microsoft.EntityFrameworkCore;
using Tasker.Core.Dto;
using Tasker.Core.Tasks;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

internal class TaskStorage(AppDbContext context) : ITaskStorage
{
    public async Task<TaskItem?> GetById(Guid projectId, Guid id, CancellationToken ct = default)
    {
        var model = await context.Tasks
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Id == id, ct);

        return model == null ? null : (await Map([model], ct))[0];
    }

    public async Task<TaskItem[]> FindByIdPrefix(Guid projectId, string idKey, CancellationToken ct = default) =>
        await Map(await context.Tasks
            .AsNoTracking()
            // Ключ — шестнадцатеричные цифры и дефисы: знаков LIKE в нём нет.
            .Where(x => x.ProjectId == projectId && EF.Functions.Like(x.Id.ToString(), idKey + "%"))
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToArrayAsync(ct), ct);

    public async Task<ListDto<TaskItem>> GetRange(
        Guid projectId,
        TaskFilter? filter,
        Page page,
        CancellationToken ct = default
    )
    {
        // Порядок — по умолчанию (создание, затем id) или по ключам filter.Sort; страницы при этом не пересекаются: в конце ключей всегда стоит id.
        var query = TaskSorting.Apply(context, Filter(projectId, filter), filter?.Sort);

        var total = await query.CountAsync(ct);
        var models = await query.Skip(page.Offset).Take(page.Limit).ToArrayAsync(ct);

        return new()
        {
            TotalCount = total,
            Offset = page.Offset,
            Limit = page.Limit,
            Data = await Map(models, ct)
        };
    }

    public async Task<TaskItem[]> GetAll(Guid projectId, TaskFilter filter, CancellationToken ct = default) =>
        await Map(await TaskSorting.Apply(context, Filter(projectId, filter), filter.Sort).ToArrayAsync(ct), ct);

    public async Task<Guid[]> GetIds(Guid projectId, TaskFilter? filter, CancellationToken ct = default) =>
        await TaskSorting.Apply(context, Filter(projectId, filter), filter?.Sort).Select(x => x.Id).ToArrayAsync(ct);

    public Task<int> Count(Guid projectId, TaskFilter filter, CancellationToken ct = default) =>
        Filter(projectId, filter).CountAsync(ct);

    // ---- серии и номера ----

    public async Task<int> GetMaxNumber(Guid projectId, Guid seriesId, CancellationToken ct = default) =>
        await context.TaskSeriesNumbers
            .Where(x => x.ProjectId == projectId && x.SeriesId == seriesId)
            .MaxAsync(x => (int?)x.Number, ct) ?? 0;

    public async Task<TaskItem[]> FindByNumber(Guid projectId, Guid seriesId, int number, CancellationToken ct = default)
    {
        var models = await context.Tasks
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId && context.TaskSeriesNumbers.Any(n =>
                n.TaskId == t.Id && n.SeriesId == seriesId && n.Number == number))
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .ToArrayAsync(ct);

        return await Map(models, ct);
    }

    public async Task<Core.TaskSeries.NumberConflict[]> GetNumberConflicts(Guid projectId, CancellationToken ct = default)
    {
        // Уникальный индекс (ProjectId, SeriesId, Number) в норме не даёт дубликатов и результат пуст;
        // запрос нужен базам, где индекса нет (например, созданным вручную).
        var groups = await context.TaskSeriesNumbers
            .Where(x => x.ProjectId == projectId)
            .GroupBy(x => new { x.SeriesId, x.Number })
            .Where(g => g.Count() > 1)
            .Select(g => new { g.Key.SeriesId, g.Key.Number })
            .OrderBy(g => g.SeriesId)
            .ThenBy(g => g.Number)
            .ToArrayAsync(ct);

        var result = new List<Core.TaskSeries.NumberConflict>();
        foreach (var group in groups)
        {
            var taskIds = await context.Tasks
                .Where(t => t.ProjectId == projectId && context.TaskSeriesNumbers.Any(n =>
                    n.TaskId == t.Id && n.SeriesId == group.SeriesId && n.Number == group.Number))
                .OrderBy(t => t.CreatedAt)
                .ThenBy(t => t.Id)
                .Select(t => t.Id)
                .ToArrayAsync(ct);
            result.Add(new(group.SeriesId, group.Number, taskIds));
        }
        return result.ToArray();
    }

    public async Task<TaskItem[]> GetWithSeriesNotIn(Guid projectId, Guid[] knownSeriesIds, CancellationToken ct = default)
    {
        var models = await context.Tasks
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId && context.TaskSeriesNumbers.Any(n =>
                n.TaskId == t.Id && !knownSeriesIds.Contains(n.SeriesId)))
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .ToArrayAsync(ct);

        return await Map(models, ct);
    }

    public async Task<TaskItem[]> GetLinkedTo(Guid projectId, Guid targetId, CancellationToken ct = default)
    {
        var models = await context.Tasks
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId && context.TaskLinks.Any(l => l.TaskId == t.Id && l.TargetId == targetId))
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .ToArrayAsync(ct);

        return await Map(models, ct);
    }

    public async Task<Dictionary<Guid, Guid[]>> GetLinkTargets(Guid projectId, Guid typeId, Guid[] sourceIds, CancellationToken ct = default)
    {
        if (sourceIds.Length == 0)
            return [];

        var rows = await context.TaskLinks
            .AsNoTracking()
            .Where(l => l.TypeId == typeId && sourceIds.Contains(l.TaskId) && context.Tasks.Any(t => t.Id == l.TaskId && t.ProjectId == projectId))
            .Select(l => new { l.TaskId, l.TargetId })
            .ToArrayAsync(ct);

        return rows.GroupBy(x => x.TaskId).ToDictionary(g => g.Key, g => g.Select(x => x.TargetId).ToArray());
    }

    public async Task<Core.Links.LinkEdge[]> GetLinkEdges(Guid projectId, Guid typeId, CancellationToken ct = default)
    {
        var rows = await context.TaskLinks
            .AsNoTracking()
            .Where(l => l.TypeId == typeId && context.Tasks.Any(t => t.Id == l.TaskId && t.ProjectId == projectId))
            .Select(l => new { l.TaskId, l.TargetId })
            .ToArrayAsync(ct);

        return rows.Select(x => new Core.Links.LinkEdge(x.TaskId, x.TargetId)).ToArray();
    }

    public async Task<Dictionary<Guid, int>> CountLinkedTo(Guid projectId, Guid[] targetIds, CancellationToken ct = default)
    {
        if (targetIds.Length == 0)
            return [];

        var counts = await context.TaskLinks
            .Where(l => targetIds.Contains(l.TargetId) && context.Tasks.Any(t => t.Id == l.TaskId && t.ProjectId == projectId))
            .GroupBy(l => l.TargetId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToArrayAsync(ct);

        return counts.ToDictionary(x => x.Id, x => x.Count);
    }

    public async Task<TaskItem[]> GetWithInvalidLinks(Guid projectId, Guid[] knownLinkTypeIds, CancellationToken ct = default)
    {
        var models = await context.Tasks
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId && context.TaskLinks.Any(l => l.TaskId == t.Id
                && (!knownLinkTypeIds.Contains(l.TypeId) || !context.Tasks.Any(other => other.Id == l.TargetId && other.ProjectId == projectId))))
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .ToArrayAsync(ct);

        return await Map(models, ct);
    }

    public Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default) => Task.FromResult(0);

    private IQueryable<TaskDbModel> Filter(Guid projectId, TaskFilter? filter)
    {
        var query = context.Tasks
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId);
        if (filter?.Ids is { } ids)
            query = query.Where(x => ids.Contains(x.Id));
        if (filter?.TypeIds is { } typeIds)
            query = query.Where(x => typeIds.Contains(x.TypeId));
        if (filter?.StatusIds is { } statusIds)
            query = query.Where(x => statusIds.Contains(x.StatusId));
        if (filter?.SeriesIds is { } seriesIds)
            query = query.Where(x => context.TaskSeriesNumbers.Any(n => n.TaskId == x.Id && seriesIds.Contains(n.SeriesId)));
        if (filter?.LinkTypeIds is { } linkTypeIds)
            query = query.Where(x => context.TaskLinks.Any(l => l.TaskId == x.Id && linkTypeIds.Contains(l.TypeId)));
        if (filter?.FieldIds is { } fieldIds)
            query = query.Where(x => context.TaskFields.Any(f => f.TaskId == x.Id && fieldIds.Contains(f.FieldId)));
        if (filter?.EnumIds is { } enumIds)
            query = query.Where(x => context.TaskFields.Any(f => f.TaskId == x.Id && f.OwnEnumId != null && enumIds.Contains(f.OwnEnumId.Value)));
        foreach (var condition in filter?.FieldValues ?? [])
            query = FieldCondition(query, condition);
        return query;
    }

    /// <summary>
    /// Условие по полю (<see cref="Core.Tasks.FieldCondition"/>). «Есть значение, которое…» — подзапрос к значениям; «нет значения…» — его отрицание
    /// (поэтому <c>!=</c> и <c>:unset</c> берут и задачи без значения). Строка относится к условию, если это поле каталога или собственное поле с тем же
    /// именем и типом. Подключённость поля: тип задачи из типов, где поле каталога есть, или запись о поле в самой задаче.
    /// </summary>
    private IQueryable<TaskDbModel> FieldCondition(IQueryable<TaskDbModel> query, Core.Tasks.FieldCondition condition)
    {
        var (field, key, ownType) = (condition.FieldId, condition.Key, (int)condition.Type);
        var (value, number) = (condition.Value, condition.Number);
        var typeIds = condition.TypeIds ?? [];
        // Только строки этого условия: подзапросы ниже добавляют своё — по задаче и значению.
        var values = field is { } catalogId
            ? context.TaskFieldValues.Where(v => v.FieldId == catalogId || (v.OwnKey == key && v.OwnType == ownType))
            : context.TaskFieldValues.Where(v => v.OwnKey == key && v.OwnType == ownType);
        var entries = field is { } catalogId2
            ? context.TaskFields.Where(f => f.FieldId == catalogId2 || (f.OwnKey == key && f.OwnType == ownType))
            : context.TaskFields.Where(f => f.OwnKey == key && f.OwnType == ownType);
        // Сравнение: int и float — по числовому столбцу, date — по тексту yyyy-MM-dd (сортируется как текст).
        return condition.Operator switch
        {
            FieldOperator.Equal => query.Where(x => values.Any(v => v.TaskId == x.Id && v.Value == value)),
            FieldOperator.NotEqual => query.Where(x => !values.Any(v => v.TaskId == x.Id && v.Value == value)),
            FieldOperator.Set => query.Where(x => values.Any(v => v.TaskId == x.Id)),
            FieldOperator.Unset => query.Where(x => !values.Any(v => v.TaskId == x.Id)),
            FieldOperator.Attached => query.Where(x => typeIds.Contains(x.TypeId) || entries.Any(f => f.TaskId == x.Id)),
            FieldOperator.Detached => query.Where(x => !typeIds.Contains(x.TypeId) && !entries.Any(f => f.TaskId == x.Id)),
            FieldOperator.Greater when number != null => query.Where(x => values.Any(v => v.TaskId == x.Id && v.Number > number)),
            FieldOperator.GreaterOrEqual when number != null => query.Where(x => values.Any(v => v.TaskId == x.Id && v.Number >= number)),
            FieldOperator.Less when number != null => query.Where(x => values.Any(v => v.TaskId == x.Id && v.Number < number)),
            FieldOperator.LessOrEqual when number != null => query.Where(x => values.Any(v => v.TaskId == x.Id && v.Number <= number)),
            FieldOperator.Greater => query.Where(x => values.Any(v => v.TaskId == x.Id && string.Compare(v.Value, value) > 0)),
            FieldOperator.GreaterOrEqual => query.Where(x => values.Any(v => v.TaskId == x.Id && string.Compare(v.Value, value) >= 0)),
            FieldOperator.Less => query.Where(x => values.Any(v => v.TaskId == x.Id && string.Compare(v.Value, value) < 0)),
            FieldOperator.LessOrEqual => query.Where(x => values.Any(v => v.TaskId == x.Id && string.Compare(v.Value, value) <= 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(condition), condition.Operator, null)
        };
    }

    public async Task<OwnFieldKind[]> GetOwnFieldKinds(Guid projectId, string nameKey, CancellationToken ct = default)
    {
        var kinds = await context.TaskFields
            .AsNoTracking()
            .Where(f => f.OwnKey == nameKey && context.Tasks.Any(t => t.Id == f.TaskId && t.ProjectId == projectId))
            .Select(f => new { f.OwnType, f.OwnEnumId })
            .Distinct()
            .ToArrayAsync(ct);
        return kinds.Select(x => new OwnFieldKind((Core.Fields.FieldType)x.OwnType!.Value, x.OwnEnumId)).ToArray();
    }

    public async Task<string> Add(TaskItem task, CancellationToken ct = default)
    {
        var model = new TaskDbModel
        {
            Id = task.Id,
            ProjectId = task.ProjectId,
            Title = task.Title,
            Description = task.Description,
            TypeId = task.TypeId,
            StatusId = task.StatusId,
            CreatedAt = DbTime.ToDb(task.CreatedAt),
            UpdatedAt = DbTime.ToDb(task.UpdatedAt),
            Version = DbVersion.Initial
        };
        var numbers = NumberModels(task);
        var links = LinkModels(task);
        var (fields, values) = FieldModels(task);
        context.Tasks.Add(model);
        context.TaskSeriesNumbers.AddRange(numbers);
        context.TaskLinks.AddRange(links);
        context.TaskFields.AddRange(fields);
        context.TaskFieldValues.AddRange(values);
        try
        {
            await context.SaveChangesAsync(ct);
        }
        finally
        {
            // Отвергнутая (например, уникальным индексом номеров) запись не должна висеть в трекере:
            // иначе следующее сохранение на этом контексте повторит её вставку.
            context.Entry(model).State = EntityState.Detached;
            Detach(numbers);
            Detach(links);
            Detach(fields, values);
        }
        return DbVersion.ToText(DbVersion.Initial);
    }

    public async Task<string?> Update(TaskItem task, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return null;

        // Строка задачи и её номера меняются вместе. Внутри IWriteScope транзакция уже открыта — берём её.
        var own = context.Database.CurrentTransaction == null
            ? await context.Database.BeginTransactionAsync(ct)
            : null;
        await using (own)
        {
            var updated = await context.Tasks
                .Where(x => x.ProjectId == task.ProjectId && x.Id == task.Id && x.Version == expected)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Title, task.Title)
                    .SetProperty(x => x.Description, task.Description)
                    .SetProperty(x => x.TypeId, task.TypeId)
                    .SetProperty(x => x.StatusId, task.StatusId)
                    .SetProperty(x => x.UpdatedAt, DbTime.ToDb(task.UpdatedAt))
                    .SetProperty(x => x.Version, expected + 1), ct);
            if (updated == 0)
                return null;

            await context.TaskSeriesNumbers.Where(x => x.TaskId == task.Id).ExecuteDeleteAsync(ct);
            await context.TaskLinks.Where(x => x.TaskId == task.Id).ExecuteDeleteAsync(ct);
            await context.TaskFieldValues.Where(x => x.TaskId == task.Id).ExecuteDeleteAsync(ct);
            await context.TaskFields.Where(x => x.TaskId == task.Id).ExecuteDeleteAsync(ct);
            var numbers = NumberModels(task);
            var links = LinkModels(task);
            var (fields, values) = FieldModels(task);
            if (numbers.Length > 0 || links.Length > 0 || fields.Length > 0)
            {
                context.TaskSeriesNumbers.AddRange(numbers);
                context.TaskLinks.AddRange(links);
                context.TaskFields.AddRange(fields);
                context.TaskFieldValues.AddRange(values);
                try
                {
                    await context.SaveChangesAsync(ct);
                }
                finally
                {
                    Detach(numbers);
                    Detach(links);
                    Detach(fields, values);
                }
            }

            if (own != null)
                await own.CommitAsync(ct);
            return DbVersion.ToText(expected + 1);
        }
    }

    public async Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return false;

        return await context.Tasks
            .Where(x => x.ProjectId == projectId && x.Id == id && x.Version == expected)
            .ExecuteDeleteAsync(ct) > 0;
    }

    private static TaskSeriesNumberDbModel[] NumberModels(TaskItem task) => task.SeriesNumbers
        .DistinctBy(x => x.SeriesId)
        .Select(x => new TaskSeriesNumberDbModel
        {
            TaskId = task.Id,
            ProjectId = task.ProjectId,
            SeriesId = x.SeriesId,
            Number = x.Number
        })
        .ToArray();

    private static TaskLinkDbModel[] LinkModels(TaskItem task) => task.Links
        .Distinct()
        .Select(x => new TaskLinkDbModel { TaskId = task.Id, TypeId = x.TypeId, TargetId = x.TargetId })
        .ToArray();

    private static (TaskFieldDbModel[] Fields, TaskFieldValueDbModel[] Values) FieldModels(TaskItem task)
    {
        var fields = task.Fields
            .DistinctBy(x => x.FieldId)
            .Select((x, i) => new TaskFieldDbModel
            {
                TaskId = task.Id,
                FieldId = x.FieldId,
                Position = i,
                OwnName = x.Own?.Name,
                OwnKey = x.Own == null ? null : FieldNames.Key(x.Own.Name),
                OwnType = x.Own == null ? null : (int)x.Own.Type,
                OwnRequired = x.Own?.Required ?? false,
                OwnMultiple = x.Own?.Multiple ?? false,
                OwnEnumId = x.Own?.EnumId
            })
            .ToArray();

        var values = task.Fields
            .DistinctBy(x => x.FieldId)
            .SelectMany(x => x.Values.Select((value, i) => new TaskFieldValueDbModel
            {
                TaskId = task.Id, FieldId = x.FieldId, Position = i, Value = value, Number = FieldNumbers.Parse(value),
                OwnKey = x.Own == null ? null : FieldNames.Key(x.Own.Name), OwnType = x.Own == null ? null : (int)x.Own.Type
            }))
            .ToArray();
        return (fields, values);
    }

    private void Detach(TaskFieldDbModel[] fields, TaskFieldValueDbModel[] values)
    {
        foreach (var field in fields)
            context.Entry(field).State = EntityState.Detached;
        foreach (var value in values)
            context.Entry(value).State = EntityState.Detached;
    }

    private void Detach(IEnumerable<TaskLinkDbModel> links)
    {
        foreach (var link in links)
            context.Entry(link).State = EntityState.Detached;
    }

    // Строки номеров не нужны трекеру ни после записи, ни после отказа: следующее сохранение той же задачи добавит их заново.
    private void Detach(IEnumerable<TaskSeriesNumberDbModel> numbers)
    {
        foreach (var number in numbers)
            context.Entry(number).State = EntityState.Detached;
    }

    /// <summary>Задачи с их номерами в сериях (по порядку серий) — одним дополнительным запросом на всю пачку.</summary>
    private async Task<TaskItem[]> Map(TaskDbModel[] models, CancellationToken ct)
    {
        if (models.Length == 0)
            return [];

        var ids = models.Select(x => x.Id).ToArray();
        var numbers = (await context.TaskSeriesNumbers
                .AsNoTracking()
                .Where(x => ids.Contains(x.TaskId))
                .OrderBy(x => x.SeriesId)
                .ToArrayAsync(ct))
            .ToLookup(x => x.TaskId, x => new Core.TaskSeries.TaskSeriesNumber(x.SeriesId, x.Number));

        // Связи — по порядку (тип, цель): порядок в файле и в БД может отличаться, список для клиента стабилен.
        var links = (await context.TaskLinks
                .AsNoTracking()
                .Where(x => ids.Contains(x.TaskId))
                .OrderBy(x => x.TypeId)
                .ThenBy(x => x.TargetId)
                .ToArrayAsync(ct))
            .ToLookup(x => x.TaskId, x => new Core.Links.TaskLink(x.TypeId, x.TargetId));

        // Поля задач — в порядке записи, значения каждого поля — в своём порядке.
        var fieldModels = (await context.TaskFields
                .AsNoTracking()
                .Where(x => ids.Contains(x.TaskId))
                .OrderBy(x => x.Position)
                .ToArrayAsync(ct))
            .ToLookup(x => x.TaskId);
        var valueModels = (await context.TaskFieldValues
                .AsNoTracking()
                .Where(x => ids.Contains(x.TaskId))
                .OrderBy(x => x.Position)
                .ToArrayAsync(ct))
            .ToLookup(x => (x.TaskId, x.FieldId), x => x.Value);

        return models.Select(model => Map(
            model, numbers[model.Id].ToArray(), links[model.Id].ToArray(),
            fieldModels[model.Id].Select(f => new Core.Tasks.TaskField(
                f.FieldId,
                valueModels[(f.TaskId, f.FieldId)].ToArray(),
                f.OwnName == null
                    ? null
                    : new OwnField(f.OwnName, (Core.Fields.FieldType)(f.OwnType ?? 0), f.OwnRequired, f.OwnMultiple, f.OwnEnumId))).ToArray()))
            .ToArray();
    }

    private static TaskItem Map(
        TaskDbModel model, Core.TaskSeries.TaskSeriesNumber[] numbers, Core.Links.TaskLink[] links, Core.Tasks.TaskField[] fields) => new()
    {
        Id = model.Id,
        ProjectId = model.ProjectId,
        Title = model.Title,
        Description = model.Description,
        TypeId = model.TypeId,
        StatusId = model.StatusId,
        SeriesNumbers = numbers,
        Links = links,
        Fields = fields,
        CreatedAt = DbTime.FromDb(model.CreatedAt),
        UpdatedAt = DbTime.FromDb(model.UpdatedAt),
        Version = DbVersion.ToText(model.Version)
    };
}
