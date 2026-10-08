using Microsoft.EntityFrameworkCore;
using Tasker.Core.Dto;
using Tasker.Core.Boards;
using Tasker.Core.Tasks;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db.Storages;

internal class BoardStorage(AppDbContext context) : IBoardStorage
{
    public async Task<Board?> GetById(Guid projectId, Guid id, CancellationToken ct = default)
    {
        var model = await Query(projectId).FirstOrDefaultAsync(x => x.Id == id, ct);
        return model == null ? null : Map(model);
    }

    public async Task<Board[]> GetAll(Guid projectId, CancellationToken ct = default)
    {
        var models = await Query(projectId).OrderBy(x => x.Name).ThenBy(x => x.Id).ToArrayAsync(ct);
        return models.Select(Map).ToArray();
    }

    public Task<ListDto<Board>> GetRange(Guid projectId, Page page, CancellationToken ct = default) =>
        Query(projectId).OrderBy(x => x.Name).ThenBy(x => x.Id).ToPage(page, Map, ct);

    public async Task<string> Add(Board board, CancellationToken ct = default)
    {
        context.Boards.Add(new BoardDbModel
        {
            Id = board.Id,
            ProjectId = board.ProjectId,
            Name = board.Name,
            Version = DbVersion.Initial,
            StatusSets = StatusSets(board),
            Columns = Columns(board)
        });
        await context.SaveChangesAsync(ct);
        return DbVersion.ToText(DbVersion.Initial);
    }

    // Сначала — условное обновление имени и версии: если доску уже изменили, дальше не идём.
    // Затем наборы и колонки (со статусами и правилами переноса, каскадом) заменяются целиком.
    // Всё в одной транзакции — при ошибке останется прежняя доска. Внутри IWriteScope (проверка по каталогу и запись доски вместе) транзакция уже открыта — берём её.
    public async Task<string?> Update(Board board, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return null;

        var own = context.Database.CurrentTransaction == null
            ? await context.Database.BeginTransactionAsync(ct)
            : null;
        await using var _ = own;

        var updated = await context.Boards
            .Where(x => x.ProjectId == board.ProjectId && x.Id == board.Id && x.Version == expected)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Name, board.Name)
                .SetProperty(x => x.Version, expected + 1), ct);
        if (updated == 0)
            return null;

        await context.Set<BoardStatusSetDbModel>().Where(x => x.BoardId == board.Id).ExecuteDeleteAsync(ct);
        await context.Set<BoardColumnDbModel>().Where(x => x.BoardId == board.Id).ExecuteDeleteAsync(ct);

        context.Set<BoardStatusSetDbModel>().AddRange(StatusSets(board));
        context.Set<BoardColumnDbModel>().AddRange(Columns(board));
        await context.SaveChangesAsync(ct);

        if (own != null)
            await own.CommitAsync(ct);
        return DbVersion.ToText(expected + 1);
    }

    public async Task<bool> Delete(Guid projectId, Guid id, string expectedVersion, CancellationToken ct = default)
    {
        if (DbVersion.Parse(expectedVersion) is not { } expected)
            return false;

        return await context.Boards
            .Where(x => x.ProjectId == projectId && x.Id == id && x.Version == expected)
            .ExecuteDeleteAsync(ct) > 0;
    }

    private static List<BoardStatusSetDbModel> StatusSets(Board board) => board.StatusSetIds
        .Select((setId, i) => new BoardStatusSetDbModel { BoardId = board.Id, StatusSetId = setId, Position = i })
        .ToList();

    private static List<BoardColumnDbModel> Columns(Board board) => board.Columns
        .Select((c, i) => new BoardColumnDbModel
        {
            Id = c.Id,
            BoardId = board.Id,
            Name = c.Name,
            Position = i,
            Statuses = c.StatusIds
                .Select((statusId, j) => new BoardColumnStatusDbModel { ColumnId = c.Id, StatusId = statusId, Position = j })
                .ToList(),
            DropStatuses = c.DropStatuses
                .Select(d => new BoardColumnDropStatusDbModel { ColumnId = c.Id, StatusSetId = d.Key, StatusId = d.Value })
                .ToList(),
            FieldFilters = c.FieldConditions
                .Select((f, j) => new BoardColumnFieldFilterDbModel
                {
                    ColumnId = c.Id, Position = j, FieldId = f.FieldId, Operator = f.Operator.ToString(), Value = f.Value
                })
                .ToList()
        })
        .ToList();

    private IQueryable<BoardDbModel> Query(Guid projectId) => context.Boards
        .AsNoTracking()
        .AsSplitQuery()
        .Include(x => x.StatusSets)
        .Include(x => x.Columns).ThenInclude(x => x.Statuses)
        .Include(x => x.Columns).ThenInclude(x => x.DropStatuses)
        .Include(x => x.Columns).ThenInclude(x => x.FieldFilters)
        .Where(x => x.ProjectId == projectId);

    private static Board Map(BoardDbModel model) => new()
    {
        Id = model.Id,
        ProjectId = model.ProjectId,
        Name = model.Name,
        StatusSetIds = model.StatusSets
            .OrderBy(x => x.Position)
            .Select(x => x.StatusSetId)
            .ToArray(),
        Columns = model.Columns
            .OrderBy(x => x.Position)
            .Select(c => new BoardColumn
            {
                Id = c.Id,
                Name = c.Name,
                StatusIds = c.Statuses.OrderBy(x => x.Position).Select(x => x.StatusId).ToArray(),
                DropStatuses = c.DropStatuses.ToDictionary(x => x.StatusSetId, x => x.StatusId),
                FieldConditions = c.FieldFilters
                    .OrderBy(x => x.Position)
                    .Select(x => new ColumnFieldFilter(x.FieldId, Enum.Parse<FieldOperator>(x.Operator), x.Value))
                    .ToArray()
            })
            .ToArray(),
        Version = DbVersion.ToText(model.Version)
    };
}
