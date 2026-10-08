using Tasker.Core.Dto;
using Tasker.Core.Fields;
using Tasker.Core.Statuses;

namespace Tasker.Core.Tasks;

/// <param name="StatusId">Статус из набора типа задачи; null — первый статус набора.</param>
/// <param name="SeriesIds">Серии проекта, в которые сразу включить задачу (по номеру в каждой); null или пусто — без серий.</param>
/// <param name="Fields">Значения полей и дополнительные поля; обязательные поля типа и собственные обязательные должны получить значения.
/// Убирать поля (<see cref="TaskFieldChanges.RemoveFields"/>) у новой задачи нечего.</param>
public record CreateTask(string Title, string? Description, Guid TypeId, Guid? StatusId, Guid[]? SeriesIds = null, TaskFieldChanges? Fields = null);

/// <summary>
/// Поля null — не меняются. Description: пустая строка — очистить.
/// При смене типа статус должен быть в наборе нового типа — иначе нужно передать и StatusId.
/// Version — версия, которую видел клиент (см. <see cref="Versioning"/>).
/// <para>
/// Fields — правка полей задачи (см. <see cref="TaskFieldChanges"/>). Смена типа: поля прежнего типа, у которых есть значения,
/// остаются дополнительными полями задачи (значение поля, общего у двух типов, сохраняется); обязательные поля нового типа
/// должны получить значения в этой же правке.
/// </para>
/// <para>
/// Обязательные поля проверяются, когда правка меняет содержимое задачи: заголовок, описание, тип или поля. Правка, которая меняет
/// только статус (перенос по доске, смена статуса), их не проверяет: пропущенное обязательное поле не мешает переносу.
/// </para>
/// </summary>
public record UpdateTask(string? Title, string? Description, Guid? TypeId, Guid? StatusId, string? Version, TaskFieldChanges? Fields = null);

public partial class TaskService(
    ITaskStorage tasks,
    ITaskTypeStorage types,
    IStatusSetStorage sets,
    TimeProvider time,
    TaskSeries.ISeriesStorage seriesStorage,
    TaskSeries.IWriteScope writes,
    Locks.EditLockService locks,
    IFieldStorage fields,
    IFieldEnumStorage enums,
    Links.TaskLinkService links,
    IStatusStorage statuses)
{
    public const int MaxTitleLength = 500;

    public async Task<TaskItem> Create(Guid projectId, CreateTask command, CancellationToken ct = default)
    {
        var title = Validate.Name(command.Title, "Title", MaxTitleLength);
        var (type, set) = await GetType(projectId, command.TypeId, ct);

        var statusId = command.StatusId ?? set.StatusIds.FirstOrDefault();
        EnsureStatusInSet(statusId, type, set);

        var seriesIds = Validate.Distinct(command.SeriesIds, "SeriesIds");
        if (command.Fields?.RemoveFields is { Length: > 0 })
            throw new TaskerValidationException("RemoveFields: a new task has no fields to remove");

        var now = time.GetUtcNow();
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = title,
            Description = Validate.Description(command.Description),
            TypeId = type.Id,
            StatusId = statusId,
            CreatedAt = now,
            UpdatedAt = now,
            Version = Versioning.New
        };

        // Поля проверяются и задача записывается в одной атомарной секции: перечисление, на которое ссылается собственное поле,
        // и поле каталога нельзя удалить между проверкой и записью. Так же выбираются номера серий.
        if (seriesIds.Length == 0 && command.Fields == null && !type.Fields.Any(x => x.Required))
            return task with { Version = await tasks.Add(task, ct) };

        return await writes.Exclusive(projectId, async () =>
        {
            var withFields = task with { Fields = await BuildFields(projectId, task, type, command.Fields, contentChanged: true, ct) };

            var numbers = new List<TaskSeries.TaskSeriesNumber>();
            foreach (var seriesId in seriesIds)
            {
                if (await seriesStorage.GetById(projectId, seriesId, ct) == null)
                    throw new TaskerValidationException($"SeriesIds: not found in the project: {seriesId}");
                numbers.Add(new TaskSeries.TaskSeriesNumber(seriesId, await tasks.GetMaxNumber(projectId, seriesId, ct) + 1));
            }

            var withNumbers = withFields with { SeriesNumbers = numbers };
            return withNumbers with { Version = await tasks.Add(withNumbers, ct) };
        }, ct);
    }

    /// <returns>null — задачи нет.</returns>
    public async Task<TaskItem?> Update(Guid projectId, Guid id, UpdateTask command, CancellationToken ct = default)
    {
        var task = await tasks.GetById(projectId, id, ct);
        if (task == null)
            return null;
        await locks.EnsureWritable(Locks.LockedEntity.Task, task.Id, Subject(task), ct);
        var expected = Versioning.Check(task.Version, command.Version, Subject(task));

        var (type, set) = await GetType(projectId, command.TypeId ?? task.TypeId, ct);
        var statusId = command.StatusId ?? task.StatusId;
        EnsureStatusInSet(statusId, type, set);

        var updated = task with
        {
            Title = command.Title == null ? task.Title : Validate.Name(command.Title, "Title", MaxTitleLength),
            Description = command.Description == null ? task.Description : Validate.Description(command.Description),
            TypeId = type.Id,
            StatusId = statusId,
            UpdatedAt = time.GetUtcNow()
        };

        // Правка только статуса (перенос по доске, смена статуса) содержимого не меняет и обязательные поля не проверяет.
        var contentChanged = command.Title != null || command.Description != null || command.Fields != null || type.Id != task.TypeId;

        async Task<TaskItem> Write()
        {
            var withFields = contentChanged
                ? updated with { Fields = await BuildFields(projectId, task, type, command.Fields, contentChanged, ct) }
                : updated;

            var version = await tasks.Update(withFields, expected, ct) ?? throw Versioning.Modified(Subject(task));
            return withFields with { Version = version };
        }

        // Правка полей — в атомарной секции записи (см. Create).
        return command.Fields != null ? await writes.Exclusive(projectId, Write, ct) : await Write();
    }

    /// <summary>
    /// Поля задачи после правки: применённая правка, значения проверены по типам, обязательные поля заполнены (если правка меняет содержимое).
    /// Каталог и перечисления читаются, только если они нужны: правка заголовка задачи без полей их не трогает.
    /// </summary>
    private async Task<IReadOnlyList<TaskField>> BuildFields(
        Guid projectId, TaskItem task, TaskType type, TaskFieldChanges? changes, bool contentChanged, CancellationToken ct)
    {
        var needsRequiredCheck = contentChanged && (type.Fields.Any(x => x.Required) || task.Fields.Any(x => x.Own is { Required: true }));
        if (changes == null && !needsRequiredCheck)
            return TaskFieldRules.Normalize(task.Fields, type);

        var catalog = (await fields.GetAll(projectId, ct)).ToDictionary(x => x.Id);
        var enumerations = changes == null ? new Dictionary<Guid, FieldEnum>() : (await enums.GetAll(projectId, ct)).ToDictionary(x => x.Id);

        var entries = TaskFieldRules.Normalize(TaskFieldRules.Apply(task.Fields, type, catalog, enumerations, changes), type);

        if (contentChanged)
        {
            var missing = TaskFieldRules.MissingRequired(entries, type, catalog);
            if (missing.Length > 0)
                throw new TaskerRequiredFieldsException(missing);
        }
        return entries;
    }

    /// <summary>
    /// Поля задачи с определениями: поля её типа, затем дополнительные и собственные. Поля, которых нет в каталоге
    /// (удалены в другой ветке), пропускаются.
    /// </summary>
    /// <returns>null — задачи нет.</returns>
    public async Task<TaskFieldView[]?> GetFields(Guid projectId, Guid taskId, CancellationToken ct = default)
    {
        var task = await tasks.GetById(projectId, taskId, ct);
        return task == null ? null : await GetFields(projectId, task, ct);
    }

    /// <summary>То же для уже прочитанной задачи (например, из списка).</summary>
    public async Task<TaskFieldView[]> GetFields(Guid projectId, TaskItem task, CancellationToken ct = default)
    {
        var type = await types.GetById(projectId, task.TypeId, ct)
            ?? throw new TaskerNotFoundException($"Task type {task.TypeId} of task {task.Id} not found (the project may have been deleted)");
        if (type.Fields.Count == 0 && task.Fields.Count == 0)
            return [];

        var catalog = (await fields.GetAll(projectId, ct)).ToDictionary(x => x.Id);
        var enumerations = (await enums.GetAll(projectId, ct)).ToDictionary(x => x.Id);

        return TaskFieldRules.Resolve(task.Fields, type, catalog)
            .Select(x => new TaskFieldView(
                x.Id, x.Name, x.Type, x.Multiple, x.EnumId, x.Required, x.Source, x.Values,
                FieldValues.Texts(x.Type, x.EnumId is { } enumId ? enumerations.GetValueOrDefault(enumId) : null, x.Values)))
            .ToArray();
    }

    /// <summary>
    /// Условия по полям из текста клиента (имя поля — без учёта регистра; оно стоит до первого из знаков <c>= ! &lt; &gt; :</c>, которых
    /// в именах нет). После имени:
    /// <list type="bullet">
    /// <item><c>=</c>, <c>!=</c> — равенство и «ни одно значение не равно» (подходят и задачи без значения), для всех типов; значение — как при записи
    /// (у enum — id или название);</item>
    /// <item><c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>, <c>&lt;=</c> — только int, float и date;</item>
    /// <item><c>:set</c>, <c>:unset</c> — у поля есть значение или нет (поле, не подключённое к задаче, значения не имеет); <c>:attached</c>, <c>:detached</c> —
    /// поле подключено к задаче (у поля каталога: входит в её тип или добавлено ей; у собственного поля: оно есть у задачи) или нет.</item>
    /// </list>
    /// Значение разбирается после знака целиком, поэтому <c>Note=x:y</c> — значение «x:y». Условия объединяются по И.
    /// <para>
    /// Имя ищется среди полей каталога и среди собственных полей задач (<see cref="OwnField"/>). Есть поле каталога — значение разбирается по его типу, и
    /// условию подходят также собственные поля с этим именем и <b>тем же типом</b> (с другим типом — пропускаются). Поля каталога нет — смотрятся собственные
    /// поля задач проекта с этим именем: при одном типе значение разбирается по нему, при нескольких — ошибка со списком типов (указать тип в условии
    /// пока нельзя). Поля каталога с недопустимым нынешним правилом именем (созданные раньше) находятся по имени, если в нём нет знаков <c>= ! &lt; &gt; :</c>.
    /// </para>
    /// </summary>
    /// <returns>null — условий нет.</returns>
    /// <exception cref="TaskerValidationException">Нет знака или слова после имени, нет такого поля, у собственных полей несколько типов, оператор не подходит типу поля, значение не подходит типу.</exception>
    public async Task<FieldCondition[]?> ParseFieldFilters(Guid projectId, IEnumerable<string>? expressions, CancellationToken ct = default)
    {
        var list = expressions?.ToArray();
        if (list is not { Length: > 0 })
            return null;

        var catalog = await fields.GetAll(projectId, ct);
        TaskType[]? taskTypes = null;
        var result = new List<FieldCondition>();
        foreach (var expression in list)
        {
            var at = expression.IndexOfAny(OperatorChars);
            if (at <= 0)
                throw new TaskerValidationException(
                    $"Field filter '{expression}': expected 'Name=value' (also !=, >, >=, <, <=) or 'Name:set|unset|attached|detached'");
            var name = expression[..at].Trim();
            var target = await ResolveFilterField(projectId, expression, name, catalog, ct);

            if (expression[at] == ':')
            {
                var word = expression[(at + 1)..].Trim();
                var presence = word.ToLowerInvariant() switch
                {
                    "set" => FieldOperator.Set,
                    "unset" => FieldOperator.Unset,
                    "attached" => FieldOperator.Attached,
                    "detached" => FieldOperator.Detached,
                    _ => throw new TaskerValidationException($"Field filter '{expression}': unknown ':{word}' (use :set, :unset, :attached or :detached)")
                };
                Guid[]? typeIds = null;
                if (presence is FieldOperator.Attached or FieldOperator.Detached && target.FieldId is { } fieldId)
                {
                    taskTypes ??= await types.GetAll(projectId, ct);
                    typeIds = taskTypes.Where(x => x.Fields.Any(f => f.FieldId == fieldId)).Select(x => x.Id).ToArray();
                }
                result.Add(new FieldCondition(target.Name, target.Type, presence, FieldId: target.FieldId, TypeIds: typeIds));
                continue;
            }

            result.Add(await ParseComparison(projectId, expression, target, at, ct));
        }
        return result.ToArray();
    }

    /// <summary>Поле условия: имя, тип, по которому разбирается значение, перечисление и поле каталога (если оно есть).</summary>
    private record FilterField(string Name, FieldType Type, Guid? EnumId, Guid? FieldId);

    private async Task<FilterField> ResolveFilterField(
        Guid projectId, string expression, string name, IReadOnlyCollection<FieldDefinition> catalog, CancellationToken ct)
    {
        if (catalog.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is { } field)
            return new FilterField(field.Name, field.Type, field.EnumId, field.Id);

        var kinds = await tasks.GetOwnFieldKinds(projectId, FieldNames.Key(name), ct);
        return kinds.Length switch
        {
            0 => throw new TaskerValidationException(
                $"Field filter '{expression}': no field '{name}' in the project's catalog and no task has an own field with this name"),
            1 => new FilterField(name, kinds[0].Type, kinds[0].EnumId, null),
            _ => throw new TaskerValidationException(
                $"Field filter '{expression}': tasks have own fields '{name}' of several types ({string.Join(", ", await KindNames(projectId, kinds, ct))}) " +
                "and the catalog has no such field, so the value cannot be read")
        };
    }

    private async Task<string[]> KindNames(Guid projectId, OwnFieldKind[] kinds, CancellationToken ct)
    {
        var enumerations = kinds.Any(x => x.EnumId != null) ? await enums.GetAll(projectId, ct) : [];
        return kinds
            .Select(x => x.EnumId is { } id && enumerations.FirstOrDefault(e => e.Id == id) is { } e
                ? $"enum '{e.Name}'"
                : x.Type.ToString().ToLowerInvariant())
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static readonly char[] OperatorChars = ['=', '!', '<', '>', ':'];

    // Двузнаковые раньше однозначных: «>=» не должно читаться как «>» со значением «=…».
    private static readonly (string Text, FieldOperator Operator)[] Operators =
    [
        ("!=", FieldOperator.NotEqual), (">=", FieldOperator.GreaterOrEqual), ("<=", FieldOperator.LessOrEqual),
        ("=", FieldOperator.Equal), (">", FieldOperator.Greater), ("<", FieldOperator.Less)
    ];

    private async Task<FieldCondition> ParseComparison(Guid projectId, string expression, FilterField field, int at, CancellationToken ct)
    {
        var (text, op) = Operators.FirstOrDefault(x => string.CompareOrdinal(expression, at, x.Text, 0, x.Text.Length) == 0);
        if (text == null)
            throw new TaskerValidationException($"Field filter '{expression}': expected one of = != > >= < <= after the name '{field.Name}'");

        var ordered = op is FieldOperator.Greater or FieldOperator.GreaterOrEqual or FieldOperator.Less or FieldOperator.LessOrEqual;
        if (ordered && field.Type is not (FieldType.Int or FieldType.Float or FieldType.Date))
            throw new TaskerValidationException(
                $"Field filter '{expression}': '{text}' does not apply to field '{field.Name}' of type {field.Type.ToString().ToLowerInvariant()} " +
                "(only int, float and date can be compared; use = or !=)");

        FieldEnum? enumeration = null;
        if (field.EnumId is { } enumId)
            enumeration = await enums.GetById(projectId, enumId, ct);
        var value = FieldValues.Normalize(field.Name, field.Type, false, enumeration, [expression[(at + text.Length)..]])[0];
        double? number = ordered && field.Type is (FieldType.Int or FieldType.Float) ? FieldNumbers.Parse(value) : null;
        return new FieldCondition(field.Name, field.Type, op, value, number, field.FieldId);
    }

    /// <summary>
    /// Страница задач проекта по фильтру и условиям по полям (см. <see cref="ParseFieldFilters"/>) — для списков клиентам.
    /// <paramref name="descriptionLength"/>: -1 — полное описание, 0 — без описания, N — первые N символов (см. <see cref="DescriptionPreview"/>).
    /// <paramref name="sort"/> — порядок текстом клиента (<see cref="ParseSort"/>: <c>status,-updated,Estimate</c>); null или пусто — по умолчанию (по времени создания).
    /// Порядок не меняет ни отбор, ни <c>TotalCount</c>.
    /// </summary>
    public async Task<ListDto<TaskListItem>> List(
        Guid projectId, TaskFilter? filter, IEnumerable<string>? fieldFilters, Page page, int descriptionLength = DescriptionPreview.Full,
        CancellationToken ct = default, string? sort = null)
    {
        DescriptionPreview.Check(descriptionLength);
        filter = await ListFilter(projectId, filter, fieldFilters, sort, ct);
        var found = await tasks.GetRange(projectId, filter, page, ct);
        return new ListDto<TaskListItem>
        {
            TotalCount = found.TotalCount, Offset = found.Offset, Limit = found.Limit,
            Data = await Preview(projectId, found.Data, descriptionLength, ct)
        };
    }

    /// <summary>Фильтр списка клиента: заданный фильтр, условия по полям (<see cref="ParseFieldFilters"/>) и порядок (<see cref="ParseSort"/>) вместе.</summary>
    private async Task<TaskFilter?> ListFilter(Guid projectId, TaskFilter? filter, IEnumerable<string>? fieldFilters, string? sort, CancellationToken ct)
    {
        var conditions = await ParseFieldFilters(projectId, fieldFilters, ct);
        var keys = await ParseSort(projectId, sort, ct);
        if (conditions == null && keys == null)
            return filter;

        return new TaskFilter
        {
            Ids = filter?.Ids,
            TypeIds = filter?.TypeIds, StatusIds = filter?.StatusIds, SeriesIds = filter?.SeriesIds,
            LinkTypeIds = filter?.LinkTypeIds, FieldIds = filter?.FieldIds, EnumIds = filter?.EnumIds,
            // Условия, уже заданные фильтром (например, условия колонки доски), и условия клиента — вместе, по И.
            FieldValues = conditions == null ? filter?.FieldValues : [.. filter?.FieldValues ?? [], .. conditions],
            Sort = keys ?? filter?.Sort
        };
    }

    /// <summary>
    /// Список задач деревом: эпик (задача с дочерними по иерархическим связям, <see cref="Links.LinkType.Hierarchical"/>) и под ним его дочерние задачи, и так вглубь
    /// (<see cref="TaskHierarchy"/>). Правила:
    /// <list type="bullet">
    /// <item>Фильтры (тип, статус, серия, поля) действуют на каждую задачу отдельно. Дочерняя задача, подошедшая под фильтр, при неподходящем эпике
    /// идёт на верхнем уровне как обычная; у задачи с несколькими родителями — под каждым подошедшим, на верхний уровень — только если не подошёл ни один.</item>
    /// <item>Задача с несколькими родителями показывается под каждым (<see cref="TaskTreeItem.Repeated"/>); <see cref="TaskTreeList.TotalCount"/> считает уникальные задачи.</item>
    /// <item><paramref name="page"/> — страница верхнего уровня: поддеревья показываются целиком и не разрываются между страницами.</item>
    /// <item>Порядок (<paramref name="sort"/>, как в <see cref="List"/>) — и на верхнем уровне, и среди дочерних каждого родителя.</item>
    /// </list>
    /// Загрузка: id найденных задач в порядке списка (одним запросом), иерархические связи проекта (id, одним запросом на тип) и затем только показанные задачи;
    /// все задачи проекта в память не читаются. Нет иерархических связей — это обычная страница <see cref="List"/> (все строки на верхнем уровне).
    /// </summary>
    public async Task<TaskTreeList> ListTree(
        Guid projectId, TaskFilter? filter, IEnumerable<string>? fieldFilters, Page page, int descriptionLength = DescriptionPreview.Full,
        CancellationToken ct = default, string? sort = null)
    {
        DescriptionPreview.Check(descriptionLength);
        filter = await ListFilter(projectId, filter, fieldFilters, sort, ct);
        var hierarchy = await LoadHierarchy(projectId, ct);

        if (hierarchy.IsEmpty)
        {
            var flat = await tasks.GetRange(projectId, filter, page, ct);
            return new TaskTreeList
            {
                TotalCount = flat.TotalCount, TopLevelCount = flat.TotalCount, Offset = flat.Offset, Limit = flat.Limit,
                Data = (await Preview(projectId, flat.Data, descriptionLength, hierarchy, ct)).Select(x => new TaskTreeItem(x, 0, false)).ToArray()
            };
        }

        var ordered = await tasks.GetIds(projectId, filter, ct);
        var top = hierarchy.TopLevel(ordered);
        var position = new Dictionary<Guid, int>(ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
            position[ordered[i]] = i;

        var rows = hierarchy.Rows(top.Skip(page.Offset).Take(page.Limit), position);
        var loaded = new Dictionary<Guid, TaskItem>();
        foreach (var chunk in rows.Select(x => x.Id).Distinct().Chunk(500))
        {
            foreach (var task in await tasks.GetAll(projectId, new TaskFilter { Ids = chunk }, ct))
                loaded[task.Id] = task;
        }

        var items = (await Preview(projectId, loaded.Values, descriptionLength, hierarchy, ct)).ToDictionary(x => x.Id);
        return new TaskTreeList
        {
            TotalCount = ordered.Length, TopLevelCount = top.Count, Offset = page.Offset, Limit = page.Limit,
            // Задачу, удалённую между запросами, пропускаем вместе с её строкой.
            Data = rows.Where(x => items.ContainsKey(x.Id)).Select(x => new TaskTreeItem(items[x.Id], x.Depth, x.Repeated)).ToArray()
        };
    }

    /// <summary>Иерархические связи проекта: пусто, если иерархических типов нет или связей по ним ещё нет.</summary>
    private async Task<TaskHierarchy> LoadHierarchy(Guid projectId, CancellationToken ct)
    {
        var edges = new List<Links.LinkEdge>();
        foreach (var typeId in await links.HierarchicalTypeIds(projectId, ct))
            edges.AddRange(await tasks.GetLinkEdges(projectId, typeId, ct));
        return edges.Count == 0 ? TaskHierarchy.Empty : new TaskHierarchy(edges);
    }

    /// <summary>
    /// Задачи в виде записей списка (см. <see cref="TaskListItem"/>): описание усечено до <paramref name="descriptionLength"/>
    /// (-1 — полное, 0 — без), плюс число связей — исходящие и входящие и иерархия (<see cref="TaskListItem.ParentIds"/>, <see cref="TaskListItem.ChildCount"/>).
    /// Входящие связи и иерархия считаются запросами на весь список, а не на задачу.
    /// Единственное место усечения: списки MCP, REST и консоли идут через него.
    /// </summary>
    public async Task<TaskListItem[]> Preview(Guid projectId, IReadOnlyCollection<TaskItem> items, int descriptionLength, CancellationToken ct = default)
    {
        DescriptionPreview.Check(descriptionLength);
        var hierarchy = await LoadHierarchy(projectId, ct);
        return await Preview(projectId, items, descriptionLength, hierarchy, ct);
    }

    private async Task<TaskListItem[]> Preview(
        Guid projectId, IReadOnlyCollection<TaskItem> items, int descriptionLength, TaskHierarchy hierarchy, CancellationToken ct)
    {
        var inbound = await tasks.CountLinkedTo(projectId, items.Select(x => x.Id).ToArray(), ct);
        return items
            .Select(x => new TaskListItem(x, descriptionLength, x.Links.Count + inbound.GetValueOrDefault(x.Id), hierarchy.ParentsOf(x.Id), hierarchy.ChildCount(x.Id)))
            .ToArray();
    }

    /// <summary>Задача с видом её полей (см. <see cref="TaskDetails"/>).</summary>
    public async Task<TaskDetails> Describe(Guid projectId, TaskItem task, CancellationToken ct = default)
    {
        var fieldViews = await GetFields(projectId, task, ct);
        var all = await links.GetLinks(projectId, task, ct);
        return new TaskDetails(task, fieldViews, all.Take(Links.TaskLinkService.MaxViewed).ToArray(), all.Length);
    }

    /// <returns>null — задачи нет.</returns>
    public async Task<TaskDetails?> Describe(Guid projectId, Guid taskId, CancellationToken ct = default) =>
        await tasks.GetById(projectId, taskId, ct) is { } task ? await Describe(projectId, task, ct) : null;

    /// <summary>На задачи ничего не ссылается, поэтому удаление без проверок ссылок.</summary>
    /// <returns>false — задачи нет.</returns>
    public async Task<bool> Delete(Guid projectId, Guid id, string? version, CancellationToken ct = default)
    {
        var task = await tasks.GetById(projectId, id, ct);
        if (task == null)
            return false;
        await locks.EnsureWritable(Locks.LockedEntity.Task, task.Id, Subject(task), ct);
        var expected = Versioning.Check(task.Version, version, Subject(task));

        if (!await tasks.Delete(projectId, id, expected, ct))
            throw Versioning.Modified(Subject(task));
        await locks.Forget(Locks.LockedEntity.Task, task.Id, ct);
        await Links.TaskLinks.RemoveInbound(tasks, time, projectId, task.Id, ct);
        return true;
    }

    private static string Subject(TaskItem task) => $"Task '{task.Title}'";

    private async Task<(TaskType Type, StatusSet Set)> GetType(Guid projectId, Guid typeId, CancellationToken ct)
    {
        var type = await types.GetById(projectId, typeId, ct)
            ?? throw new TaskerValidationException($"TypeId: not found in the project: {typeId}");

        // Набор типа всегда в том же проекте — это проверяется при создании и изменении типа.
        var set = await sets.GetById(projectId, type.StatusSetId, ct)
            ?? throw new TaskerNotFoundException($"Status set {type.StatusSetId} of task type {type.Id} not found (the project may have been deleted)");

        return (type, set);
    }

    private static void EnsureStatusInSet(Guid statusId, TaskType type, StatusSet set)
    {
        if (!set.StatusIds.Contains(statusId))
            throw new TaskerValidationException($"StatusId: status {statusId} is not in the status set of task type '{type.Name}'");
    }

    // ---- серии и номера ----

    /// <summary>
    /// Включает задачу в серию: она получает следующий номер (максимум серии + 1) под <see cref="TaskSeries.IWriteScope"/>.
    /// Уже в этой серии — возвращает задачу как есть.
    /// </summary>
    /// <returns>null — нет задачи или серии.</returns>
    public Task<TaskItem?> AddToSeries(Guid projectId, Guid taskId, Guid seriesId, string? version, CancellationToken ct = default) =>
        writes.Exclusive<TaskItem?>(projectId, async () =>
        {
            var task = await tasks.GetById(projectId, taskId, ct);
            if (task == null || await seriesStorage.GetById(projectId, seriesId, ct) == null)
                return null;
            await locks.EnsureWritable(Locks.LockedEntity.Task, task.Id, Subject(task), ct);
            var expected = Versioning.Check(task.Version, version, Subject(task));

            if (task.SeriesNumbers.Any(x => x.SeriesId == seriesId))
                return task;

            var number = await tasks.GetMaxNumber(projectId, seriesId, ct) + 1;
            return await Save(task, [.. task.SeriesNumbers, new TaskSeries.TaskSeriesNumber(seriesId, number)], expected, ct);
        }, ct);

    /// <summary>Убирает задачу из серии (её номер освобождается). Не в серии — возвращает задачу как есть.</summary>
    /// <returns>null — задачи нет.</returns>
    public Task<TaskItem?> RemoveFromSeries(Guid projectId, Guid taskId, Guid seriesId, string? version, CancellationToken ct = default) =>
        writes.Exclusive<TaskItem?>(projectId, async () =>
        {
            var task = await tasks.GetById(projectId, taskId, ct);
            if (task == null)
                return null;
            await locks.EnsureWritable(Locks.LockedEntity.Task, task.Id, Subject(task), ct);
            var expected = Versioning.Check(task.Version, version, Subject(task));

            if (task.SeriesNumbers.All(x => x.SeriesId != seriesId))
                return task;

            return await Save(task, task.SeriesNumbers.Where(x => x.SeriesId != seriesId).ToArray(), expected, ct);
        }, ct);

    /// <summary>
    /// Убирает из серии задачу с этим номером. Несколько задач с таким номером (дубликат) — ошибка со списком их id.
    /// </summary>
    /// <returns>null — в серии нет задачи с таким номером.</returns>
    public Task<TaskItem?> RemoveFromSeriesByNumber(Guid projectId, Guid seriesId, int number, CancellationToken ct = default) =>
        writes.Exclusive<TaskItem?>(projectId, async () =>
        {
            var found = await tasks.FindByNumber(projectId, seriesId, number, ct);
            if (found.Length == 0)
                return null;
            if (found.Length > 1)
                throw new TaskerValidationException(
                    $"Several tasks have number {number} in the series: {string.Join(", ", found.Select(x => x.Id))}; remove by task id instead");

            var task = found[0];
            await locks.EnsureWritable(Locks.LockedEntity.Task, task.Id, Subject(task), ct);
            return await Save(task, task.SeriesNumbers.Where(x => x.SeriesId != seriesId).ToArray(), task.Version, ct);
        }, ct);

    /// <summary>
    /// Меняет номер задачи в серии: <paramref name="to"/> — этот номер (он должен быть свободен), null — следующий свободный (максимум + 1).
    /// </summary>
    /// <returns>null — нет задачи, серии или задача не в серии.</returns>
    /// <exception cref="TaskerConflictException"><paramref name="to"/> меньше 1 или уже занят другой задачей.</exception>
    public Task<TaskItem?> Renumber(Guid projectId, Guid taskId, Guid seriesId, int? to, string? version, CancellationToken ct = default) =>
        writes.Exclusive<TaskItem?>(projectId, async () =>
        {
            var task = await tasks.GetById(projectId, taskId, ct);
            if (task == null || await seriesStorage.GetById(projectId, seriesId, ct) == null)
                return null;
            await locks.EnsureWritable(Locks.LockedEntity.Task, task.Id, Subject(task), ct);
            var expected = Versioning.Check(task.Version, version, Subject(task));

            var current = task.SeriesNumbers.FirstOrDefault(x => x.SeriesId == seriesId);
            if (current == null)
                return null;

            int number;
            if (to == null)
                number = await tasks.GetMaxNumber(projectId, seriesId, ct) + 1;
            else
            {
                number = to.Value;
                if (number < 1)
                    throw new TaskerConflictException($"Series number must be 1 or greater, got {number}");
                if ((await tasks.FindByNumber(projectId, seriesId, number, ct)).Any(x => x.Id != taskId))
                    throw new TaskerConflictException($"Number {number} is already taken in the series");
                if (number == current.Number)
                    return task;
            }

            var numbers = task.SeriesNumbers.Select(x => x.SeriesId == seriesId ? x with { Number = number } : x).ToArray();
            return await Save(task, numbers, expected, ct);
        }, ct);

    /// <summary>Задачи по ссылке: Guid — одна или ни одной; «ПРЕФИКС-номер» — все с этим номером (дубликат — несколько). Нет серии с таким префиксом — пусто.</summary>
    /// <param name="allowIdPrefix">
    /// Принимать и короткий id — первые 8 и более шестнадцатеричных символов Guid (<see cref="ShortId"/>): одна задача или ни одной.
    /// REST этого не разрешает, консоль и MCP — да.
    /// </param>
    /// <exception cref="TaskerValidationException">
    /// Ссылка не похожа ни на Guid, ни на «ПРЕФИКС-номер»; префикс id подходит нескольким задачам (в сообщении — их полные id).
    /// </exception>
    public async Task<TaskItem[]> Resolve(Guid projectId, string reference, CancellationToken ct = default, bool allowIdPrefix = false)
    {
        var parsed = TaskSeries.TaskReference.Parse(reference, allowIdPrefix);
        if (parsed.Id is { } id)
            return await tasks.GetById(projectId, id, ct) is { } task ? [task] : [];
        if (parsed.IdKey is { } key)
            return await FindByIdPrefix(projectId, key, ct);

        // Серий с одним префиксом должно быть не больше одной; если после слияния их несколько — ищем во всех.
        var found = new Dictionary<Guid, TaskItem>();
        foreach (var series in (await seriesStorage.GetAll(projectId, ct)).Where(x => x.Prefix == parsed.Prefix))
        foreach (var task in await tasks.FindByNumber(projectId, series.Id, parsed.Number!.Value, ct))
            found[task.Id] = task;

        return found.Values.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArray();
    }

    /// <summary>
    /// Id задачи из текста клиента: полный Guid (задача не проверяется — это дело вызывающего) или префикс id (<see cref="ShortId"/>), который
    /// подходит ровно одной задаче проекта.
    /// </summary>
    /// <returns>null — задачи с таким префиксом нет.</returns>
    /// <exception cref="TaskerValidationException">Текст не id и не префикс id; префикс подходит нескольким задачам.</exception>
    public async Task<Guid?> ResolveId(Guid projectId, string id, CancellationToken ct = default)
    {
        var parsed = TaskSeries.TaskReference.TryParse(id, allowIdPrefix: true);
        if (parsed?.Id is { } guid)
            return guid;
        if (parsed?.IdKey is not { } key)
            throw new TaskerValidationException($"'{id}' is not a task id: give the full id or its first {ShortId.Length}+ hex characters");

        var found = await FindByIdPrefix(projectId, key, ct);
        return found.Length == 0 ? null : found[0].Id;
    }

    private async Task<TaskItem[]> FindByIdPrefix(Guid projectId, string key, CancellationToken ct)
    {
        var found = await tasks.FindByIdPrefix(projectId, key, ct);
        return found.Length > 1
            ? throw new TaskerValidationException(
                $"Several tasks start with '{key.Replace("-", "")}', use a longer prefix or the full id: {string.Join(", ", found.Select(x => x.Id))}")
            : found;
    }

    private async Task<TaskItem> Save(TaskItem task, IReadOnlyList<TaskSeries.TaskSeriesNumber> numbers, string expected, CancellationToken ct)
    {
        var updated = task with { SeriesNumbers = numbers, UpdatedAt = time.GetUtcNow() };
        var version = await tasks.Update(updated, expected, ct) ?? throw Versioning.Modified(Subject(task));
        return updated with { Version = version };
    }
}
