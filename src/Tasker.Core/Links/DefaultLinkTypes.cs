using System.Security.Cryptography;
using System.Text;

namespace Tasker.Core.Links;

/// <summary>
/// Типы связей по умолчанию — как в Jira: Blocks, Duplicate, Cloners, Relates и Problem/Incident, плюс иерархический Parent/Child (эпик и его задачи).
/// У каждого проекта они появляются при первом обращении к типам связей (<see cref="LinkTypeService"/>) и дальше
/// обычные типы: можно переименовать, изменить названия сторон и удалить.
/// <para>
/// Id детерминированный — из проекта и ключа типа. Поэтому в файловом режиме две ветки git, в которых типы
/// «появились» независимо, получают те же файлы и сливаются без конфликта и дублей.
/// </para>
/// </summary>
public static class DefaultLinkTypes
{
    /// <param name="AllowCycles">false — цикл из связей этого типа запрещён (задачи, вечно ждущие друг друга, не имеют смысла).</param>
    /// <param name="Hierarchical">true — связь «родитель включает потомка» (<see cref="LinkType.Hierarchical"/>); циклы у такого типа запрещены всегда.</param>
    public record Definition(string Key, string Name, string OutwardName, string InwardName, bool AllowCycles = true, bool Hierarchical = false);

    /// <summary>Ключ иерархического типа по умолчанию — «Parent/Child».</summary>
    public const string ParentKey = "parent";

    public static readonly IReadOnlyList<Definition> All =
    [
        new("blocks", "Blocks", "blocks", "is blocked by", AllowCycles: false),
        new("duplicate", "Duplicate", "duplicates", "is duplicated by"),
        new("cloners", "Cloners", "clones", "is cloned by"),
        new("relates", "Relates", "relates to", "relates to"),
        new("problem", "Problem/Incident", "causes", "is caused by"),
        new(ParentKey, "Parent/Child", "includes", "is part of", AllowCycles: false, Hierarchical: true)
    ];

    /// <summary>
    /// Типы, появившиеся в наборе позже первых пяти. Проекты, где типы уже сохранены (файлы или БД), их не имеют: чтение показывает такой тип «виртуально»,
    /// пока в проекте нет никакого иерархического типа и типа с этим названием, а запись сохраняет (<see cref="LinkTypeService"/>) — миграции данных нет.
    /// </summary>
    public static readonly IReadOnlyList<Definition> AddedLater = All.Where(x => x.Hierarchical).ToArray();

    /// <summary>
    /// Значение <see cref="LinkType.AllowCycles"/> для файла типа, в котором признака ещё нет (записан до его появления): «Blocks»
    /// по умолчанию циклы запрещает, остальные типы, как и раньше, допускают.
    /// </summary>
    public static bool AllowCyclesWhenUnset(Guid projectId, Guid typeId) =>
        typeId != IdOf(projectId, "blocks");

    /// <summary>Один и тот же результат для одного проекта и ключа: UUID из MD5 (не для защиты — только для стабильности).</summary>
    public static Guid IdOf(Guid projectId, string key)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"tasker-link-type:{projectId:D}:{key}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }
}
