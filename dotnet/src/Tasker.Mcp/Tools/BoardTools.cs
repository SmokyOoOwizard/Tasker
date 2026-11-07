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

    public record ColumnTasks(Guid ColumnId, string Name, string? Query, ListDto<TaskListItem>? Tasks, string? Error = null);

    [McpServerTool(Name = "list_boards", ReadOnly = true)]
    [Description("Lists boards of a project with their columns, drop rules and column queries (query: the column's task query; null - the column is defined by its statuses only). Paged: offset/limit (limit 1-200, default 50); the result has totalCount.")]
    public static Task<ListDto<Board>> ListBoards(
        ProjectService projects, IBoardStorage boards, Guid projectId,
        int offset = 0, int limit = Page.DefaultLimit, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            return await boards.GetRange(projectId, Page.Of(offset, limit), ct);
        });

    [McpServerTool(Name = "get_board", ReadOnly = true)]
    [Description("Gets a board together with the tasks in each column (first `tasksPerColumn` tasks per column, 1-200; each column reports totalCount). Each column also shows its own query (stored: a task is in the column only if its status fits AND it matches the query; a `| sort` in it orders the column unless `sort` or a `| sort` in `query` is given). A column whose stored query no longer parses (a field was renamed or deleted) has `error` instead of `tasks`. field (optional): [\"Name=value\", \"Name>=value\", \"Name:set\", ...] shows only tasks matching the field conditions (catalog or own fields), in every column, in addition to the column's own (a view filter, not stored; see list_tasks). " + TaskTools.SortHint + " In get_board the order applies inside every column. " + TaskTools.QueryHint + " In get_board the query applies inside every column, AND with the column's own query. " + TaskTools.DescriptionHint)]
    public static Task<BoardView> GetBoard(
        ProjectService projects, IBoardStorage boards, BoardService boardService, TaskService tasks, Guid projectId, Guid boardId,
        int tasksPerColumn = Page.DefaultLimit, string[]? field = null, int descriptionLength = DescriptionPreview.McpDefault, string? sort = null, string? query = null, CancellationToken ct = default) =>
        McpCall.Run(async () =>
        {
            await McpCall.RequireProject(projects, projectId, ct);
            await McpCall.CheckQuery(tasks, projectId, query, ct);
            var board = McpCall.Found(await boards.GetById(projectId, boardId, ct), $"Board {boardId}");
            var page = Page.Of(0, tasksPerColumn);

            var columns = new List<ColumnTasks>();
            foreach (var column in board.Columns)
            {
                try
                {
                    var columnTasks = await boardService.GetColumnTasks(projectId, boardId, column.Id, page, ct, field, descriptionLength, sort, query);
                    columns.Add(new ColumnTasks(column.Id, column.Name, column.Query, McpCall.Found(columnTasks, $"Column {column.Id}")));
                }
                catch (Tasker.Core.TaskerValidationException e) when (e.Error.Code == BoardService.ColumnQueryInvalidCode)
                {
                    columns.Add(new ColumnTasks(column.Id, column.Name, column.Query, null, e.Message));
                }
            }
            return new BoardView(board, columns.ToArray());
        });

    [McpServerTool(Name = "move_task")]
    [Description(
        "Moves a task to a board column: the task gets the status from the column's drop rule for its task type's status set. " +
        "A task that would not match the column's query after the move is refused (a move changes only the status): update the task first. " +
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
        "query (optional, the task query language: conditions such as .Field.Name == \"value\", .Status, .Type, functions, &&, ||, ! and parentheses, then an optional `| sort` with keys; e.g. `.Field.Component == \"API\" && .Field.Estimate >= 3 | sort -.Field.Estimate`; " +
        "a task is in the column only if its status fits AND it matches the query; its `| sort` orders the column). Fields are named in the query, so after renaming " +
        "a field the column shows an error until its query is fixed. A status may be in several columns only if their queries exclude each other " +
        "(e.g. .Field.Component == \"API\" and .Field.Component != \"API\"); otherwise one column only.")]
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
        "A column's query: omitted (null) keeps the existing column's query (none for a new column), \"\" removes it, text replaces it. " +
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
