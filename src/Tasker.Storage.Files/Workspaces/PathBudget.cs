using Tasker.Storage.Files.Storages;

namespace Tasker.Storage.Files.Workspaces;

/// <summary>
/// Длина путей файлов .tasker относительно предела Windows в 260 знаков (MAX_PATH). Сам Tasker (.NET) открывает и длинные пути,
/// а вот <c>git</c> без <c>core.longpaths</c>, проводник и многие программы — нет, поэтому рабочая папка, в которой самый длинный файл
/// не уместится в 260 знаков, на Windows создаёт проблемы не в Tasker, а вокруг него. Длина названия в имени файла ограничена
/// (<see cref="EntityFileNames.MaxSlugLength"/>) и от места рабочей папки не зависит: иначе один и тот же файл назывался бы по-разному
/// на разных машинах и каждый checkout его переименовывал бы. Чистые функции — проверяются на любой платформе.
/// </summary>
public static class PathBudget
{
    /// <summary>MAX_PATH Windows: 259 знаков и завершающий нуль.</summary>
    public const int WindowsMaxPath = 260;

    /// <summary>
    /// Самый длинный путь файла внутри рабочей папки (без неё самой): <c>.tasker\projects\{guid}\status-sets\{название}-{id8}.yaml.tmp</c>
    /// — с самой длинной папкой сущностей, названием предельной длины и временным файлом записи.
    /// </summary>
    public static int LongestRelativePath { get; } =
        TaskerDirectory.Name.Length + 1
        + "projects".Length + 1
        + Guid.Empty.ToString().Length + 1
        + EntityFolders.All.Max(x => x.Name.Length) + 1
        + EntityFileNames.MaxSlugLength + 1 + EntityFileNames.IdLength + YamlFile.Extension.Length + ".tmp".Length;

    /// <summary>Самая длинная рабочая папка, при которой все файлы ещё укладываются в MAX_PATH: путь файла = папка + «\» + относительный путь.</summary>
    public static int MaxWorkspaceRootLength => WindowsMaxPath - 1 - 1 - LongestRelativePath;

    /// <summary>Не уместится ли в 260 знаков самый длинный возможный файл .tasker этой рабочей папки (путь — как есть, без обращения к диску).</summary>
    public static bool ExceedsLegacyLimit(string workspaceRoot) => workspaceRoot.TrimEnd('\\', '/').Length > MaxWorkspaceRootLength;
}
