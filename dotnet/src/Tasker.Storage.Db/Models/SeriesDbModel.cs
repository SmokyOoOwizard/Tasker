namespace Tasker.Storage.Db.Models;

internal class SeriesDbModel
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }

    // Латиница и цифры, регистр важен: уникальность (ProjectId, Prefix) — точное сравнение.
    public required string Prefix { get; set; }

    public int Version { get; set; }
}
