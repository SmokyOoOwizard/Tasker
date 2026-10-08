using System.ComponentModel;
using ModelContextProtocol.Server;
using Tasker.Core.Boards;
using Tasker.Core.Dto;
using Tasker.Core.Projects;
using Tasker.Core.Tasks;

namespace Tasker.Mcp.Tools;

[McpServerToolType]
public static class BoardTools
{
    /// <summary>Доска с задачами по колонкам — агенту удобнее одним вызовом, чем колонка за колонкой.</summary>
    public record BoardView(Board Board, ColumnTasks[] Columns);

    /// <param name="FieldFilters">Условия колонки по полям текстом (<c>Имя=значение</c>, <c>Имя&gt;=3</c>, <c>Имя:set</c>), пусто — колонку определяют одни статусы.</param>
    public record ColumnTasks(Guid ColumnId, string Name, string[] FieldFilters, ListDto<TaskListItem> Tasks);

    [McpServerTool(Name = "list_boards", ReadOnly = true)]
    [Description("Lists boards of a project with their columns, drop rules and field conditions (fieldConditions: fieldId, operator, value - an enum value is its id; get_board shows them as text). Paged: offset/limit (limit 1-200, default 50); the result has totalCount.")]
    public static Task<ListDto<Board>> ListBoards(
        ProjectService projects, IBoardStorage boards, Guid projectId,
        int offset = 0, int limit = Page.DefaultLimit, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await boards.GetRange(projectId, Page.Of(offset, limit), ct);
        });

    [McpServerTool(Name = "get_board", ReadOnly = true)]
    [Description("Gets a board together with the tasks in each column (first `tasksPerColumn` tasks per column, 1-200; each column reports totalCount). Each column also lists its own fieldFilters (stored conditions: a task is in the column only if its status AND all of them fit). field (optional): [\"Name=value\", \"Name>=value\", \"Name:set\", ...] shows only tasks matching the field conditions (catalog or own fields), in every column, in addition to the column's own (a view filter, not stored; see list_tasks). " + TaskTools.SortHint + " In get_board the order applies inside every column. " + TaskTools.DescriptionHint)]
    public static Task<BoardView> GetBoard(
        ProjectService projects, IBoardStorage boards, BoardService boardService, Guid projectId, Guid boardId,
        int tasksPerColumn = Page.DefaultLimit, string[]? field = null, int descriptionLength = DescriptionPreview.McpDefault, string? sort = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            var board = McpCall.Found(await boards.GetById(projectId, boardId, ct), $"Board {boardId}");
            var page = Page.Of(0, tasksPerColumn);

            var columns = new List<ColumnTasks>();
            foreach (var column in board.Columns)
            {
                var tasks = await boardService.GetColumnTasks(projectId, boardId, column.Id, page, ct, field, descriptionLength, sort);
                columns.Add(new ColumnTasks(column.Id, column.Name, await boardService.DescribeFilters(projectId, column, ct), McpCall.Found(tasks, $"Column {column.Id}")));
            }
            return new BoardView(board, columns.ToArray());
        });

    [McpServerTool(Name = "move_task")]
    [Description(
        "Moves a task to a board column: the task gets the status from the column's drop rule for its task type's status set. " +
        "A task that does not match the column's field conditions is refused (a move does not change fields): update its fields first. " +
        "`version` is the task's current version (from get_task). " + TaskTools.TaskIdHint)]
    public static Task<TaskItem> MoveTask(
        ProjectService projects, BoardService boards, TaskService tasks, Guid projectId, Guid boardId, Guid columnId, string taskId, string version,
        CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return McpCall.Found(await boards.MoveTask(projectId, boardId, columnId, new MoveTask(await McpCall.TaskId(tasks, projectId, taskId, ct), version), ct), "Board, column or task");
        });

    [McpServerTool(Name = "create_board")]
    [Description(
        "Creates a board. statusSetIds: status sets whose tasks appear on the board. " +
        "columns (left to right): name, statusIds (tasks with these statuses show in the column), " +
        "dropStatuses (status set id -> status id a task of that set gets when moved into the column; must be one of the column's statuses), " +
        "fieldFilters (optional, text as in list_tasks `field`: [\"Component=API\", \"Estimate>=3\", \"Owner:set\"]; a task is in the column only if its status AND all conditions fit; " +
        "stored by field id, so renaming a field keeps working). A status may be in several columns only if their conditions exclude each other " +
        "(e.g. Component=API and Component!=API); otherwise one column only.")]
    public static Task<Board> CreateBoard(
        ProjectService projects, BoardService boards, Guid projectId, string name, Guid[] statusSetIds, BoardColumnInput[] columns,
        CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await boards.Create(projectId, new CreateBoard(name, statusSetIds, columns), ct);
        });

    [McpServerTool(Name = "update_board", Idempotent = true)]
    [Description(
        "Changes a board (null keeps a field). columns replaces all columns: a column with `id` keeps it, without `id` is new, missing columns are removed. " +
        "A column's fieldFilters: omitted (null) keeps the existing column's conditions (none for a new column), [] removes them, a list replaces them. " +
        "`version` is the board's current version (from list_boards/get_board); on [modified] re-read and retry.")]
    public static Task<Board> UpdateBoard(
        ProjectService projects, BoardService boards, Guid projectId, Guid boardId, string version,
        string? name = null, Guid[]? statusSetIds = null, BoardColumnInput[]? columns = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return McpCall.Found(await boards.Update(projectId, boardId, new UpdateBoard(name, statusSetIds, columns, version), ct), $"Board {boardId}");
        });

    [McpServerTool(Name = "delete_board", Destructive = true)]
    [Description("Deletes a board (tasks stay). `version` is the board's current version.")]
    public static Task<string> DeleteBoard(ProjectService projects, BoardService boards, Guid projectId, Guid boardId, string version, CancellationToken ct) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            McpCall.Found(await boards.Delete(projectId, boardId, version, ct), $"Board {boardId}");
            return "deleted";
        });
}
