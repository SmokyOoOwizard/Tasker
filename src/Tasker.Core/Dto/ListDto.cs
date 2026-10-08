namespace Tasker.Core.Dto;

/// <summary>Страница списка. Все списки в API и MCP отдаются так — задач (и не только) может быть очень много.</summary>
public class ListDto<T>
{
    public required int TotalCount { get; init; }
    public required int Offset { get; init; }
    public required int Limit { get; init; }

    public required T[] Data { get; init; }
}

/// <summary>
/// Параметры страницы: <see cref="Offset"/> от 0, <see cref="Limit"/> от 1 до <see cref="MaxLimit"/>
/// (byte: больше 255 не бывает по построению).
/// </summary>
public readonly record struct Page(int Offset, byte Limit)
{
    public const byte MaxLimit = 200;
    public const byte DefaultLimit = 50;

    /// <summary>Значения от клиента: отрицательный offset — 0, limit вне 1..200 — в эти границы, не задан — 50.</summary>
    public static Page Of(int? offset, int? limit) =>
        new(Math.Max(offset ?? 0, 0), (byte)Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit));

    /// <summary>Страница из списка, уже загруженного в память (для небольших списков: статусы проекта и т.п.).</summary>
    public ListDto<T> Apply<T>(IReadOnlyCollection<T> all) => new()
    {
        TotalCount = all.Count,
        Offset = Offset,
        Limit = Limit,
        Data = all.Skip(Offset).Take(Limit).ToArray()
    };
}
