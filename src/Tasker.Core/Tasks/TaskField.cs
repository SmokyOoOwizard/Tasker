using Tasker.Core.Fields;

namespace Tasker.Core.Tasks;

/// <summary>
/// Собственное поле задачи: его полное определение хранится в самой задаче и больше нигде (в каталоге его нет).
/// Определение не меняется; нужно другое — поле убирают и добавляют новое.
/// </summary>
/// <param name="Name">Имя поля. Уникально среди полей задачи без учёта регистра (при добавлении).</param>
/// <param name="Required">У задачи должно быть хотя бы одно значение (проверяется при создании и правке задачи).</param>
/// <param name="EnumId">Перечисление проекта — только у поля типа <see cref="FieldType.Enum"/>.</param>
public record OwnField(string Name, FieldType Type, bool Required, bool Multiple, Guid? EnumId);

/// <summary>
/// Поле в задаче: значения и, у собственного поля, определение. Здесь хранятся только значения полей и дополнительные
/// поля задачи (из каталога или собственные); поле типа без значения в задаче не записано — оно есть у задачи по типу.
/// <para>
/// Значения — канонический текст (<see cref="FieldValues"/>): int и float — число в инвариантной культуре, bool — <c>true</c>/<c>false</c>,
/// date — <c>yyyy-MM-dd</c>, enum — id значения перечисления, string — сам текст. Пусто — значения нет.
/// </para>
/// </summary>
/// <param name="FieldId">Поле каталога (<see cref="FieldDefinition.Id"/>) или, если <paramref name="Own"/> задано, id собственного поля в этой задаче.</param>
/// <param name="Own">Определение собственного поля; null — поле из каталога.</param>
public record TaskField(Guid FieldId, IReadOnlyList<string> Values, OwnField? Own = null);

/// <summary>Откуда у задачи поле.</summary>
public enum TaskFieldSource
{
    /// <summary>Поле её типа: есть всегда, у задачи не удаляется.</summary>
    Type,

    /// <summary>Дополнительное поле из каталога: добавлено задаче (или осталось от прежнего типа), у задачи удаляется.</summary>
    Extra,

    /// <summary>Собственное поле задачи: определение хранится в ней.</summary>
    Own
}

/// <summary>Поле задачи в виде для клиентов: определение (из типа, каталога или самой задачи) вместе со значениями.</summary>
/// <param name="FieldId">Id поля каталога или собственного поля задачи — по нему поле правят у задачи.</param>
/// <param name="Required">Для поля типа — признак из типа; для собственного — из определения; дополнительные поля из каталога необязательны.</param>
/// <param name="Values">Значения в каноническом виде (у enum — id значений); пусто — значения нет.</param>
/// <param name="Texts">Те же значения для показа: у enum — названия, у остальных — как есть.</param>
public record TaskFieldView(
    Guid FieldId, string Name, FieldType Type, bool Multiple, Guid? EnumId, bool Required, TaskFieldSource Source,
    IReadOnlyList<string> Values, IReadOnlyList<string> Texts);

/// <summary>Значения поля при создании и правке задачи. <paramref name="Values"/> null или пусто — убрать значения.</summary>
/// <param name="FieldId">Поле типа задачи, дополнительное или собственное (id собственного — из <see cref="TaskFieldView.FieldId"/>);
/// поле каталога, которого у задачи ещё нет, добавляется ей как дополнительное.</param>
/// <param name="Values">Значения текстом: int, float (точка), bool (<c>true</c>/<c>false</c>), date (<c>yyyy-MM-dd</c>);
/// у enum — id значения или его название; несколько — только у поля с «несколькими значениями».</param>
public record TaskFieldValueInput(Guid FieldId, string[]? Values);

/// <summary>Новое собственное поле задачи. Значения (необязательно) задаются сразу.</summary>
public record NewOwnField(string Name, FieldType Type, string[]? Values = null, bool Required = false, bool Multiple = false, Guid? EnumId = null);

/// <summary>
/// Что менять в полях задачи; null в любом списке — ничего. Поля типа задачи убрать нельзя, добавлять свои — можно.
/// Порядок применения: убрать, добавить из каталога, значения, добавить собственные.
/// </summary>
/// <param name="Values">Задать значения (или убрать — пустым списком).</param>
/// <param name="AddFields">Добавить задаче поля из каталога (без значений); поле её типа или уже добавленное — ничего не меняет.</param>
/// <param name="NewOwnFields">Добавить собственные поля.</param>
/// <param name="RemoveFields">Убрать дополнительные и собственные поля вместе со значениями (id — как в <see cref="TaskFieldView.FieldId"/>).</param>
public record TaskFieldChanges
{
    public TaskFieldValueInput[]? Values { get; init; }
    public Guid[]? AddFields { get; init; }
    public NewOwnField[]? NewOwnFields { get; init; }
    public Guid[]? RemoveFields { get; init; }
}

/// <summary>
/// Задача вместе с вычисленным видом её полей — ответ REST и MCP при чтении, создании и правке одной задачи.
/// Всё, что есть у <see cref="TaskItem"/> (в том числе <see cref="TaskItem.Fields"/> — записанные значения), плюс
/// <see cref="FieldViews"/>: поля задачи с определениями, источником и названиями значений (<see cref="TaskService.GetFields(Guid, TaskItem, CancellationToken)"/>).
/// В списках задач вид полей не считается (для каждой задачи это чтение каталога): он только у одной задачи.
/// <para>
/// <see cref="LinkViews"/> — связи задачи с обеих сторон (исходящие и входящие; входящие хранятся в других задачах и вычисляются при чтении),
/// как у <c>get_task_links</c>. <see cref="TaskItem.Links"/> остаётся сырыми исходящими связями (id типа и цели). В списках входящие не
/// считаются: там только <see cref="TaskItem.Links"/>.
/// </para>
/// </summary>
public record TaskDetails : TaskItem
{
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public TaskDetails(TaskItem task, IReadOnlyList<TaskFieldView> fieldViews, IReadOnlyList<Links.TaskLinkView> linkViews, int linkCount) : base(task)
    {
        FieldViews = fieldViews;
        LinkViews = linkViews;
        LinkCount = linkCount;
    }

    public IReadOnlyList<TaskFieldView> FieldViews { get; init; }

    /// <summary>Действительные связи с обеих сторон, не больше <see cref="Links.TaskLinkService.MaxViewed"/>; порядок как у <see cref="Links.TaskLinkService.GetLinks(Guid, Guid, CancellationToken)"/>.</summary>
    public IReadOnlyList<Links.TaskLinkView> LinkViews { get; init; }

    /// <summary>Сколько всего действительных связей у задачи (больше длины <see cref="LinkViews"/>, если список обрезан).</summary>
    public int LinkCount { get; init; }
}
