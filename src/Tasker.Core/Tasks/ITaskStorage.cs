using Tasker.Core.Dto;

namespace Tasker.Core.Tasks;

/// <summary>Тип собственного поля (и его перечисление — у типа enum).</summary>
public record OwnFieldKind(Fields.FieldType Type, Guid? EnumId);

/// <summary>
/// Хранилище задач. Реализации: Tasker.Storage.Db (SQLite / Postgres)
/// и Tasker.Storage.Files (папка .tasker в рабочем каталоге).
/// Все методы работают в рамках одного проекта.
/// Запись — с проверкой версии, как в <see cref="Projects.IProjectStorage"/>.
/// </summary>
public interface ITaskStorage
{
    Task<TaskItem?> GetById(Guid projectId, Guid id, CancellationToken ct = default);

    /// <summary>
    /// Задачи проекта, id которых начинается с <paramref name="idKey"/> — началом Guid в виде <c>D</c> строчными (<see cref="ShortId.TryKey"/>), по <c>CreatedAt</c>, затем по Guid.
    /// Короткий id (<see cref="ShortId"/>) в консоли ищется этим методом.
    /// </summary>
    Task<TaskItem[]> FindByIdPrefix(Guid projectId, string idKey, CancellationToken ct = default);

    /// <summary>Страница задач проекта в порядке создания (старые первыми). <paramref name="filter"/> null — все задачи.</summary>
    Task<ListDto<TaskItem>> GetRange(Guid projectId, TaskFilter? filter, Page page, CancellationToken ct = default);

    /// <summary>
    /// Все задачи проекта, подходящие под фильтр, в порядке создания, — для сквозных правок («убрать значение у всех задач типа»).
    /// Для списков клиентам — <see cref="GetRange"/>: здесь страниц нет.
    /// </summary>
    Task<TaskItem[]> GetAll(Guid projectId, TaskFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Id всех задач проекта, подходящих под фильтр, в порядке списка (<see cref="TaskFilter.Sort"/>, по умолчанию по созданию, затем по id) — без загрузки
    /// самих задач. Для иерархии списка: порядок верхнего уровня и дочерних задач берётся из него.
    /// </summary>
    Task<Guid[]> GetIds(Guid projectId, TaskFilter? filter, CancellationToken ct = default);

    /// <summary>Сколько задач подходит под фильтр — для проверок «используется ли статус» и т.п.</summary>
    Task<int> Count(Guid projectId, TaskFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Какими бывают собственные поля задач проекта с этим именем (<paramref name="nameKey"/> — <see cref="FieldNames.Key"/>): различные пары
    /// «тип, перечисление» среди полей, записанных в задачах. Для разбора условия фильтра по полю, которого нет в каталоге.
    /// </summary>
    Task<OwnFieldKind[]> GetOwnFieldKinds(Guid projectId, string nameKey, CancellationToken ct = default);

    /// <summary>Наибольший номер в серии среди задач проекта; 0 — в серии ещё нет задач. Считает и недействительные ссылки.</summary>
    Task<int> GetMaxNumber(Guid projectId, Guid seriesId, CancellationToken ct = default);

    /// <summary>Все задачи проекта с этим номером в серии (больше одной — дубликат номера): по <c>CreatedAt</c>, затем по Guid.</summary>
    Task<TaskItem[]> FindByNumber(Guid projectId, Guid seriesId, int number, CancellationToken ct = default);

    /// <summary>Все дубликаты номеров проекта, по серии и номеру. Пусто — дубликатов нет.</summary>
    Task<TaskSeries.NumberConflict[]> GetNumberConflicts(Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// Задачи, у которых есть номер в серии, не входящей в <paramref name="knownSeriesIds"/> (недействительные ссылки),
    /// по <c>CreatedAt</c>, затем по Guid. Для чистки; задач с такими ссылками обычно единицы.
    /// </summary>
    Task<TaskItem[]> GetWithSeriesNotIn(Guid projectId, Guid[] knownSeriesIds, CancellationToken ct = default);

    /// <summary>
    /// Задачи проекта, у которых есть исходящая связь на <paramref name="targetId"/> (то есть входящие связи этой задачи):
    /// по <c>CreatedAt</c>, затем по Guid.
    /// </summary>
    Task<TaskItem[]> GetLinkedTo(Guid projectId, Guid targetId, CancellationToken ct = default);

    /// <summary>
    /// Исходящие связи типа <paramref name="typeId"/> у задач <paramref name="sourceIds"/> (одним запросом): источник → цели, только для задач
    /// этого проекта и только если связи есть. Цели не проверяются на существование. Для обхода графа связей (поиск цикла) по уровням.
    /// </summary>
    Task<Dictionary<Guid, Guid[]>> GetLinkTargets(Guid projectId, Guid typeId, Guid[] sourceIds, CancellationToken ct = default);

    /// <summary>Все связи типа в проекте (источник → цель): для проверки состояния (циклы после слияния git). Порядок не определён.</summary>
    Task<Links.LinkEdge[]> GetLinkEdges(Guid projectId, Guid typeId, CancellationToken ct = default);

    /// <summary>
    /// Сколько входящих связей у каждой из задач (<paramref name="targetIds"/>): число исходящих связей других задач проекта на неё.
    /// Одним запросом на страницу списка, без загрузки самих задач. Задач без входящих связей в результате нет.
    /// </summary>
    Task<Dictionary<Guid, int>> CountLinkedTo(Guid projectId, Guid[] targetIds, CancellationToken ct = default);

    /// <summary>
    /// Задачи проекта, у которых есть связь на задачу или на тип связи, которых нет (недействительная связь): по <c>CreatedAt</c>,
    /// затем по Guid. Для чистки; задач с такими связями обычно единицы.
    /// </summary>
    /// <param name="knownLinkTypeIds">Типы связей, которые есть в проекте: связь любого другого типа недействительна.</param>
    Task<TaskItem[]> GetWithInvalidLinks(Guid projectId, Guid[] knownLinkTypeIds, CancellationToken ct = default);

    /// <summary>
    /// Сколько файлов задач проекта не удалось прочитать (конфликт слияния, ошибка YAML). Нечитаемая задача выглядела бы несуществующей,
    /// и связи на неё считались бы недействительными — поэтому чистка связей при таких файлах ничего не убирает. В БД файлов нет — 0.
    /// </summary>
    Task<int> CountUnreadable(Guid projectId, CancellationToken ct = default);

    Task<string> Add(TaskItem task, CancellationToken ct = default);
    Task<string?> Update(TaskItem task, string expectedVersion, CancellationToken ct = default);
    Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default);
}
