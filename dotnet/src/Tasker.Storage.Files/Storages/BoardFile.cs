using Tasker.Core.Boards;
using Tasker.Core.Tasks;
using Tasker.Core.Workspace;

namespace Tasker.Storage.Files.Storages;

/// <summary>
/// Формат файла доски <c>boards/&lt;id&gt;.yaml</c>. Колонки — в порядке слева направо;
/// <c>onDrop</c> — какой статус получает задача набора (ключ), перетянутая в колонку;
/// <c>fieldFilters</c> (с формата 6, только у колонок с условиями) — условия по полям каталога, по И со статусами: <c>field</c> — id поля, <c>op</c> —
/// оператор словом (equal, notEqual, greater, greaterOrEqual, less, lessOrEqual, set, unset, attached, detached), <c>value</c> — значение в
/// каноническом виде (у enum — id значения; у set, unset, attached и detached его нет):
/// <code>
/// id: 3f2e1d0c-9b8a-4c7d-8e6f-5a4b3c2d1e0f
/// name: Разработка
/// statusSets:
/// - 7a1b2c3d-4e5f-4a6b-8c9d-0e1f2a3b4c5d
/// columns:
/// - id: 1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d
///   name: В работе
///   statuses:
///   - 5d2c1a3b-7e4f-4a6b-8c9d-0e1f2a3b4c5d
///   onDrop:
///     7a1b2c3d-4e5f-4a6b-8c9d-0e1f2a3b4c5d: 5d2c1a3b-7e4f-4a6b-8c9d-0e1f2a3b4c5d
///   fieldFilters:
///   - field: 9c8b7a6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d
///     op: greaterOrEqual
///     value: "3"
/// </code>
/// </summary>
internal static class BoardFile
{
    public static async Task<Board?> Read(Guid projectId, string path, CancellationToken ct) =>
        await YamlFile.Read<BoardFileModel>(path, ct) is { } file ? Map(projectId, file, path) : null;

    public static Task<string> Write(string path, Board board, CancellationToken ct) =>
        YamlFile.Write(path, ToFile(board), ct);

    /// <summary>Перезапись; <paramref name="newPath"/> другой — файл переименовывается (изменилось название или имя старого формата).</summary>
    public static Task<string?> Update(string path, string newPath, Board board, string expectedVersion, CancellationToken ct) =>
        YamlFile.WriteIfMatch(path, newPath, ToFile(board), expectedVersion, ct);

    private static BoardFileModel ToFile(Board board) => new()
    {
        Id = board.Id,
        Name = board.Name,
        StatusSets = board.StatusSetIds.ToList(),
        Columns = board.Columns
            .Select(c => new ColumnFileModel
            {
                Id = c.Id,
                Name = c.Name,
                Statuses = c.StatusIds.ToList(),
                OnDrop = c.DropStatuses.ToDictionary(),
                FieldFilters = c.FieldConditions.Length == 0
                    ? null
                    : c.FieldConditions.Select(f => new FilterFileModel { Field = f.FieldId, Op = OperatorWord(f.Operator), Value = f.Value }).ToList()
            })
            .ToList()
    };

    private static string OperatorWord(FieldOperator op) => char.ToLowerInvariant(op.ToString()[0]) + op.ToString()[1..];

    private static Board Map(Guid projectId, Versioned<BoardFileModel> file, string path) => new()
    {
        Id = file.Model.Id,
        ProjectId = projectId,
        Name = file.Model.Name ?? "",
        StatusSetIds = file.Model.StatusSets?.ToArray() ?? [],
        Columns = file.Model.Columns?
            .Select(c => new BoardColumn
            {
                Id = c.Id,
                Name = c.Name ?? "",
                StatusIds = c.Statuses?.ToArray() ?? [],
                DropStatuses = c.OnDrop ?? new Dictionary<Guid, Guid>(),
                FieldConditions = c.FieldFilters?
                    .Select(f => new ColumnFieldFilter(
                        f.Field,
                        Enum.TryParse<FieldOperator>(f.Op, ignoreCase: true, out var op) && Enum.IsDefined(op)
                            ? op
                            : throw new UnsupportedFormatException($"{Path.GetFileName(path)}: unknown field filter operator '{f.Op}': update Tasker"),
                        f.Value))
                    .ToArray() ?? []
            })
            .ToArray() ?? [],
        Version = file.Version
    };

    private class BoardFileModel : FileModel
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public List<Guid>? StatusSets { get; set; }
        public List<ColumnFileModel>? Columns { get; set; }
    }

    private class ColumnFileModel
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public List<Guid>? Statuses { get; set; }
        public Dictionary<Guid, Guid>? OnDrop { get; set; }
        public List<FilterFileModel>? FieldFilters { get; set; }
    }

    private class FilterFileModel
    {
        public Guid Field { get; set; }
        public string? Op { get; set; }
        public string? Value { get; set; }
    }
}
