using Tasker.Core.Boards;
using Tasker.Core.Tasks;

namespace Tasker.Core;

/// <summary>
/// Сбор мест, где используется сущность, в одну ошибку 409 —
/// чтобы пользователь сразу увидел всё, что мешает удалению или изменению.
/// </summary>
internal class Usages(string subject)
{
    private readonly List<string> _items = [];

    public void Add(string usage) => _items.Add(usage);

    public void AddTasks(int count)
    {
        if (count > 0)
            _items.Add($"{count} task(s)");
    }

    public void AddBoardColumn(Board board, BoardColumn column) =>
        _items.Add($"board '{board.Name}' (column '{column.Name}')");

    public void ThrowIfAny(string action)
    {
        if (_items.Count > 0)
            throw new TaskerConflictException($"{subject} is used by {string.Join("; ", _items)} and cannot be {action}");
    }
}
