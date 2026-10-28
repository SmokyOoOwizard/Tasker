namespace Tasker.Storage.Db.Models;

/// <summary>
/// Номер задачи в серии. ProjectId дублирует проект задачи — для уникального индекса (ProjectId, SeriesId, Number).
/// SeriesId — без внешнего ключа на серии: удаление серии оставляет недействительную ссылку, её убирает чистка.
/// </summary>
internal class TaskSeriesNumberDbModel
{
    public Guid TaskId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SeriesId { get; set; }
    public int Number { get; set; }
}
