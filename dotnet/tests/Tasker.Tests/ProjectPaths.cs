using Tasker.Storage.Files;

namespace Tasker.Tests;

/// <summary>Папка проекта в рабочей папке: <c>projects/&lt;slug имени&gt;-&lt;id8&gt;/</c> — по имени её не собрать, ищется по id проекта.</summary>
internal static class ProjectPaths
{
    /// <param name="workspace">Рабочая папка (в ней <c>.tasker</c>).</param>
    public static string Folder(string workspace, Guid projectId) => new TaskerDirectory(workspace).Project(projectId).Root;

    /// <summary>Есть ли у проекта хоть одна папка.</summary>
    public static bool Exists(string workspace, Guid projectId) => new TaskerDirectory(workspace).ProjectDirs(projectId).Length > 0;
}
