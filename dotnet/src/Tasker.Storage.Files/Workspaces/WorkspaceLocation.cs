using System.Security.Cryptography;
using System.Text;

namespace Tasker.Storage.Files.Workspaces;

public enum WorkspaceKind
{
    /// <summary>Папка: данные — YAML-файлы в &lt;папка&gt;/.tasker.</summary>
    Files,

    /// <summary>Файл SQLite (только из аргументов запуска: <c>--sqlite=...</c>).</summary>
    Sqlite
}

/// <summary>
/// Где лежат данные рабочей области (вкладки десктопа). Путь канонический (см. <see cref="CanonicalPath"/>):
/// одна и та же папка, открытая через симлинк или в другом регистре, — одна рабочая область.
/// </summary>
public sealed record WorkspaceLocation
{
    private WorkspaceLocation(WorkspaceKind kind, string path)
    {
        Kind = kind;
        Path = path;
        Id = CreateId(kind, path);
    }

    public WorkspaceKind Kind { get; }

    /// <summary>Канонический путь: папка (<see cref="WorkspaceKind.Files"/>) или файл БД (<see cref="WorkspaceKind.Sqlite"/>).</summary>
    public string Path { get; }

    /// <summary>
    /// Идентичность области — хэш канонического пути: одна папка открывается один раз, как бы её ни открыли.
    /// В адресах не используется — там имя папки (см. WorkspaceRegistry.UniqueKey).
    /// </summary>
    public string Id { get; }

    /// <summary>Название для вкладки: имя папки или файла БД.</summary>
    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;

    /// <summary>
    /// Папка с данными в файлах. Выбрали саму <c>.tasker</c> — берём папку над ней:
    /// иначе появилась бы <c>.tasker/.tasker</c>.
    /// </summary>
    public static WorkspaceLocation Files(string folder)
    {
        var path = CanonicalPath.Of(folder);
        if (string.Equals(System.IO.Path.GetFileName(path), TaskerDirectory.Name, StringComparison.OrdinalIgnoreCase)
            && System.IO.Path.GetDirectoryName(path) is { } parent)
            path = parent;

        return new WorkspaceLocation(WorkspaceKind.Files, path);
    }

    public static WorkspaceLocation Sqlite(string file) => new(WorkspaceKind.Sqlite, CanonicalPath.Of(file));

    private static string CreateId(WorkspaceKind kind, string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{path}"));
        return Convert.ToHexStringLower(bytes)[..12];
    }

    public override string ToString() => $"{Kind} {Path}";
}
