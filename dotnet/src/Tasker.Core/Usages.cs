using Tasker.Core.Boards;

namespace Tasker.Core;

/// <summary>
/// Сбор мест, где используется сущность, в одну ошибку 409 —
/// чтобы пользователь сразу увидел всё, что мешает удалению или изменению.
/// </summary>
public sealed class Usages(ErrorSubject subject)
{
    private readonly List<(ErrorUsage Usage, string English)> _items = [];

    public ErrorSubject Subject { get; } = subject;

    public IReadOnlyList<ErrorUsage> Items => _items.Select(x => x.Usage).ToArray();

    public void AddTasks(int count, IReadOnlyList<string>? references = null)
    {
        if (count > 0)
            _items.Add((new ErrorUsage(ErrorUsage.TasksKind, Count: count, Tasks: references ?? []), $"{count} task(s)"));
    }

    public void AddLinks(int count) =>
        _items.Add((new ErrorUsage(ErrorUsage.LinksKind, Count: count), $"links of {count} task(s)"));

    public void AddOwnFields(int count) =>
        _items.Add((new ErrorUsage(ErrorUsage.OwnFieldsKind, Count: count), $"own fields of {count} task(s)"));

    public void AddBoard(string name) =>
        _items.Add((new ErrorUsage(ErrorUsage.BoardKind, Name: name), $"board '{name}'"));

    public void AddBoardColumn(Board board, BoardColumn column) =>
        _items.Add((new ErrorUsage(ErrorUsage.BoardColumnKind, Board: board.Name, Column: column.Name), $"board '{board.Name}' (column '{column.Name}')"));

    public void AddTaskType(string name) =>
        _items.Add((new ErrorUsage(ErrorUsage.TaskTypeKind, Name: name), $"task type '{name}'"));

    public void AddStatusSet(string name) =>
        _items.Add((new ErrorUsage(ErrorUsage.StatusSetKind, Name: name), $"status set '{name}'"));

    public void AddField(string name) =>
        _items.Add((new ErrorUsage(ErrorUsage.FieldKind, Name: name), $"field '{name}'"));

    public bool Any => _items.Count > 0;

    public string Message(string action) =>
        $"{Subject} is used by {string.Join("; ", _items.Select(x => x.English))} and cannot be {action}";

    public void ThrowIfAny(Func<Usages, TaskerConflictException> error)
    {
        if (Any)
            throw error(this);
    }
}
