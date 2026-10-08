using Microsoft.EntityFrameworkCore;
using Tasker.Core.Dto;

namespace Tasker.Storage.Db.Storages;

internal static class DbPage
{
    /// <summary>
    /// Страница упорядоченной выборки: общее число и сама страница — двумя запросами в БД.
    /// Порядок должен быть однозначным (например, имя, затем id), иначе страницы могут пересекаться.
    /// </summary>
    public static async Task<ListDto<T>> ToPage<TModel, T>(
        this IOrderedQueryable<TModel> query,
        Page page,
        Func<TModel, T> map,
        CancellationToken ct
    )
    {
        var total = await query.CountAsync(ct);
        var models = await query.Skip(page.Offset).Take(page.Limit).ToArrayAsync(ct);

        return new()
        {
            TotalCount = total,
            Offset = page.Offset,
            Limit = page.Limit,
            Data = models.Select(map).ToArray()
        };
    }
}
