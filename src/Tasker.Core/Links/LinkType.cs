namespace Tasker.Core.Links;

/// <summary>
/// Тип связи между задачами проекта («блокирует», «дублирует», «связана с»…). Как в Jira, у связи два названия —
/// для каждой из сторон: задача, от которой связь исходит, видит <see cref="OutwardName"/> («блокирует»),
/// задача, на которую она указывает, — <see cref="InwardName"/> («заблокирована»).
/// Типы настраиваются в проекте; набор по умолчанию — как в Jira (<see cref="DefaultLinkTypes"/>).
/// </summary>
public record LinkType
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }

    /// <summary>Название типа (Blocks): по нему тип находят в командах. Уникально в проекте без учёта регистра.</summary>
    public required string Name { get; init; }

    /// <summary>Как называется связь для задачи, от которой она исходит: «blocks» — «A blocks B».</summary>
    public required string OutwardName { get; init; }

    /// <summary>Как называется связь для задачи, на которую она указывает: «is blocked by» — «B is blocked by A».</summary>
    public required string InwardName { get; init; }

    /// <summary>См. <see cref="Versioning"/>.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// Допускает ли тип циклы: «A blocks B» и «B blocks A» (или любая цепочка, замыкающаяся в круг). false — добавление связи, замыкающей цикл,
    /// отклоняется (<see cref="TaskLinkService.Add"/>). У типа без направления («relates to») циклов нет по определению, признак ни на что не влияет.
    /// У типов по умолчанию нет только у «Blocks» (<see cref="DefaultLinkTypes"/>); у созданных пользователем — как он выбрал (по умолчанию да).
    /// </summary>
    public bool AllowCycles { get; init; }

    /// <summary>
    /// Иерархический тип («includes» / «is part of»): исходящая связь делает цель дочерней задачей источника, а источник — родителем (эпиком).
    /// Такие связи <c>task list</c> показывает деревом (<see cref="Tasks.TaskService.ListTree"/>). У задачи может быть несколько родителей
    /// (граф без циклов), поэтому у иерархического типа циклы всегда запрещены (<see cref="AllowCycles"/> = false) и стороны названы по-разному
    /// (родитель и потомок различимы). Задаётся при создании и правке типа; у типов по умолчанию он есть у «Parent/Child» (<see cref="DefaultLinkTypes"/>).
    /// </summary>
    public bool Hierarchical { get; init; }

    /// <summary>Названия сторон совпадают («relates to»): у связи нет направления, для обеих задач она выглядит одинаково.</summary>
    public bool IsSymmetric => string.Equals(OutwardName, InwardName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Исходящая связь задачи: «эта задача —<see cref="TypeId"/>→ <see cref="TargetId"/>». Хранится в файле задачи-источника
/// (как номера серий); обратная сторона вычисляется. Задача, на которую указывает связь, должна быть в том же проекте;
/// недействительные ссылки (задачу или тип удалили в другой ветке git) в представлениях пропускаются.
/// </summary>
public record TaskLink(Guid TypeId, Guid TargetId);

/// <summary>С какой стороны связи смотрит задача.</summary>
public enum LinkDirection
{
    /// <summary>Связь исходит от этой задачи («блокирует»).</summary>
    Outward,

    /// <summary>Связь указывает на эту задачу («заблокирована»).</summary>
    Inward
}

/// <summary>Задача на другом конце связи — достаточно, чтобы показать её в списке (<c>TSK-7</c>, статус, название).</summary>
public record LinkedTask(Guid Id, string Title, Guid StatusId, IReadOnlyList<TaskSeries.TaskSeriesNumber> SeriesNumbers);

/// <summary>
/// Связь глазами одной задачи. <see cref="Name"/> — название именно с её стороны: «blocks» у источника и «is blocked by» у цели.
/// </summary>
public record TaskLinkView(Guid TypeId, string TypeName, LinkDirection Direction, string Name, LinkedTask Task);
