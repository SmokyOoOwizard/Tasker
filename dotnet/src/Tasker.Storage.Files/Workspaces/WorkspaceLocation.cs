using System.Security.Cryptography;
using System.Text;

namespace Tasker.Storage.Files.Workspaces;

/// <summary>
/// Где лежат данные рабочей области (вкладки десктопа). Путь канонический (см. <see cref="CanonicalPath"/>):
/// одна и та же папка, открытая через симлинк или в другом регистре, — одна рабочая область.
/// </summary>
public sealed record WorkspaceLocation
{
    private WorkspaceLocation(string path)
    {
        Path = path;
        Id = CreateId(path);
    }

    /// <summary>Канонический путь папки.</summary>
    public string Path { get; }

    /// <summary>
    /// Идентичность области — хэш канонического пути: одна папка открывается один раз, как бы её ни открыли.
    /// В адресах не используется — там имя папки (см. WorkspaceRegistry.UniqueKey).
    /// </summary>
    public string Id { get; }

    /// <summary>Название для вкладки: имя папки.</summary>
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

        return new WorkspaceLocation(path);
    }

    private static string CreateId(string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"Files:{path}"));
        return Convert.ToHexStringLower(bytes)[..12];
    }

    public override string ToString() => Path;
}
