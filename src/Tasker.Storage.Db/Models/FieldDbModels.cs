namespace Tasker.Storage.Db.Models;

/// <summary>Поле каталога. EnumId — ссылка на перечисление (NoAction: перечисление, на которое ссылается поле, удалить нельзя).</summary>
internal class FieldDbModel
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public int Type { get; set; }
    public bool Multiple { get; set; }
    public Guid? EnumId { get; set; }
    public int Version { get; set; }
}

internal class FieldEnumDbModel
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public int Version { get; set; }

    public List<FieldEnumValueDbModel> Values { get; set; } = [];
}

/// <summary>Значение перечисления; Position задаёт порядок. Удаляется вместе с перечислением.</summary>
internal class FieldEnumValueDbModel
{
    public Guid EnumId { get; set; }
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public int Position { get; set; }
}
