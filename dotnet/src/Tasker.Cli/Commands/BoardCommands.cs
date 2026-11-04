using System.CommandLine;
using Tasker.Cli.Completion;
using Tasker.Core.Boards;
using Tasker.Core.Dto;
using Tasker.Core.Statuses;
using Tasker.Core.Tasks;

namespace Tasker.Cli.Commands;

internal static class BoardCommands
{
    private const string ColumnHelp =
        "Column, left to right: \"Name=status,status\" (statuses by id or name). " +
        "A task moved into the column gets the first of its statuses that belongs to the task type's status set";

    private const string FilterHelp =
        "Field condition of a column, in addition to its statuses (AND): \"Column:Field=value\", \"Column:Field>=3\", \"Column:Field:set\" and so on - " +
        "the same conditions as 'task list --field' (=, !=, >, >=, <, <=, :set, :unset, :attached, :detached), by the field's name in the catalog " +
        "(stored by id). Repeat for several conditions; the column is named as in --column. A task is in the column only if its status and all conditions fit";

    private static async Task<Board> Find(Context ctx, Guid projectId, string reference) =>
        Refs.FindItem(await ctx.Get<IBoardStorage>().GetAll(projectId, ctx.Ct), reference, x => x.Id, x => x.Name, "board");

    /// <summary>Колонка доски по id или названию (без учёта регистра).</summary>
    private static BoardColumn FindColumn(Board board, string reference) =>
        Refs.FindItem(board.Columns, reference, x => x.Id, x => x.Name, $"column of board '{board.Name}'");

    /// <summary>Задачи колонки страницей; задачи колонки получает сервис доски (статус колонки и набор статусов типа задачи).</summary>
    private static async Task<ListDto<TaskListItem>> ColumnTasks(
        Context ctx, Guid projectId, Board board, BoardColumn column, Page page, string[]? fieldFilters = null, int descriptionLength = DescriptionPreview.Full,
        string? sort = null) =>
        await ctx.Get<BoardService>().GetColumnTasks(projectId, board.Id, column.Id, page, ctx.Ct, fieldFilters, descriptionLength, sort)
        ?? throw new CliException($"No column '{column.Name}' on board '{board.Name}'");

    public static Command Build(GlobalOptions g)
    {
        var name = Kit.Name("name", "Board name");
        var column = new Argument<string>("column") { Description = "Column of the board (id or name)" };
        var tasksPaging = new Kit.Paging();
        var viewFields = Kit.Many("--field", "Show only tasks by a field (catalog or own field of tasks): \"Name=value\", \"Name>=value\", Name:set and so on (as in 'task list --field'); a view filter, the column does not store it");
        var sort = Kit.Sort();
        var showLimit = new Option<int>("--limit") { Description = $"How many tasks to show in each column, 1-{Page.MaxLimit}", DefaultValueFactory = _ => Page.DefaultLimit };
        var showAll = new Option<bool>("--all") { Description = "Show all tasks of every column (cannot be combined with --limit)" };
        var sets = Kit.Many("--status-set", "Status sets whose tasks are on the board (id or name)", required: true);
        var columns = Kit.Many("--column", ColumnHelp, required: true);
        var columnFilters = Kit.Many("--column-filter", FilterHelp);
        var reference = Kit.Ref("Board");
        var newName = new Option<string?>("--name") { Description = "New name" };
        var newSets = Kit.Many("--status-set", "New status sets of the board (id or name); replaces the current ones");
        var newColumns = Kit.Many("--column", ColumnHelp + ". Replaces all columns; a column with the name of an existing one stays the same column and keeps its field conditions");
        var newColumnFilters = Kit.Many("--column-filter", FilterHelp +
            ". Replaces the conditions of the named column (without --column - of that column of the current board; other columns keep theirs); \"Column:\" removes them");
        var descriptionLength = Kit.DescriptionLength();
        var expected = Kit.ExpectedVersion();

        reference.Suggests(g, Sources.Boards);
        column.Suggests(g, Sources.BoardColumns(reference));
        sets.Suggests(g, Sources.StatusSets);
        newSets.Suggests(g, Sources.StatusSets);
        columns.Suggests(g, Sources.ColumnSpec);
        newColumns.Suggests(g, Sources.ColumnSpec);
        columnFilters.Suggests(g, Sources.ColumnFilters(columns));
        newColumnFilters.Suggests(g, Sources.ColumnFilters(newColumns, reference));
        viewFields.Suggests(g, Sources.FieldValues);
        sort.Suggests(g, Sources.SortKeys);
        
        return Kit.Group("board", "Boards",
            Kit.Leaf(g, "create", "Creates a board", c =>
            {
                c.Arguments.Add(name);
                c.Options.Add(sets);
                c.Options.Add(columns);
                c.Options.Add(columnFilters);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var allSets = await ctx.Get<IStatusSetStorage>().GetAll(projectId, ctx.Ct);
                var allStatuses = await ctx.Get<IStatusStorage>().GetAll(projectId, ctx.Ct);

                var setIds = Refs.FindAll(allSets, parse.GetRequiredValue(sets), x => x.Id, x => x.Name, "status set");
                var specs = parse.GetRequiredValue(columns);
                var filters = AssignFilters(Kit.Values(parse, columnFilters), specs.Select(ColumnName).ToArray());
                var inputs = specs.Select((x, i) => ParseColumn(x, allSets, allStatuses, setIds, [], filters[i])).ToArray();

                var board = await ctx.Get<BoardService>().Create(projectId, new CreateBoard(parse.GetRequiredValue(name), setIds, inputs), ctx.Ct);
                ctx.Print(board, $"Created board '{board.Name}' {board.Id}");
            }),
            Kit.List(g, "Lists boards",
                async (ctx, page) => await ctx.Get<IBoardStorage>().GetRange(await ctx.ProjectId(), page, ctx.Ct),
                x => Kit.Row(x.Id, x.Name, $"({string.Join(" | ", x.Columns.Select(c => c.Name))})")),
            Kit.Leaf(g, "get", "Shows a board with its columns", c => c.Arguments.Add(reference), async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var board = await Find(ctx, projectId, parse.GetRequiredValue(reference));
                var allSets = await ctx.Get<IStatusSetStorage>().GetAll(projectId, ctx.Ct);
                var allStatuses = await ctx.Get<IStatusStorage>().GetAll(projectId, ctx.Ct);

                var service = ctx.Get<BoardService>();
                var columnLines = new List<string>();
                foreach (var c in board.Columns)
                {
                    var where = await service.DescribeFilters(projectId, c, ctx.Ct);
                    columnLines.Add($"  {c.Name}: {string.Join(", ", c.StatusIds.Select(s => Kit.Named(allStatuses, s, x => x.Id, x => x.Name)))}" +
                        (where.Length > 0 ? $"; where {string.Join(" and ", where)}" : ""));
                }

                var text = Kit.Fields(("id", board.Id), ("name", board.Name), ("version", board.Version))
                    + "\nstatus sets:\n" + string.Join("\n", board.StatusSetIds.Select(x => "  " + Kit.Named(allSets, x, s => s.Id, s => s.Name)))
                    + "\ncolumns:\n" + string.Join("\n", columnLines);
                ctx.Print(board, text);
            }),
            Kit.Leaf(g, "tasks", "Lists the tasks of a board column (the same lines as 'task list')", c =>
            {
                c.Arguments.Add(reference);
                c.Arguments.Add(column);
                c.Options.Add(viewFields);
                c.Options.Add(sort);
                c.Options.Add(descriptionLength);
                tasksPaging.AddTo(c);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var board = await Find(ctx, projectId, parse.GetRequiredValue(reference));
                var found = FindColumn(board, parse.GetRequiredValue(column));
                var line = await TaskCommands.LineFormat(ctx, projectId);
                ctx.Print(await tasksPaging.Load(parse, page => ColumnTasks(ctx, projectId, board, found, page, parse.GetValue(viewFields), parse.GetValue(descriptionLength), parse.GetValue(sort))), line);
            }),
            Kit.Leaf(g, "show", "Shows the whole board: every column in order with its tasks", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(showLimit);
                c.Options.Add(showAll);
                c.Options.Add(viewFields);
                c.Options.Add(sort);
                c.Options.Add(descriptionLength);
            }, async (parse, ctx) =>
            {
                var all = parse.GetValue(showAll);
                if (all && parse.GetResult(showLimit) is { Implicit: false })
                    throw new CliException("Use either --all or --limit, not both");

                var projectId = await ctx.ProjectId();
                var board = await Find(ctx, projectId, parse.GetRequiredValue(reference));
                var line = await TaskCommands.LineFormat(ctx, projectId);

                var shown = new List<(BoardColumn Column, ListDto<TaskListItem> Tasks, string[] Where)>();
                foreach (var item in board.Columns)
                {
                    // С --all читаем колонку целиком (страницами по 200), иначе — первые --limit задач.
                    var tasks = all
                        ? await ReadAll(page => ColumnTasks(ctx, projectId, board, item, page, parse.GetValue(viewFields), parse.GetValue(descriptionLength), parse.GetValue(sort)))
                        : await ColumnTasks(ctx, projectId, board, item, Page.Of(0, parse.GetValue(showLimit)), parse.GetValue(viewFields), parse.GetValue(descriptionLength), parse.GetValue(sort));
                    shown.Add((item, tasks, await ctx.Get<BoardService>().DescribeFilters(projectId, item, ctx.Ct)));
                }

                // Колонки таблицы общие на всю доску: ссылки, статусы и типы выровнены и между колонками доски.
                var rows = ctx.Table(shown.SelectMany(x => x.Tasks.Data).Select(line).ToArray(), "  ");
                var next = 0;
                var lines = new List<string>();
                foreach (var (item, tasks, where) in shown)
                {
                    if (lines.Count > 0)
                        lines.Add("");
                    lines.Add($"{item.Name}{(where.Length > 0 ? $" [{string.Join(" and ", where)}]" : "")} ({tasks.TotalCount})");
                    lines.AddRange(rows[next..(next + tasks.Data.Length)]);
                    next += tasks.Data.Length;
                    if (tasks.Data.Length == 0)
                        lines.Add("  (no tasks)");
                    else if (tasks.Data.Length < tasks.TotalCount)
                        lines.Add($"  ... and {tasks.TotalCount - tasks.Data.Length} more (use --all, or 'board tasks {board.Name} \"{item.Name}\"')");
                }

                ctx.Print(new
                {
                    board = new { board.Id, board.Name },
                    columns = shown.Select(x => new { x.Column.Id, x.Column.Name, fieldFilters = x.Where, totalCount = x.Tasks.TotalCount, tasks = x.Tasks.Data })
                }, string.Join('\n', lines));
            }),
            Kit.Leaf(g, "update", "Changes a board", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(newName);
                c.Options.Add(newSets);
                c.Options.Add(newColumns);
                c.Options.Add(newColumnFilters);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                Kit.RequireChange(parse, newName, newSets, newColumns, newColumnFilters);
                var projectId = await ctx.ProjectId();
                var board = await Find(ctx, projectId, parse.GetRequiredValue(reference));
                var allSets = await ctx.Get<IStatusSetStorage>().GetAll(projectId, ctx.Ct);
                var allStatuses = await ctx.Get<IStatusStorage>().GetAll(projectId, ctx.Ct);

                var setIds = Kit.Values(parse, newSets) is { } setRefs
                    ? Refs.FindAll(allSets, setRefs, x => x.Id, x => x.Name, "status set")
                    : null;
                var specs = Kit.Values(parse, newColumns);
                var filterSpecs = Kit.Values(parse, newColumnFilters);
                BoardColumnInput[]? inputs = null;
                if (specs != null)
                {
                    var filters = AssignFilters(filterSpecs, specs.Select(ColumnName).ToArray());
                    inputs = specs.Select((x, i) => ParseColumn(x, allSets, allStatuses, setIds ?? board.StatusSetIds, board.Columns, filters[i])).ToArray();
                }
                else if (filterSpecs != null)
                {
                    // Только условия: колонки остаются как есть, условия меняются у названных.
                    var filters = AssignFilters(filterSpecs, board.Columns.Select(x => x.Name).ToArray());
                    inputs = board.Columns
                        .Select((x, i) => new BoardColumnInput(x.Id, x.Name, x.StatusIds, x.DropStatuses.ToDictionary(), filters[i]))
                        .ToArray();
                }

                var updated = await ctx.Get<BoardService>().Update(projectId, board.Id,
                    new UpdateBoard(parse.GetValue(newName), setIds, inputs, parse.GetValue(expected) ?? board.Version), ctx.Ct)
                    ?? throw new CliException($"No board '{board.Id}'");
                ctx.Print(updated, $"Updated board '{updated.Name}' {updated.Id}");
            }),
            Kit.Leaf(g, "delete", "Deletes a board (tasks stay)", c =>
            {
                c.Arguments.Add(reference);
                c.Options.Add(expected);
            }, async (parse, ctx) =>
            {
                var projectId = await ctx.ProjectId();
                var board = await Find(ctx, projectId, parse.GetRequiredValue(reference));
                await ctx.Get<BoardService>().Delete(projectId, board.Id, parse.GetValue(expected) ?? board.Version, ctx.Ct);
                ctx.Print(new { deleted = board.Id }, Kit.Deleted("board", board.Name, board.Id));
            }));
    }

    /// <summary>Все задачи колонки: страницами по <see cref="Page.MaxLimit"/>, одним списком.</summary>
    private static async Task<ListDto<TaskListItem>> ReadAll(Func<Page, Task<ListDto<TaskListItem>>> loadPage)
    {
        var items = new List<TaskListItem>();
        while (true)
        {
            var page = await loadPage(new Page(items.Count, Page.MaxLimit));
            items.AddRange(page.Data);
            if (page.Data.Length == 0 || items.Count >= page.TotalCount)
                break;
        }

        return new ListDto<TaskListItem> { TotalCount = items.Count, Offset = 0, Limit = items.Count, Data = [.. items] };
    }

    /// <summary>Название колонки в <c>--column "Название=статус,статус"</c>.</summary>
    private static string ColumnName(string spec)
    {
        var separator = spec.IndexOf('=');
        return separator <= 0 ? spec.Trim() : spec[..separator].Trim();
    }

    /// <summary>
    /// Раскладывает <c>--column-filter "Колонка:условие"</c> по колонкам: название колонки — самое длинное из названий, за которым стоит «:»
    /// (в названии колонки двоеточие допустимо). Пустое условие («Колонка:») — убрать условия колонки. null у колонки — для неё ничего не задано.
    /// </summary>
    private static string[]?[] AssignFilters(string[]? specs, IReadOnlyList<string> names)
    {
        var result = new List<string>?[names.Count];
        foreach (var spec in specs ?? [])
        {
            var text = spec.TrimStart();
            var index = -1;
            for (var i = 0; i < names.Count; i++)
            {
                if (text.StartsWith(names[i] + ":", StringComparison.OrdinalIgnoreCase) && (index < 0 || names[i].Length > names[index].Length))
                    index = i;
            }
            if (index < 0)
                throw new CliException($"--column-filter '{spec}': expected \"Column:condition\" with the name of one of the columns ({string.Join(", ", names)})");

            var condition = text[(names[index].Length + 1)..].Trim();
            result[index] ??= [];
            if (condition.Length > 0)
                result[index]!.Add(condition);
        }
        return result.Select(x => x?.ToArray()).ToArray();
    }

    private static BoardColumnInput ParseColumn(
        string spec, StatusSet[] sets, Status[] statuses, Guid[] boardSetIds, IEnumerable<BoardColumn> existing, string[]? filters)
    {
        var separator = spec.IndexOf('=');
        if (separator <= 0)
            throw new CliException($"Column '{spec}': expected \"Name=status,status\"");

        var refs = spec[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var statusIds = Refs.FindAll(statuses, refs, x => x.Id, x => x.Name, "status");

        // Правило переноса: для каждого набора доски — первый статус колонки из этого набора.
        var drop = new Dictionary<Guid, Guid>();
        foreach (var setId in boardSetIds)
        {
            var set = sets.First(x => x.Id == setId);
            var statusId = statusIds.Cast<Guid?>().FirstOrDefault(x => set.StatusIds.Contains(x!.Value));
            if (statusId != null)
                drop[setId] = statusId.Value;
        }

        // Колонка с именем существующей остаётся той же колонкой, а не удаляется и создаётся заново.
        var name = spec[..separator].Trim();
        var same = existing.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return new BoardColumnInput(same?.Id, name, statusIds, drop, filters);
    }
}
